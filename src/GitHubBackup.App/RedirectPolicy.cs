namespace GitHubBackup.App;

internal sealed class HttpTransferException(string code, NetworkFailureKind failureKind = NetworkFailureKind.Unknown,
    DateTimeOffset? rateLimitReset = null) : IOException(code)
{
    internal string Code { get; } = code;
    internal NetworkFailureKind FailureKind { get; } = failureKind;
    internal DateTimeOffset? RateLimitReset { get; } = rateLimitReset;
}

internal static class RedirectPolicy
{
    internal static Uri ValidateNext(Uri current, Uri location, string expectedResourceKey, bool apiOrigin)
    {
        string raw = location.OriginalString;
        if (!location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps || !location.IsDefaultPort
            || location.UserInfo.Length != 0 || location.Fragment.Length != 0
            || location.HostNameType != UriHostNameType.Dns || location.Host.EndsWith('.')
            || raw.Any(char.IsControl) || raw.Contains('\\'))
            throw new HttpTransferException("HTTP_REDIRECT_REJECTED");
        int authorityStart = raw.IndexOf("://", StringComparison.Ordinal) + 3;
        int pathStart = raw.IndexOf('/', authorityStart);
        if (authorityStart < 3 || raw[..(pathStart < 0 ? raw.Length : pathStart)].Contains('%'))
            throw new HttpTransferException("HTTP_REDIRECT_REJECTED");
        bool nextIsApi = string.Equals(location.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);
        if (nextIsApi)
        {
            if (!apiOrigin || !string.Equals(current.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
                || pathStart < 0 || raw[pathStart..] != expectedResourceKey)
                throw new HttpTransferException("HTTP_REDIRECT_REJECTED");
        }
        else if (!string.Equals(location.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && !location.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            throw new HttpTransferException("HTTP_REDIRECT_REJECTED");
        return location;
    }
}
