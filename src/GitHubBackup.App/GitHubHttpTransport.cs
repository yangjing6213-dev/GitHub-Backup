using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitHubBackup.App;

internal sealed class GitHubRequest
{
    internal AssetIdentity? Asset { get; }
    internal string Owner { get; }
    internal string Path { get; }
    internal string FixedQuery { get; }
    internal int Page { get; }
    internal bool Paginated { get; }
    internal string ResourceKey => Path + (FixedQuery.Length == 0 ? "" : "?" + FixedQuery)
        + (Page == 1 ? "" : (FixedQuery.Length == 0 ? "?" : "&") + "page=" + Page.ToString(CultureInfo.InvariantCulture));
    private GitHubRequest(AssetIdentity? asset, string owner, string path, string query, bool paginated, int page)
    { Asset = asset; Owner = owner; Path = path; FixedQuery = query; Paginated = paginated; Page = page; }
    internal static GitHubRequest ForAsset(AssetIdentity asset) => new(asset, asset.Owner, asset.ResourceKey, "", false, 1);
    internal static GitHubRequest ForOwnedRepositories(string owner, int? page = null)
    {
        if (!AuthConfigLease.IsLogin(owner)) throw new ArgumentException("HTTP_REPOSITORY_IDENTITY_INVALID");
        return new(null, owner, "/user/repos", "affiliation=owner&per_page=100", true, ValidPage(page));
    }
    internal static GitHubRequest ForMetadata(string owner, string repository, string fileName, int? page = null)
    {
        _ = new AssetIdentity(owner, repository, 1);
        if (repository[0] == '-' || repository.EndsWith('.'))
            throw new ArgumentException("HTTP_REPOSITORY_IDENTITY_INVALID");
        string prefix = $"/repos/{owner}/{repository}";
        (string suffix, string query, bool paginated) = fileName switch
        {
            "repository.json" => ("", "", false),
            "issues.pages.json" => ("/issues", "state=all&per_page=100", true),
            "pull-requests.pages.json" => ("/pulls", "state=all&per_page=100", true),
            "issue-comments.pages.json" => ("/issues/comments", "per_page=100", true),
            "review-comments.pages.json" => ("/pulls/comments", "per_page=100", true),
            "releases.pages.json" => ("/releases", "per_page=100", true),
            "labels.pages.json" => ("/labels", "per_page=100", true),
            "milestones.pages.json" => ("/milestones", "state=all&per_page=100", true),
            "workflows.pages.json" => ("/actions/workflows", "per_page=100", true),
            _ => throw new ArgumentException("HTTP_METADATA_FILE_INVALID")
        };
        if (!paginated && page is not null) throw new ArgumentException("HTTP_PAGINATION_REJECTED");
        return new(null, owner, prefix + suffix, query, paginated, ValidPage(page));
    }
    private static int ValidPage(int? page) => page is null ? 1 : page is > 0 and <= 1_000_000
        ? page.Value : throw new ArgumentException("HTTP_PAGINATION_REJECTED");

    internal int? ParseNextPage(string? link)
    {
        if (link is null) return null;
        if (!Paginated) throw new HttpTransferException("HTTP_PAGINATION_REJECTED");
        int? next = null;
        int? last = null;
        var relations = new HashSet<string>(StringComparer.Ordinal);
        foreach (string part in link.Split(','))
        {
            string item = part.Trim();
            int close = item.IndexOf('>');
            if (!item.StartsWith('<') || close < 0) throw new HttpTransferException("HTTP_PAGINATION_REJECTED");
            string relation = item[(close + 1)..].Trim();
            string name = relation switch
            {
                "; rel=\"next\"" or "; rel=next" => "next",
                "; rel=\"last\"" or "; rel=last" => "last",
                "; rel=\"prev\"" or "; rel=prev" => "prev",
                "; rel=\"first\"" or "; rel=first" => "first",
                _ => throw new HttpTransferException("HTTP_PAGINATION_REJECTED")
            };
            if (!relations.Add(name)) throw new HttpTransferException("HTTP_PAGINATION_REJECTED");
            string raw = item[1..close];
            const string origin = "https://api.github.com";
            int queryStart = raw.IndexOf('?');
            if (!raw.StartsWith(origin + "/", StringComparison.Ordinal) || queryStart < 0
                || raw[origin.Length..queryStart] != Path
                || raw.Any(c => c is '%' or '#' or '@' or '\\')
                || !Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "api.github.com", StringComparison.Ordinal)
                || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != Path
                || !raw.StartsWith("https://api.github.com/", StringComparison.Ordinal))
                throw new HttpTransferException("HTTP_PAGINATION_REJECTED");
            string query = uri.Query.TrimStart('?');
            var fields = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach (string pair in query.Split('&'))
            {
                string[] parts = pair.Split('=');
                if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0
                    || !fields.TryAdd(parts[0], parts[1])) throw new HttpTransferException("HTTP_PAGINATION_REJECTED");
            }
            var fixedFields = FixedQuery.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=')).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            if (fields.Count != fixedFields.Count + 1 || fixedFields.Any(pair => !fields.TryGetValue(pair.Key, out string? value) || value != pair.Value)
                || !fields.TryGetValue("page", out string? pageText) || pageText.Length is < 1 or > 7
                || pageText[0] == '0' || !pageText.All(char.IsAsciiDigit)
                || !int.TryParse(pageText, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                || number > 1_000_000 || (name == "next" ? number != Page + 1 :
                    name == "last" ? number < Page : number >= Page))
                throw new HttpTransferException("HTTP_PAGINATION_REJECTED");
            if (name == "next") next = number;
            if (name == "last") last = number;
        }
        if (last > Page && next is null || next > last)
            throw new HttpTransferException("HTTP_PAGINATION_REJECTED");
        return next;
    }
}

internal sealed class GitHubResponse(int statusCode, IReadOnlyDictionary<string,string> headers, Stream body, int? nextPage = null) : IDisposable
{
    public int StatusCode { get; } = statusCode;
    public IReadOnlyDictionary<string,string> Headers { get; } = headers;
    [JsonIgnore]
    internal int? NextPage { get; } = nextPage;
    [JsonIgnore]
    public Stream Body { get; } = body;
    public void Dispose() => Body.Dispose();
}

internal sealed class HttpBindingCleanupException(GitHubHttpTransport transport, bool wasCancelled) : IOException("HTTP_CLEANUP_FAILED")
{
    [JsonIgnore]
    internal GitHubHttpTransport Transport { get; } = transport;
    internal bool WasCancelled { get; } = wasCancelled;
}

internal interface IGitHubHttpTransport : IDisposable
{
    string BoundLogin { get; }
    long BoundAccountId { get; }
    Task<GitHubResponse> SendAsync(GitHubRequest request, CancellationToken token);
    Task<GitHubResponse> DownloadAssetAsync(AssetIdentity asset, CancellationToken token);
}

internal sealed record HttpTimeoutLimits
{
    internal static HttpTimeoutLimits Default { get; } = new(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
    internal TimeSpan Control { get; }
    internal TimeSpan AssetHeaders { get; }
    internal TimeSpan AssetIdle { get; }
    internal HttpTimeoutLimits(TimeSpan control, TimeSpan assetHeaders, TimeSpan assetIdle)
    {
        if (control <= TimeSpan.Zero || control > TimeSpan.FromSeconds(120)
            || assetHeaders <= TimeSpan.Zero || assetHeaders > TimeSpan.FromSeconds(30)
            || assetIdle <= TimeSpan.Zero || assetIdle > TimeSpan.FromSeconds(60))
            throw new ArgumentException("HTTP_TIMEOUT_LIMIT_INVALID");
        Control = control; AssetHeaders = assetHeaders; AssetIdle = assetIdle;
    }
}

internal sealed class GitHubHttpTransport : IGitHubHttpTransport
{
    private static readonly Uri UserUri = new("https://api.github.com/user");
    private readonly GitHubCredentialLease lease;
    private readonly HttpClient client;
    private readonly HttpMessageHandler handler;
    private readonly HttpTimeoutLimits limits;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<GitHubResponse> active = [];
    private readonly object gate = new();
    private bool disposed;
    private bool cleanupComplete;
    private bool clientReleased;
    private bool handlerReleased;
    private bool permanentCleanupFailure;
    public string BoundLogin { get; private set; } = "";
    public long BoundAccountId { get; private set; }

    private GitHubHttpTransport(GitHubCredentialLease lease, HttpClient client, HttpMessageHandler handler, HttpTimeoutLimits limits)
    { this.lease = lease; this.client = client; this.handler = handler; this.limits = limits; }

    internal static async Task<IGitHubHttpTransport> BindAsync(GitHubCredentialLease lease, string selectedOwner,
        ProxyProfile profile, CancellationToken token, Func<SocketsHttpHandler,HttpMessageHandler>? handlerFactory = null,
        HttpTimeoutLimits? limits = null)
    {
        GitHubHttpTransport? transport = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!AuthConfigLease.IsLogin(selectedOwner) || !string.Equals(lease.Login, selectedOwner, StringComparison.OrdinalIgnoreCase))
                throw new HttpTransferException("HTTP_IDENTITY_REJECTED");
            var sockets = CreateHandler(profile);
            HttpMessageHandler handler;
            try { handler = handlerFactory?.Invoke(sockets) ?? sockets; }
            catch { sockets.Dispose(); throw; }
            if (!ReferenceEquals(handler, sockets)) sockets.Dispose();
            var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            transport = new GitHubHttpTransport(lease, client, handler, limits ?? HttpTimeoutLimits.Default);
            using GitHubResponse response = await transport.SendWithPolicyAsync(UserUri, "/user", "application/vnd.github+json", false, token).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(response.Body, cancellationToken: token).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count(p => p.NameEquals("login")) != 1
                || root.EnumerateObject().Count(p => p.NameEquals("id")) != 1
                || !root.TryGetProperty("login", out var login)
                || login.ValueKind != JsonValueKind.String || !string.Equals(login.GetString(), selectedOwner, StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt64(out long accountId) || accountId <= 0)
                throw new HttpTransferException("HTTP_IDENTITY_REJECTED");
            transport.BoundLogin = selectedOwner.ToLowerInvariant();
            transport.BoundAccountId = accountId;
            return transport;
        }
        catch (JsonException)
        {
            DisposeOnBindingFailure(transport, lease, false);
            throw new HttpTransferException("HTTP_IDENTITY_REJECTED");
        }
        catch (Exception ex)
        {
            DisposeOnBindingFailure(transport, lease, ex is OperationCanceledException);
            throw;
        }
    }

    private static void DisposeOnBindingFailure(GitHubHttpTransport? transport, GitHubCredentialLease lease, bool wasCancelled)
    {
        try { transport?.Dispose(); }
        catch (Exception) when (transport is not null) { throw new HttpBindingCleanupException(transport, wasCancelled); }
        finally { lease.Dispose(); }
    }

    public Task<GitHubResponse> SendAsync(GitHubRequest request, CancellationToken token)
    {
        if (request.Asset is not null) CheckAsset(request.Asset);
        else CheckOwner(request.Owner);
        return SendWithPolicyAsync(new Uri("https://api.github.com" + request.ResourceKey), request.ResourceKey,
            request.Asset is null ? "application/vnd.github+json" : "application/octet-stream", false, token, request);
    }

    public Task<GitHubResponse> DownloadAssetAsync(AssetIdentity asset, CancellationToken token)
    {
        CheckAsset(asset);
        return SendWithPolicyAsync(new Uri("https://api.github.com" + asset.ResourceKey), asset.ResourceKey,
            "application/octet-stream", true, token);
    }

    private void CheckAsset(AssetIdentity asset)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (BoundAccountId <= 0 || !string.Equals(asset.Owner, BoundLogin, StringComparison.OrdinalIgnoreCase))
            throw new HttpTransferException("HTTP_ASSET_IDENTITY_REJECTED");
    }

    private void CheckOwner(string owner)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (BoundAccountId <= 0 || !string.Equals(owner, BoundLogin, StringComparison.OrdinalIgnoreCase))
            throw new HttpTransferException("HTTP_REPOSITORY_IDENTITY_REJECTED");
    }

    private async Task<GitHubResponse> SendWithPolicyAsync(Uri initial, string resourceKey, string accept, bool redirects, CancellationToken token, GitHubRequest? request = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            Uri current = initial;
            bool apiOrigin = true;
            int hops = 0;
            while (true)
            {
                Exchange exchange;
                try { exchange = await ExchangeAsync(current, accept, apiOrigin, redirects, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested)
                {
                    if (RetryPolicy.ShouldRetry(NetworkFailureKind.Timeout, attempt)) break;
                    throw new HttpTransferException("HTTP_TIMEOUT", NetworkFailureKind.Timeout);
                }
                catch (OperationCanceledException) { throw new OperationCanceledException("HTTP_CANCELLED"); }
                catch (HttpRequestException error)
                {
                    NetworkFailureKind kind = error.InnerException is SocketException socket ? socket.SocketErrorCode switch
                    {
                        SocketError.ConnectionRefused => NetworkFailureKind.ConnectionRefused,
                        SocketError.ConnectionReset => NetworkFailureKind.ConnectionReset,
                        SocketError.TimedOut => NetworkFailureKind.Timeout,
                        _ => NetworkFailureKind.Unknown
                    } : NetworkFailureKind.Unknown;
                    if (RetryPolicy.ShouldRetry(kind, attempt)) break;
                    throw new HttpTransferException(kind == NetworkFailureKind.Unknown ? "HTTP_REQUEST_FAILED" : "NETWORK_" + kind.ToString().ToUpperInvariant(), kind);
                }
                catch (IOException) { throw new HttpTransferException("HTTP_REQUEST_FAILED"); }
                int status = (int)exchange.Response.StatusCode;
                if (status == 200)
                {
                    try { return await OpenResponseAsync(exchange, redirects, token, request).ConfigureAwait(false); }
                    catch { exchange.Dispose(); throw; }
                }
                if (redirects && status is 301 or 302 or 303 or 307 or 308)
                {
                    try
                    {
                        if (hops++ >= 5) throw new HttpTransferException("HTTP_REDIRECT_LIMIT");
                        Uri next = exchange.Response.Headers.Location ?? throw new HttpTransferException("HTTP_REDIRECT_REJECTED");
                        current = RedirectPolicy.ValidateNext(current, next, resourceKey, apiOrigin);
                        apiOrigin = string.Equals(current.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);
                    }
                    finally { exchange.Dispose(); }
                    continue;
                }
                NetworkFailureKind statusKind = NetworkProbe.ClassifyHttpStatus(status, ReadHeader(exchange.Response, "X-RateLimit-Remaining"));
                DateTimeOffset? reset = statusKind == NetworkFailureKind.RateLimited
                    ? ParseReset(ReadHeader(exchange.Response, "X-RateLimit-Reset")) : null;
                exchange.Dispose();
                if (RetryPolicy.ShouldRetry(statusKind, attempt)) break;
                throw new HttpTransferException(statusKind == NetworkFailureKind.RateLimited ? "HTTP_RATE_LIMITED"
                    : "HTTP_STATUS_" + status.ToString(CultureInfo.InvariantCulture), statusKind, reset);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), token).ConfigureAwait(false);
        }
    }

    private async Task<Exchange> ExchangeAsync(Uri uri, string accept, bool apiOrigin, bool asset, CancellationToken token)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        timeout.CancelAfter(asset ? limits.AssetHeaders : limits.Control);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        HttpResponseMessage? response = null;
        try
        {
            request.Headers.UserAgent.ParseAdd("GitHubBackup/1.0");
            request.Headers.Accept.ParseAdd(accept);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (apiOrigin)
            {
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
                lease.AttachAuthorization(request);
            }
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            request.Headers.Authorization = null;
            if (asset) timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            return new(response, request, timeout);
        }
        catch { response?.Dispose(); request.Headers.Authorization = null; request.Dispose(); timeout.Dispose(); throw; }
    }

    private async Task<GitHubResponse> OpenResponseAsync(Exchange exchange, bool asset, CancellationToken token, GitHubRequest? request)
    {
        var encoding = exchange.Response.Content.Headers.ContentEncoding;
        if (encoding.Count > 1 || encoding.Count == 1 && !string.Equals(encoding.Single(), "identity", StringComparison.OrdinalIgnoreCase))
            throw new HttpTransferException("HTTP_ENCODING_UNSUPPORTED");
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(exchange.Timeout.Token);
        if (asset) acquisition.CancelAfter(limits.AssetIdle);
        Task<Stream>? opening = null;
        Stream? body = null;
        try
        {
            opening = exchange.Response.Content.ReadAsStreamAsync(acquisition.Token);
            body = await opening.WaitAsync(acquisition.Token).ConfigureAwait(false);
            acquisition.Token.ThrowIfCancellationRequested();
        }
        catch (Exception)
        {
            bool callerCancelled = token.IsCancellationRequested || lifetime.IsCancellationRequested;
            bool deadlineExpired = acquisition.IsCancellationRequested;
            if (body is not null) { try { body.Dispose(); } catch { } }
            else if (opening is not null)
            {
                try { (await opening.ConfigureAwait(false)).Dispose(); }
                catch { }
            }
            if (callerCancelled)
                throw new OperationCanceledException("HTTP_CANCELLED");
            if (deadlineExpired)
                throw new HttpTransferException(asset ? "HTTP_BODY_IDLE_TIMEOUT" : "HTTP_TIMEOUT", NetworkFailureKind.Timeout);
            throw new HttpTransferException("HTTP_BODY_FAILED");
        }
        var headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if (exchange.Response.Content.Headers.ContentLength is long length)
            headers["Content-Length"] = length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        GitHubResponse? result = null;
        var stream = new ResponseBodyStream(body, exchange, token, lifetime.Token, asset, limits.AssetIdle,
            () => { lock (gate) active.Remove(result!); });
        int? nextPage;
        try { nextPage = request is { Asset: null } ? request.ParseNextPage(ReadPaginationHeader(exchange.Response)) : null; }
        catch { stream.Dispose(); throw; }
        result = new(200, new ReadOnlyDictionary<string,string>(headers), stream, nextPage);
        bool accepted;
        lock (gate)
        {
            accepted = !disposed && !acquisition.IsCancellationRequested;
            if (accepted) active.Add(result);
        }
        if (!accepted)
        {
            result.Dispose();
            if (token.IsCancellationRequested || lifetime.IsCancellationRequested || disposed)
                throw new OperationCanceledException("HTTP_CANCELLED");
            throw new HttpTransferException(asset ? "HTTP_BODY_IDLE_TIMEOUT" : "HTTP_TIMEOUT", NetworkFailureKind.Timeout);
        }
        return result;
    }

    private static SocketsHttpHandler CreateHandler(ProxyProfile profile)
    {
        var values = new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in profile.Environment)
        {
            bool noProxyKey = pair.Key.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase);
            bool proxyKey = pair.Key.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase);
            if (!noProxyKey && !proxyKey) continue;
            if (!values.TryAdd(pair.Key, pair.Value)) throw new HttpTransferException("HTTP_PROXY_PROFILE_REJECTED");
            if (proxyKey && !ProxyScope.Parse(pair.Value).IsValid)
                throw new HttpTransferException("HTTP_PROXY_PROFILE_REJECTED");
        }
        string? noProxy = values.GetValueOrDefault("NO_PROXY");
        ProxyScope.ValidateCommonNoProxy(noProxy);
        string? proxy = values.GetValueOrDefault("HTTPS_PROXY") ?? values.GetValueOrDefault("ALL_PROXY");
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            Credentials = null,
            DefaultProxyCredentials = null,
            PreAuthenticate = false,
            UseProxy = proxy is not null,
            Proxy = proxy is null ? null : new FrozenProxy(new Uri(proxy), noProxy)
        };
        return handler;
    }

    private static string? ReadHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;
        string[] bounded = values.Take(2).ToArray();
        return bounded.Length == 1 ? bounded[0] : null;
    }

    private static string? ReadPaginationHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;
        string[] bounded = values.Take(2).ToArray();
        if (bounded.Length != 1) throw new HttpTransferException("HTTP_PAGINATION_REJECTED");
        return bounded[0];
    }

    private static DateTimeOffset? ParseReset(string? value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public void Dispose()
    {
        GitHubResponse[] responses;
        lock (gate)
        {
            if (cleanupComplete) return;
            disposed = true;
            responses = active.ToArray();
        }
        lease.Dispose();
        try { lifetime.Cancel(); } catch (Exception) { permanentCleanupFailure = true; }
        bool responseFailed = false;
        foreach (GitHubResponse response in responses)
        {
            try { response.Dispose(); } catch (Exception) { responseFailed = true; }
        }
        if (responseFailed || permanentCleanupFailure) throw new IOException("HTTP_CLEANUP_FAILED");
        if (!clientReleased)
        {
            try { client.Dispose(); clientReleased = true; }
            catch (Exception) { permanentCleanupFailure = true; }
        }
        if (!handlerReleased)
        {
            try { handler.Dispose(); handlerReleased = true; }
            catch (Exception) { }
        }
        if (permanentCleanupFailure || !handlerReleased) throw new IOException("HTTP_CLEANUP_FAILED");
        lifetime.Dispose();
        lock (gate) cleanupComplete = true;
    }

    private sealed class FrozenProxy(Uri proxy, string? noProxy) : IWebProxy
    {
        public ICredentials? Credentials { get => null; set { if (value is not null) throw new InvalidOperationException("HTTP_PROXY_CREDENTIAL_REJECTED"); } }
        public Uri GetProxy(Uri destination) => IsBypassed(destination) ? destination : proxy;
        public bool IsBypassed(Uri host) => ProxyScope.CommonNoProxyMatches(noProxy, host.Host);
    }

    private sealed class Exchange(HttpResponseMessage response, HttpRequestMessage request, CancellationTokenSource timeout) : IDisposable
    {
        internal HttpResponseMessage Response { get; } = response;
        internal HttpRequestMessage Request { get; } = request;
        internal CancellationTokenSource Timeout { get; } = timeout;
        private bool disposed;
        private bool failed;
        public void Dispose()
        {
            if (disposed) return;
            if (failed) throw new IOException("HTTP_CLEANUP_FAILED");
            try { Response.Dispose(); }
            catch (Exception) { failed = true; }
            try { Request.Dispose(); Timeout.Dispose(); }
            catch (Exception) { failed = true; }
            if (failed) throw new IOException("HTTP_CLEANUP_FAILED");
            disposed = true;
        }
    }

    private sealed class ResponseBodyStream(Stream inner, Exchange exchange, CancellationToken userToken, CancellationToken lifetimeToken,
        bool asset, TimeSpan assetIdleTimeout, Action onDispose) : Stream
    {
        private bool disposed;
        private bool innerDisposed;
        private bool exchangeDisposed;
        public override bool CanRead => !disposed && inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var read = CancellationTokenSource.CreateLinkedTokenSource(exchange.Timeout.Token, cancellationToken);
            if (asset) read.CancelAfter(assetIdleTimeout);
            try
            {
                int count = await inner.ReadAsync(buffer, read.Token).ConfigureAwait(false);
                read.Token.ThrowIfCancellationRequested();
                return count;
            }
            catch (OperationCanceledException) when (read.IsCancellationRequested && !userToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested && !lifetimeToken.IsCancellationRequested)
            { throw new HttpTransferException(asset ? "HTTP_BODY_IDLE_TIMEOUT" : "HTTP_TIMEOUT", NetworkFailureKind.Timeout); }
            catch (OperationCanceledException) when (userToken.IsCancellationRequested || cancellationToken.IsCancellationRequested || lifetimeToken.IsCancellationRequested)
            { throw new OperationCanceledException("HTTP_CANCELLED"); }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
            { throw new HttpTransferException("HTTP_BODY_FAILED"); }
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (!disposed && disposing)
            {
                if (!innerDisposed)
                {
                    try { inner.Dispose(); innerDisposed = true; }
                    catch (Exception) { throw new IOException("HTTP_CLEANUP_FAILED"); }
                }
                if (!exchangeDisposed)
                {
                    try { exchange.Dispose(); exchangeDisposed = true; }
                    catch (Exception) { throw new IOException("HTTP_CLEANUP_FAILED"); }
                }
                onDispose();
                disposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
