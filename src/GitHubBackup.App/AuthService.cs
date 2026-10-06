using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace GitHubBackup.App;
internal sealed record AuthResult(bool AuthReady, string Login, string ErrorCode)
{
    internal NetworkCheckResult? NetworkFailure { get; init; }
    internal bool BrowserOpenFailed { get; init; }
}
internal sealed class AuthCleanupException(AuthResult outcome, GitRuntimeCleanupException cleanup) : IOException(outcome.ErrorCode)
{
    internal AuthResult Outcome { get; } = outcome;
    internal GitRuntimeCleanupException Cleanup { get; } = cleanup;
}
internal interface ICredentialStore { void Probe(); HashSet<string> PerUserTargets(); }
internal sealed class AuthService(AppPaths paths, IProcessRunner runner, ICredentialStore store,
    Func<OperationJob,CancellationToken,Task<ToolInventory>> detectTools, Action<string>? openDeviceLoginBrowser = null)
{
    internal const string DeviceLoginUrl = "https://github.com/login/device";

    internal AuthService(AppPaths paths, IProcessRunner runner, Func<OperationJob,CancellationToken,Task<ToolInventory>> detectTools)
        : this(paths, runner, new WindowsCredentialStore(), detectTools) { }
    internal AuthConfigLease AcquireConfig()
    {
        var lease = new AuthConfigLease(paths);
        if (lease.Login.Length != 0) return lease;
        lease.Dispose(); throw new AuthBoundaryException("AUTH_APP_LOGIN_REQUIRED");
    }
    internal async Task<GitHubCredentialLease> AcquireApiCredentialAsync(string selectedOwner, AuthConfigLease config,
        IGitHubCredentialReader reader, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AuthConfigLease.IsLogin(selectedOwner)) throw new AuthBoundaryException("AUTH_LOGIN_MISMATCH");
        SettingsLoadResult saved;
        try { saved = await new SettingsStore(paths).LoadAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new AuthBoundaryException("AUTH_API_SETTINGS_UNTRUSTED"); }
        if (saved.Warnings.Count != 0 || !saved.Settings.HasApiCredentialConsentFor(selectedOwner))
            throw new AuthBoundaryException("AUTH_API_CONSENT_REQUIRED");
        config.Revalidate();
        if (!string.Equals(config.Login, selectedOwner, StringComparison.OrdinalIgnoreCase))
            throw new AuthBoundaryException("AUTH_LOGIN_MISMATCH");
        cancellationToken.ThrowIfCancellationRequested();
        GitHubCredentialLease credential = reader.ReadExact(config.Login);
        if (string.Equals(credential.Login, config.Login, StringComparison.OrdinalIgnoreCase)) return credential;
        credential.Dispose();
        throw new AuthBoundaryException("AUTH_LOGIN_MISMATCH");
    }
    internal async Task<AuthResult> CheckAsync(ToolInventory tools, IReadOnlyDictionary<string,string?> childEnvironment, OperationJob job, CancellationToken cancellationToken)
    {
        try
        {
            using var lease = AcquireConfig();
            if (tools.GitHubCli is not { IsSupported: true } gh) return Failure("AUTH_TOOL_UNAVAILABLE");
            return await CheckCore(gh, lease, childEnvironment, job, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthNetworkException ex) { return Failure(ex.Code) with { NetworkFailure = ex.Failure }; }
        catch (AuthBoundaryException ex) { return Failure(ex.Code); }
        catch (OperationCanceledException) { return Failure("AUTH_CANCELLED"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { return Failure("AUTH_CHECK_FAILED"); }
    }
    internal async Task<AuthResult> LoginAsync(ToolInventory tools, IReadOnlyDictionary<string,string?> childEnvironment, OperationJob job, bool sharedCredentialChangeConfirmed, IProgress<string>? ephemeralUiProgress, CancellationToken cancellationToken)
    {
        if (!sharedCredentialChangeConfirmed) return Failure("AUTH_SHARED_CREDENTIAL_CONFIRMATION_REQUIRED");
        if (cancellationToken.IsCancellationRequested || job.IsCancellationRequested) return Failure("AUTH_CANCELLED");
        if (tools.GitHubCli is not { IsSupported: true } gh) return Failure("AUTH_TOOL_UNAVAILABLE");
        HashSet<string>? before = null; bool launched = false, refreshed = false; AuthResult outcome;
        AuthConfigLease? lease = null; GitRuntimeCleanupException? cleanup = null;
        using var codeProgress = new DeviceCodeProgress(ephemeralUiProgress, openDeviceLoginBrowser ?? OpenDeviceLoginPage);
        try
        {
            lease = new AuthConfigLease(paths, create: true);
            store.Probe(); before = store.PerUserTargets();
            lease.SeedForConfirmedLogin();
            await using var context = await GitRuntimeContext.CreatePublicProbeAsync(childEnvironment, cancellationToken).ConfigureAwait(false);
            var environment = lease.CreateEnvironment(context.Environment, bindIdentity: false);
            string working = Path.GetDirectoryName(environment["GIT_CONFIG_GLOBAL"])!;
            ProcessResult result;
            try
            {
                launched = true;
                result = await runner.RunAsync(new(gh.AbsolutePath, ["auth", "login", "--hostname", "github.com", "--git-protocol", "https", "--web"],
                    working, environment, TimeSpan.FromMinutes(10), ProcessOutputMode.EphemeralText, ExpectedExecutableIdentity: gh.Identity), job, codeProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PostValidate(lease); lease.RemoveHosts(); throw;
            }
            catch
            {
                PostValidate(lease); throw;
            }
            PostValidate(lease);
            if (result.Cancelled || result.TimedOut) lease.RemoveHosts();
            refreshed = true;
            ToolInventory fresh = await ToolInventoryRefresh.AfterActionAsync(detectTools, job, cancellationToken).ConfigureAwait(false);
            if (result.Cancelled || cancellationToken.IsCancellationRequested || job.IsCancellationRequested)
            { lease.RemoveHosts(); outcome = Failure("AUTH_CANCELLED_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED"); }
            else if (result.TimedOut) outcome = Failure("AUTH_LOGIN_TIMEOUT_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED");
            else if (result.ExitCode != 0) outcome = Failure("AUTH_LOGIN_FAILED_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED");
            else if (lease.Login.Length == 0) outcome = Failure("AUTH_APP_LOGIN_REQUIRED");
            else if (fresh.GitHubCli is not { IsSupported: true } detectedGh) outcome = Failure("AUTH_TOOL_REDETECTION_FAILED");
            else outcome = await CheckCore(detectedGh, lease, childEnvironment, job, cancellationToken).ConfigureAwait(false);
        }
        catch (GitRuntimeCleanupException ex) { cleanup = ex; outcome = Failure("AUTH_TOOL_REDETECTION_FAILED"); }
        catch (AuthNetworkException ex) { outcome = Failure(ex.Code) with { NetworkFailure = ex.Failure }; }
        catch (AuthBoundaryException ex) { outcome = Failure(ex.Code); }
        catch (OperationCanceledException)
        {
            outcome = Failure(launched ? "AUTH_CANCELLED_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED" : "AUTH_CANCELLED");
            if (launched && lease is not null)
            {
                try { PostValidate(lease); lease.RemoveHosts(); }
                catch (AuthBoundaryException ex) { outcome = Failure(ex.Code); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { outcome = Failure("AUTH_LOGIN_FAILED_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED"); }
        finally { lease?.Dispose(); }
        if (before is not null)
        {
            try { if (!before.IsSubsetOf(store.PerUserTargets())) outcome = Failure("AUTH_KEYRING_TARGET_MISSING"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { outcome = Failure("AUTH_KEYRING_ENUMERATION_FAILED"); }
        }
        if (launched && !refreshed && cleanup is null)
        {
            try
            {
                ToolInventory fresh = await ToolInventoryRefresh.AfterActionAsync(detectTools, job, cancellationToken).ConfigureAwait(false);
                if (fresh.GitHubCli is not { IsSupported: true } && outcome.AuthReady) outcome = Failure("AUTH_TOOL_REDETECTION_FAILED");
            }
            catch (GitRuntimeCleanupException ex) { cleanup = ex; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception) { outcome = Failure("AUTH_TOOL_REDETECTION_FAILED"); }
        }
        if (cleanup is not null) throw new AuthCleanupException(outcome with { AuthReady = false, Login = "", BrowserOpenFailed = codeProgress.BrowserOpenFailed }, cleanup);
        if (outcome.AuthReady) outcome = outcome with { ErrorCode = "AUTH_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED" };
        return codeProgress.BrowserOpenFailed ? outcome with { BrowserOpenFailed = true } : outcome;
    }

    private static void OpenDeviceLoginPage(string url)
    {
        if (!string.Equals(url, DeviceLoginUrl, StringComparison.Ordinal)) throw new InvalidOperationException("AUTH_BROWSER_URL_REJECTED");
        using Process? browser = Process.Start(new ProcessStartInfo(DeviceLoginUrl) { UseShellExecute = true });
        if (browser is null) throw new InvalidOperationException("AUTH_BROWSER_OPEN_FAILED");
    }

    private sealed class DeviceCodeProgress(IProgress<string>? uiProgress, Action<string> openBrowser) : IProgress<string>, IDisposable
    {
        private const string Prompt = "! First copy your one-time code: ";
        private readonly StringBuilder line = new();
        private bool discardLine;
        private int openAttempted;
        private int openFailed;
        internal bool BrowserOpenFailed => Volatile.Read(ref openFailed) != 0;

        public void Report(string value)
        {
            uiProgress?.Report(value);
            foreach (char character in value)
            {
                if (character is '\r' or '\n')
                {
                    if (!discardLine && IsDeviceCodePrompt(line.ToString())) OpenOnce();
                    line.Clear(); discardLine = false;
                }
                else if (!discardLine)
                {
                    if (line.Length < 100) line.Append(character);
                    else { line.Clear(); discardLine = true; }
                }
            }
        }

        private void OpenOnce()
        {
            if (Interlocked.Exchange(ref openAttempted, 1) != 0) return;
            try { openBrowser(DeviceLoginUrl); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException
                or System.Security.SecurityException)
            { Interlocked.Exchange(ref openFailed, 1); }
        }

        private static bool IsDeviceCodePrompt(ReadOnlySpan<char> line)
        {
            line = line.Trim();
            if (!line.StartsWith(Prompt, StringComparison.Ordinal) || line.Length != Prompt.Length + 9) return false;
            ReadOnlySpan<char> code = line[Prompt.Length..];
            for (int index = 0; index < code.Length; index++)
            {
                char current = code[index];
                bool valid = index == 4 ? current == '-' : current is >= 'A' and <= 'Z' or >= '0' and <= '9';
                if (!valid) return false;
            }
            return true;
        }

        public void Dispose() => line.Clear();
    }

    private async Task<AuthResult> CheckCore(ToolDetection gh, AuthConfigLease lease, IReadOnlyDictionary<string,string?> child, OperationJob job, CancellationToken token)
    {
        byte[] status = await Capture(gh, ["auth", "status", "--active", "--hostname", "github.com", "--json", "hosts"], "AUTH_STATUS", lease, child, job, token).ConfigureAwait(false);
        string login;
        try { login = ParseStatus(status, lease); }
        finally { CryptographicOperations.ZeroMemory(status); }
        byte[] user = await Capture(gh, ["api", "user", "--jq", ".login"], "AUTH_API", lease, child, job, token).ConfigureAwait(false);
        try
        {
            if (lease.Login.Length == 0) return Failure("AUTH_APP_LOGIN_REQUIRED");
            string apiLogin = Encoding.UTF8.GetString(user).TrimEnd('\r', '\n');
            if (!AuthConfigLease.IsLogin(apiLogin) || !string.Equals(login, apiLogin, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(login, lease.Login, StringComparison.OrdinalIgnoreCase)) return Failure("AUTH_LOGIN_MISMATCH");
        }
        finally { CryptographicOperations.ZeroMemory(user); }
        return new(true, login, "");
    }
    private async Task<byte[]> Capture(ToolDetection gh, string[] args, string phase, AuthConfigLease lease, IReadOnlyDictionary<string,string?> child, OperationJob job, CancellationToken token)
    {
        if (token.IsCancellationRequested || job.IsCancellationRequested) throw new OperationCanceledException(token);
        await using var context = await GitRuntimeContext.CreatePublicProbeAsync(child, token).ConfigureAwait(false);
        var environment = lease.CreateEnvironment(context.Environment);
        string working = Path.GetDirectoryName(environment["GIT_CONFIG_GLOBAL"])!;
        string output = Path.Combine(working, "query-" + Guid.NewGuid().ToString("N") + ".tmp");
        ProcessResult result;
        using var observer = new NetworkDiagnosticObserver();
        try { result = await runner.RunAsync(new(gh.AbsolutePath, args, working, environment, TimeSpan.FromSeconds(30), ProcessOutputMode.CapturedFile, output, 1024 * 1024, gh.Identity, EphemeralStandardError: true), job, observer, token).ConfigureAwait(false); }
        catch { PostValidate(lease); throw; }
        PostValidate(lease);
        if (result.Cancelled || token.IsCancellationRequested || job.IsCancellationRequested) throw new OperationCanceledException(token);
        if (result.TimedOut) throw new AuthNetworkException(phase + "_TIMEOUT", NetworkProbe.Failure(NetworkFailureKind.Timeout));
        if (result.ExitCode != 0) throw new AuthNetworkException(phase + "_FAILED", NetworkProbe.Failure(observer.Complete()));
        byte[] bytes;
        NativeFileIdentity identity;
        using (var handle = NativeFileSystem.Open(output))
        {
            identity = NativeFileSystem.Inspect(handle, output, false); AclPolicy.VerifyRestricted(handle, WindowsIdentity.GetCurrent().User!);
            bytes = AuthConfigLease.ReadLimited(handle);
        }
        try { AtomicFile.DeleteOwnedFile(output, identity); return bytes; }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }
    private static string ParseStatus(byte[] bytes, AuthConfigLease lease)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 8 });
            JsonElement hosts = document.RootElement.GetProperty("hosts");
            if (hosts.EnumerateObject().Count() != 1) throw new AuthBoundaryException("AUTH_STATUS_INVALID");
            JsonElement entries = hosts.GetProperty("github.com");
            if (entries.GetArrayLength() != 1) throw new AuthBoundaryException("AUTH_STATUS_NOT_READY");
            JsonElement entry = entries[0];
            if (entry.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new AuthBoundaryException("AUTH_STATUS_INVALID");
            if (entry.GetProperty("tokenSource").GetString() != "keyring") { lease.RemoveHosts(); throw new AuthBoundaryException("AUTH_PLAINTEXT_STORAGE_REJECTED"); }
            string login = entry.GetProperty("login").GetString() ?? "";
            if (!entry.GetProperty("active").GetBoolean() || entry.GetProperty("host").GetString() != "github.com") throw new AuthBoundaryException("AUTH_STATUS_NOT_READY");
            if (!AuthConfigLease.IsLogin(login) || !string.Equals(login, lease.Login, StringComparison.OrdinalIgnoreCase)) throw new AuthBoundaryException("AUTH_LOGIN_MISMATCH");
            string? state = entry.GetProperty("state").GetString();
            if (state != "success")
            {
                NetworkFailureKind kind = NetworkFailureKind.Unknown;
                if (state == "timeout") kind = NetworkFailureKind.Timeout;
                else if (state == "error" && entry.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    kind = NetworkProbe.Classify(error.GetString() ?? "");
                throw new AuthNetworkException("AUTH_STATUS_NOT_READY", NetworkProbe.Failure(kind));
            }
            return login;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { throw new AuthBoundaryException("AUTH_STATUS_INVALID"); }
    }
    private static void PostValidate(AuthConfigLease lease)
    {
        try { lease.Revalidate(); }
        catch (AuthBoundaryException ex) when (ex.Code == "AUTH_PLAINTEXT_RESIDUE_REMOVED") { throw new AuthBoundaryException("AUTH_PLAINTEXT_STORAGE_REJECTED"); }
    }
    private static AuthResult Failure(string code) => new(false, "", code);
    private sealed class AuthNetworkException(string code, NetworkCheckResult failure) : IOException(code)
    {
        internal string Code { get; } = code;
        internal NetworkCheckResult Failure { get; } = failure;
    }
}
