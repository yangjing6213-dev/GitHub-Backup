using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
namespace GitHubBackup.App;
internal sealed record ProxyParseResult(bool IsValid, Uri? Uri, string UserMessage);
internal sealed record ProxyProfile(string DisplayName, IReadOnlyDictionary<string,string?> Environment)
{
    internal static ProxyProfile Direct { get; } = new("Direct", new ReadOnlyDictionary<string,string?>(new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase)));
}
internal sealed class ProxyScope(NetworkProbe probe, IReadOnlyDictionary<string,string?> parent, Func<Uri,Uri?> resolver)
{
    private static readonly string[] Keys = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"];
    internal ProxyScope(NetworkProbe probe) : this(probe, ReadParent(), origin => WebRequest.GetSystemWebProxy().GetProxy(origin)) { }
    private static IReadOnlyDictionary<string,string?> ReadParent()
    {
        var values = new Dictionary<string,string?>(StringComparer.Ordinal);
        foreach (string key in Keys.SelectMany(k => new[] { k, k.ToLowerInvariant() })) values[key] = Environment.GetEnvironmentVariable(key);
        return values;
    }
    internal static ProxyParseResult Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value != value.Trim()
            || value.Contains('\\') || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || uri.Host.Length == 0 || uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath is not ("" or "/"))
            return new(false, null, "代理地址无效；仅支持无凭据的 HTTP(S) 代理。");
        int authority = value.IndexOf("://",StringComparison.Ordinal);
        int rawPath = authority < 0 ? -1 : value.IndexOf('/',authority + 3);
        if (authority < 0 || (rawPath >= 0 && value[rawPath..] != "/"))
            return new(false, null, "代理地址无效；仅支持无凭据的 HTTP(S) 代理。");
        return new(true, uri, "");
    }
    internal static void ValidateCommonNoProxy(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == "*") return;
        foreach (string host in value.Split(','))
        {
            if (host.Length is < 1 or > 253 || host.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-'))
                || host.StartsWith('.') || host.EndsWith('.') || host.Contains("..", StringComparison.Ordinal))
                throw new ArgumentException("PROXY_PROFILE_UNSUPPORTED");
            if (host.All(c => char.IsAsciiDigit(c) || c == '.'))
            {
                if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork
                    || address.ToString() != host) throw new ArgumentException("PROXY_PROFILE_UNSUPPORTED");
                continue;
            }
            foreach (string label in host.Split('.'))
                if (label.Length is < 1 or > 63 || !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1]))
                    throw new ArgumentException("PROXY_PROFILE_UNSUPPORTED");
        }
    }
    internal static bool CommonNoProxyMatches(string? value, string host)
    {
        ValidateCommonNoProxy(value);
        if (string.IsNullOrEmpty(value)) return false;
        if (value == "*") return true;
        return value.Split(',').Any(rule => string.Equals(host, rule, StringComparison.OrdinalIgnoreCase)
            || (!IPAddress.TryParse(rule, out _) && host.EndsWith("." + rule, StringComparison.OrdinalIgnoreCase)));
    }
    internal static ProxyProfile CreateValidatedParentProfile(IReadOnlyDictionary<string,string?> parent)
    {
        var values = new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in Keys)
        {
            var variants = parent.Where(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(p.Value)).Select(p => p.Value!).ToArray();
            if (variants.Length == 0) continue;
            var normalized = new List<string>();
            foreach (string value in variants)
            {
                if (key == "NO_PROXY")
                {
                    ValidateCommonNoProxy(value);
                    normalized.Add(value);
                }
                else
                {
                    var parsed = Parse(value);
                    if (!parsed.IsValid) throw new ArgumentException("PROXY_PARENT_INVALID");
                    normalized.Add(parsed.Uri!.AbsoluteUri);
                }
            }
            if (normalized.Distinct(StringComparer.Ordinal).Count() != 1) throw new ArgumentException("PROXY_PARENT_CONFLICT");
            values[key] = normalized[0];
        }
        if (!values.Keys.Any(k => k != "NO_PROXY")) return ProxyProfile.Direct;
        return new("Existing environment", new ReadOnlyDictionary<string,string?>(values));
    }
    internal static ProxyProfile CreateSystemProxy(Uri uri, IReadOnlyDictionary<string,string?> parent)
    {
        var parsed = Parse(uri.OriginalString);
        if (!parsed.IsValid) throw new ArgumentException("PROXY_SYSTEM_INVALID");
        return new("Windows system proxy", new ReadOnlyDictionary<string,string?>(new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase)
        { ["HTTP_PROXY"] = parsed.Uri!.AbsoluteUri, ["HTTPS_PROXY"] = parsed.Uri.AbsoluteUri, ["ALL_PROXY"] = parsed.Uri.AbsoluteUri }));
    }
    internal static IReadOnlyDictionary<string,string?> Merge(IReadOnlyDictionary<string,string?> environment, ProxyProfile profile)
    {
        if (profile.Environment.TryGetValue("NO_PROXY", out string? bypass)) ValidateCommonNoProxy(bypass);
        var clean = environment.Where(p => !Keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string,string?> owned = environment is RuntimeEnvironment runtime ? new RuntimeEnvironment(clean, runtime.Owner, runtime.Authentication, runtime.AuthenticatedLogin) : clean;
        return ChildEnvironmentBuilder.Build(owned, profile.Environment);
    }
    internal (ProxyProfile? Profile,string? ErrorCode) GetSystemProxyAlternative(ProxyProfile current)
    {
        var origins = new[] { new Uri("https://github.com"), new Uri("https://api.github.com") };
        Uri?[] resolved;
        try { resolved = origins.Select(resolver).ToArray(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Net.WebException or SocketException or PlatformNotSupportedException)
        { return (null,"PROXY_SYSTEM_INVALID"); }
        if (resolved.All(uri => uri is null) || resolved.Select((uri,i) => uri == origins[i]).All(isDirect => isDirect)) return (null,null);
        if (resolved.Any(uri => uri is null) || resolved.Select((uri,i) => uri == origins[i]).Any(isDirect => isDirect))
            return (null,"PROXY_SYSTEM_INCONSISTENT");
        var parsed = resolved.Select(uri => Parse(uri!.OriginalString)).ToArray();
        if (parsed.Any(profile => !profile.IsValid)) return (null,"PROXY_SYSTEM_INVALID");
        if (parsed[0].Uri != parsed[1].Uri) return (null,"PROXY_SYSTEM_INCONSISTENT");
        var system = CreateSystemProxy(parsed[0].Uri!, parent);
        if (current.Environment.Count == system.Environment.Count
            && current.Environment.All(pair => system.Environment.TryGetValue(pair.Key,out var value) && value == pair.Value)) return (null,null);
        return (system,null);
    }
    internal async Task<(ProxyProfile Profile, NetworkCheckResult Check)> SelectAsync(Func<ProxyProfile,CancellationToken,Task<NetworkCheckResult>> check, CancellationToken token)
    {
        ProxyProfile initial;
        try { initial = CreateValidatedParentProfile(parent); }
        catch (ArgumentException ex) when (ex.Message == "PROXY_PROFILE_UNSUPPORTED")
        { return (ProxyProfile.Direct, new(false, NetworkFailureKind.Unknown, null, "PROXY_PROFILE_UNSUPPORTED")); }
        catch (ArgumentException) { initial = ProxyProfile.Direct; }
        NetworkCheckResult result = await check(initial, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result.Success || !IsConnectionFailure(result.FailureKind)) return (initial, result);
        var alternative = GetSystemProxyAlternative(initial);
        token.ThrowIfCancellationRequested();
        if (alternative.ErrorCode is not null) return (initial, new(false,NetworkFailureKind.Unknown,null,alternative.ErrorCode));
        return alternative.Profile is null ? (initial,result) : (alternative.Profile,await check(alternative.Profile,token).ConfigureAwait(false));
    }
    private static bool IsConnectionFailure(NetworkFailureKind kind) => kind is NetworkFailureKind.Timeout or NetworkFailureKind.ConnectionRefused or NetworkFailureKind.ConnectionReset;
    internal async Task<ProxyProfile> SelectForLoginAsync(ToolInventory tools, GitRuntimeContext publicProbeContext, OperationJob job, CancellationToken cancellationToken)
    {
        var selected = await SelectAsync((profile, token) => probe.CheckPublicGitAsync(tools, Merge(publicProbeContext.Environment, profile), job, token), cancellationToken).ConfigureAwait(false);
        if (!selected.Check.Success) throw new IOException(selected.Check.ErrorCode);
        return selected.Profile;
    }
}
