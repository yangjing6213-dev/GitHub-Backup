using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class RunCoordinatorTests
{
    [TestMethod]
    public async Task Real_child_process_tree_finishes_before_cleanup_and_operation_unlock()
    {
        using var tools = await TestToolBuilder.CreateAsync();
        using var job = OperationJob.Create();
        var coordinator = new RunCoordinator();
        string ready = Path.Combine(tools.Root, "coordinator.ready");
        var request = new ProcessRequest(tools.Executable, ["spawn-child", ready], tools.Root, tools.Environment,
            TimeSpan.FromSeconds(30), ExpectedExecutableIdentity: ExecutableTrust.CaptureTrustedIdentity(tools.Executable));
        bool cleaned = false;
        var run = coordinator.RunAsync(job, async token =>
        {
            var process = await new ProcessRunner().RunAsync(request, job, null, token);
            Assert.IsTrue(process.Cancelled); return Result();
        }, () =>
        {
            Assert.AreEqual(0, job.ActiveLeaseCount);
            Assert.IsTrue(File.ReadAllLines(ready).Select(int.Parse).All(id => !TestToolBuilder.ProcessExists(id)));
            Assert.IsTrue(coordinator.IsBusy); cleaned = true; return Task.CompletedTask;
        }, default);
        await TestToolBuilder.UntilAsync(() => File.Exists(ready));
        await coordinator.CancelAsync(); await run;
        Assert.IsTrue(cleaned); Assert.IsFalse(coordinator.IsBusy);
    }

    internal static BackupRunResult Result() => new(BackupSummary.PreflightFailure(BackupMode.Daily,
        "fixture-user", "test", @"C:\fixture", "test", "TEST"), false);

    [TestMethod]
    public async Task Cancel_waits_for_owned_work_and_cleanup_and_blocks_next_operation()
    {
        var coordinator = new RunCoordinator();
        using var job = OperationJob.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clean = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<BackupRunResult> run = coordinator.RunAsync(job, async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { stopped.SetResult(); }
            await release.Task;
            return Result();
        }, async () => { cleaning.SetResult(); await clean.Task; }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task cancel = coordinator.CancelAsync();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(coordinator.IsBusy); Assert.IsFalse(cancel.IsCompleted);
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.RunAsync(job, _ => Task.FromResult(Result()), () => Task.CompletedTask, default));
        release.SetResult(); await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(coordinator.IsBusy); Assert.IsFalse(cancel.IsCompleted);
        clean.SetResult(); await cancel; await run;
        Assert.IsFalse(coordinator.IsBusy);
    }

    [TestMethod]
    public async Task Failed_cleanup_keeps_exclusion_until_explicit_successful_retry()
    {
        var coordinator = new RunCoordinator(); using var job = OperationJob.Create();
        int attempts = 0; bool fail = true;
        var result = await coordinator.RunAsync(job, _ => Task.FromResult(Result()), () =>
        {
            attempts++; if (fail) throw new IOException("PRIVATE_RAW_EXCEPTION"); return Task.CompletedTask;
        }, default);
        Assert.IsTrue(result.HasPendingCleanup); Assert.IsTrue(coordinator.IsBusy);
        Assert.IsTrue(coordinator.HasPendingCleanup); Assert.AreEqual(1, attempts);
        Assert.DoesNotContain("PRIVATE", result.Summary.ErrorCode);
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.RunAsync(job, _ => Task.FromResult(Result()), () => Task.CompletedTask, default));
        await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.RetryCleanupAsync());
        Assert.AreEqual(2, attempts); Assert.IsTrue(coordinator.HasPendingCleanup);
        fail = false; await coordinator.RetryCleanupAsync();
        Assert.AreEqual(3, attempts); Assert.IsFalse(coordinator.IsBusy);
    }
}
