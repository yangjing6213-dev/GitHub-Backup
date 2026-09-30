namespace GitHubBackup.App;

// Only capabilities reach the form; no credential or live preflight session is a UI value.
internal sealed record DesktopActions(
    Func<CancellationToken, Task<SettingsLoadResult>> LoadSettings,
    Func<AppSettings, CancellationToken, Task> SaveSettings,
    Func<BackupMode, AppSettings, CancellationToken, Task<EnvironmentStatus>> Check,
    Func<BackupMode, AppSettings, IProgress<BackupProgress>, CancellationToken, Task<BackupRunResult>> Run,
    Func<IProgress<string>, CancellationToken, Task<AuthResult>> Login,
    Func<Task> Cancel, Func<Task> RetryCleanup, Func<bool> HasPendingCleanup,
    Func<AppSettings, CancellationToken, Task<SummaryReadResult>> ReadHistory,
    Func<AppSettings, CancellationToken, Task<ValidatedLogDocument>> ReadLatestLog,
    Func<AppSettings, CancellationToken, Task<SafeOpenResult>> OpenBackupFolder,
    Func<AppSettings, CancellationToken, Task<DiagnosticDisplayPreview>> PreviewDiagnostics,
    Func<AppSettings, string, string, bool, CancellationToken, Task<DiagnosticSaveStatus>> SaveDiagnostics,
    Func<CancellationToken, Task<ToolInventory>> DetectTools,
    Func<BackupMode, AppSettings, DependencyId, bool, CancellationToken, Task<EnvironmentStatus>> Install,
    Func<BackupMode, AppSettings, bool, bool, CancellationToken, Task<EnvironmentStatus>> SelectRoot,
    Func<BackupMode, AppSettings, EnvironmentStatus, IReadOnlyList<SensitivePathAssessment>, bool, CancellationToken, Task<EnvironmentStatus>> Repair);

internal sealed record EnvironmentStatus(PreflightReport Report, ToolInventory Tools, string SelectedProxyDisplayName,
    IReadOnlyList<RepositoryDescriptor> Repositories, IReadOnlyList<SensitivePathAssessment> UnsafeSensitivePaths);

internal sealed record DiagnosticDisplayPreview(string PreviewId, IReadOnlyList<string> DisplayLines);

internal sealed class DesktopWorkflow
{
    private readonly RunCoordinator coordinator = new();
    private readonly PreflightService preflight;
    private readonly SettingsStore settings;
    private readonly BackupOrchestrator backup;
    private readonly AuthService auth;
    private readonly ToolDetector detector;
    private readonly IReadOnlyDictionary<string, string?> environment;
    private readonly ProxyScope proxies;
    private readonly IGitHubCredentialReader credentials;
    private readonly AppPaths paths;
    private readonly SafeOpenService safeOpen;
    private readonly DependencySetupService dependencies;
    private readonly Func<OperationJob, CancellationToken, Task<ToolInventory>> detectTools;
    private readonly Func<BackupMode, AppSettings, OperationJob, CancellationToken, Task<PreflightCheckResult>> check;
    private readonly Func<Task> cleanupPreflight;
    private (BackupMode Mode, AppSettings Settings, PreflightSnapshot Snapshot, EnvironmentStatus Display)? latestCheck;
    private (AppSettings Settings, DiagnosticsStore Store, DiagnosticExportPreview Preview)? diagnostic;

    internal DesktopWorkflow(AppPaths paths, IProcessRunner? processRunner = null,
        Func<OperationJob, CancellationToken, Task<ToolInventory>>? detectTools = null,
        Func<BackupMode, AppSettings, OperationJob, CancellationToken, Task<PreflightCheckResult>>? check = null,
        Func<Task>? cleanupPreflight = null)
    {
        this.paths = paths;
        environment = ChildEnvironmentBuilder.CreateCurrentBase([]);
        var runner = processRunner ?? new ProcessRunner(); settings = new(paths);
        detector = new(runner, environment); auth = new(paths, runner, detector.DetectAsync);
        proxies = new(new NetworkProbe(runner)); credentials = new GitHubCredentialReader();
        preflight = new(auth, runner, detector.DetectAsync, environment, proxies, new());
        backup = new(runner, paths, coordinator: coordinator);
        safeOpen = new(runner);
        this.detectTools = detectTools ?? detector.DetectAsync;
        this.check = check ?? ((mode, selected, job, token) => preflight.CheckNativeAsync(mode, selected, job, credentials, token));
        this.cleanupPreflight = cleanupPreflight ?? preflight.RetryCleanupAsync;
        dependencies = new(runner, this.detectTools);
    }

    internal DesktopActions Actions => new(settings.LoadAsync, (selected, token) => { latestCheck = null; return settings.SaveAsync(selected, token); }, CheckAsync,
        (mode, selected, progress, token) => backup.RunWithPreflightAsync(mode, selected,
            (job, cancellation) => preflight.CheckNativeAsync(mode, selected, job, credentials, cancellation),
            preflight.RetryCleanupAsync, progress, token),
        LoginAsync, coordinator.CancelAsync, coordinator.RetryCleanupAsync, () => coordinator.HasPendingCleanup,
        (selected, token) => new SummaryStore(paths).ReadLatestAsync(Path.Combine(selected.BackupRoot, selected.Owner), paths.DiagnosticFallbackRoot, token, selected.Owner),
        ReadLatestLogAsync,
        (selected, token) => RunViewAsync(coordinator, (job, cancellation) => safeOpen.OpenBackupFolderAsync(selected.BackupRoot, job, cancellation), token),
        PreviewDiagnosticsAsync, SaveDiagnosticsAsync,
        token => { latestCheck = null; return RunViewAsync(coordinator, detectTools, token); },
        InstallAsync, SelectRootAsync, RepairAsync);

    internal static async Task<T> RunViewAsync<T>(RunCoordinator coordinator, Func<OperationJob, CancellationToken, Task<T>> work, CancellationToken token)
    {
        if (coordinator.IsBusy) throw new InvalidOperationException("OPERATION_ALREADY_ACTIVE");
        var job = OperationJob.Create(); AtomicFileCleanupException? owned = null; GitRuntimeContext? runtime = null; T value = default!;
        Task<BackupRunResult> operation;
        try
        {
            operation = coordinator.RunAsync(job, async cancellation =>
            {
                try { cancellation.ThrowIfCancellationRequested(); value = await work(job, cancellation).ConfigureAwait(false); }
                catch (AtomicFileCleanupException cleanup) { owned = cleanup; throw; }
                catch (GitRuntimeCleanupException cleanup) { runtime = cleanup.Context; throw; }
                return new(BackupSummary.PreflightFailure(BackupMode.Daily, "", "", "", "view", ""), false);
            }, async () =>
            {
                await job.CancelAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (owned is not null) { owned.RetryCleanup(); owned = null; }
                if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
                job.Dispose();
            }, token);
        }
        catch { job.Dispose(); throw; }
        var result = await operation.ConfigureAwait(false);
        if (result.HasPendingCleanup) throw new IOException("BACKUP_CLEANUP_PENDING");
        return value;
    }

    private Task<ValidatedLogDocument> ReadLatestLogAsync(AppSettings selected, CancellationToken token) =>
        RunViewAsync(coordinator, async (_, cancellation) =>
        {
            string owner = OwnerRoot(selected);
            var history = await new SummaryStore(paths).ReadLatestAsync(owner, paths.DiagnosticFallbackRoot, cancellation, selected.Owner).ConfigureAwait(false);
            if (history.Latest is null || history.LatestIsFallback) throw new IOException("OWNER_LOG_UNAVAILABLE");
            return await safeOpen.ReadLatestLogAsync(owner, history.Latest, cancellation).ConfigureAwait(false);
        }, token);

    private Task<DiagnosticDisplayPreview> PreviewDiagnosticsAsync(AppSettings selected, CancellationToken token) =>
        RunViewAsync(coordinator, async (_, cancellation) =>
        {
            diagnostic = null;
            string owner = OwnerRoot(selected); var store = new DiagnosticsStore(paths, owner);
            var sources = DiagnosticSources(owner, cancellation);
            if (sources.Count == 0) throw new IOException("DIAGNOSTIC_SOURCES_UNAVAILABLE");
            var preview = await store.PreviewAsync(sources, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested(); diagnostic = (selected, store, preview);
            return new DiagnosticDisplayPreview(preview.PreviewId, preview.DisplayLines);
        }, token);

    private async Task<DiagnosticSaveStatus> SaveDiagnosticsAsync(AppSettings selected, string previewId, string target, bool confirmed, CancellationToken token)
    {
        try { return await RunViewAsync(coordinator, async (_, cancellation) =>
        {
            var issued = diagnostic;
            if (issued is null || issued.Value.Preview.PreviewId != previewId) return DiagnosticSaveStatus.Stale;
            diagnostic = null; // One confirmation attempt owns this capability; disk previews follow the store's retention policy.
            if (!confirmed) return DiagnosticSaveStatus.Cancelled;
            if (issued.Value.Settings != selected) return DiagnosticSaveStatus.Stale;
            return await issued.Value.Store.SaveAsync(issued.Value.Preview, target, true, cancellation).ConfigureAwait(false);
        }, token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (diagnostic?.Preview.PreviewId == previewId) diagnostic = null;
            throw;
        }
    }

    private static string OwnerRoot(AppSettings selected)
    {
        if (!AuthConfigLease.IsLogin(selected.Owner)) throw new ArgumentException("OWNER_INVALID");
        return Path.Combine(NativeFileSystem.CanonicalPath(selected.BackupRoot), selected.Owner);
    }

    private IReadOnlyList<string> DiagnosticSources(string owner, CancellationToken token)
    {
        var sources = new List<string>();
        // Match the existing store allowlist, never accept UI-supplied source paths or enumerate payload trees.
        foreach (var (directory, prefix, suffix, appData) in new[]
        {
            (Path.Combine(owner, "logs"), "backup-", ".log", false),
            (Path.Combine(owner, "manifests"), "summary-", ".json", false),
            (paths.DiagnosticLogRoot, "diagnostic-", ".log", true),
            (Path.Combine(paths.LocalAppDataRoot, "logs"), "diagnostic-", ".log", true),
            (paths.DiagnosticFallbackRoot, "summary-", ".json", true)
        })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using IDisposable parent = appData ? AppDataPathPolicy.Acquire(paths, directory, AppDataEntryKind.Directory)
                    : SummaryStore.RequirePrivateDirectory(owner);
                using IDisposable? child = appData ? null : SummaryStore.RequirePrivateDirectory(directory);
                foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    token.ThrowIfCancellationRequested(); string name = Path.GetFileName(path);
                    if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)
                        || name.Length <= prefix.Length + suffix.Length || !name[prefix.Length..^suffix.Length].All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) continue;
                    sources.Add(path);
                    sources.Sort((a, b) => StringComparer.Ordinal.Compare(Path.GetFileName(b), Path.GetFileName(a)));
                    if (sources.Count > 20) sources.RemoveAt(20);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        }
        return sources;
    }

    internal Task<EnvironmentStatus> CheckAsync(BackupMode mode, AppSettings selected, CancellationToken token) =>
        SetupAndRecheckAsync(mode, selected, null, token);

    private async Task<EnvironmentStatus> SetupAndRecheckAsync(BackupMode mode, AppSettings selected,
        Func<OperationJob, CancellationToken, Task>? setup, CancellationToken token)
    {
        latestCheck = null; PreflightSnapshot? snapshot = null;
        await CheckAndReleaseAsync(coordinator, (job, cancellation) => check(mode, selected, job, cancellation),
            cleanupPreflight, value => snapshot = value, token, setup).ConfigureAwait(false);
        if (snapshot is null) throw new InvalidOperationException("PREFLIGHT_CHECK_FAILED");
        var display = new EnvironmentStatus(snapshot.Report, snapshot.Tools, snapshot.SelectedProxyProfile.DisplayName,
            Array.AsReadOnly(snapshot.Repositories.ToArray()), Array.AsReadOnly(snapshot.UnsafeSensitivePaths.ToArray()));
        latestCheck = (mode, selected, snapshot, display);
        return display;
    }

    private async Task<EnvironmentStatus> InstallAsync(BackupMode mode, AppSettings selected, DependencyId id, bool confirmed, CancellationToken token)
    {
        if (!confirmed) { latestCheck = null; throw new OperationCanceledException("DEPENDENCY_CONFIRMATION_REQUIRED"); }
        DependencyInstallResult? installation = null;
        var status = await SetupAndRecheckAsync(mode, selected, async (job, cancellation) =>
        {
            installation = await dependencies.InstallAsync(id, true, job, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
        }, token).ConfigureAwait(false);
        if (installation?.Status == DependencyInstallStatus.Installed) return status;
        latestCheck = null;
        return status with { Report = status.Report with { ToolsReady = false,
            Issues = Array.AsReadOnly(status.Report.Issues.Prepend(new PreflightIssue("DEPENDENCY_SETUP_INCOMPLETE", "安装未完成，请使用官方手动安装指引或重新检测。", true)).ToArray()) } };
    }

    private Task<EnvironmentStatus> SelectRootAsync(BackupMode mode, AppSettings selected, bool confirmed, bool createConfirmed, CancellationToken token)
    {
        if (!confirmed) { latestCheck = null; throw new OperationCanceledException("STORAGE_CONFIRMATION_REQUIRED"); }
        latestCheck = null;
        string root = NativeFileSystem.CanonicalPath(selected.BackupRoot);
        selected = selected with { BackupRoot = root };
        return SetupAndRecheckAsync(mode, selected, async (_, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed || Path.GetDirectoryName(root) is not { } parent)
                throw new IOException("STORAGE_FIXED_VOLUME_REQUIRED");
            // Pin existing ancestors: confirmation permits creating this exact root, never missing parents.
            using var parents = NativeFileSystem.PinDirectories(parent);
            if (!Directory.Exists(root))
            {
                if (!createConfirmed) throw new OperationCanceledException("STORAGE_CREATE_CONFIRMATION_REQUIRED");
                AclPolicy.CreateRestrictedDirectory(root, System.Security.Principal.WindowsIdentity.GetCurrent().User!, requireNew: true);
            }
            using var directory = NativeFileSystem.PinDirectories(root);
            var validation = await StoragePathPolicy.ValidateAsync(root, cancellation).ConfigureAwait(false);
            if (!validation.Allowed) throw new IOException(validation.ErrorCode);
            await settings.SaveAsync(selected, cancellation).ConfigureAwait(false);
        }, token);
    }

    private Task<EnvironmentStatus> RepairAsync(BackupMode mode, AppSettings selected, EnvironmentStatus display,
        IReadOnlyList<SensitivePathAssessment> entries, bool confirmed, CancellationToken token)
    {
        var issued = latestCheck;
        latestCheck = null;
        if (!confirmed) throw new OperationCanceledException("ACL_REPAIR_CONFIRMATION_REQUIRED");
        if (issued is null || issued.Value.Mode != mode || issued.Value.Settings != selected || !ReferenceEquals(issued.Value.Display, display)
            || entries.Count == 0 || entries.Any(entry => !RepairPolicy.IsRepairable(entry.ErrorCode)
                || !issued.Value.Snapshot.UnsafeSensitivePaths.Contains(entry)
                || !string.Equals(entry.FullPath, NativeFileSystem.CanonicalPath(entry.FullPath), StringComparison.OrdinalIgnoreCase))
            || entries.Select(e => e.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new InvalidOperationException("ACL_REPAIR_SELECTION_STALE");
        string[] confirmedPaths = entries.Select(e => e.FullPath).ToArray();
        return SetupAndRecheckAsync(mode, selected, (_, cancellation) =>
        {
            foreach (string path in confirmedPaths)
            {
                cancellation.ThrowIfCancellationRequested();
                AclPolicy.HardenExisting(path, System.Security.Principal.WindowsIdentity.GetCurrent().User!, confirmedPaths);
            }
            return Task.CompletedTask;
        }, token);
    }

    internal static async Task<BackupRunResult> CheckAndReleaseAsync(RunCoordinator coordinator,
        Func<OperationJob, CancellationToken, Task<PreflightCheckResult>> check, Func<Task> cleanupPreflight,
        Action<PreflightSnapshot> receive, CancellationToken token, Func<OperationJob, CancellationToken, Task>? setup = null)
    {
        if (coordinator.IsBusy) throw new InvalidOperationException("OPERATION_ALREADY_ACTIVE");
        var job = OperationJob.Create(); PreflightSession? session = null; PreflightSnapshot? snapshot = null;
        AtomicFileCleanupException? atomic = null; GitRuntimeContext? runtime = null;
        Task<BackupRunResult> operation;
        try { operation = coordinator.RunAsync(job, async cancellation =>
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (setup is not null) await setup(job, cancellation).ConfigureAwait(false);
                var result = await check(job, cancellation).ConfigureAwait(false);
                session = result.LiveSession; snapshot = result.Snapshot;
            }
            catch (AtomicFileCleanupException ex) { atomic = ex; throw; }
            catch (GitRuntimeCleanupException ex) { runtime = ex.Context; throw; }
            return new(BackupSummary.PreflightFailure(BackupMode.Daily, "", "", "", "preflight", ""), false);
        }, async () =>
        {
            await job.CancelAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            await cleanupPreflight().ConfigureAwait(false);
            if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
            if (atomic is not null) { atomic.RetryCleanup(); atomic = null; }
            job.Dispose();
        }, token); }
        catch { job.Dispose(); throw; }
        var completed = await operation.ConfigureAwait(false);
        if (completed.HasPendingCleanup) throw new IOException("BACKUP_CLEANUP_PENDING");
        token.ThrowIfCancellationRequested();
        receive(snapshot ?? throw new InvalidOperationException("PREFLIGHT_CHECK_FAILED"));
        return completed;
    }

    private async Task<AuthResult> LoginAsync(IProgress<string> progress, CancellationToken token)
    {
        if (coordinator.IsBusy) throw new InvalidOperationException("OPERATION_ALREADY_ACTIVE");
        latestCheck = null;
        var job = OperationJob.Create(); GitRuntimeContext? context = null, failedProbe = null;
        AuthResult result = new(false, "", "AUTH_CHECK_FAILED");
        var completed = await coordinator.RunAsync(job, async cancellation =>
        {
            try
            {
                var tools = await detectTools(job, cancellation).ConfigureAwait(false);
                context = await GitRuntimeContext.CreatePublicProbeAsync(environment, cancellation).ConfigureAwait(false);
                var profile = await proxies.SelectForLoginAsync(tools, context, job, cancellation).ConfigureAwait(false);
                result = await auth.LoginAsync(tools, ProxyScope.Merge(environment, profile), job, true, progress, cancellation).ConfigureAwait(false);
            }
            catch (GitRuntimeCleanupException ex) { failedProbe = ex.Context; throw; }
            catch (AuthCleanupException ex) { failedProbe = ex.Cleanup.Context; throw; }
            return new(BackupSummary.PreflightFailure(BackupMode.Daily, "", "", "", "login", result.ErrorCode), false);
        }, async () =>
        {
            await job.CancelAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (context is not null) await context.DisposeAsync().ConfigureAwait(false);
            if (failedProbe is not null) await failedProbe.DisposeAsync().ConfigureAwait(false);
            job.Dispose();
        }, token).ConfigureAwait(false);
        if (completed.HasPendingCleanup) throw new IOException("BACKUP_CLEANUP_PENDING");
        return result;
    }
}
