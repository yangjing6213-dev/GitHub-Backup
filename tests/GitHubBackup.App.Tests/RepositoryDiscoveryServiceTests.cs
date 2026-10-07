using System.Text;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class RepositoryDiscoveryServiceTests
{
    [TestMethod]
    public async Task Owned_repository_discovery_follows_validated_pages_and_keeps_stable_ids()
    {
        var transport = new FixtureTransport(page => page == 1
            ? ("[" + string.Join(',', Enumerable.Range(1, 100).Select(id => Item(id))) + "]", 2)
            : ("[" + Item(101) + "]", null));

        IReadOnlyList<RepositoryDescriptor> found = await new RepositoryDiscoveryService(transport)
            .DiscoverAsync("fixture-user", default);

        Assert.HasCount(101, found);
        Assert.AreEqual(101L, found[100].RepositoryId);
        Assert.AreEqual("fixture-user/repo101", found[100].NameWithOwner);
        Assert.AreEqual("https://github.com/fixture-user/repo101", found[100].Url);
        Assert.AreEqual(42L, found[100].DiskUsage);
        Assert.IsTrue(found[100].IsPrivate && found[100].IsArchived && found[100].IsFork && !found[100].HasWikiEnabled);
        CollectionAssert.AreEqual(new[] { 1, 2 }, transport.Pages);
    }

    [TestMethod]
    public async Task Explicit_organization_scope_accepts_org_owner_and_uses_org_endpoint()
    {
        Assert.IsTrue(RepositoryEndpointPolicy.ValidateAndCreateForScope("fixture-user", "acme", "repo", "acme/repo", "https://github.com/acme/repo").Allowed);
        var transport = new FixtureTransport(_ => ("[" + Item(1, 42, "acme", "repo") + "]", null));

        IReadOnlyList<RepositoryDescriptor> found = await new RepositoryDiscoveryService(transport)
            .DiscoverAsync("fixture-user", "acme", false, default);

        Assert.AreEqual("acme/repo", found.Single().NameWithOwner);
        Assert.AreEqual("/orgs/acme/repos", transport.Paths.Single());
    }

    [TestMethod]
    public async Task Explicit_collaborator_scope_accepts_a_different_owner()
    {
        Assert.IsTrue(RepositoryEndpointPolicy.ValidateAndCreateForScope("fixture-user", "partner", "repo", "partner/repo", "https://github.com/partner/repo").Allowed);
        var transport = new FixtureTransport(_ => ("[" + Item(1, 42, "partner", "repo") + "]", null));

        IReadOnlyList<RepositoryDescriptor> found = await new RepositoryDiscoveryService(transport)
            .DiscoverAsync("fixture-user", "", true, default);

        Assert.AreEqual("partner/repo", found.Single().NameWithOwner);
        Assert.AreEqual("/user/repos", transport.Paths.Single());
    }

    [TestMethod]
    [DataRow("{\"id\":1,\"name\":\"repo\",\"full_name\":\"other/repo\",\"html_url\":\"https://github.com/other/repo\",\"owner\":{\"login\":\"other\"},\"size\":1,\"private\":false,\"archived\":false,\"fork\":false,\"has_wiki\":true}")]
    [DataRow("{\"id\":1,\"name\":\"repo\",\"full_name\":\"fixture-user/repo\",\"html_url\":\"https://github.com/fixture-user/repo?token=x\",\"owner\":{\"login\":\"fixture-user\"},\"size\":1,\"private\":false,\"archived\":false,\"fork\":false,\"has_wiki\":true}")]
    [DataRow("{\"id\":1,\"name\":\"repo\",\"full_name\":\"fixture-user/repo\",\"html_url\":\"https://github.com/fixture-user/repo\",\"owner\":{\"login\":\"fixture-user\"},\"size\":-1,\"private\":false,\"archived\":false,\"fork\":false,\"has_wiki\":true}")]
    public async Task Invalid_identity_url_or_size_blocks_entire_discovery(string item)
    {
        var transport = new FixtureTransport(_ => ("[" + item + "]", null));
        var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            new RepositoryDiscoveryService(transport).DiscoverAsync("fixture-user", default));
        Assert.AreEqual("REPOSITORY_DISCOVERY_INVALID", error.Code);
    }

    [TestMethod]
    public async Task Nonforward_continuation_from_transport_is_rejected()
    {
        var transport = new FixtureTransport(_ => ("[]", 1));
        var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            new RepositoryDiscoveryService(transport).DiscoverAsync("fixture-user", default));
        Assert.AreEqual("HTTP_PAGINATION_REJECTED", error.Code);
    }

    [TestMethod]
    public async Task Skipped_page_from_transport_is_rejected_before_followup_request()
    {
        var transport = new FixtureTransport(_ => ("[]", 3));
        var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            new RepositoryDiscoveryService(transport).DiscoverAsync("fixture-user", default));
        Assert.AreEqual("HTTP_PAGINATION_REJECTED", error.Code);
        CollectionAssert.AreEqual(new[] { 1 }, transport.Pages);
    }

    [TestMethod]
    public async Task Repository_owner_numeric_identity_must_match_bound_account()
    {
        var transport = new FixtureTransport(_ => ("[" + Item(1, 8) + "]", null));
        Assert.AreEqual("REPOSITORY_DISCOVERY_INVALID", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            new RepositoryDiscoveryService(transport).DiscoverAsync("fixture-user", default))).Code);
    }

    [TestMethod]
    public async Task Discovery_page_over_frozen_item_count_is_rejected()
    {
        var transport = new FixtureTransport(_ => ("[" + string.Join(',', Enumerable.Range(1, 101).Select(id => Item(id))) + "]", null));
        Assert.AreEqual("REPOSITORY_DISCOVERY_INVALID", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            new RepositoryDiscoveryService(transport).DiscoverAsync("fixture-user", default))).Code);
    }

    [TestMethod]
    public async Task Oversized_discovery_page_is_rejected_before_parsing()
    {
        string payload = "[" + Item(1).TrimEnd('}') + ",\"description\":\"" + new string('x', 8 * 1024 * 1024) + "\"}]";
        var transport = new FixtureTransport(_ => (payload, null));
        Assert.AreEqual("REPOSITORY_DISCOVERY_INVALID", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            new RepositoryDiscoveryService(transport).DiscoverAsync("fixture-user", default))).Code);
    }

    [TestMethod]
    [DataRow(5L)]
    [DataRow(8L * 1024 * 1024 + 1)]
    public async Task Discovery_rejects_truncated_or_oversized_declared_content_length(long declaredLength)
    {
        var transport = new FixtureTransport(_ => ("[]", null), declaredLength);
        Assert.AreEqual("REPOSITORY_DISCOVERY_INVALID", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            new RepositoryDiscoveryService(transport).DiscoverAsync("fixture-user", default))).Code);
    }

    private static string Item(int id, long ownerId = 7, string owner = "fixture-user", string? name = null) => "{\"id\":" + id + ",\"name\":\"" + (name ?? "repo" + id)
        + "\",\"full_name\":\"" + owner + "/" + (name ?? "repo" + id)
        + "\",\"html_url\":\"https://github.com/" + owner + "/" + (name ?? "repo" + id)
        + "\",\"owner\":{\"login\":\"" + owner + "\",\"id\":" + ownerId + "},\"size\":42,\"private\":true,\"archived\":true,\"fork\":true,\"has_wiki\":false,\"updated_at\":\"2026-09-20T00:00:00Z\"}";

    private sealed class FixtureTransport(Func<int, (string Body, int? Next)> response, long? declaredLength = null) : IGitHubHttpTransport
    {
        internal int[] Pages => pages.ToArray();
        internal IReadOnlyList<string> Paths => paths;
        private readonly List<int> pages = [];
        private readonly List<string> paths = [];
        public string BoundLogin => "fixture-user";
        public long BoundAccountId => 7;
        public Task<GitHubResponse> SendAsync(GitHubRequest request, CancellationToken token)
        {
            pages.Add(request.Page);
            paths.Add(request.Path);
            var (body, next) = response(request.Page);
            return Task.FromResult(new GitHubResponse(200, declaredLength is null ? new Dictionary<string,string>()
                : new Dictionary<string,string> { ["Content-Length"] = declaredLength.Value.ToString() },
                new MemoryStream(Encoding.UTF8.GetBytes(body)), next));
        }
        public Task<GitHubResponse> DownloadAssetAsync(AssetIdentity asset, CancellationToken token) => throw new AssertFailedException();
        public void Dispose() { }
    }
}
