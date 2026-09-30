using System.Net;
using System.Net.Sockets;

namespace GitHubBackup.App;
internal sealed record PreflightIssue(string ErrorCode, string UserMessage, bool BlocksBackup)
{
    internal NetworkCheckResult? NetworkFailure { get; init; }
}
internal sealed record PreflightReport(bool StorageReady, bool ToolsReady, bool AuthReady, bool OwnerMatches, bool NetworkReady, bool RepositoryVisibilityKnown, bool PrivacyReady, bool SourceIntegrityReady, IReadOnlyList<PreflightIssue> Issues)
{
    internal bool CanStartBackup => StorageReady && ToolsReady && AuthReady && OwnerMatches && NetworkReady && RepositoryVisibilityKnown && PrivacyReady && SourceIntegrityReady && Issues.All(x => !x.BlocksBackup);
}
internal sealed record PreflightSnapshot(PreflightReport Report, ToolInventory Tools, ProxyProfile SelectedProxyProfile, IReadOnlyDictionary<string,string?> ChildEnvironment, string? EmptyHooksDirectory, IReadOnlyList<RepositoryDescriptor> Repositories, IReadOnlyList<SensitivePathAssessment> UnsafeSensitivePaths)
{
    internal string? ApprovedBackupRoot { get; init; }
}
internal sealed class PreflightSession(PreflightSnapshot snapshot, GitRuntimeContext runtime, AuthConfigLease config, OperationJob job,
    IGitHubHttpTransport? transport = null) : IAsyncDisposable
{
    private bool disposed;
    private readonly string login = config.Login;
    private readonly HashSet<RecoveryLease> recoveries=[];
    internal PreflightSnapshot Snapshot { get; } = snapshot;
    internal IGitHubHttpTransport? HttpTransport { get { Revalidate(); return transport; } }
    internal IReadOnlyDictionary<string,string?> CreateEnvironment() { Revalidate(); return config.CreateEnvironment(Snapshot.ChildEnvironment); }
    internal OperationJob Job {get {Revalidate();return job;}}
    internal RecoveryLease MintRecoveryLease()
    {
        Revalidate();
        if(!Snapshot.Report.CanStartBackup||Snapshot.ChildEnvironment is not RuntimeEnvironment env||!ReferenceEquals(env.Owner,runtime))
            throw new InvalidOperationException("RECOVERY_SESSION_INVALID");
        runtime.RequireSessionBinding(Snapshot.Tools,Snapshot.EmptyHooksDirectory,Snapshot.ChildEnvironment,job);
        var lease=new RecoveryLease(this,runtime,job,Snapshot.Tools.Git!,CreateEnvironment());
        recoveries.Add(lease);return lease;
    }
    internal void RevalidateRecovery(RecoveryLease lease)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!recoveries.Contains(lease))throw new InvalidOperationException("RECOVERY_SESSION_INVALID");
        config.Revalidate();
        if(!string.Equals(login,config.Login,StringComparison.OrdinalIgnoreCase))throw new AuthBoundaryException("AUTH_LOGIN_MISMATCH");
    }
    internal void ReleaseRecovery(RecoveryLease lease)=>recoveries.Remove(lease);
    internal void Revalidate()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if (job.IsCancellationRequested) throw new OperationCanceledException();
        config.Revalidate();
        if (!string.Equals(login,config.Login,StringComparison.OrdinalIgnoreCase)) throw new AuthBoundaryException("AUTH_LOGIN_MISMATCH");
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        if(recoveries.Count!=0)throw new InvalidOperationException("PREFLIGHT_RECOVERY_PENDING");
        // Preserve ownership on drain/disposal failure; the caller may retry cleanup.
        transport?.Dispose();
        await job.CancelAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await runtime.DisposeAsync().ConfigureAwait(false);
        config.Dispose(); disposed = true;
    }
}
internal sealed record PreflightCheckResult(PreflightSnapshot Snapshot, PreflightSession? LiveSession);
internal sealed class PreflightService(AuthService auth, IProcessRunner runner, Func<OperationJob,CancellationToken,Task<ToolInventory>> detectTools,
    IReadOnlyDictionary<string,string?> baseEnvironment, ProxyScope proxies, SourceIntegrityAudit integrity, StoragePathProbes? storageProbes = null, Func<string,long>? availableBytes = null)
{
    internal Task<PreflightCheckResult> CheckAsync(BackupMode mode, AppSettings settings, OperationJob job,
        Func<string,ToolInventory,IReadOnlyDictionary<string,string?>,OperationJob,CancellationToken,Task<IReadOnlyList<RepositoryDescriptor>>> discoverRepositories, CancellationToken cancellationToken)
        => CheckCoreAsync(mode, settings, job, discoverRepositories, null, cancellationToken, null);

    internal Task<PreflightCheckResult> CheckNativeAsync(BackupMode mode, AppSettings settings, OperationJob job,
        IGitHubCredentialReader reader, CancellationToken cancellationToken,
        Func<SocketsHttpHandler,HttpMessageHandler>? handlerFactory = null)
        => CheckCoreAsync(mode, settings, job, null, reader, cancellationToken, handlerFactory);

    private async Task<PreflightCheckResult> CheckCoreAsync(BackupMode mode, AppSettings settings, OperationJob job,
        Func<string,ToolInventory,IReadOnlyDictionary<string,string?>,OperationJob,CancellationToken,Task<IReadOnlyList<RepositoryDescriptor>>>? discoverRepositories,
        IGitHubCredentialReader? nativeReader, CancellationToken cancellationToken,
        Func<SocketsHttpHandler,HttpMessageHandler>? handlerFactory)
    {
        var issues = new List<PreflightIssue>();
        bool storage = false, toolsReady = false, authReady = false, ownerMatches = false, network = false, visibility = false, privacy = false, source = false;
        ToolInventory tools = ToolInventory.Empty; ProxyProfile selected = ProxyProfile.Direct;
        IReadOnlyList<RepositoryDescriptor> repositories = Array.Empty<RepositoryDescriptor>();
        IReadOnlyList<SensitivePathAssessment> unsafePaths = Array.Empty<SensitivePathAssessment>();
        GitRuntimeContext? publicContext = null, runtime = null; AuthConfigLease? config = null;
        IGitHubHttpTransport? transport = null;
        IReadOnlyDictionary<string,string?> environment = ProxyProfile.Direct.Environment;
        bool succeeded = false;
        try
        {
            if (HasPendingCleanup) throw new PreflightFailure("PREFLIGHT_CLEANUP_PENDING","前一次操作的清理尚未完成，不能开始新操作。");
            CheckCancellation();
            StoragePathValidation validation = storageProbes is null
                ? await StoragePathPolicy.ValidateAsync(settings.BackupRoot,cancellationToken).ConfigureAwait(false)
                : await StoragePathPolicy.ValidateAsync(settings.BackupRoot,storageProbes,cancellationToken).ConfigureAwait(false);
            if (!validation.Allowed)
            {
                string fallback = Path.Combine(baseEnvironment.TryGetValue("USERPROFILE",out var profile) && profile is not null ? profile : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),"GitHub-Backups");
                throw new PreflightFailure(validation.ErrorCode,"备份目录不可用；请选择可写的本地固定磁盘目录，例如 " + fallback);
            }
            long free = availableBytes is null ? new DriveInfo(Path.GetPathRoot(settings.BackupRoot)!).AvailableFreeSpace : availableBytes(settings.BackupRoot);
            if (free < 1024L * 1024 * 1024) throw new PreflightFailure("STORAGE_FREE_SPACE_REQUIRED","备份目录至少需要 1 GiB 可用空间。");
            storage = true;
            if (!AuthConfigLease.IsLogin(settings.Owner)) throw new PreflightFailure("OWNER_INVALID","请输入有效的 GitHub 用户名。");
            // Local residue sanitization precedes any network operation or discovery.
            string? localAuthFailure = null;
            try { using var startup = auth.AcquireConfig(); }
            catch (AuthBoundaryException ex) { localAuthFailure = ex.Code; }
            tools = await detectTools(job,cancellationToken).ConfigureAwait(false);
            toolsReady = tools.Git is { IsSupported: true } && tools.GitHubCli is { IsSupported: true } && tools.GitLfs is { IsSupported: true };
            if (!toolsReady) throw new PreflightFailure("PREFLIGHT_TOOLS_REQUIRED","需要满足最低版本要求的 Git、GitHub CLI 和 Git LFS。");
            if (localAuthFailure is not null) throw new AuthBoundaryException(localAuthFailure);
            publicContext = await GitRuntimeContext.CreatePublicProbeAsync(baseEnvironment,cancellationToken).ConfigureAwait(false);
            AuthResult lastAuth = new(false,"","AUTH_APP_LOGIN_REQUIRED");
            var probe = new NetworkProbe(runner);
            var choice = await proxies.SelectAsync(async (profile,token) =>
            {
                var candidate = ProxyScope.Merge(publicContext.Environment,profile);
                var git = await probe.CheckPublicGitAsync(tools,candidate,job,token).ConfigureAwait(false);
                if (!git.Success) return git;
                return await RetryPolicy.ExecuteAsync(async retryToken =>
                {
                    lastAuth = await auth.CheckAsync(tools,candidate,job,retryToken).ConfigureAwait(false);
                    CheckCancellation();
                    return lastAuth.AuthReady ? new(true,NetworkFailureKind.None,null,"") : lastAuth.NetworkFailure ?? new(false,NetworkFailureKind.Unknown,null,lastAuth.ErrorCode);
                },token).ConfigureAwait(false);
            },cancellationToken).ConfigureAwait(false);
            selected = choice.Profile; authReady = lastAuth.AuthReady; network = choice.Check.Success;
            if (!network)
            {
                if (!lastAuth.AuthReady) issues.Add(new(lastAuth.ErrorCode,"GitHub 登录或账户验证未通过。",true));
                throw new PreflightFailure(choice.Check.ErrorCode,choice.Check.FailureKind == NetworkFailureKind.RateLimited ? "GitHub 限流；重置时间不可用，请稍后重新检查。" : "网络检查未通过，请检查当前网络或代理。");
            }
            ownerMatches = string.Equals(settings.Owner,lastAuth.Login,StringComparison.OrdinalIgnoreCase);
            if (!ownerMatches) throw new PreflightFailure("AUTH_OWNER_MISMATCH","备份所有者必须与已登录的 GitHub 用户一致。");
            await publicContext.DisposeAsync().ConfigureAwait(false); publicContext = null;
            config = auth.AcquireConfig();
            Revalidate();
            var authenticatedBase = config.CreateEnvironment(ProxyScope.Merge(baseEnvironment,selected));
            Revalidate();
            runtime = await GitRuntimeContext.CreateAsync(tools.Git!,tools.GitHubCli!,runner,job,authenticatedBase,cancellationToken).ConfigureAwait(false);
            Revalidate();
            environment = config.CreateEnvironment(runtime.Environment);
            if (nativeReader is not null)
            {
                // Consent and exact-target acquisition precede the fixed /user binding request.
                GitHubCredentialLease credential = await auth.AcquireApiCredentialAsync(settings.Owner, config,
                    nativeReader, cancellationToken).ConfigureAwait(false);
                try
                {
                    transport = await GitHubHttpTransport.BindAsync(credential, settings.Owner, selected,
                        cancellationToken, handlerFactory).ConfigureAwait(false);
                }
                finally { if (transport is null) credential.Dispose(); }
                try { repositories = await new RepositoryDiscoveryService(transport).DiscoverAsync(settings.Owner, cancellationToken).ConfigureAwait(false); }
                finally { Revalidate(); }
            }
            else
            {
                try { repositories = Array.AsReadOnly((await discoverRepositories!(settings.Owner,tools,environment,job,cancellationToken).ConfigureAwait(false)).ToArray()); }
                finally { Revalidate(); }
            }
            CheckCancellation(); ValidateRepositories(settings.Owner,repositories); visibility = true;
            if (repositories.Count != 0)
            {
                var actual = await new NetworkProbe(runner).CheckRepositoryAsync(tools,new Uri(repositories[0].Url),environment,job,cancellationToken).ConfigureAwait(false);
                network = actual.Success;
                if (!network) throw new PreflightFailure(actual.ErrorCode,"实际仓库的 Git 传输检查未通过。");
            }
            else issues.Add(new("GIT_OWNER_PROBE_NOT_APPLICABLE","账户当前没有仓库，未执行仓库专用 Git 检查。",false));
            string root = NativeFileSystem.CanonicalPath(settings.BackupRoot), ownerRoot = Path.Combine(root,settings.Owner);
            var candidates = repositories.SelectMany(r => new[] { Path.Combine(ownerRoot,"mirrors",r.LocalName + ".git"),Path.Combine(ownerRoot,"wikis",r.LocalName + ".wiki.git"),Path.Combine(ownerRoot,"metadata",r.LocalName),Path.Combine(ownerRoot,"releases",r.LocalName) }).ToArray();
            var common = integrity.ValidateBackupRoot(root);
            var trees = integrity.ValidateExistingTrees(ownerRoot,candidates,true);
            unsafePaths = Array.AsReadOnly(common.UnsafePaths.Concat(trees.UnsafePaths).DistinctBy(p => p.FullPath,StringComparer.OrdinalIgnoreCase).OrderBy(p => p.FullPath,StringComparer.OrdinalIgnoreCase).ToArray());
            privacy = unsafePaths.Count == 0;
            source = !unsafePaths.Any(p => !p.ErrorCode.StartsWith("PREFLIGHT_PRIVATE_",StringComparison.Ordinal));
            foreach (var path in unsafePaths) issues.Add(new(path.ErrorCode,"现有备份路径未通过隐私或完整性检查；请查看并修复对应路径。",true));
            Revalidate(); CheckCancellation();
            succeeded = Report().CanStartBackup;
            void Revalidate()
            {
                config.Revalidate();
                if (!string.Equals(lastAuth.Login,config.Login,StringComparison.OrdinalIgnoreCase)) throw new AuthBoundaryException("AUTH_LOGIN_MISMATCH");
            }
        }
        catch (HttpBindingCleanupException ex)
        {
            transport = ex.Transport;
            issues.Add(ex.WasCancelled
                ? new("PREFLIGHT_CANCELLED","预检已取消。",true)
                : new("PREFLIGHT_CHECK_FAILED","预检失败，请重新检查设置和本地路径。",true));
        }
        catch (GitRuntimeCleanupException ex) { runtime = ex.Context; issues.Add(new("PREFLIGHT_CLEANUP_FAILED","临时运行环境清理未完成，仍需保留当前操作。",true)); }
        catch (PreflightFailure ex) { issues.Add(new(ex.Code,ex.UserMessage,true)); }
        catch (AuthBoundaryException ex) { authReady = false; issues.Add(new(ex.Code,"应用认证配置已失效，请重新检查登录。",true)); }
        catch (OperationCanceledException) { issues.Add(new("PREFLIGHT_CANCELLED","预检已取消。",true)); }
        catch (HttpTransferException ex) when (ex.FailureKind == NetworkFailureKind.RateLimited)
        {
            issues.Add(new("HTTP_RATE_LIMITED", "GitHub 请求次数已受限。", true)
                { NetworkFailure = new(false, NetworkFailureKind.RateLimited, ex.RateLimitReset, "HTTP_RATE_LIMITED") });
        }
        catch (Exception)
        { issues.Add(new("PREFLIGHT_CHECK_FAILED","预检失败，请重新检查设置和本地路径。",true)); }
        if (!succeeded)
        {
            // Diagnostic data cannot authorize work. Keep ownership until draining
            // completes; cleanup failures retain capabilities on this service.
            try
            {
                transport?.Dispose(); transport = null;
                await job.CancelAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (publicContext is not null) await publicContext.DisposeAsync().ConfigureAwait(false);
                if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
                config?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                retainedCleanup.Add((job,publicContext,runtime,config,transport));
                issues.Add(new("PREFLIGHT_CLEANUP_FAILED","清理尚未完成；当前操作仍被保留，不能开始备份。",true));
            }
            return new(new(Report(),tools,selected,ProxyProfile.Direct.Environment,null,repositories,unsafePaths),null);
        }
        runtime!.FreezeSessionInventory(tools);
        var snapshot = new PreflightSnapshot(Report(),tools,selected,environment,runtime.EmptyHooksDirectory,repositories,unsafePaths)
            { ApprovedBackupRoot = NativeFileSystem.CanonicalPath(settings.BackupRoot) };
        return new(snapshot,new(snapshot,runtime,config!,job,transport));
        PreflightReport Report() => new(storage,toolsReady,authReady,ownerMatches,network,visibility,privacy,source,Array.AsReadOnly(issues.ToArray()));
        void CheckCancellation() { cancellationToken.ThrowIfCancellationRequested(); if (job.IsCancellationRequested) throw new OperationCanceledException(cancellationToken); }
    }
    private readonly List<(OperationJob Job,GitRuntimeContext? Public,GitRuntimeContext? Runtime,AuthConfigLease? Config,
        IGitHubHttpTransport? Transport)> retainedCleanup = [];
    internal bool HasPendingCleanup => retainedCleanup.Count != 0;
    internal async Task RetryCleanupAsync()
    {
        try
        {
            foreach (var item in retainedCleanup.ToArray())
            {
                item.Transport?.Dispose();
                await item.Job.CancelAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (item.Public is not null) await item.Public.DisposeAsync().ConfigureAwait(false);
                if (item.Runtime is not null) await item.Runtime.DisposeAsync().ConfigureAwait(false);
                item.Config?.Dispose(); retainedCleanup.Remove(item);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { throw new IOException("PREFLIGHT_CLEANUP_FAILED"); }
    }
    private static void ValidateRepositories(string owner,IReadOnlyList<RepositoryDescriptor> repositories)
    {
        var ids = new HashSet<long>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in repositories)
        {
            if (repository.RepositoryId <= 0 || !ids.Add(repository.RepositoryId) || !names.Add(repository.LocalName)
                || string.IsNullOrEmpty(repository.LocalName) || repository.LocalName is "." or ".." || repository.LocalName.EndsWith('.') || repository.LocalName.EndsWith(' ')
                || repository.LocalName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                || !Uri.TryCreate(repository.Url,UriKind.Absolute,out var uri) || !NetworkProbe.IsRepositoryUrl(uri)
                || !uri.AbsolutePath.StartsWith("/" + owner + "/",StringComparison.OrdinalIgnoreCase)) throw new PreflightFailure("REPOSITORY_DISCOVERY_INVALID","仓库列表包含无效或不属于当前账户的条目。");
        }
    }
    private sealed class PreflightFailure(string code,string userMessage) : Exception(code)
    { internal string Code { get; } = code; internal string UserMessage { get; } = userMessage; }
}
