using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class ModelsTests
{
    [TestMethod]
    public void Defaults_match_the_approved_spec()
    {
        AppSettings value = AppSettings.Default;
        Assert.AreEqual("yangjing6213-dev", value.Owner);
        Assert.AreEqual(@"D:\GitHub-Backups", value.BackupRoot);
        Assert.AreEqual(NetworkMode.Auto, value.NetworkMode);
    }

    [TestMethod]
    public void Wire_status_values_are_stable()
    {
        CollectionAssert.AreEqual(
            new[] { "PASS", "PARTIAL", "FAIL", "CANCELLED" },
            Enum.GetValues<RunStatus>().Select(x => x.ToString().ToUpperInvariant()).ToArray());
    }
}
