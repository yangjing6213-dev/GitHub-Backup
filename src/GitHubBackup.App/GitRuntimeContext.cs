using System.Text;
using System.Security.Principal;
using System.Collections.ObjectModel;

namespace GitHubBackup.App;

internal static class ChildEnvironmentBuilder
{
    private static readonly HashSet<string> Frozen = new(StringComparer.OrdinalIgnoreCase) { "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "CommonProgramFiles(x86)" };
    private static readonly HashSet<string> Generated = new(StringComparer.OrdinalIgnoreCase) { "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_TERMINAL_PROMPT", "GCM_INTERACTIVE", "GIT_CONFIG_COUNT", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "GH_CONFIG_DIR", "LC_ALL" };
    internal static IReadOnlySet<string> AllowedKeys { get; } = new HashSet<string>(Frozen.Concat(Generated).Concat(["PATH", "GH_TELEMETRY", "GH_NO_UPDATE_NOTIFIER"]), StringComparer.OrdinalIgnoreCase);
    internal static IReadOnlyDictionary<string,string?> CreateCurrentBase(IReadOnlyList<string> validatedToolDirectories) =>
        CreateBase(Frozen.ToDictionary(key => key, Environment.GetEnvironmentVariable, StringComparer.OrdinalIgnoreCase), validatedToolDirectories);
    internal static IReadOnlyDictionary<string, string?> CreateBase(IReadOnlyDictionary<string, string?> parent, IReadOnlyList<string> validatedToolDirectories)
    {
        var normalized = new Dictionary<string,string?>(parent, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in Frozen) if (normalized.TryGetValue(key, out var value)) result[key] = value;
        if (!result.TryGetValue("SystemRoot", out string? systemRoot) || string.IsNullOrEmpty(systemRoot)) throw new ArgumentException("CHILD_ENV_SYSTEMROOT_REQUIRED");
        result["PATH"] = string.Join(Path.PathSeparator, validatedToolDirectories.Select(NativeFileSystem.CanonicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Append(Path.Combine(NativeFileSystem.CanonicalPath(systemRoot), "System32")));
        // Generated invariants, never inherited or replaceable via additions.
        result["GH_TELEMETRY"] = "false";
        result["GH_NO_UPDATE_NOTIFIER"] = "1";
        return result;
    }
    internal static IReadOnlyDictionary<string, string?> Build(IReadOnlyDictionary<string, string?> baseEnvironment, IReadOnlyDictionary<string, string?> additions)
    {
        ValidateKeys(baseEnvironment);
        var result = new Dictionary<string,string?>(baseEnvironment, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in additions)
        {
            bool indexed = IsIndexed(pair.Key, "GIT_CONFIG_KEY_") || IsIndexed(pair.Key, "GIT_CONFIG_VALUE_");
            if (!Generated.Contains(pair.Key) && !indexed) throw new ArgumentException("CHILD_ENV_KEY_NOT_ALLOWED");
            if (pair.Value?.Any(char.IsControl) == true) throw new ArgumentException("CHILD_ENV_VALUE_INVALID");
            string key = pair.Key.ToUpperInvariant(); result.Remove(key); result[key] = pair.Value;
        }
        if (additions.Keys.Any(k => k.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase)))
        {
            if (!additions.TryGetValue("GIT_CONFIG_COUNT", out var text) || !int.TryParse(text, out int count) || count < 0 || count > 128)
                throw new ArgumentException("CHILD_ENV_CONFIG_INVALID");
            for (int i = 0; i < count; i++) if (!additions.ContainsKey($"GIT_CONFIG_KEY_{i}") || !additions.ContainsKey($"GIT_CONFIG_VALUE_{i}")) throw new ArgumentException("CHILD_ENV_CONFIG_INVALID");
            if (additions.Keys.Count(k => IsIndexed(k, "GIT_CONFIG_KEY_") || IsIndexed(k, "GIT_CONFIG_VALUE_")) != count * 2) throw new ArgumentException("CHILD_ENV_CONFIG_INVALID");
        }
        return baseEnvironment is RuntimeEnvironment runtime ? new RuntimeEnvironment(result, runtime.Owner, runtime.Authentication, runtime.AuthenticatedLogin,runtime.Staging,runtime.Recovery) : result;
    }
    internal static RuntimeEnvironment PinStaging(IReadOnlyDictionary<string,string?> environment,StagingRepository staging,RecoveryLease? recovery=null)
    {
        if(environment is not RuntimeEnvironment runtime||runtime.Owner is null)throw new ArgumentException("STAGING_RUNTIME_REQUIRED");
        ValidateKeys(environment);staging.Revalidate();
        var result=new Dictionary<string,string?>(environment,StringComparer.OrdinalIgnoreCase){["GIT_DIR"]=staging.Root,["GIT_COMMON_DIR"]=staging.Root,["GIT_IMPLICIT_WORK_TREE"]="0"};
        return new(result,runtime.Owner,runtime.Authentication,runtime.AuthenticatedLogin,staging,recovery);
    }
    internal static void ValidateKeys(IReadOnlyDictionary<string,string?> environment)
    {
        bool pinned=environment is RuntimeEnvironment {Staging:not null};
        if(pinned)((RuntimeEnvironment)environment).Staging!.ValidateEnvironment(environment);
        if (environment.Keys.Any(key => !(pinned && (key=="GIT_DIR"||key=="GIT_COMMON_DIR"||key=="GIT_IMPLICIT_WORK_TREE")) && !AllowedKeys.Contains(key) && !IsIndexed(key, "GIT_CONFIG_KEY_") && !IsIndexed(key, "GIT_CONFIG_VALUE_")))
            throw new ArgumentException("CHILD_ENV_KEY_NOT_ALLOWED");
        if (!environment.TryGetValue("GH_TELEMETRY", out string? telemetry) || telemetry != "false"
            || !environment.TryGetValue("GH_NO_UPDATE_NOTIFIER", out string? notifier) || notifier != "1")
            throw new ArgumentException("CHILD_ENV_GH_SAFETY_REQUIRED");
    }
    private static bool IsIndexed(string key, string prefix) => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(key[prefix.Length..], out int index) && index is >= 0 and < 128 && key[prefix.Length..] == index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class GitRuntimeContext : IAsyncDisposable
{
    private readonly string directory;
    private readonly string config;
    private readonly PathLease directoryLease;
    private readonly OperationJob? operation;
    private readonly object gate = new();
    private int activeRequests;
    private readonly HashSet<RecoveryLease> recoveries=[];
    private ToolDetection? validatedGit,validatedGh;
    private ToolInventory? sessionTools;
    private bool disposed;
    private readonly NativeFileIdentity directoryIdentity;
    private readonly NativeFileIdentity hooksIdentity;
    internal IReadOnlyDictionary<string, string?> Environment { get; }
    internal string EmptyHooksDirectory { get; }
    // Scheduling seam only; creation, identity capture and rollback use real files.
    private GitRuntimeContext(IReadOnlyDictionary<string,string?> baseEnvironment, OperationJob? operation, Action<string>? resourceCreated = null)
    {
        this.operation = operation;
        string temp = baseEnvironment.TryGetValue("TEMP", out var value) && value is not null ? value : Path.GetTempPath();
        directory = Path.Combine(temp, "GitHubBackup-git-" + Guid.NewGuid().ToString("N"));
        config = Path.Combine(directory, "global.gitconfig");
        EmptyHooksDirectory = Path.Combine(directory, "hooks");
        var clean = baseEnvironment.Where(x => !x.Key.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        // All pure environment validation precedes filesystem acquisition.
        var inherited = baseEnvironment as RuntimeEnvironment;
        inherited?.RevalidateAuthentication();
        Environment = new RuntimeEnvironment(ChildEnvironmentBuilder.Build(clean, new Dictionary<string,string?> { ["GIT_CONFIG_GLOBAL"] = config, ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never" }), this, inherited?.Authentication, inherited?.AuthenticatedLogin);
        using PathLease parents = NativeFileSystem.PinDirectories(temp);
        var created = new List<(string Path, NativeFileIdentity Identity, bool Directory)>();
        PathLease? acquiredLease = null;
        var user = WindowsIdentity.GetCurrent().User!;
        try
        {
            AclPolicy.CreateRestrictedDirectory(directory, user, requireNew: true);
            using (var handle = NativeFileSystem.Open(directory)) directoryIdentity = NativeFileSystem.Inspect(handle, directory, true);
            created.Add((directory, directoryIdentity, true));
            directoryLease = acquiredLease = SummaryStore.RequirePrivateDirectory(directory);
            resourceCreated?.Invoke(directory);
            using (var stream = AclPolicy.CreateRestrictedFile(config, user))
                created.Add((config, NativeFileSystem.Inspect(stream.SafeFileHandle, config, false), false));
            resourceCreated?.Invoke(config);
            AclPolicy.CreateRestrictedDirectory(EmptyHooksDirectory, user, requireNew: true);
            using (var handle = NativeFileSystem.Open(EmptyHooksDirectory)) hooksIdentity = NativeFileSystem.Inspect(handle, EmptyHooksDirectory, true);
            created.Add((EmptyHooksDirectory, hooksIdentity, true));
            resourceCreated?.Invoke(EmptyHooksDirectory);
        }
        catch (Exception initializationFailure)
        {
            var rollbackFailures = new List<Exception>();
            try
            {
                for (int index = created.Count - 1; index >= 0; index--)
                {
                    // Pin the runtime directory until its children are removed;
                    // then release its deny-delete lease before deleting that root.
                    if (index == 0) acquiredLease?.Dispose();
                    var entry = created[index];
                    try
                    {
                        using var handle = NativeFileSystem.Open(entry.Path, NativeFileSystem.ReadControl | NativeFileSystem.ReadAttributes | NativeFileSystem.DeleteAccess | 1);
                        if (NativeFileSystem.Inspect(handle, entry.Path, entry.Directory) != entry.Identity) throw new PathBoundaryException("IDENTITY_CHANGED");
                        AclPolicy.VerifyRestricted(handle, user);
                        NativeFileSystem.DeleteByHandle(handle);
                    }
                    catch (Exception rollbackFailure) { rollbackFailures.Add(rollbackFailure); }
                }
            }
            finally { acquiredLease?.Dispose(); }
            if (rollbackFailures.Count != 0)
                throw new AggregateException("GIT_RUNTIME_INITIALIZATION_ROLLBACK_FAILED", new[] { initializationFailure }.Concat(rollbackFailures));
            throw;
        }
    }
    internal static Task<GitRuntimeContext> CreatePublicProbeAsync(IReadOnlyDictionary<string,string?> baseEnvironment, CancellationToken cancellationToken)
        => CreatePublicProbeAsync(baseEnvironment, cancellationToken, null);
    internal static Task<GitRuntimeContext> CreatePublicProbeAsync(IReadOnlyDictionary<string,string?> baseEnvironment, CancellationToken cancellationToken, Action<string>? resourceCreated)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(new GitRuntimeContext(baseEnvironment, null, resourceCreated)); }

    internal static Task<GitRuntimeContext> CreateAsync(ToolDetection git, ToolDetection gh, IProcessRunner runner, OperationJob job, IReadOnlyDictionary<string,string?> baseEnvironment, CancellationToken cancellationToken)
        => CreateAsync(git, gh, runner, job, baseEnvironment, cancellationToken, null);
    internal static async Task<GitRuntimeContext> CreateAsync(ToolDetection git, ToolDetection gh, IProcessRunner runner, OperationJob job, IReadOnlyDictionary<string,string?> baseEnvironment, CancellationToken cancellationToken, Action<string>? resourceCreated)
    {
        if (!git.IsSupported || !gh.IsSupported) throw new InvalidOperationException("GIT_RUNTIME_UNSUPPORTED_TOOL");
        var context = new GitRuntimeContext(WithGitDirectory(baseEnvironment, git), job, resourceCreated);
        try
        {
            // gh resolves its nested Git command through PATH. Pin the selected
            // executable and its trusted directory until that entire request exits.
            using (ExecutableTrust.Acquire(git.AbsolutePath, git.Identity))
                await context.RunAsync(gh, ["auth", "setup-git", "--hostname", "github.com"], runner, job, cancellationToken).ConfigureAwait(false);
            string contents = await context.QueryAsync(git, runner, job, cancellationToken).ConfigureAwait(false);
            ValidateHelpers(contents, gh.AbsolutePath);
            string helper = Helper(gh.AbsolutePath);
            string canonical = "[credential \"https://github.com\"]\n\thelper =\n\thelper = " + ConfigQuote(helper) + "\n";
            // Keep parent capability through replacement, but do not deny-delete the
            // existing destination while AtomicFile performs Replace.
            await AtomicFile.WriteAsync(context.config, async (stream, token) => await stream.WriteAsync(Encoding.UTF8.GetBytes(canonical), token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            string rewritten = await context.QueryAsync(git, runner, job, cancellationToken).ConfigureAwait(false);
            ValidateHelpers(rewritten, gh.AbsolutePath, allowGist: false);
            context.validatedGit=git;context.validatedGh=gh;
            return context;
        }
        catch
        {
            try
            {
                await job.CancelAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await context.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { throw new GitRuntimeCleanupException(context); }
            throw;
        }
    }
    private static IReadOnlyDictionary<string,string?> WithGitDirectory(IReadOnlyDictionary<string,string?> environment, ToolDetection git)
    {
        string directory = Path.GetDirectoryName(NativeFileSystem.CanonicalPath(git.AbsolutePath))!;
        if (!string.Equals(Path.GetFileName(git.AbsolutePath), "git.exe", StringComparison.OrdinalIgnoreCase)
            || directory.Contains(Path.PathSeparator) || directory.Any(char.IsControl))
            throw new InvalidDataException("GIT_RUNTIME_GIT_PATH_INVALID");
        var values = new Dictionary<string,string?>(environment, StringComparer.OrdinalIgnoreCase)
        {
            // Rebuild from the selected tool, never the process/user PATH.
            ["PATH"] = ChildEnvironmentBuilder.CreateBase(environment, [directory])["PATH"]
        };
        return environment is RuntimeEnvironment runtime
            ? new RuntimeEnvironment(values, runtime.Owner, runtime.Authentication, runtime.AuthenticatedLogin, runtime.Staging, runtime.Recovery)
            : values;
    }
    private async Task RunAsync(ToolDetection tool, string[] arguments, IProcessRunner runner, OperationJob job, CancellationToken token)
    {
        ProcessResult result = await runner.RunAsync(new(tool.AbsolutePath, arguments, directory, Environment, TimeSpan.FromSeconds(30), ExpectedExecutableIdentity: tool.Identity), job, null, token).ConfigureAwait(false);
        if (result.Cancelled) throw new OperationCanceledException(token);
        if (ProcessOutcomeClassifier.Classify(result) != ProcessTerminalKind.Succeeded) throw new IOException("GIT_RUNTIME_COMMAND_FAILED");
    }
    private async Task<string> QueryAsync(ToolDetection git, IProcessRunner runner, OperationJob job, CancellationToken token)
    {
        // gh may have atomically replaced its config. Reacquire the actual private
        // file without following links, and keep that identity pinned through Git's read.
        using var configLease = NativeFileSystem.Open(config);
        NativeFileSystem.Inspect(configLease, config, false);
        if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(configLease), WindowsIdentity.GetCurrent().User!, true) != AclRisk.Safe)
            throw new UnauthorizedAccessException("GIT_RUNTIME_ACL_UNSAFE");
        string output = Path.Combine(directory, "query-" + Guid.NewGuid().ToString("N") + ".tmp");
        ProcessResult result = await runner.RunAsync(new(git.AbsolutePath, ["config", "--file", config, "--null", "--list", "--no-includes"], directory, Environment, TimeSpan.FromSeconds(30), ProcessOutputMode.CapturedFile, output, 65536, git.Identity), job, null, token).ConfigureAwait(false);
        if (result.Cancelled) throw new OperationCanceledException(token);
        if (ProcessOutcomeClassifier.Classify(result) != ProcessTerminalKind.Succeeded) throw new IOException("GIT_RUNTIME_CONFIG_QUERY_FAILED");
        using var handle = NativeFileSystem.Open(output);
        var identity = NativeFileSystem.Inspect(handle, output, false);
        AclPolicy.VerifyRestricted(handle, WindowsIdentity.GetCurrent().User!);
        string data;
        using (var input = new StreamReader(new FileStream(handle, FileAccess.Read), new UTF8Encoding(false, true))) data = await input.ReadToEndAsync(token).ConfigureAwait(false);
        AtomicFile.DeleteOwnedFile(output, identity);
        return data;
    }
    internal static string Helper(string path)
    {
        if (path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("GIT_HELPER_INVALID");
        bool bare = path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '.' or ':');
        return "!" + (bare ? path : "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'") + " auth git-credential";
    }
    internal static void ValidateHelpers(string output, string ghPath, bool allowGist = true)
    {
        var entries = new Dictionary<string,List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!output.EndsWith('\0')) throw new InvalidDataException("GIT_HELPER_INVALID");
        foreach (string row in output[..^1].Split('\0'))
        {
            int separator = row.IndexOf('\n');
            if (separator < 0) throw new InvalidDataException("GIT_HELPER_INVALID");
            string key = row[..separator]; string value = row[(separator + 1)..];
            if (key != "credential.https://github.com.helper" && !(allowGist && key == "credential.https://gist.github.com.helper")) throw new InvalidDataException("GIT_HELPER_INVALID");
            if (!entries.TryGetValue(key, out var values)) entries[key] = values = [];
            values.Add(value);
        }
        if (!entries.ContainsKey("credential.https://github.com.helper")) throw new InvalidDataException("GIT_HELPER_INVALID");
        foreach (var values in entries.Values)
            if (values.Count != 2 || values[0] != "" || !string.Equals(DecodeHelper(values[1]), ghPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GIT_HELPER_INVALID");
    }
    private static string DecodeHelper(string helper)
    {
        const string suffix = " auth git-credential";
        if (!helper.StartsWith('!') || helper.Any(char.IsControl)) throw new InvalidDataException("GIT_HELPER_INVALID");
        int index = 1; var decoded = new StringBuilder();
        if (index < helper.Length && helper[index] == '\'')
        {
            index++;
            while (true)
            {
                int close = helper.IndexOf('\'', index);
                if (close < 0) throw new InvalidDataException("GIT_HELPER_INVALID");
                decoded.Append(helper.AsSpan(index, close - index)); index = close + 1;
                if (helper.AsSpan(index).StartsWith("\\''", StringComparison.Ordinal)) { decoded.Append('\''); index += 3; continue; }
                break;
            }
        }
        else
        {
            while (index < helper.Length && helper[index] != ' ')
            {
                char c = helper[index++];
                if (!(char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '.' or ':')) throw new InvalidDataException("GIT_HELPER_INVALID");
                decoded.Append(c);
            }
        }
        if (decoded.Length == 0 || helper[index..] != suffix || !Path.IsPathFullyQualified(decoded.ToString())) throw new InvalidDataException("GIT_HELPER_INVALID");
        return decoded.ToString();
    }
    private static string ConfigQuote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return ValueTask.CompletedTask;
            if (activeRequests != 0 || recoveries.Count!=0 || (operation is not null && (!operation.IsCancellationRequested || operation.ActiveLeaseCount != 0))) throw new InvalidOperationException("GIT_RUNTIME_OPERATION_STILL_ACTIVE");
            DeleteOwned(); disposed = true; return ValueTask.CompletedTask;
        }
    }
    internal ValueTask DisposeCandidateAsync(OperationJob job)
    {
        // A rejected route is not a session. Close it without cancelling the job
        // needed by the next route, using the request path's runtime -> job order.
        lock (gate)
        {
            if (operation is null || !ReferenceEquals(operation,job))
                throw new InvalidOperationException("GIT_RUNTIME_OPERATION_MISMATCH");
            lock (job.Gate)
            {
                if (disposed) return ValueTask.CompletedTask;
                if (sessionTools is not null || activeRequests != 0 || recoveries.Count != 0 || job.ActiveLeaseCount != 0)
                    throw new InvalidOperationException("GIT_RUNTIME_OPERATION_STILL_ACTIVE");
                DeleteOwned(); disposed = true; return ValueTask.CompletedTask;
            }
        }
    }
    internal void RegisterRecovery(RecoveryLease recovery,OperationJob normal)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(!ReferenceEquals(operation,normal)||normal.IsCancellationRequested)throw new InvalidOperationException("RECOVERY_SESSION_MISMATCH");
            recoveries.Add(recovery);
        }
    }
    internal void FreezeSessionInventory(ToolInventory tools)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(sessionTools is not null||tools.Git!=validatedGit||tools.GitHubCli!=validatedGh)
                throw new InvalidOperationException("RECOVERY_SESSION_MISMATCH");
            sessionTools=tools;
        }
    }
    internal void RequireSessionBinding(ToolInventory tools,string? emptyHooksDirectory,IReadOnlyDictionary<string,string?> environment,OperationJob normal)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(!ReferenceEquals(normal,operation)||normal.IsCancellationRequested||validatedGit is null||validatedGh is null
                ||sessionTools is null||tools!=sessionTools||emptyHooksDirectory!=EmptyHooksDirectory||environment.Count!=Environment.Count
                ||Environment.Any(pair=>!environment.TryGetValue(pair.Key,out var value)||value!=pair.Value))
                throw new InvalidOperationException("RECOVERY_SESSION_MISMATCH");
        }
    }
    internal void ReleaseRecovery(RecoveryLease recovery){lock(gate)recoveries.Remove(recovery);}
    internal IDisposable AcquireRequest(OperationJob job,ProcessRequest? request=null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (operation is not null && !ReferenceEquals(job, operation))
            {
                RecoveryLease? recovery=request?.Environment is RuntimeEnvironment e?e.Recovery:null;
                if(recovery is null||!recoveries.Contains(recovery)||request is null)throw new InvalidOperationException("GIT_RUNTIME_OPERATION_MISMATCH");
                recovery.ValidateRequest(request,job);
            }
            activeRequests++; return new RequestUse(this);
        }
    }
    private sealed class RequestUse(GitRuntimeContext owner) : IDisposable
    {
        public void Dispose() { lock (owner.gate) owner.activeRequests--; }
    }
    private void DeleteOwned()
    {
        using PathLease parents = NativeFileSystem.PinDirectories(Path.GetDirectoryName(directory)!);
        directoryLease.Dispose();
        using var root = NativeFileSystem.Open(directory, NativeFileSystem.ReadControl | NativeFileSystem.ReadAttributes | NativeFileSystem.DeleteAccess | 1);
        if (NativeFileSystem.Inspect(root, directory, true) != directoryIdentity) throw new PathBoundaryException("IDENTITY_CHANGED");
        AclPolicy.VerifyRestricted(root, WindowsIdentity.GetCurrent().User!);
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(file);
            if (file != config && !(name.StartsWith("query-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal))) throw new IOException("GIT_RUNTIME_UNEXPECTED_FILE");
            using var handle = NativeFileSystem.Open(file, NativeFileSystem.ReadControl | NativeFileSystem.ReadAttributes | NativeFileSystem.DeleteAccess | 1);
            NativeFileSystem.Inspect(handle, file, false);
            if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(handle), WindowsIdentity.GetCurrent().User!, true) != AclRisk.Safe) throw new UnauthorizedAccessException("GIT_RUNTIME_ACL_UNSAFE");
            NativeFileSystem.DeleteByHandle(handle);
        }
        using (var hooks = NativeFileSystem.Open(EmptyHooksDirectory, NativeFileSystem.ReadControl | NativeFileSystem.ReadAttributes | NativeFileSystem.DeleteAccess | 1))
        {
            if (NativeFileSystem.Inspect(hooks, EmptyHooksDirectory, true) != hooksIdentity) throw new PathBoundaryException("IDENTITY_CHANGED");
            AclPolicy.VerifyRestricted(hooks, WindowsIdentity.GetCurrent().User!); NativeFileSystem.DeleteByHandle(hooks);
        }
        NativeFileSystem.DeleteByHandle(root);
    }
}

internal sealed class GitRuntimeCleanupException(GitRuntimeContext context) : IOException("GIT_RUNTIME_CLEANUP_FAILED")
{
    internal GitRuntimeContext Context { get; } = context;
}

// Carries the context's lifetime across vetted per-request environment additions.
internal sealed class RuntimeEnvironment(IReadOnlyDictionary<string,string?> values, GitRuntimeContext? owner, AuthConfigLease? authentication = null, string? authenticatedLogin = null,StagingRepository? staging=null,RecoveryLease? recovery=null)
    : ReadOnlyDictionary<string,string?>(new Dictionary<string,string?>(values, StringComparer.OrdinalIgnoreCase))
{
    internal GitRuntimeContext? Owner { get; } = owner;
    internal AuthConfigLease? Authentication { get; } = authentication;
    internal string? AuthenticatedLogin { get; } = authenticatedLogin;
    internal StagingRepository? Staging {get;}=staging;
    internal RecoveryLease? Recovery {get;}=recovery;
    internal void RevalidateAuthentication()
    {
        if (Authentication is null) return;
        Authentication.Revalidate();
        if (string.IsNullOrEmpty(AuthenticatedLogin) || !string.Equals(Authentication.Login,AuthenticatedLogin,StringComparison.OrdinalIgnoreCase))
            throw new AuthBoundaryException("AUTH_LOGIN_MISMATCH");
    }
}
