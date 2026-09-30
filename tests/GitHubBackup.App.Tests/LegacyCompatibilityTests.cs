using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class LegacyCompatibilityTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static readonly DateTime FrozenTime = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [TestMethod]
    public async Task Frozen_legacy_formats_remain_readable_and_unchanged()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        string manifests = Path.Combine(owner, "manifests");
        string metadata = Path.Combine(owner, "metadata");
        string releases = Path.Combine(owner, "releases");
        AclPolicy.CreateRestrictedDirectory(manifests, root.User);
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        AclPolicy.CreateRestrictedDirectory(releases, root.User);
        var copies = new Dictionary<string, (byte[] Hash, DateTime Written)>();

        async Task<string> Copy(string fixture, string destination, bool bom = false)
        {
            byte[] original = await File.ReadAllBytesAsync(Fixture(fixture));
            byte[] bytes = bom ? Encoding.UTF8.GetPreamble().Concat(original).ToArray() : original;
            await File.WriteAllBytesAsync(destination, bytes);
            File.SetLastWriteTimeUtc(destination, FrozenTime);
            copies.Add(destination, (SHA256.HashData(bytes), File.GetLastWriteTimeUtc(destination)));
            return destination;
        }

        await Copy("summary-v1.json", Path.Combine(manifests, "summary-20260920-010203.json"), bom: true);
        var summaries = new SummaryStore(AppPaths.Create(root.Path));
        SummaryReadResult legacy = await summaries.ReadLatestAsync(owner, AppPaths.Create(root.Path).DiagnosticFallbackRoot, default);
        Assert.AreEqual(1, legacy.Latest?.SchemaVersion);
        Assert.AreEqual(RunStatus.Partial, legacy.Latest?.Status);
        Assert.AreEqual(14, legacy.Latest?.RepositoryCount);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-20T01:02:03+00:00"), legacy.Latest?.CompletedAt);
        Assert.AreEqual(@"C:\BackupRoot\sample-owner", legacy.Latest?.BackupRoot);

        await Copy("summary-v2.json", Path.Combine(manifests, "summary-20260920-020304.json"));
        SummaryReadResult current = await summaries.ReadLatestAsync(owner, AppPaths.Create(root.Path).DiagnosticFallbackRoot, default);
        Assert.AreEqual(2, current.Latest?.SchemaVersion);
        Assert.AreEqual(RunStatus.Pass, current.Latest?.Status);
        Assert.AreEqual(BackupMode.Daily, current.Latest?.Mode);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-20T02:03:04+00:00"), current.Latest?.CompletedAt);

        await Copy("repositories-v1.json", Path.Combine(manifests, "repositories-20260920-010203.json"), bom: true);
        IReadOnlyList<RepositoryDescriptor> repositories = await new ManifestStore().ReadLatestAsync(owner, default);
        Assert.HasCount(2, repositories);
        Assert.AreEqual("sample-owner/alpha", repositories[0].NameWithOwner);
        Assert.AreEqual(0L, repositories[0].RepositoryId);
        Assert.AreEqual("alpha", repositories[0].LocalName);
        Assert.AreEqual("beta", repositories[1].LocalName);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-19T00:00:00Z"), repositories[1].UpdatedAt);

        string pagesPath = await Copy("pages-v1.json", Path.Combine(metadata, "issues.pages.json"));
        JsonElement pages = await ManifestStore.ReadPagesAsync(pagesPath, default);
        Assert.AreEqual(JsonValueKind.Array, pages.ValueKind);
        Assert.AreEqual(2, pages.GetArrayLength());
        Assert.AreEqual(1, pages[0].GetArrayLength());
        Assert.AreEqual(1, pages[0][0].GetProperty("id").GetInt32());
        Assert.AreEqual(0, pages[1].GetArrayLength());

        foreach (string name in new[] { "releases-v1.json", "release-v1.json" })
        {
            string path = await Copy(name, Path.Combine(releases, name), bom: name == "release-v1.json");
            using JsonDocument json = await MetadataJson.ReadDocumentAsync(path, default);
            JsonElement release = name == "releases-v1.json" ? json.RootElement[0] : json.RootElement;
            Assert.AreEqual("v1", release.GetProperty("tagName").GetString());
            Assert.AreEqual("Old release", release.GetProperty("name").GetString());
            Assert.IsFalse(release.GetProperty("isDraft").GetBoolean());
            Assert.IsFalse(release.GetProperty("isPrerelease").GetBoolean());
            Assert.AreEqual(DateTimeOffset.Parse("2025-01-01T00:00:00Z"), release.GetProperty("publishedAt").GetDateTimeOffset());
            if (name == "release-v1.json")
            {
                Assert.AreEqual(1, release.GetProperty("assets").GetArrayLength());
                Assert.AreEqual("RA_opaque_legacy", release.GetProperty("assets")[0].GetProperty("id").GetString());
            }
        }

        string emptyPages = Path.Combine(metadata, "labels.pages.json");
        await File.WriteAllBytesAsync(emptyPages, "[[],[]]"u8.ToArray());
        Assert.AreEqual(0, (await ManifestStore.ReadPagesAsync(emptyPages, default))[1].GetArrayLength());
        foreach ((string path, (byte[] hash, DateTime written)) in copies)
        {
            CollectionAssert.AreEqual(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)), path);
            Assert.AreEqual(written, File.GetLastWriteTimeUtc(path), path);
        }
    }

    [TestMethod]
    public async Task Historical_deleted_and_unresolved_names_stay_reserved()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, "manifests"), root.User);
        await File.WriteAllBytesAsync(Path.Combine(owner, "manifests", "repositories-20260920-010203.json"),
            await File.ReadAllBytesAsync(Fixture("repositories-v1.json")));
        IReadOnlyList<RepositoryDescriptor> legacy = await new ManifestStore().ReadLatestAsync(owner, default);
        RepositoryDescriptor deleted = legacy[0] with { RepositoryId = 5 };
        RepositoryDescriptor unresolved = legacy[1];
        IReadOnlyList<RepositoryDescriptor> mapped = ManifestStore.Reconcile(owner, [deleted, unresolved],
            [deleted with { RepositoryId = 11 }, unresolved with { RepositoryId = 12 }], []);

        Assert.AreEqual("deleted", mapped.Single(x => x.RepositoryId == 5).RemoteState);
        Assert.AreEqual("legacy-unresolved", mapped.Single(x => x.RepositoryId == 0).RemoteState);
        Assert.AreEqual("alpha", mapped.Single(x => x.RepositoryId == 5).LocalName);
        Assert.AreEqual("beta", mapped.Single(x => x.RepositoryId == 0).LocalName);
        Assert.AreNotEqual("alpha", mapped.Single(x => x.RepositoryId == 11).LocalName, ignoreCase: true);
        Assert.AreNotEqual("beta", mapped.Single(x => x.RepositoryId == 12).LocalName, ignoreCase: true);
        Assert.HasCount(4, mapped);
    }

    [TestMethod]
    public async Task Corrupt_latest_receipt_fails_closed()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        string manifests = Path.Combine(owner, "manifests");
        AclPolicy.CreateRestrictedDirectory(manifests, root.User);
        await File.WriteAllBytesAsync(Path.Combine(manifests, "summary-20260920-010203.json"),
            await File.ReadAllBytesAsync(Fixture("summary-v1.json")));
        await File.WriteAllTextAsync(Path.Combine(manifests, "summary-20260920-020304.json"), "{");
        await File.WriteAllTextAsync(Path.Combine(manifests, "summary-20260920-030405.json"),
            "{\"schemaVersion\":999,\"status\":\"PASS\",\"owner\":\"sample-owner\"}");
        AppPaths paths = AppPaths.Create(root.Path);

        SummaryReadResult result = await new SummaryStore(paths).ReadLatestAsync(owner, paths.DiagnosticFallbackRoot, default);

        Assert.AreEqual("20260920-010203", result.Latest?.StartedRunId);
        Assert.AreEqual(RunStatus.Partial, result.Latest?.Status);
        Assert.AreEqual(RunStatus.Partial, result.LatestCoreSuccess?.Status);
        Assert.HasCount(2, result.Warnings);
        Assert.IsTrue(result.Warnings.All(x => x.Contains("ignored", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Native_backup_surface_remains_read_only()
    {
        const string owner = "fixture-user";
        const string repository = "fixture-repo";
        var requests = new List<(HttpMethod Method, string Uri, bool Authorized)>();
        using var handler = new NativeHandler(request =>
        {
            string uri = request.RequestUri!.AbsoluteUri;
            requests.Add((request.Method, uri, request.Headers.Authorization is not null));
            return request.RequestUri.PathAndQuery switch
            {
                "/user" => Ok("{\"login\":\"fixture-user\",\"id\":7}"),
                "/user/repos?affiliation=owner&per_page=100" => Ok("[{\"id\":11,\"name\":\"fixture-repo\",\"full_name\":\"fixture-user/fixture-repo\",\"html_url\":\"https://github.com/fixture-user/fixture-repo\",\"owner\":{\"login\":\"fixture-user\",\"id\":7},\"size\":1,\"private\":false,\"archived\":false,\"fork\":false,\"has_wiki\":true,\"updated_at\":\"2026-09-20T00:00:00Z\"}]"),
                "/repos/fixture-user/fixture-repo/issues?state=all&per_page=100" => Ok("[{\"id\":1}]"),
                "/repos/fixture-user/fixture-repo/releases?per_page=100" => Ok("[{\"id\":21,\"tag_name\":\"v1\",\"name\":\"Release\",\"draft\":false,\"prerelease\":false,\"published_at\":\"2026-09-20T00:00:00Z\",\"assets\":[{\"id\":42,\"name\":\"a.bin\",\"size\":3,\"digest\":null,\"updated_at\":\"2026-09-20T00:00:00Z\"}]}]"),
                "/repos/fixture-user/fixture-repo/releases/assets/42" => Redirect("https://objects.githubusercontent.com/fixture?sig=synthetic"),
                "/fixture?sig=synthetic" when request.RequestUri.Host == "objects.githubusercontent.com" => Ok("abc"),
                _ => throw new AssertFailedException("Unexpected native request: " + uri)
            };
        });
        using IGitHubHttpTransport transport = await GitHubHttpTransport.BindAsync(
            new GitHubCredentialLease(owner, "synthetic-token"u8.ToArray()), owner, ProxyProfile.Direct, default, _ => handler);
        Assert.AreEqual(owner, transport.BoundLogin);
        Assert.AreEqual(7L, transport.BoundAccountId);

        IReadOnlyList<RepositoryDescriptor> discovered = await new RepositoryDiscoveryService(transport).DiscoverAsync(owner, default);
        Assert.AreEqual(repository, discovered.Single().Name);
        using var root = new StorageTestRoot();
        string metadata = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        await new RawPageStore(metadata).SaveMetadataAsync(owner, repository, "issues.pages.json", transport, default);
        Assert.AreEqual("[[{\"id\":1}]]", await File.ReadAllTextAsync(Path.Combine(metadata, "issues.pages.json")));
        ReleaseInventory inventory = await new ReleaseAssetService(transport).ReadInventoryAsync(owner, repository, default);
        Assert.AreEqual(42L, inventory.Releases.Single().Assets.Single().Id);
        using GitHubResponse asset = await transport.DownloadAssetAsync(new AssetIdentity(owner, repository, 42), default);
        Assert.AreEqual("abc", await new StreamReader(asset.Body).ReadToEndAsync());

        CollectionAssert.AreEqual(new[]
        {
            "https://api.github.com/user",
            "https://api.github.com/user/repos?affiliation=owner&per_page=100",
            "https://api.github.com/repos/fixture-user/fixture-repo/issues?state=all&per_page=100",
            "https://api.github.com/repos/fixture-user/fixture-repo/releases?per_page=100",
            "https://api.github.com/repos/fixture-user/fixture-repo/releases/assets/42",
            "https://objects.githubusercontent.com/fixture?sig=synthetic"
        }, requests.Select(x => x.Uri).ToArray());
        Assert.IsTrue(requests.All(x => x.Method == HttpMethod.Get));
        Assert.IsTrue(requests.Take(5).All(x => x.Authorized));
        Assert.IsFalse(requests[^1].Authorized);
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class NativeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(reply(request));
        }
    }
}
