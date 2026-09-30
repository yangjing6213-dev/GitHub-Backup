using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class RedirectPolicyTests
{
    private const string AssetPath = "/repos/fixture-user/fixture-repo/releases/assets/42";
    private static readonly Uri Initial = new("https://api.github.com" + AssetPath);

    [TestMethod]
    public void Api_redirect_can_only_repeat_the_frozen_asset_path_without_query()
    {
        Assert.AreEqual(Initial, RedirectPolicy.ValidateNext(Initial, Initial, AssetPath, true));
        foreach (string value in new[]
        {
            "https://api.github.com/repos/fixture-user/fixture-repo/releases/assets/43",
            "https://api.github.com/repos/fixture-user/fixture-repo/releases/assets/42?x=1",
            "https://api.github.com/repos/fixture-user/fixture-repo/releases/assets/%34%32",
            "https://api.github.com/repos/fixture-user/fixture-repo/releases/assets/../42"
        })
            Assert.AreEqual("HTTP_REDIRECT_REJECTED", Assert.ThrowsExactly<HttpTransferException>(() =>
                RedirectPolicy.ValidateNext(Initial, new Uri(value), AssetPath, true)).Code);
    }

    [TestMethod]
    [DataRow("http://objects.githubusercontent.com/file")]
    [DataRow("https://objects.githubusercontent.com:444/file")]
    [DataRow("https://name@objects.githubusercontent.com/file")]
    [DataRow("https://objects.githubusercontent.com/file#fragment")]
    [DataRow("https://127.0.0.1/file")]
    [DataRow("https://[::1]/file")]
    [DataRow("https://objects.githubusercontent.com.evil.test/file")]
    [DataRow("https://githubusercontent.com/file")]
    [DataRow("https://evilgithub.com/file")]
    [DataRow("https://api.github.com/user")]
    public void Unapproved_redirect_target_is_rejected_without_echo(string value)
    {
        var error = Assert.ThrowsExactly<HttpTransferException>(() =>
            RedirectPolicy.ValidateNext(Initial, new Uri(value), AssetPath, true));
        Assert.AreEqual("HTTP_REDIRECT_REJECTED", error.Code);
        Assert.IsFalse(error.ToString().Contains(value, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Approved_cdn_hosts_are_exact_or_true_subdomains_and_return_to_api_is_forbidden()
    {
        var cdn = new Uri("https://objects.githubusercontent.com/download?sig=SYNTHETIC_SECRET");
        Assert.AreEqual(cdn, RedirectPolicy.ValidateNext(Initial, cdn, AssetPath, true));
        var apex = new Uri("https://github.com/release/download");
        Assert.AreEqual(apex, RedirectPolicy.ValidateNext(cdn, apex, AssetPath, false));
        Assert.AreEqual("HTTP_REDIRECT_REJECTED", Assert.ThrowsExactly<HttpTransferException>(() =>
            RedirectPolicy.ValidateNext(cdn, Initial, AssetPath, false)).Code);
    }
}
