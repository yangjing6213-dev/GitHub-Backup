using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class ByteFaithfulEndToEndTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Full_backup_preserves_raw_bytes_and_keeps_credentials_out_of_control_plane()
    {
        const string canary = "SYNTHETIC_TASK9_CREDENTIAL_CANARY";
        const string signature = "SYNTHETIC_TASK9_SIGNED_QUERY";
        byte[] secret = Encoding.ASCII.GetBytes(canary);
        // Source content may itself contain a secret-looking value; raw backup must not redact it.
        byte[] repository = Encoding.UTF8.GetBytes(" \n{\"body\":\"\\u001b é\",\"c1\":\"\u0081\",\"nul\":\"\\u0000\",\"source\":\"" + canary + "\"}\t");
        byte[] issues = " \n[{\"body\":\"\\u001b\\u0000 é\"}]\t"u8.ToArray();
        byte[] asset = [0, 27, 129, 255, 1];
        var requests = new List<string>();
        var reader = new CanaryReader(secret);
        await using var f = await OrchestrationFixture.CreateAsync(reader: reader, configureHttp: http =>
        {
            http.ObserveRequest = request =>
            {
                var uri = request.RequestUri!;
                requests.Add(uri.Host + uri.AbsolutePath); // Never retain signed query or Authorization.
                Assert.AreEqual(HttpMethod.Get, request.Method);
                Assert.AreEqual("GitHubBackup/1.0", request.Headers.UserAgent.ToString());
                Assert.AreEqual("identity", request.Headers.AcceptEncoding.Single().Value);
                Assert.IsFalse(request.Headers.Contains("Cookie") || request.Headers.Contains("Proxy-Authorization"));
                if (uri.Host == "api.github.com")
                {
                    Assert.IsTrue(string.Equals(request.Headers.Authorization?.ToString(), "Bearer " + canary, StringComparison.Ordinal));
                    Assert.AreEqual("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
                }
                else
                {
                    Assert.IsNull(request.Headers.Authorization);
                    Assert.IsFalse(request.Headers.Contains("X-GitHub-Api-Version"));
                }
                Assert.AreEqual(uri.AbsolutePath.Contains("/assets/") || uri.Host != "api.github.com"
                    ? "application/octet-stream" : "application/vnd.github+json", request.Headers.Accept.Single().MediaType);
            };
            http.ReleaseJson = JsonSerializer.Serialize(new[] { new { id = 10, tag_name = "v1", name = "release", draft = false,
                prerelease = false, published_at = (string?)null, assets = new[] { 100, 101 }.Select(id => new { id,
                    name = "asset" + id + ".bin", size = asset.Length, digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(asset)),
                    updated_at = "2026-09-20T00:00:00Z" }) } });
            http.Override = (path, _) => Task.FromResult<HttpResponseMessage?>(path switch
            {
                "/repos/fixture-user/repo" => Ok(repository),
                "/repos/fixture-user/repo/issues" => Ok(issues),
                "/repos/fixture-user/repo/releases/assets/100" => Ok(asset),
                "/repos/fixture-user/repo/releases/assets/101" => Redirect("https://objects.githubusercontent.com/first?sig=" + signature),
                "/first" => Redirect("https://github.com/final?sig=" + signature),
                "/final" => Ok(asset),
                _ => null
            });
        });

        Assert.AreEqual(1, reader.Reads);
        CollectionAssert.AreEqual(new[] { "api.github.com/user", "api.github.com/user/repos" }, requests);
        var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Full), default);

        Assert.AreEqual(RunStatus.Pass, result.Summary.Status, result.Summary.ErrorCode);
        Assert.IsFalse(result.HasPendingCleanup || f.Orchestrator.IsBusy || f.Orchestrator.HasPendingCleanup);
        Assert.IsTrue(secret.All(value => value == 0));
        Assert.ThrowsExactly<ObjectDisposedException>(() => f.Session.HttpTransport!.SendAsync(
            GitHubRequest.ForMetadata("fixture-user", "repo", "repository.json"), default));
        Assert.IsFalse(File.Exists(Path.Combine(f.Local.OwnerRoot, "RUNNING.json")));
        Assert.IsEmpty(Directory.GetFiles(f.Local.Root.Path, ".atomic-*.tmp", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetDirectories(f.Local.Root.Path, "GitHubBackup-git-*"));
        using (OperationLocks.AcquireStorage(f.Local.OwnerRoot, "after-run")) { }

        string metadata = Path.Combine(f.Local.OwnerRoot, "metadata", "repo");
        await ExactBytes("repository", repository, Path.Combine(metadata, "repository.json"));
        await ExactBytes("issues envelope", "["u8.ToArray().Concat(issues).Concat("]"u8.ToArray()).ToArray(), Path.Combine(metadata, "issues.pages.json"));
        string releaseRoot = Path.Combine(f.Local.OwnerRoot, "releases", "repo");
        string[] assetFiles = Directory.GetFiles(releaseRoot, "asset*.bin-*", SearchOption.AllDirectories);
        Assert.HasCount(2, assetFiles);
        foreach (string file in assetFiles) await ExactBytes("asset", asset, file);
        CollectionAssert.AreEqual(new[] { "api.github.com/repos/fixture-user/repo/releases/assets/101",
            "objects.githubusercontent.com/first", "github.com/final" }, requests.TakeLast(3).ToArray());

        foreach (var request in f.Local.Runner.Requests.Concat(f.Git.Requests))
            NoSecrets(JsonSerializer.Serialize(new { request.FilePath, request.Arguments, request.Environment, request.WorkingDirectory }));
        NoSecrets(JsonSerializer.Serialize(result));
        // Only generated control files belong in this scan; raw metadata/assets are deliberately opaque.
        var controls = Directory.GetFiles(f.Local.Paths.LocalAppDataRoot, "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(f.Local.OwnerRoot, "logs")))
            .Concat(Directory.GetFiles(Path.Combine(f.Local.OwnerRoot, "manifests")))
            .Concat(Directory.GetFiles(releaseRoot, "*.json", SearchOption.AllDirectories))
            .Append(Path.Combine(metadata, "repository-summary.json")).ToArray();
        foreach (string file in controls) NoSecrets(await File.ReadAllTextAsync(file));
        string[] diagnosticSources = Directory.GetFiles(Path.Combine(f.Local.OwnerRoot, "logs"), "backup-*.log")
            .Concat(Directory.GetFiles(Path.Combine(f.Local.OwnerRoot, "manifests"), "summary-*.json")).ToArray();
        var diagnostics = new DiagnosticsStore(f.Local.Paths, f.Local.OwnerRoot);
        var preview = await diagnostics.PreviewAsync(diagnosticSources, default);
        NoSecrets(JsonSerializer.Serialize(preview));
        string export = f.Local.Root.Child("diagnostic-export.txt");
        Assert.AreEqual(DiagnosticSaveStatus.Saved, await diagnostics.SaveAsync(preview, export, true, default));
        NoSecrets(await File.ReadAllTextAsync(export));
        // The production assembly is a publish input; actual single-EXE publication belongs to Task 10.
        byte[] productionAssembly = await File.ReadAllBytesAsync(typeof(AppSettings).Assembly.Location);
        foreach (string value in new[] { canary, signature })
        {
            Assert.AreEqual(-1, productionAssembly.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)));
            Assert.AreEqual(-1, productionAssembly.AsSpan().IndexOf(Encoding.Unicode.GetBytes(value)));
        }
        TestContext.WriteLine($"Control files scanned: {controls.Length}; process requests: {f.Local.Runner.Requests.Count + f.Git.Requests.Count}; credential reads: {reader.Reads}; cleanup complete.");

        void NoSecrets(string text)
        {
            Assert.IsFalse(text.Contains(canary, StringComparison.Ordinal), "Credential appeared on a control surface.");
            Assert.IsFalse(text.Contains(signature, StringComparison.Ordinal), "Signed query appeared on a control surface.");
        }
    }

    [TestMethod]
    public async Task Production_pagination_preserves_all_101_pages_and_the_final_short_page()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[][] pages = Enumerable.Range(1, 101).Select(page => Encoding.UTF8.GetBytes(" \n[" +
            string.Join(',', Enumerable.Range((page - 1) * 100 + 1, page == 101 ? 1 : 100)
                .Select(id => "{\"id\":" + id + ",\"body\":\"\\u001b é\"}")) + "]\t")).ToArray();
        var requestedPages = new List<int>();
        var handler = new OrchestrationHttp
        {
            ObserveRequest = request =>
            {
                if (request.RequestUri!.AbsolutePath == "/user") return;
                string query = request.RequestUri.Query;
                Assert.IsTrue(query.Contains("state=all") && query.Contains("per_page=100"));
                requestedPages.Add(query.Split('&').FirstOrDefault(part => part.StartsWith("page=", StringComparison.Ordinal)) is { } number
                    ? int.Parse(number[5..]) : 1);
            },
            Override = (path, _) =>
            {
                if (path == "/user") return Task.FromResult<HttpResponseMessage?>(null);
                int page = requestedPages[^1];
                var response = Ok(pages[page - 1]);
                if (page < 101) response.Headers.TryAddWithoutValidation("Link",
                    $"<https://api.github.com/repos/fixture-user/repo/issues?page={page + 1}&per_page=100&state=all>; rel=\"next\"");
                return Task.FromResult<HttpResponseMessage?>(response);
            }
        };
        using var transport = await GitHubHttpTransport.BindAsync(new("fixture-user", "SYNTHETIC_TASK9_PAGES"u8.ToArray()),
            "fixture-user", ProxyProfile.Direct, default, _ => handler);
        var receipt = await new RawPageStore(directory).SaveMetadataAsync("fixture-user", "repo", "issues.pages.json", transport, default);
        CollectionAssert.AreEqual(Enumerable.Range(1, 101).ToArray(), requestedPages);
        byte[] expected = Encoding.UTF8.GetBytes("[" + string.Join(',', pages.Select(Encoding.UTF8.GetString)) + "]");
        await ExactBytes("101 pages", expected, Path.Combine(directory, "issues.pages.json"));
        Assert.AreEqual(expected.LongLength, receipt.Length);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(expected)), receipt.Sha256);
        using var json = JsonDocument.Parse(expected);
        Assert.AreEqual(101, json.RootElement.GetArrayLength());
        Assert.AreEqual(10001, json.RootElement.EnumerateArray().Sum(page => page.GetArrayLength()));
        Assert.AreEqual(10001, json.RootElement[100][0].GetProperty("id").GetInt32());
        Assert.IsEmpty(Directory.GetFiles(directory, ".atomic-*.tmp"));
    }

    private async Task ExactBytes(string label, byte[] expected, string path)
    {
        byte[] actual = await File.ReadAllBytesAsync(path);
        CollectionAssert.AreEqual(expected, actual);
        string hash = Convert.ToHexString(SHA256.HashData(expected));
        Assert.AreEqual(hash, Convert.ToHexString(SHA256.HashData(actual)));
        TestContext.WriteLine($"{label}: input/output bytes={actual.Length}; SHA256={hash}");
    }

    private static HttpResponseMessage Ok(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("application/json");
        return response;
    }
    private static HttpResponseMessage Redirect(string uri) => new(HttpStatusCode.Found) { Headers = { Location = new(uri) } };
    private sealed class CanaryReader(byte[] secret) : IGitHubCredentialReader
    {
        internal int Reads;
        public GitHubCredentialLease ReadExact(string login) { Reads++; return new(login, secret); }
    }
}
