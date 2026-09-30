using System.Text.RegularExpressions;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class AtomicFileTests
{
    [TestMethod]
    public async Task Writer_failure_preserves_old_bytes_and_removes_owned_temp()
    {
        using var root = new StorageTestRoot();
        string path = root.Child("target");
        await File.WriteAllTextAsync(path, "old");
        await Assert.ThrowsExactlyAsync<IOException>(() => AtomicFile.WriteAsync(path, async (stream, token) =>
        {
            await stream.WriteAsync("new"u8.ToArray(), token);
            throw new IOException("injected");
        }, CancellationToken.None));
        Assert.AreEqual("old", await File.ReadAllTextAsync(path));
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(root.Path));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Cancellation_has_one_commit_boundary(bool existing, bool afterBoundary)
    {
        using var root = new StorageTestRoot();
        string path = root.Child("target");
        if (existing) await File.WriteAllTextAsync(path, "old");
        using var cts = new CancellationTokenSource();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Pause() { reached.SetResult(); await resume.Task; }
        var hooks = new AtomicFileCommitHooks(afterBoundary ? null : Pause, afterBoundary ? Pause : null);
        Task write = AtomicFile.WriteAsync(path, (stream, token) => stream.WriteAsync("new"u8.ToArray(), token).AsTask(), cts.Token, hooks);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        resume.SetResult();
        if (afterBoundary) await write;
        else await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => write);
        Assert.AreEqual(afterBoundary ? "new" : existing ? "old" : null, File.Exists(path) ? await File.ReadAllTextAsync(path) : null);
        Assert.HasCount(afterBoundary || existing ? 1 : 0, Directory.GetFiles(root.Path));
    }

    [TestMethod]
    public async Task Failed_filesystem_commit_preserves_old_target()
    {
        using var root = new StorageTestRoot();
        string path = root.Child("target");
        await File.WriteAllTextAsync(path, "old");
        using FileStream locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<IOException>(() => AtomicFile.WriteAsync(path, (stream, token) => stream.WriteAsync("new"u8.ToArray(), token).AsTask(), CancellationToken.None));
        Assert.AreEqual("old", await File.ReadAllTextAsync(path));
        Assert.HasCount(1, Directory.GetFiles(root.Path));
    }

    [TestMethod]
    public void Run_ids_are_UTC_and_unique_at_a_fixed_millisecond()
    {
        var clock = new FixedTime();
        string[] ids = Enumerable.Range(0, 10000).Select(_ => RunIdFactory.Create(clock)).ToArray();
        Assert.AreEqual(10000, ids.Distinct().Count());
        Assert.IsTrue(ids.All(x => Regex.IsMatch(x, @"^20260923T010203004Z-[0-9A-F]{16}$")));
    }
    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 23, 3, 2, 3, 4, TimeSpan.FromHours(2));
    }

    [TestMethod]
    public void Cleanup_refuses_another_files_identity()
    {
        using var root = new StorageTestRoot();
        string owned = root.Child("owned");
        string other = root.Child("other");
        using FileStream first = AclPolicy.CreateRestrictedFile(owned, root.User);
        var identity = NativeFileSystem.Inspect(first.SafeFileHandle, owned, false);
        File.WriteAllText(other, "preserved");
        Assert.ThrowsExactly<PathBoundaryException>(() => AtomicFile.DeleteOwnedFile(other, identity));
        Assert.AreEqual("preserved", File.ReadAllText(other));
    }
}
