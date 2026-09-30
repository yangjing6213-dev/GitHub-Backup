using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class RepositoryEndpointPolicyTests
{
    [TestMethod]
    public void Validated_identity_constructs_clone_and_wiki_endpoints()
    {
        RepositoryEndpointValidation result = RepositoryEndpointPolicy.ValidateAndCreate(
            "sample-owner", "sample-owner", "repo", "sample-owner/repo", "https://github.com/sample-owner/repo");

        Assert.IsTrue(result.Allowed);
        Assert.AreEqual("https://github.com/sample-owner/repo.git", result.CanonicalCloneUrl);
        Assert.AreEqual("https://github.com/sample-owner/repo.wiki.git", result.CanonicalWikiUrl);
    }

    [TestMethod]
    public void Raw_or_historical_url_cannot_supply_clone_endpoint()
    {
        foreach (string raw in new[]
        {
            "https://evil.example/sample-owner/repo", "https://github.com/sample-owner/repo?x=1",
            "https://github.com/sample-owner/repo/extra", "https://github.com/sample-owner/repo%2Fother"
        })
            Assert.IsFalse(RepositoryEndpointPolicy.ValidateAndCreate("sample-owner", "sample-owner", "repo",
                "sample-owner/repo", raw).Allowed);
        Assert.IsFalse(RepositoryEndpointPolicy.ValidateAndCreate("sample-owner", "other", "repo",
            "other/repo", "https://github.com/other/repo").Allowed);
    }
}
