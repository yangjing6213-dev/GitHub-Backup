using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class StoragePathPolicyTests
{
    [TestMethod]
    [DataRow(@"\\server\share\backups", DriveType.Network, false)]
    [DataRow(@"Z:\backups", DriveType.Network, false)]
    [DataRow(@"C:\", DriveType.Fixed, false)]
    [DataRow(@"relative\backups", DriveType.Fixed, false)]
    [DataRow(@"C:\backups", DriveType.Fixed, true)]
    [DataRow(@"\\?\C:\backups", DriveType.Fixed, false)]
    [DataRow(@"E:\backups", DriveType.Removable, false)]
    public async Task Root_requires_nonroot_fixed_local_volume(string path, DriveType type, bool expected)
    {
        var probes = new StoragePathProbes(_ => type, _ => FileAttributes.Directory, (_, _) => Task.CompletedTask);
        Assert.AreEqual(expected, (await StoragePathPolicy.ValidateAsync(path, probes, CancellationToken.None)).Allowed);
    }

    [TestMethod]
    public async Task Reparse_ancestor_is_rejected_before_probe()
    {
        int calls = 0;
        var probes = new StoragePathProbes(_ => DriveType.Fixed,
            p => p == @"C:\Users\Test\OneDrive" ? FileAttributes.Directory | FileAttributes.ReparsePoint : FileAttributes.Directory,
            (_, _) => { calls++; return Task.CompletedTask; });
        var result = await StoragePathPolicy.ValidateAsync(@"C:\Users\Test\OneDrive\GitHub-Backups", probes, CancellationToken.None);
        Assert.AreEqual("STORAGE_REPARSE_POINT_REJECTED", result.ErrorCode);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task Real_probe_uses_owned_directory_and_cleans_it()
    {
        using var root = new StorageTestRoot();
        string sentinel = root.Child("sentinel");
        await File.WriteAllTextAsync(sentinel, "untouched");
        var result = await StoragePathPolicy.ValidateAsync(root.Child("future"), CancellationToken.None);
        Assert.IsTrue(result.Allowed, result.ErrorCode);
        Assert.AreEqual("untouched", await File.ReadAllTextAsync(sentinel));
        Assert.IsEmpty(Directory.GetDirectories(root.Path));
    }

    [TestMethod]
    public async Task Probe_failure_blocks_and_cancellation_propagates()
    {
        var probes = new StoragePathProbes(_ => DriveType.Fixed, _ => FileAttributes.Directory,
            (_, _) => throw new IOException("unsupported replacement"));
        Assert.AreEqual("STORAGE_ATOMIC_PROBE_FAILED", (await StoragePathPolicy.ValidateAsync(@"C:\backups", probes, CancellationToken.None)).ErrorCode);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => StoragePathPolicy.ValidateAsync(@"C:\backups", probes, cts.Token));
    }
}
