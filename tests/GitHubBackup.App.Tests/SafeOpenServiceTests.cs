using GitHubBackup.App;
using System.Text;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SafeOpenServiceTests
{
    [TestMethod]
    [DataRow("..\\outside.log")] [DataRow("https://example.test/a.log")]
    [DataRow("backup-run.txt")] [DataRow("outside")] [DataRow("")]
    public async Task Unapproved_summary_log_never_reaches_reader_or_process(string candidate)
    {
        using var f = new SafeViewFixture();
        string path = candidate == "outside" ? f.Root.Child("outside.log") : candidate;
        if (candidate == "backup-run.txt") path = Path.Combine(f.Logs, candidate);
        await Assert.ThrowsAsync<Exception>(() => f.Service.ReadLatestLogAsync(f.Owner, f.Summary with { Log = path }, default));
        Assert.HasCount(0, f.Runner.Requests);
    }

    [TestMethod]
    public async Task Private_log_streams_redacted_bounded_lines_internally()
    {
        using var f = new SafeViewFixture();
        await f.WriteLogAsync(string.Join('\n', Enumerable.Range(0, 2100).Select(i => "line-" + i)) +
            "\nAuthorization: Bearer SYNTHETIC_PRIVATE\n" + new string('x', 9000) + "\nhttps://user:secret@example.test/path?token=PRIVATE\n");
        var document = await f.Service.ReadLatestLogAsync(f.Owner, f.Summary, default);
        Assert.HasCount(2000, document.Lines); Assert.IsTrue(document.Lines.All(line => line.Length <= 8192));
        Assert.DoesNotContain("PRIVATE", string.Join('\n', document.Lines));
        Assert.DoesNotContain("user:secret", string.Join('\n', document.Lines));
        Assert.Contains("TRUNCATED", string.Join('\n', document.Lines)); Assert.HasCount(0, f.Runner.Requests);
    }

    [TestMethod]
    [DataRow("acl")] [DataRow("junction")] [DataRow("hardlink")]
    public async Task Unsafe_log_boundaries_fail_closed(string attack)
    {
        using var f = new SafeViewFixture(); await f.WriteLogAsync("private");
        if (attack == "acl") f.Root.SetNullDacl(f.Log);
        if (attack == "junction")
        {
            File.Delete(f.Log); Directory.Delete(f.Logs); string outside = f.Root.Child("outside");
            Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "backup-run.log"), "outside");
            StorageTestRoot.CreateJunction(f.Logs, outside);
        }
        if (attack == "hardlink") StorageTestRoot.CreateHardLink(f.Root.Child("alias.log"), f.Log);
        await Assert.ThrowsAsync<Exception>(() => f.Service.ReadLatestLogAsync(f.Owner, f.Summary, default));
        Assert.HasCount(0, f.Runner.Requests);
    }

    [TestMethod]
    public async Task Folder_launch_uses_one_canonical_argument_and_pinned_executable_identity()
    {
        using var f = new SafeViewFixture(); using var job = OperationJob.Create();
        f.Runner.Results.Enqueue(request =>
        {
            Assert.ThrowsAsync<IOException>(() => Task.Run(() => Directory.Move(f.Owner, f.Owner + "-moved"))).GetAwaiter().GetResult();
            return new(0, false, false, [], []);
        });
        Assert.IsTrue((await f.Service.OpenBackupFolderAsync(f.Owner + "\\", job, default)).Opened);
        var request = f.Runner.Requests.Single(); CollectionAssert.AreEqual(new[] { f.Owner }, request.Arguments.ToArray());
        Assert.AreEqual(request.ExpectedExecutableIdentity, f.Explorer.Identity);
    }

    [TestMethod]
    [DataRow("identity")] [DataRow("acl")] [DataRow("url")] [DataRow("missing")]
    public async Task Rejected_folder_or_changed_explorer_starts_zero_processes(string attack)
    {
        using var f = new SafeViewFixture(); using var job = OperationJob.Create();
        if (attack == "identity") File.AppendAllText(f.Explorer.AbsolutePath, "changed");
        if (attack == "acl") f.Root.SetNullDacl(f.Owner);
        string path = attack == "url" ? "https://example.test/" : attack == "missing" ? f.Root.Child("missing") : f.Owner;
        Assert.IsFalse((await f.Service.OpenBackupFolderAsync(path, job, default)).Opened);
        Assert.HasCount(0, f.Runner.Requests);
    }

    [TestMethod]
    public async Task Cancelled_log_read_and_folder_open_do_no_work()
    {
        using var f = new SafeViewFixture(); using var job = OperationJob.Create(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Service.ReadLatestLogAsync(f.Owner, f.Summary, cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Service.OpenBackupFolderAsync(f.Owner, job, cancelled.Token));
        Assert.HasCount(0, f.Runner.Requests);
    }

    [TestMethod]
    public async Task Log_read_pins_file_and_parent_during_streaming_and_honors_mid_read_cancellation()
    {
        using var f = new SafeViewFixture();
        using (var output = AclPolicy.CreateRestrictedFile(f.Log, f.Root.User)) output.SetLength(32 * 1024 * 1024);
        using var cancelled = new CancellationTokenSource();
        var reading = f.Service.ReadLatestLogAsync(f.Owner, f.Summary, cancelled.Token);
        Assert.IsFalse(reading.IsCompleted);
        Assert.Throws<IOException>(() => File.Move(f.Log, f.Root.Child("moved.log")));
        Assert.Throws<IOException>(() => Directory.Move(f.Logs, f.Root.Child("moved-logs")));
        cancelled.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => reading);
        Assert.HasCount(0, f.Runner.Requests);
    }

    [TestMethod]
    public async Task Absolute_traversal_and_outside_owner_are_rejected_before_reading()
    {
        using var f = new SafeViewFixture(); await f.WriteLogAsync("safe");
        string outside = f.Root.Child("backup-outside.log"); File.WriteAllText(outside, "owned external sentinel");
        using var inaccessible = new FileStream(outside, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        foreach (string path in new[] { outside, Path.Combine(f.Logs, "..", "logs", "backup-run.log") })
        {
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadLatestLogAsync(f.Owner, f.Summary with { Log = path }, default));
            Assert.AreEqual("UNAPPROVED_RUN_LOG_PATH", error.Message);
        }
        Assert.HasCount(0, f.Runner.Requests);
    }

    [TestMethod]
    public async Task Folder_acl_is_revalidated_after_explorer_resolution()
    {
        using var f = new SafeViewFixture(); using var job = OperationJob.Create();
        var service = new SafeOpenService(f.Runner, () => { f.Root.SetNullDacl(f.Owner); return f.Explorer; });
        Assert.IsFalse((await service.OpenBackupFolderAsync(f.Owner, job, default)).Opened);
        Assert.HasCount(0, f.Runner.Requests);
    }

    [TestMethod]
    public async Task Folder_reparse_rejected_without_launching()
    {
        using var f = new SafeViewFixture(); using var job = OperationJob.Create(); string link = f.Root.Child("folder-link");
        StorageTestRoot.CreateJunction(link, f.Owner);
        try { Assert.IsFalse((await f.Service.OpenBackupFolderAsync(link, job, default)).Opened); Assert.HasCount(0, f.Runner.Requests); }
        finally { Directory.Delete(link); }
    }
}

internal sealed class SafeViewFixture : IDisposable
{
    internal readonly StorageTestRoot Root = CreateRoot();
    internal readonly ScriptedProcessRunner Runner = new();
    internal string Owner { get; }
    internal string Logs => Path.Combine(Owner, "logs");
    internal string Log => Path.Combine(Logs, "backup-run.log");
    internal ToolDetection Explorer { get; }
    internal SafeOpenService Service { get; }
    internal BackupSummary Summary => RunCoordinatorTests.Result().Summary with { Log = Log, BackupRoot = Owner };
    internal SafeViewFixture()
    {
        Owner = Root.Child("owner"); AclPolicy.CreateRestrictedDirectory(Logs, Root.User);
        AclPolicy.CreateRestrictedDirectory(Root.Child("app"), Root.User);
        string executable = Root.Child("explorer.exe");
        using (var output = AclPolicy.CreateRestrictedFile(executable, Root.User)) output.Write("owned synthetic executable; never launched"u8);
        Explorer = new(executable, ExecutableTrust.CaptureTrustedIdentity(executable), "", true);
        Service = new(Runner, () => Explorer);
    }
    internal async Task WriteLogAsync(string text)
    { await using var file = AclPolicy.CreateRestrictedFile(Log, Root.User); await file.WriteAsync(Encoding.UTF8.GetBytes(text)); }
    private static StorageTestRoot CreateRoot()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "GitHubBackup-safeview-tests-" + Guid.NewGuid().ToString("N"));
        AclPolicy.CreateRestrictedDirectory(path, System.Security.Principal.WindowsIdentity.GetCurrent().User!, true);
        return new(path);
    }
    public void Dispose()
    {
        if (Directory.Exists(Logs) && File.GetAttributes(Logs).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(Logs);
        Root.Dispose();
    }
}
