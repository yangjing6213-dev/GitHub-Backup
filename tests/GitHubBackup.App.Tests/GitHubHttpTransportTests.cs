using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class GitHubHttpTransportTests
{
    private const string Canary = "SYNTHETIC_CANARY_DO_NOT_LOG";
    private static readonly AssetIdentity Asset = new("fixture-user", "fixture-repo", 42);

    [TestMethod]
    public async Task Metadata_link_only_exposes_validated_numeric_page_and_rebuilds_frozen_request()
    {
        var uris = new List<string>();
        var handler = new ScriptedHandler(request =>
        {
            uris.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsolutePath == "/user") return Ok("{\"login\":\"fixture-user\",\"id\":7}");
            var result = Ok("[]");
            if (uris.Count == 2) result.Headers.TryAddWithoutValidation("Link",
                "<https://api.github.com/user/repos?page=2&per_page=100&affiliation=owner>; rel=\"next\"");
            return result;
        });
        using var transport = await Bind(handler);
        using var first = await transport.SendAsync(GitHubRequest.ForOwnedRepositories("fixture-user"), default);
        Assert.AreEqual(2, first.NextPage);
        Assert.IsFalse(JsonSerializer.Serialize(first).Contains("Link", StringComparison.OrdinalIgnoreCase));
        using var second = await transport.SendAsync(GitHubRequest.ForOwnedRepositories("fixture-user", first.NextPage), default);
        Assert.IsNull(second.NextPage);
        CollectionAssert.AreEqual(new[] {
            "https://api.github.com/user",
            "https://api.github.com/user/repos?affiliation=owner&per_page=100",
            "https://api.github.com/user/repos?affiliation=owner&per_page=100&page=2" }, uris);
    }

    [TestMethod]
    [DataRow("https://objects.githubusercontent.com/file?sig=SYNTHETIC_SECRET")]
    [DataRow("https://api.github.com/user/repos?affiliation=owner&per_page=100&page=2&page=3")]
    [DataRow("https://api.github.com/user/repos?affiliation=owner&per_page=100&since=2")]
    [DataRow("https://api.github.com/user/repos?affiliation=owner&per_page=99&page=2")]
    [DataRow("https://api.github.com/user/repos?affiliation=owner&per_page=100&page=1")]
    [DataRow("https://api.github.com/user/repos?affiliation=owner&per_page=100&page=%32")]
    [DataRow("https://api.github.com/user/repos?affiliation=owner&per_page=100&page=2&")]
    [DataRow("https://api.github.com/user/repos?affiliation=owner&per_page=100&page=3")]
    [DataRow("https://api.github.com/user/../user/repos?affiliation=owner&per_page=100&page=2")]
    public async Task Invalid_next_link_is_rejected_without_publishing_raw_url(string next)
    {
        var handler = new ScriptedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/user") return Ok("{\"login\":\"fixture-user\",\"id\":7}");
            var response = Ok("[]");
            response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
            return response;
        });
        using var transport = await Bind(handler);
        var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            transport.SendAsync(GitHubRequest.ForOwnedRepositories("fixture-user"), default));
        Assert.AreEqual("HTTP_PAGINATION_REJECTED", error.Code);
        Assert.IsFalse(error.ToString().Contains(next, StringComparison.Ordinal));
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task Last_page_without_next_is_rejected_as_incomplete_chain()
    {
        var handler = new ScriptedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/user") return Ok("{\"login\":\"fixture-user\",\"id\":7}");
            var response = Ok("[]");
            response.Headers.TryAddWithoutValidation("Link",
                "<https://api.github.com/user/repos?affiliation=owner&per_page=100&page=2>; rel=\"last\"");
            return response;
        });
        using var transport = await Bind(handler);
        Assert.AreEqual("HTTP_PAGINATION_REJECTED", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            transport.SendAsync(GitHubRequest.ForOwnedRepositories("fixture-user"), default))).Code);
    }

    [TestMethod]
    public async Task Multiple_link_header_values_are_rejected_without_disclosing_them()
    {
        const string signed = "https://objects.githubusercontent.com/file?sig=SYNTHETIC_SECRET";
        var handler = new ScriptedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/user") return Ok("{\"login\":\"fixture-user\",\"id\":7}");
            var response = Ok("[]");
            response.Headers.TryAddWithoutValidation("Link", new[] {
                "<https://api.github.com/user/repos?affiliation=owner&per_page=100&page=2>; rel=\"next\"",
                $"<{signed}>; rel=\"next\"" });
            return response;
        });
        using var transport = await Bind(handler);
        var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            transport.SendAsync(GitHubRequest.ForOwnedRepositories("fixture-user"), default));
        Assert.AreEqual("HTTP_PAGINATION_REJECTED", error.Code);
        Assert.IsFalse(error.ToString().Contains(signed, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Owned_repository_request_cannot_switch_its_bound_owner()
    {
        var handler = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}") : Ok("[]"));
        using var transport = await Bind(handler);
        Assert.AreEqual("HTTP_REPOSITORY_IDENTITY_REJECTED", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            transport.SendAsync(GitHubRequest.ForOwnedRepositories("other-user"), default))).Code);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow("repository.json", "/repos/fixture-user/fixture-repo")]
    [DataRow("issues.pages.json", "/repos/fixture-user/fixture-repo/issues?state=all&per_page=100")]
    [DataRow("pull-requests.pages.json", "/repos/fixture-user/fixture-repo/pulls?state=all&per_page=100")]
    [DataRow("issue-comments.pages.json", "/repos/fixture-user/fixture-repo/issues/comments?per_page=100")]
    [DataRow("review-comments.pages.json", "/repos/fixture-user/fixture-repo/pulls/comments?per_page=100")]
    [DataRow("releases.pages.json", "/repos/fixture-user/fixture-repo/releases?per_page=100")]
    [DataRow("labels.pages.json", "/repos/fixture-user/fixture-repo/labels?per_page=100")]
    [DataRow("milestones.pages.json", "/repos/fixture-user/fixture-repo/milestones?state=all&per_page=100")]
    [DataRow("workflows.pages.json", "/repos/fixture-user/fixture-repo/actions/workflows?per_page=100")]
    public async Task Frozen_metadata_table_sends_only_approved_paths_and_filters(string fileName, string expected)
    {
        string? actual = null;
        var handler = new ScriptedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/user") return Ok("{\"login\":\"fixture-user\",\"id\":7}");
            actual = request.RequestUri.PathAndQuery;
            Assert.AreEqual("application/vnd.github+json", request.Headers.Accept.Single().MediaType);
            return Ok(fileName == "repository.json" || fileName == "workflows.pages.json" ? "{}" : "[]");
        });
        using var transport = await Bind(handler);
        using var response = await transport.SendAsync(GitHubRequest.ForMetadata("fixture-user", "fixture-repo", fileName), default);
        Assert.AreEqual(expected, actual);
        Assert.IsNull(response.NextPage);
    }

    [TestMethod]
    [DataRow("-argument")]
    [DataRow("trailing.")]
    public void Metadata_factory_rejects_noncanonical_repository_segments(string repository) =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            GitHubRequest.ForMetadata("fixture-user", repository, "repository.json"));

    [TestMethod]
    public async Task Handler_cleanup_failure_still_zeros_owned_credential()
    {
        byte[] secret = Encoding.ASCII.GetBytes(Canary);
        var handler = new ThrowingDisposeHandler();
        var transport = await GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", secret),
            "fixture-user", ProxyProfile.Direct, default, _ => handler);

        Assert.AreEqual("HTTP_CLEANUP_FAILED", Assert.ThrowsExactly<IOException>(() => transport.Dispose()).Message);

        Assert.IsTrue(secret.All(value => value == 0));
    }

    [TestMethod]
    public async Task Failed_binding_cleanup_carrier_contains_no_public_transport_or_raw_failure()
    {
        byte[] secret = Encoding.ASCII.GetBytes(Canary);
        var handler = new ThrowingDisposeHandler("invalid", 1);

        var error = await Assert.ThrowsExactlyAsync<HttpBindingCleanupException>(() => GitHubHttpTransport.BindAsync(
            new GitHubCredentialLease("fixture-user", secret), "fixture-user", ProxyProfile.Direct, default, _ => handler));

        Assert.AreEqual("HTTP_CLEANUP_FAILED", error.Message);
        Assert.IsNull(error.InnerException);
        Assert.IsFalse(error.ToString().Contains(Canary, StringComparison.Ordinal));
        Assert.IsFalse(typeof(HttpBindingCleanupException).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Any(property => typeof(IGitHubHttpTransport).IsAssignableFrom(property.PropertyType)));
        Assert.IsTrue(secret.All(value => value == 0));
        Assert.AreEqual(1, handler.DisposeAttempts);
        error.Transport.Dispose();
        Assert.AreEqual(2, handler.DisposeAttempts);
    }

    [TestMethod]
    public async Task Response_body_release_retries_after_transient_dispose_failures()
    {
        byte[] secret = Encoding.ASCII.GetBytes(Canary);
        var body = new ThrowTwiceDisposeStream();
        var handler = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        var transport = await GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", secret),
            "fixture-user", ProxyProfile.Direct, default, _ => handler);
        var response = await transport.DownloadAssetAsync(Asset, default);

        Assert.AreEqual("HTTP_CLEANUP_FAILED", Assert.ThrowsExactly<IOException>(() => response.Dispose()).Message);
        Assert.AreEqual("HTTP_CLEANUP_FAILED", Assert.ThrowsExactly<IOException>(() => transport.Dispose()).Message);
        Assert.IsTrue(secret.All(value => value == 0));
        Assert.AreEqual(2, body.DisposeAttempts);
        transport.Dispose();
        Assert.IsGreaterThanOrEqualTo(3, body.DisposeAttempts);
    }

    [TestMethod]
    public async Task Fixed_user_binding_precedes_every_other_authenticated_request_and_keeps_one_lease()
    {
        byte[] secret = Encoding.ASCII.GetBytes(Canary);
        var calls = new List<(string Method, string Uri, string? Auth, string? Version, string? Accept, string? Encoding, string? Agent)>();
        var handler = new ScriptedHandler(request =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Headers.GetValues("X-GitHub-Api-Version").Single(), request.Headers.Accept.Single().MediaType,
                request.Headers.AcceptEncoding.Single().Value, request.Headers.UserAgent.ToString()));
            return request.RequestUri!.AbsolutePath == "/user"
                ? Ok("{\"login\":\"FIXTURE-USER\",\"id\":123456789}")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0x1b, 0x80, 0xff]) };
        });
        using var transport = await GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", secret),
            "Fixture-User", ProxyProfile.Direct, default, _ => handler);
        Assert.AreEqual("fixture-user", transport.BoundLogin);
        Assert.AreEqual(123456789L, transport.BoundAccountId);
        using var response = await transport.DownloadAssetAsync(Asset, default);
        using var output = new MemoryStream();
        await response.Body.CopyToAsync(output);
        CollectionAssert.AreEqual(new byte[] { 0, 0x1b, 0x80, 0xff }, output.ToArray());
        CollectionAssert.AreEqual(new[] { "https://api.github.com/user", "https://api.github.com/repos/fixture-user/fixture-repo/releases/assets/42" },
            calls.Select(x => x.Uri).ToArray());
        Assert.IsTrue(calls.All(x => x.Method == "GET" && x.Auth == "Bearer " + Canary
            && x.Version == "2022-11-28" && x.Encoding == "identity" && x.Agent == "GitHubBackup/1.0"));
        Assert.AreEqual("application/vnd.github+json", calls[0].Accept);
        Assert.AreEqual("application/octet-stream", calls[1].Accept);
        transport.Dispose();
        Assert.IsTrue(secret.All(x => x == 0));
    }

    [TestMethod]
    [DataRow("{\"login\":\"other\",\"id\":4}")]
    [DataRow("{\"login\":\"fixture-user\",\"id\":0}")]
    [DataRow("{\"login\":\"fixture-user\",\"id\":\"4\"}")]
    [DataRow("{\"login\":\"fixture-user\",\"id\":-1}")]
    [DataRow("{\"login\":\"fixture-user\",\"id\":9223372036854775808}")]
    [DataRow("{\"login\":\"fixture-user \",\"id\":7}")]
    [DataRow("{\"login\":\"other\",\"login\":\"fixture-user\",\"id\":7}")]
    [DataRow("{\"login\":\"fixture-user\",\"id\":8,\"id\":7}")]
    [DataRow("not-json")]
    public async Task Invalid_user_identity_destroys_the_unverified_lease(string body)
    {
        byte[] secret = Encoding.ASCII.GetBytes(Canary);
        var handler = new ScriptedHandler(_ => Ok(body));
        var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() => GitHubHttpTransport.BindAsync(
            new GitHubCredentialLease("fixture-user", secret), "fixture-user", ProxyProfile.Direct, default, _ => handler));
        Assert.AreEqual("HTTP_IDENTITY_REJECTED", error.Code);
        Assert.IsTrue(secret.All(x => x == 0));
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task Mismatched_selected_owner_and_precancelled_binding_send_nothing_and_release_secret()
    {
        byte[] mismatchSecret = Encoding.ASCII.GetBytes(Canary);
        var handler = new ScriptedHandler(_ => throw new AssertFailedException("Must not send"));
        await Assert.ThrowsExactlyAsync<HttpTransferException>(() => GitHubHttpTransport.BindAsync(
            new GitHubCredentialLease("fixture-user", mismatchSecret), "other-user", ProxyProfile.Direct, default, _ => handler));
        Assert.IsTrue(mismatchSecret.All(x => x == 0));
        byte[] cancelledSecret = Encoding.ASCII.GetBytes(Canary);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => GitHubHttpTransport.BindAsync(
            new GitHubCredentialLease("fixture-user", cancelledSecret), "fixture-user", ProxyProfile.Direct, cancel.Token, _ => handler));
        Assert.IsTrue(cancelledSecret.All(x => x == 0));
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task Handler_disables_ambient_identity_cookies_decoding_redirects_and_uses_frozen_proxy()
    {
        var profile = ProxyScope.CreateValidatedParentProfile(new Dictionary<string,string?>
        { ["HTTPS_PROXY"] = "http://proxy.example:8080", ["NO_PROXY"] = "github.com" });
        var handler = new ScriptedHandler(_ => Ok("{\"login\":\"fixture-user\",\"id\":7}"));
        using var transport = await GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", Encoding.ASCII.GetBytes(Canary)),
            "fixture-user", profile, default, sockets =>
            {
                Assert.IsFalse(sockets.AllowAutoRedirect);
                Assert.IsFalse(sockets.UseCookies);
                Assert.AreEqual(DecompressionMethods.None, sockets.AutomaticDecompression);
                Assert.IsNull(sockets.Credentials);
                Assert.IsNull(sockets.DefaultProxyCredentials);
                Assert.IsFalse(sockets.PreAuthenticate);
                Assert.IsTrue(sockets.UseProxy);
                Assert.IsNotNull(sockets.Proxy);
                Assert.IsNull(sockets.Proxy.Credentials);
                Assert.IsTrue(sockets.Proxy!.IsBypassed(new Uri("https://api.github.com/user")));
                Assert.IsFalse(sockets.Proxy!.IsBypassed(new Uri("https://objects.githubusercontent.com/file")));
                Assert.AreEqual("http://proxy.example:8080/", sockets.Proxy!.GetProxy(new Uri("https://objects.githubusercontent.com/file"))!.AbsoluteUri);
                return handler;
            });
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task Directly_supplied_proxy_profile_rejects_lowercase_credentials_and_conflicting_case_keys()
    {
        foreach (var environment in new[]
        {
            new Dictionary<string,string?>(StringComparer.Ordinal)
            { ["https_proxy"] = "http://synthetic-user:synthetic-secret@proxy.example:8080" },
            new Dictionary<string,string?>(StringComparer.Ordinal)
            { ["HTTPS_PROXY"] = "http://proxy.example:8080", ["https_proxy"] = "http://other.example:8080" }
        })
        {
            byte[] secret = Encoding.ASCII.GetBytes(Canary);
            int factoryCalls = 0;
            var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() => GitHubHttpTransport.BindAsync(
                new GitHubCredentialLease("fixture-user", secret), "fixture-user", new ProxyProfile("synthetic", environment),
                default, _ => { factoryCalls++; throw new AssertFailedException("Must not send"); }));
            Assert.AreEqual("HTTP_PROXY_PROFILE_REJECTED", error.Code);
            Assert.AreEqual(0, factoryCalls);
            Assert.IsTrue(secret.All(x => x == 0));
        }

        var safeLowercase = new ProxyProfile("synthetic", new Dictionary<string,string?>
        { ["https_proxy"] = "http://proxy.example:8080" });
        var handler = new ScriptedHandler(_ => Ok("{\"login\":\"fixture-user\",\"id\":7}"));
        using var transport = await GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", Encoding.ASCII.GetBytes(Canary)),
            "fixture-user", safeLowercase, default, sockets =>
            {
                Assert.AreEqual("http://proxy.example:8080/", sockets.Proxy!.GetProxy(new Uri("https://api.github.com/user"))!.AbsoluteUri);
                return handler;
            });
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task Redirect_chain_permanently_drops_authorization_and_api_version_and_stops_after_five_hops()
    {
        var calls = new List<(string Uri, string Method, bool Auth, bool Version, string? Accept, string? Encoding, bool Cookie)>();
        int redirects = 0;
        var handler = new ScriptedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/user") return Ok("{\"login\":\"fixture-user\",\"id\":7}");
            calls.Add((request.RequestUri.AbsoluteUri, request.Method.Method, request.Headers.Authorization is not null,
                request.Headers.Contains("X-GitHub-Api-Version"), request.Headers.Accept.Single().MediaType,
                request.Headers.AcceptEncoding.Single().Value, request.Headers.Contains("Cookie")));
            if (redirects++ < 2)
                return Redirect(redirects == 1 ? "https://objects.githubusercontent.com/a?sig=SYNTHETIC_SECRET"
                    : "https://github.com/b?sig=SYNTHETIC_SECRET");
            return Ok("binary");
        });
        using var transport = await Bind(handler);
        using var response = await transport.DownloadAssetAsync(Asset, default);
        Assert.AreEqual("binary", await new StreamReader(response.Body).ReadToEndAsync());
        CollectionAssert.AreEqual(new[]
        {
            "https://api.github.com/repos/fixture-user/fixture-repo/releases/assets/42",
            "https://objects.githubusercontent.com/a?sig=SYNTHETIC_SECRET",
            "https://github.com/b?sig=SYNTHETIC_SECRET"
        }, calls.Select(x => x.Uri).ToArray());
        Assert.IsTrue(calls[0].Auth && calls[0].Version);
        Assert.IsTrue(calls.All(x => x.Method == "GET" && x.Encoding == "identity" && !x.Cookie));
        Assert.IsTrue(calls.Skip(1).All(x => !x.Auth && !x.Version && x.Accept == "application/octet-stream"));

        int hops = 0;
        var endless = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}")
            : Redirect("https://objects.githubusercontent.com/hop" + ++hops + "?sig=SYNTHETIC_SECRET"));
        using var bounded = await Bind(endless);
        var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() => bounded.DownloadAssetAsync(Asset, default));
        Assert.AreEqual("HTTP_REDIRECT_LIMIT", error.Code);
        Assert.AreEqual(6, hops);
        Assert.IsFalse(error.ToString().Contains("SYNTHETIC_SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Redirected_asset_result_cannot_display_or_serialize_signed_query()
    {
        var handler = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}")
            : request.RequestUri.Host == "api.github.com"
                ? Redirect("https://objects.githubusercontent.com/file?sig=SYNTHETIC_SECRET")
                : SignedLinkResponse());
        using var transport = await Bind(handler);
        using var response = await transport.DownloadAssetAsync(Asset, default);

        Assert.IsFalse(response.GetType().GetProperties().Any(p => p.PropertyType == typeof(Uri)));
        Assert.IsFalse(response.Headers.ContainsKey("Link"));
        Assert.IsFalse(response.Headers.Values.Any(value => value.Contains("SYNTHETIC_SECRET", StringComparison.Ordinal)));
        Assert.IsFalse(response.ToString()!.Contains("SYNTHETIC_SECRET", StringComparison.Ordinal));
        string serialized = JsonSerializer.Serialize(response);
        Assert.IsTrue(serialized.Contains("\"StatusCode\":200", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("SYNTHETIC_SECRET", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("\"Body\"", StringComparison.Ordinal));

        var apiAsset = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}") : SignedLinkResponse());
        using var apiTransport = await Bind(apiAsset);
        using var apiResponse = await apiTransport.SendAsync(GitHubRequest.ForAsset(Asset), default);
        Assert.IsFalse(apiResponse.Headers.ContainsKey("Link"));
        Assert.IsFalse(JsonSerializer.Serialize(apiResponse).Contains("SYNTHETIC_SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Non_200_and_non_identity_content_encoding_cannot_become_body()
    {
        foreach (HttpStatusCode statusCode in new[] { HttpStatusCode.NoContent, HttpStatusCode.PartialContent, HttpStatusCode.NotModified })
        {
            var status = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
                ? Ok("{\"login\":\"fixture-user\",\"id\":7}") : new HttpResponseMessage(statusCode));
            using var transport = await Bind(status);
            Assert.AreEqual("HTTP_STATUS_" + (int)statusCode, (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
                transport.DownloadAssetAsync(Asset, default))).Code);
        }
        var encoded = new ScriptedHandler(request =>
        {
            var response = Ok(request.RequestUri!.AbsolutePath == "/user" ? "{\"login\":\"fixture-user\",\"id\":7}" : "compressed");
            if (request.RequestUri.AbsolutePath != "/user") response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        });
        using (var transport = await Bind(encoded))
            Assert.AreEqual("HTTP_ENCODING_UNSUPPORTED", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() => transport.DownloadAssetAsync(Asset, default))).Code);
    }

    [TestMethod]
    public async Task Transient_status_retries_at_most_three_times_and_auth_rejection_never_retries()
    {
        int attempts = 0;
        var transient = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}")
            : ++attempts < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok("last"));
        using (var transport = await Bind(transient))
        using (var response = await transport.DownloadAssetAsync(Asset, default))
            Assert.AreEqual("last", await new StreamReader(response.Body).ReadToEndAsync());
        Assert.AreEqual(3, attempts);
        attempts = 0;
        var denied = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}")
            : (++attempts, new HttpResponseMessage(HttpStatusCode.Forbidden)).Item2);
        using (var transport = await Bind(denied))
            Assert.AreEqual("HTTP_STATUS_403", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
                transport.DownloadAssetAsync(Asset, default))).Code);
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task Rate_limit_errors_use_only_official_headers_and_keep_reset_bounded()
    {
        foreach (var (status, remaining, reset, expected, expectedReset) in new[]
        {
            (HttpStatusCode.TooManyRequests, "1", "1700000000", NetworkFailureKind.RateLimited, DateTimeOffset.FromUnixTimeSeconds(1700000000)),
            (HttpStatusCode.Forbidden, "0", "1700000000", NetworkFailureKind.RateLimited, DateTimeOffset.FromUnixTimeSeconds(1700000000)),
            (HttpStatusCode.Forbidden, "1", "1700000000", NetworkFailureKind.Forbidden, (DateTimeOffset?)null),
            (HttpStatusCode.TooManyRequests, "0", "999999999999999999999", NetworkFailureKind.RateLimited, (DateTimeOffset?)null)
        })
        {
            int attempts = 0;
            var handler = new ScriptedHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/user") return Ok("{\"login\":\"fixture-user\",\"id\":7}");
                attempts++;
                var response = new HttpResponseMessage(status) { Content = new StringContent("SYNTHETIC_SECRET") };
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining);
                response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset);
                return response;
            });
            using var transport = await Bind(handler);
            var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() => transport.DownloadAssetAsync(Asset, default));
            Assert.AreEqual(expected, error.FailureKind);
            Assert.AreEqual(expectedReset, error.RateLimitReset);
            Assert.AreEqual(1, attempts);
            Assert.IsFalse(error.ToString().Contains("SYNTHETIC_SECRET", StringComparison.Ordinal));
            Assert.IsFalse(error.ToString().Contains(reset, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task Simulated_header_timeout_retries_three_times_with_a_fixed_error()
    {
        int attempts = 0;
        var handler = new AsyncHandler((_, _) =>
        { attempts++; throw new OperationCanceledException("https://api.github.com/user?sig=SYNTHETIC_SECRET"); });
        byte[] secret = Encoding.ASCII.GetBytes(Canary);
        var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() => GitHubHttpTransport.BindAsync(
            new GitHubCredentialLease("fixture-user", secret), "fixture-user", ProxyProfile.Direct, default, _ => handler));
        Assert.AreEqual("HTTP_TIMEOUT", error.Code);
        Assert.AreEqual(NetworkFailureKind.Timeout, error.FailureKind);
        Assert.AreEqual(3, attempts);
        Assert.IsTrue(secret.All(x => x == 0));
        Assert.IsFalse(error.ToString().Contains("SYNTHETIC_SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Connection_refusal_is_typed_and_retried_without_leaking_exception_text()
    {
        int attempts = 0;
        var handler = new AsyncHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/user") return Task.FromResult(Ok("{\"login\":\"fixture-user\",\"id\":7}"));
            attempts++;
            throw new HttpRequestException("SYNTHETIC_SECRET", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        });
        using var transport = await Bind(handler);
        var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() => transport.DownloadAssetAsync(Asset, default));
        Assert.AreEqual(NetworkFailureKind.ConnectionRefused, error.FailureKind);
        Assert.AreEqual(3, attempts);
        Assert.IsFalse(error.ToString().Contains("SYNTHETIC_SECRET", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Bounded_real_header_and_body_timers_are_exercised_with_owned_handlers()
    {
        var shortLimits = new HttpTimeoutLimits(TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25));
        byte[] secret = Encoding.ASCII.GetBytes(Canary);
        int headers = 0;
        var blockedHeaders = new AsyncHandler(async (_, token) =>
        { headers++; await Task.Delay(Timeout.InfiniteTimeSpan, token); return Ok("unexpected"); });
        var bindingError = await Assert.ThrowsExactlyAsync<HttpTransferException>(() => GitHubHttpTransport.BindAsync(
            new GitHubCredentialLease("fixture-user", secret), "fixture-user", ProxyProfile.Direct,
            default, _ => blockedHeaders, shortLimits));
        Assert.AreEqual("HTTP_TIMEOUT", bindingError.Code);
        Assert.AreEqual(3, headers);
        Assert.IsTrue(secret.All(x => x == 0));

        int assetHeaders = 0;
        var blockedAssetHeaders = new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/user") return Ok("{\"login\":\"fixture-user\",\"id\":7}");
            assetHeaders++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ok("unexpected");
        });
        using (var headerTransport = await GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", Encoding.ASCII.GetBytes(Canary)),
            "fixture-user", ProxyProfile.Direct, default, _ => blockedAssetHeaders, shortLimits))
            Assert.AreEqual("HTTP_TIMEOUT", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
                headerTransport.DownloadAssetAsync(Asset, default))).Code);
        Assert.AreEqual(3, assetHeaders);

        byte[] controlBodySecret = Encoding.ASCII.GetBytes(Canary);
        var controlBody = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new BlockingStream()) });
        Assert.AreEqual("HTTP_TIMEOUT", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", controlBodySecret), "fixture-user",
                ProxyProfile.Direct, default, _ => controlBody, shortLimits))).Code);
        Assert.IsTrue(controlBodySecret.All(x => x == 0));

        var blockedBody = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) });
        using var transport = await GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", Encoding.ASCII.GetBytes(Canary)),
            "fixture-user", ProxyProfile.Direct, default, _ => blockedBody, shortLimits);
        using var response = await transport.DownloadAssetAsync(Asset, default);
        var idleError = await Assert.ThrowsExactlyAsync<HttpTransferException>(async () =>
            await response.Body.ReadExactlyAsync(new byte[1]));
        Assert.AreEqual("HTTP_BODY_IDLE_TIMEOUT", idleError.Code);
    }

    [TestMethod]
    public async Task A_retry_keeps_the_selected_proxy_snapshot_after_parent_changes()
    {
        var parent = new Dictionary<string,string?> { ["HTTPS_PROXY"] = "http://proxy.example:8080", ["NO_PROXY"] = "github.com" };
        ProxyProfile profile = ProxyScope.CreateValidatedParentProfile(parent);
        int factoryCalls = 0, assetCalls = 0;
        IWebProxy? route = null;
        var handler = new ScriptedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/user") return Ok("{\"login\":\"fixture-user\",\"id\":7}");
            assetCalls++;
            parent["HTTPS_PROXY"] = "http://other.invalid:8080";
            return assetCalls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok("ok");
        });
        using var transport = await GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", Encoding.ASCII.GetBytes(Canary)),
            "fixture-user", profile, default, sockets => { factoryCalls++; route = sockets.Proxy; return handler; });
        using var response = await transport.DownloadAssetAsync(Asset, default);
        Assert.AreEqual("ok", await new StreamReader(response.Body).ReadToEndAsync());
        Assert.AreEqual(1, factoryCalls);
        Assert.AreEqual(2, assetCalls);
        Assert.AreEqual("http://proxy.example:8080/", route!.GetProxy(new Uri("https://objects.githubusercontent.com/file"))!.AbsoluteUri);
        Assert.AreEqual("http://proxy.example:8080/", profile.Environment["HTTPS_PROXY"]);
    }

    [TestMethod]
    public async Task A_late_header_or_body_result_after_timeout_is_not_accepted()
    {
        var shortLimits = new HttpTimeoutLimits(TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25));
        byte[] secret = Encoding.ASCII.GetBytes(Canary);
        var lateHeaders = new AsyncHandler(async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { }
            return Ok("{\"login\":\"fixture-user\",\"id\":7}");
        });
        Assert.AreEqual("HTTP_TIMEOUT", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", secret), "fixture-user",
                ProxyProfile.Direct, default, _ => lateHeaders, shortLimits))).Code);
        Assert.IsTrue(secret.All(x => x == 0));

        var lateBody = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new LateStream()) });
        using var transport = await GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", Encoding.ASCII.GetBytes(Canary)),
            "fixture-user", ProxyProfile.Direct, default, _ => lateBody, shortLimits);
        using var response = await transport.DownloadAssetAsync(Asset, default);
        Assert.AreEqual("HTTP_BODY_IDLE_TIMEOUT", (await Assert.ThrowsExactlyAsync<HttpTransferException>(async () =>
            await response.Body.ReadExactlyAsync(new byte[1]))).Code);
    }

    [TestMethod]
    public async Task Cancellation_during_stream_acquisition_cannot_publish_a_200_result()
    {
        using var caller = new CancellationTokenSource();
        var content = new StreamReadContent(_ =>
        {
            caller.Cancel();
            return Task.FromResult<Stream>(new MemoryStream([42]));
        });
        var handler = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var transport = await Bind(handler);

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => transport.DownloadAssetAsync(Asset, caller.Token));
        Assert.AreEqual("HTTP_CANCELLED", error.Message);
        Assert.IsTrue(content.Disposed);
    }

    [TestMethod]
    public async Task Blocked_stream_acquisition_has_an_idle_deadline_and_disposes_late_stream()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateStream = new TrackingStream();
        var content = new StreamReadContent(token =>
        {
            token.Register(() => deadlineReached.TrySetResult());
            started.SetResult();
            return release.Task;
        });
        var handler = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var limits = new HttpTimeoutLimits(TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25));
        using var transport = await GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", Encoding.ASCII.GetBytes(Canary)),
            "fixture-user", ProxyProfile.Direct, default, _ => handler, limits);

        var pending = transport.DownloadAssetAsync(Asset, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await deadlineReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.IsFalse(pending.IsCompleted, "Operation must retain ownership until late stream cleanup finishes");
        }
        finally { release.TrySetResult(lateStream); }
        try
        {
            var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() => pending);
            Assert.AreEqual("HTTP_BODY_IDLE_TIMEOUT", error.Code);
            Assert.AreEqual(NetworkFailureKind.Timeout, error.FailureKind);
            Assert.IsTrue(content.Disposed);
            Assert.IsTrue(lateStream.Disposed.Task.IsCompleted, "Late stream cleanup must precede operation completion");
        }
        finally { await lateStream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task User_redirect_and_asset_return_to_api_never_send_a_second_authenticated_request()
    {
        byte[] secret = Encoding.ASCII.GetBytes(Canary);
        var userRedirect = new ScriptedHandler(_ => Redirect("https://api.github.com/user"));
        Assert.AreEqual("HTTP_STATUS_302", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", secret), "fixture-user",
                ProxyProfile.Direct, default, _ => userRedirect))).Code);
        Assert.AreEqual(1, userRedirect.Calls);
        Assert.IsTrue(secret.All(x => x == 0));

        int calls = 0;
        var returnToApi = new ScriptedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/user") return Ok("{\"login\":\"fixture-user\",\"id\":7}");
            calls++;
            return Redirect(calls == 1 ? "https://objects.githubusercontent.com/file?sig=SYNTHETIC_SECRET"
                : "https://api.github.com/repos/fixture-user/fixture-repo/releases/assets/42");
        });
        using var transport = await Bind(returnToApi);
        Assert.AreEqual("HTTP_REDIRECT_REJECTED", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            transport.DownloadAssetAsync(Asset, default))).Code);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task Cancellation_during_user_headers_and_asset_body_releases_owned_resources()
    {
        byte[] secret = Encoding.ASCII.GetBytes(Canary);
        using var headerCancel = new CancellationTokenSource();
        var blockedHeaders = new AsyncHandler(async (_, token) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Ok("unexpected"); });
        var binding = GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", secret),
            "fixture-user", ProxyProfile.Direct, headerCancel.Token, _ => blockedHeaders);
        headerCancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => binding);
        Assert.IsTrue(secret.All(x => x == 0));

        using var bodyCancel = new CancellationTokenSource();
        var blockedBody = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) });
        using var transport = await Bind(blockedBody);
        using var response = await transport.DownloadAssetAsync(Asset, bodyCancel.Token);
        var read = response.Body.ReadAsync(new byte[1], 0, 1, bodyCancel.Token);
        bodyCancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => read);
    }

    [TestMethod]
    public async Task Disposing_transport_interrupts_an_active_body_without_reporting_idle_timeout()
    {
        var blockedBody = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) });
        var transport = await Bind(blockedBody);
        using var response = await transport.DownloadAssetAsync(Asset, default);
        var read = response.Body.ReadAsync(new byte[1], 0, 1, default);
        transport.Dispose();
        try { await read; Assert.Fail("Expected cancellation or disposed stream"); }
        catch (HttpTransferException error) { Assert.AreNotEqual("HTTP_BODY_IDLE_TIMEOUT", error.Code); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    [TestMethod]
    public async Task Typed_asset_request_does_not_follow_redirect_and_rejects_another_owner()
    {
        var handler = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}")
            : Redirect("https://objects.githubusercontent.com/signed?sig=SYNTHETIC_SECRET"));
        using var transport = await Bind(handler);
        Assert.AreEqual("HTTP_STATUS_302", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            transport.SendAsync(GitHubRequest.ForAsset(Asset), default))).Code);
        Assert.AreEqual("HTTP_ASSET_IDENTITY_REJECTED", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            transport.DownloadAssetAsync(new AssetIdentity("other-user", "repo", 1), default))).Code);
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task Per_request_authorization_reference_is_removed_after_response_headers()
    {
        HttpRequestMessage? assetRequest = null;
        var handler = new ScriptedHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/user") return Ok("{\"login\":\"fixture-user\",\"id\":7}");
            Assert.AreEqual("Bearer " + Canary, request.Headers.Authorization?.ToString());
            assetRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) };
        });
        using var transport = await Bind(handler);
        using var response = await transport.DownloadAssetAsync(Asset, default);
        Assert.IsNotNull(assetRequest);
        Assert.IsNull(assetRequest.Headers.Authorization);
    }

    [TestMethod]
    public async Task Network_and_body_exceptions_never_expose_signed_location_or_canary()
    {
        const string signed = "https://objects.githubusercontent.com/file?sig=SYNTHETIC_SECRET";
        var badBody = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? Ok("{\"login\":\"fixture-user\",\"id\":7}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ExplodingStream(signed + Canary)) });
        using (var transport = await Bind(badBody))
        using (var response = await transport.DownloadAssetAsync(Asset, default))
        {
            var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(async () =>
                await response.Body.ReadExactlyAsync(new byte[1]));
            Assert.AreEqual("HTTP_BODY_FAILED", error.Code);
            Assert.IsFalse(error.ToString().Contains("SYNTHETIC_SECRET", StringComparison.Ordinal));
            Assert.IsFalse(error.ToString().Contains(Canary, StringComparison.Ordinal));
        }
        using var cancel = new CancellationTokenSource();
        var badCancellation = new AsyncHandler(async (_, token) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new OperationCanceledException(signed + Canary, token); });
        var binding = GitHubHttpTransport.BindAsync(new GitHubCredentialLease("fixture-user", Encoding.ASCII.GetBytes(Canary)),
            "fixture-user", ProxyProfile.Direct, cancel.Token, _ => badCancellation);
        cancel.Cancel();
        try { await binding; Assert.Fail("Expected cancellation"); }
        catch (OperationCanceledException error)
        {
            Assert.IsFalse(error.ToString().Contains("SYNTHETIC_SECRET", StringComparison.Ordinal));
            Assert.IsFalse(error.ToString().Contains(Canary, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [DataRow("bad owner", "repo", 1)] [DataRow("owner", "../repo", 1)]
    [DataRow(null, "repo", 1)] [DataRow("owner", null, 1)]
    [DataRow("owner", "repo", 0)] [DataRow("owner", "repo", -1)]
    public void Asset_identity_rejects_untrusted_path_segments_and_nonpositive_ids(string? owner, string? repo, long id) =>
        Assert.ThrowsExactly<ArgumentException>(() => new AssetIdentity(owner!, repo!, id));

    private static Task<IGitHubHttpTransport> Bind(HttpMessageHandler handler) => GitHubHttpTransport.BindAsync(
        new GitHubCredentialLease("fixture-user", Encoding.ASCII.GetBytes(Canary)), "fixture-user", ProxyProfile.Direct, default, _ => handler);

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
    private static HttpResponseMessage SignedLinkResponse()
    {
        var response = Ok("binary");
        response.Headers.TryAddWithoutValidation("Link", "<https://objects.githubusercontent.com/next?sig=SYNTHETIC_SECRET>; rel=\"next\"");
        return response;
    }
    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }
    private sealed class ThrowingDisposeHandler(string body = "{\"login\":\"fixture-user\",\"id\":7}", int failures = int.MaxValue) : HttpMessageHandler
    {
        internal int DisposeAttempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Ok(body));
        protected override void Dispose(bool disposing)
        {
            if (DisposeAttempts++ < failures) throw new IOException("SYNTHETIC_DISPOSE_FAILURE");
            base.Dispose(disposing);
        }
    }
    private sealed class ThrowTwiceDisposeStream : MemoryStream
    {
        internal int DisposeAttempts;
        protected override void Dispose(bool disposing)
        {
            DisposeAttempts++;
            if (DisposeAttempts <= 2) throw new IOException("SYNTHETIC_BODY_DISPOSE_FAILURE");
            base.Dispose(disposing);
        }
    }
    private sealed class ScriptedHandler(Func<HttpRequestMessage,HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(reply(request)); }
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken);
    }
    private sealed class StreamReadContent(Func<CancellationToken,Task<Stream>> read) : HttpContent
    {
        internal bool Disposed { get; private set; }
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => read(cancellationToken);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class TrackingStream : MemoryStream
    {
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Dispose(bool disposing) { Disposed.TrySetResult(); base.Dispose(disposing); }
    }
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class ExplodingStream(string message) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(message);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException(message));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class LateStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { }
            buffer.Span[0] = 42;
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
