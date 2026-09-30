using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class DiskSpacePolicyTests
{
    [TestMethod]
    public void Full_requirement_uses_sequential_peak_changed_bytes_and_reserve()
    {
        const long gib = 1L << 30;
        var repositories = new[] {
            Repo(7, 20L * gib / 1024), Repo(8, 1L * gib / 1024)
        };
        DiskSpaceRequirement requirement = DiskSpacePolicy.Calculate(BackupMode.Full, repositories,
            new Dictionary<long, long> { [7] = 20L * gib, [8] = 3L * gib }, 100);

        Assert.AreEqual(41L * gib + 100, requirement.RequiredFreeBytes);
        Assert.IsFalse(DiskSpacePolicy.HasEnoughFreeSpace(requirement.RequiredFreeBytes - 1, requirement));
        Assert.IsTrue(DiskSpacePolicy.HasEnoughFreeSpace(requirement.RequiredFreeBytes, requirement));
        Assert.AreEqual(41L * gib + 100, DiskSpacePolicy.RequiredForRepository(requirement, 7, 100));
        Assert.AreEqual(gib + 100, DiskSpacePolicy.RequiredForAssetBatch(100));
    }

    [TestMethod]
    public void Empty_daily_account_still_requires_reserve_and_overflow_rejects()
    {
        Assert.AreEqual(1L << 30, DiskSpacePolicy.Calculate(BackupMode.Daily, [], new Dictionary<long, long>(), 0).RequiredFreeBytes);
        Assert.ThrowsExactly<OverflowException>(() => DiskSpacePolicy.Calculate(BackupMode.Full,
            [Repo(7, long.MaxValue)], new Dictionary<long, long> { [7] = 1 }, 0));
    }

    [TestMethod]
    public void Mirror_measurement_counts_nested_lfs_and_rejects_reparse()
    {
        using var root = new StorageTestRoot();
        string mirror = root.Child("mirror.git");
        Directory.CreateDirectory(Path.Combine(mirror, "lfs", "objects"));
        File.WriteAllBytes(Path.Combine(mirror, "HEAD"), [1, 2]);
        File.WriteAllBytes(Path.Combine(mirror, "lfs", "objects", "obj"), [1, 2, 3]);
        Assert.AreEqual(5, DiskSpacePolicy.MeasureExistingMirror(mirror));
        string junction = Path.Combine(mirror, "lfs", "objects", "bad");
        StorageTestRoot.CreateJunction(junction, root.Path);
        try
        {
            Assert.AreEqual("BACKUP_MIRROR_SIZE_REPARSE_REJECTED",
                Assert.ThrowsExactly<PathSafetyException>(() => DiskSpacePolicy.MeasureExistingMirror(mirror)).ErrorCode);
        }
        finally { Directory.Delete(junction); }
    }

    private static RepositoryDescriptor Repo(long id, long usageKiB) => new(id, "repo", "fixture-user/repo",
        "https://github.com/fixture-user/repo", true, false, false, false, null, usageKiB, "repo", "current");
}
