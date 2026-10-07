using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class BackupCheckpointStoreTests
{
    [TestMethod]
    public async Task Paused_checkpoint_round_trips_without_credentials_or_content()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("fixture-user");
        AclPolicy.CreateRestrictedDirectory(owner, root.User);
        var checkpoint = new BackupCheckpoint(1, "fixture-user", "20261007T120000000Z-ABC", DateTimeOffset.UtcNow,
            true, 3, ["repo-one"], "repo-two", "metadata", DateTimeOffset.UtcNow.AddMinutes(5));

        var store = new BackupCheckpointStore();
        await store.WriteAsync(owner, checkpoint, default);
        BackupCheckpoint? found = await store.ReadAsync(owner, "fixture-user", default);

        Assert.IsNotNull(found);
        Assert.IsTrue(found.PausedByRateLimit);
        Assert.AreEqual("repo-two", found.CurrentRepository);
        CollectionAssert.AreEqual(new[] { "repo-one" }, found.CompletedRepositories.ToArray());
        string raw = await File.ReadAllTextAsync(Path.Combine(owner, "progress", "backup-progress.json"));
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content", raw, StringComparison.OrdinalIgnoreCase);
    }
}
