using System.Text.RegularExpressions;
namespace GitHubBackup.App;
internal enum NetworkFailureKind { None, Timeout, ConnectionRefused, ConnectionReset, Http5xx, Unauthorized, Forbidden, NotFound, ProxyAuthentication, TlsCertificate, RateLimited, Unknown }
internal sealed record NetworkCheckResult(bool Success, NetworkFailureKind FailureKind, DateTimeOffset? RateLimitReset, string ErrorCode);
internal sealed class NetworkProbe(IProcessRunner runner)
{
    internal const string PublicGitProbeUrl = "https://github.com/github/gitignore.git";
    internal Task<NetworkCheckResult> CheckPublicGitAsync(ToolInventory tools, IReadOnlyDictionary<string,string?> childEnvironment, OperationJob job, CancellationToken cancellationToken) =>
        CheckRepositoryAsync(tools, new Uri(PublicGitProbeUrl), childEnvironment, job, cancellationToken);
    internal Task<NetworkCheckResult> CheckRepositoryAsync(ToolInventory tools, Uri validatedRepositoryUrl, IReadOnlyDictionary<string,string?> childEnvironment, OperationJob job, CancellationToken cancellationToken)
    {
        if (!IsRepositoryUrl(validatedRepositoryUrl)) throw new ArgumentException("GIT_PROBE_URL_INVALID");
        if (tools.Git is not { IsSupported: true } git) return Task.FromResult(new NetworkCheckResult(false, NetworkFailureKind.Unknown, null, "GIT_PROBE_TOOL_UNAVAILABLE"));
        return RetryPolicy.ExecuteAsync(async token =>
        {
            if (job.IsCancellationRequested) throw new OperationCanceledException(token);
            using var observer = new NetworkDiagnosticObserver();
            ProcessResult result = await runner.RunAsync(new(git.AbsolutePath, ["ls-remote", validatedRepositoryUrl.AbsoluteUri, "HEAD"],
                Path.GetDirectoryName(childEnvironment["GIT_CONFIG_GLOBAL"])!, childEnvironment, TimeSpan.FromSeconds(30),
                ProcessOutputMode.EphemeralText, ExpectedExecutableIdentity: git.Identity, EphemeralStandardError: true), job, observer, token).ConfigureAwait(false);
            if (result.Cancelled || job.IsCancellationRequested) throw new OperationCanceledException(token);
            if (result.TimedOut) return Failure(NetworkFailureKind.Timeout);
            return result.ExitCode == 0 ? new(true, NetworkFailureKind.None, null, "") : Failure(observer.Complete());
        }, cancellationToken);
    }
    internal static bool IsRepositoryUrl(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Host == "github.com"
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && !uri.OriginalString.Any(char.IsControl) && !uri.OriginalString.Contains('%') && !uri.OriginalString.Contains('\\')
        && Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)
        && uri.Segments[^1] is not ("." or "..");
    internal static NetworkCheckResult Failure(NetworkFailureKind kind) => new(false, kind, null, "NETWORK_" + kind.ToString().ToUpperInvariant());
    internal static NetworkFailureKind Classify(string text)
    {
        if (text.Length > 8192) return NetworkFailureKind.Unknown;
        text = text.TrimEnd('\r', '\n');
        if (text.Any(char.IsControl)) return NetworkFailureKind.Unknown;
        var http = Regex.Match(text, @"^gh: ([^\r\n]* \(HTTP (?<status>[1-5][0-9]{2})\)|HTTP (?<status>[1-5][0-9]{2}))$", RegexOptions.CultureInvariant);
        if (http.Success)
        {
            int status = int.Parse(http.Groups["status"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (status == 429 || (status == 403 && (text.StartsWith("gh: API rate limit exceeded", StringComparison.Ordinal) || text.StartsWith("gh: You have exceeded a secondary rate limit", StringComparison.Ordinal)))) return NetworkFailureKind.RateLimited;
            return HttpKind(status);
        }
        // Only known diagnostic envelopes are accepted. Arbitrary server prose is
        // never searched for transport words (it could spoof a route fallback).
        bool transport = Regex.IsMatch(text, "^(Get|Post|Head) \\\"https://(api\\.github\\.com|github\\.com)/[^\\\" ]*\\\": ", RegexOptions.CultureInvariant);
        bool git = text.StartsWith("fatal: unable to access 'https://github.com/", StringComparison.Ordinal) && text.Contains("': ", StringComparison.Ordinal);
        if (!transport && !git) return NetworkFailureKind.Unknown;
        if (text.EndsWith("connect: connection refused", StringComparison.Ordinal) || (git && text.EndsWith("Could not connect to server", StringComparison.Ordinal))) return NetworkFailureKind.ConnectionRefused;
        if (text.EndsWith("read: connection reset by peer", StringComparison.Ordinal) || (git && text.EndsWith("Recv failure: Connection was reset", StringComparison.Ordinal))) return NetworkFailureKind.ConnectionReset;
        if (text.EndsWith("context deadline exceeded", StringComparison.Ordinal) || text.EndsWith("i/o timeout", StringComparison.Ordinal) || (git && Regex.IsMatch(text, @"Operation timed out after [0-9]+ milliseconds( with [0-9]+ bytes received)?$"))) return NetworkFailureKind.Timeout;
        if (text.EndsWith("x509: certificate signed by unknown authority", StringComparison.Ordinal) || (git && text.EndsWith("SSL certificate problem: unable to get local issuer certificate", StringComparison.Ordinal))) return NetworkFailureKind.TlsCertificate;
        var statusMatch = git ? Regex.Match(text, @"': The requested URL returned error: ([1-5][0-9]{2})$") : Match.Empty;
        return statusMatch.Success ? HttpKind(int.Parse(statusMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)) : NetworkFailureKind.Unknown;
    }
    private static NetworkFailureKind HttpKind(int status) => status switch
    { 401 => NetworkFailureKind.Unauthorized, 403 => NetworkFailureKind.Forbidden, 404 => NetworkFailureKind.NotFound, 407 => NetworkFailureKind.ProxyAuthentication, 429 => NetworkFailureKind.RateLimited, >= 500 and <= 599 => NetworkFailureKind.Http5xx, _ => NetworkFailureKind.Unknown };
    internal static NetworkFailureKind ClassifyHttpStatus(int status, string? remaining) =>
        status == 403 && remaining == "0" ? NetworkFailureKind.RateLimited : HttpKind(status);
}

// Synchronous IProgress is intentional: ProcessRunner's completed drains must also
// mean classification has completed. Only this bounded ephemeral buffer holds text.
internal sealed class NetworkDiagnosticObserver : IProgress<string>, IDisposable
{
    private readonly char[] buffer = new char[8192];
    private int count;
    private bool overflow;
    public void Report(string value)
    {
        lock (buffer)
        {
            if (overflow) return;
            if (value.Length > buffer.Length - count) { overflow = true; Array.Clear(buffer); return; }
            value.CopyTo(0, buffer, count, value.Length); count += value.Length;
        }
    }
    internal NetworkFailureKind Complete()
    {
        lock (buffer)
        {
            try { return overflow ? NetworkFailureKind.Unknown : NetworkProbe.Classify(new string(buffer, 0, count)); }
            finally { Array.Clear(buffer); count = 0; }
        }
    }
    public void Dispose() { lock (buffer) { Array.Clear(buffer); count = 0; overflow = true; } }
}
