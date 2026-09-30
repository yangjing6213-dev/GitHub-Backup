using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class DesktopWorkflowTests
{
    [TestMethod]
    public async Task Diagnostic_workflow_previews_redacted_sources_and_requires_same_settings_and_confirmation()
    {
        using var f = new SafeViewFixture(); await f.WriteLogAsync("Authorization: Bearer SYNTHETIC_PRIVATE\nsafe");
        var actions = new DesktopWorkflow(AppPaths.Create(f.Root.Child("app")), f.Runner).Actions;
        var settings = new AppSettings("owner", f.Root.Path, NetworkMode.Auto);
        string target = f.Root.Child("export.txt");
        var preview = await actions.PreviewDiagnostics(settings, default);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", string.Join('\n', preview.DisplayLines));
        Assert.AreEqual(DiagnosticSaveStatus.Stale, await actions.SaveDiagnostics(settings, "unissued", target, true, default));
        Assert.IsFalse(File.Exists(target));
        Assert.AreEqual(DiagnosticSaveStatus.Cancelled, await actions.SaveDiagnostics(settings, preview.PreviewId, target, false, default));
        Assert.AreEqual(DiagnosticSaveStatus.Stale, await actions.SaveDiagnostics(settings, preview.PreviewId, target, true, default));
        preview = await actions.PreviewDiagnostics(settings, default);
        Assert.AreEqual(DiagnosticSaveStatus.Stale, await actions.SaveDiagnostics(settings with { Owner = "another" }, preview.PreviewId, target, true, default));
        Assert.IsFalse(File.Exists(target));
        preview = await actions.PreviewDiagnostics(settings, default);
        Assert.AreEqual(DiagnosticSaveStatus.Saved, await actions.SaveDiagnostics(settings, preview.PreviewId, target, true, default));
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", await File.ReadAllTextAsync(target));
        Assert.HasCount(0, f.Runner.Requests);
    }

    [TestMethod]
    [DataRow("source")] [DataRow("preview")] [DataRow("cancel")]
    public async Task Diagnostic_workflow_stale_or_cancelled_preview_preserves_target(string change)
    {
        using var f = new SafeViewFixture(); await f.WriteLogAsync("safe"); var paths = AppPaths.Create(f.Root.Child("app"));
        var actions = new DesktopWorkflow(paths, f.Runner).Actions; var settings = new AppSettings("owner", f.Root.Path, NetworkMode.Auto);
        var preview = await actions.PreviewDiagnostics(settings, default); string target = f.Root.Child("export.txt"); File.WriteAllText(target, "preserve");
        if (change == "source") File.AppendAllText(f.Log, "changed");
        if (change == "preview") File.AppendAllText(Directory.GetFiles(paths.DiagnosticLogRoot, "preview-*.txt").Single(), "tampered");
        if (change == "cancel")
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => actions.SaveDiagnostics(settings, preview.PreviewId, target, true, cancelled.Token));
            Assert.AreEqual(DiagnosticSaveStatus.Stale, await actions.SaveDiagnostics(settings, preview.PreviewId, target, true, default));
        }
        else Assert.AreEqual(DiagnosticSaveStatus.Stale, await actions.SaveDiagnostics(settings, preview.PreviewId, target, true, default));
        Assert.AreEqual("preserve", File.ReadAllText(target)); Assert.IsFalse(actions.HasPendingCleanup());
    }

    [TestMethod]
    public async Task Diagnostic_atomic_failure_retains_exact_owned_cleanup_in_shared_slot()
    {
        using var f = new SafeViewFixture(); string owned = f.Root.Child("atomic.tmp"); NativeFileIdentity identity;
        using (var file = AclPolicy.CreateRestrictedFile(owned, f.Root.User)) identity = NativeFileSystem.Inspect(file.SafeFileHandle, owned, false);
        using var pinned = NativeFileSystem.Open(owned); var coordinator = new RunCoordinator();
        await Assert.ThrowsAsync<AtomicFileCleanupException>(() => DesktopWorkflow.RunViewAsync<int>(coordinator, (_, _) => throw new AtomicFileCleanupException(owned, identity), default));
        Assert.IsTrue(coordinator.HasPendingCleanup);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopWorkflow.RunViewAsync(coordinator, (_, _) => Task.FromResult(1), default));
        await Assert.ThrowsAsync<IOException>(() => coordinator.RetryCleanupAsync()); Assert.IsTrue(File.Exists(owned));
        pinned.Dispose(); await coordinator.RetryCleanupAsync(); Assert.IsFalse(File.Exists(owned)); Assert.IsFalse(coordinator.IsBusy);
    }

    [TestMethod]
    public async Task Successful_view_is_not_released_when_job_cleanup_fails()
    {
        var coordinator = new RunCoordinator(); RequestJobLease? request = null;
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => DesktopWorkflow.RunViewAsync(coordinator, (job, _) =>
            {
                request = job.CreateRequestJob();
                // Fault only this fixture's owned SafeHandle; never close a recyclable numeric handle behind its owner.
                var handle = (SafeJobHandle)typeof(RequestJobLease).GetField("handle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(request)!;
                handle.Dispose();
                return Task.FromResult(new DiagnosticDisplayPreview("must-not-reach-dialog", ["safe"]));
            }, default));
            Assert.AreEqual("BACKUP_CLEANUP_PENDING", error.Message); Assert.IsTrue(coordinator.HasPendingCleanup);
            await Assert.ThrowsAsync<IOException>(() => coordinator.RetryCleanupAsync());
        }
        finally { request?.Dispose(); await coordinator.RetryCleanupAsync(); }
        Assert.IsFalse(coordinator.IsBusy);
    }

    [TestMethod]
    public async Task Fallback_receipt_without_owner_log_does_not_turn_into_a_file_read()
    {
        using var f = new SafeViewFixture(); var paths = AppPaths.Create(f.Root.Child("app"));
        await new SummaryStore(paths).WriteFallbackAsync(f.Summary with { Owner = "owner", Log = "https://example.test/private" }, default);
        var actions = new DesktopWorkflow(paths, f.Runner).Actions;
        await Assert.ThrowsAsync<IOException>(() => actions.ReadLatestLog(new("owner", f.Root.Path, NetworkMode.Auto), default));
        Assert.HasCount(0, f.Runner.Requests);
    }

    [TestMethod]
    [DataRow("rejected", "Fail")]
    [DataRow("exception", "Fail")]
    [DataRow("cancelled", "Cancelled")]
    public async Task Pre_run_failure_writes_durable_fallback_without_replacing_prior_core_success(string failure, string expected)
    {
        using var f = new PreflightFixture(); var store = new SummaryStore(f.Paths);
        var old = RunCoordinatorTests.Result().Summary with { StartedRunId = "prior", Status = RunStatus.Pass,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1), CompletedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        await store.WriteFallbackAsync(old, default);
        var orchestrator = new BackupOrchestrator(f.Runner, f.Paths);
        var result = await orchestrator.RunWithPreflightAsync(BackupMode.Daily,
            new("fixture-user", Path.Combine(f.Root.Path, "unavailable"), NetworkMode.Auto), (_, _) => failure switch
            {
                "exception" => throw new IOException("PRIVATE_RAW_EXCEPTION"),
                "cancelled" => throw new OperationCanceledException(),
                _ => Task.FromResult(RejectedPreflight())
            }, () => Task.CompletedTask, null, default);
        var history = await store.ReadLatestAsync(f.OwnerRoot, f.Paths.DiagnosticFallbackRoot, default, "fixture-user");
        Assert.AreEqual(expected, result.Summary.Status.ToString()); Assert.AreEqual(expected, history.Latest!.Status.ToString());
        Assert.AreEqual(result.Summary.StartedRunId, history.Latest.StartedRunId);
        Assert.AreNotEqual("prior", history.Latest.StartedRunId);
        Assert.AreEqual("prior", history.LatestCoreSuccess!.StartedRunId);
        Assert.DoesNotContain("PRIVATE", history.Latest.ErrorCode); Assert.IsFalse(orchestrator.IsBusy);
    }

    private static PreflightCheckResult RejectedPreflight() => new(new(
        UiFixture.Ready with { StorageReady = false, Issues = [new("STORAGE_FIXED_VOLUME_REQUIRED", "ignored", true)] },
        ToolInventory.Empty, ProxyProfile.Direct, ProxyProfile.Direct.Environment, null, [], []), null);

    [TestMethod]
    public async Task Rejected_run_retains_atomic_receipt_cleanup_until_explicit_retry()
    {
        using var f = new PreflightFixture(); Microsoft.Win32.SafeHandles.SafeFileHandle? pinned = null;
        bool fail = true; string? temporary = null;
        var hooks = new AtomicFileCommitHooks(AfterFlush: () =>
        {
            if (!fail) return Task.CompletedTask;
            temporary = Directory.GetFiles(f.Paths.DiagnosticFallbackRoot, ".atomic-*.tmp").Single();
            pinned = NativeFileSystem.Open(temporary); throw new IOException("INJECTED_FLUSH_FAILURE");
        });
        var orchestrator = new BackupOrchestrator(f.Runner, f.Paths, summaryCommitHooks: hooks);
        var settings = new AppSettings("fixture-user", f.BackupRoot, NetworkMode.Auto);
        try
        {
            var result = await orchestrator.RunWithPreflightAsync(BackupMode.Daily, settings,
                (_, _) => Task.FromResult(RejectedPreflight()), () => Task.CompletedTask, null, default);
            Assert.IsTrue(result.HasPendingCleanup); Assert.IsTrue(orchestrator.IsBusy); Assert.IsTrue(File.Exists(temporary));
            Assert.ThrowsExactly<InvalidOperationException>(() => orchestrator.RunWithPreflightAsync(BackupMode.Daily, settings,
                (_, _) => throw new AssertFailedException("must not restart network"), () => Task.CompletedTask, null, default));
            await Assert.ThrowsExactlyAsync<IOException>(orchestrator.RetryCleanupAsync);
            Assert.IsTrue(orchestrator.IsBusy);
            Assert.IsNotNull(pinned); pinned.Dispose(); pinned = null; fail = false;
            await orchestrator.RetryCleanupAsync();
            Assert.IsFalse(File.Exists(temporary)); Assert.IsFalse(orchestrator.IsBusy);
            var history = await new SummaryStore(f.Paths).ReadLatestAsync(f.OwnerRoot, f.Paths.DiagnosticFallbackRoot, default);
            Assert.AreEqual(RunStatus.Fail, history.Latest!.Status); Assert.AreEqual(result.Summary.StartedRunId, history.Latest.StartedRunId);
        }
        finally { pinned?.Dispose(); fail = false; await orchestrator.RetryCleanupAsync(); }
    }

    [TestMethod]
    public async Task Failed_preflight_cleanup_still_records_failure_while_slot_remains_owned()
    {
        using var f = new PreflightFixture(); bool fail = true;
        var orchestrator = new BackupOrchestrator(f.Runner, f.Paths);
        try
        {
            var result = await orchestrator.RunWithPreflightAsync(BackupMode.Daily,
                new("fixture-user", f.BackupRoot, NetworkMode.Auto), (_, _) => Task.FromResult(RejectedPreflight()),
                () => { if (fail) throw new IOException("private cleanup failure"); return Task.CompletedTask; }, null, default);
            Assert.IsTrue(result.HasPendingCleanup); Assert.IsTrue(orchestrator.IsBusy);
            var history = await new SummaryStore(f.Paths).ReadLatestAsync(f.OwnerRoot, f.Paths.DiagnosticFallbackRoot, default);
            Assert.IsNotNull(history.Latest); Assert.AreEqual(RunStatus.Fail, history.Latest.Status);
            Assert.AreEqual("BACKUP_CLEANUP_PENDING", history.Latest.ErrorCode);
        }
        finally { fail = false; await orchestrator.RetryCleanupAsync(); }
    }

    [TestMethod]
    public async Task History_filters_shared_fallback_to_selected_account()
    {
        using var f = new PreflightFixture(); var store = new SummaryStore(f.Paths);
        var first = RunCoordinatorTests.Result().Summary with { StartedRunId = "first", CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-2) };
        var second = first with { Owner = "another-user", StartedRunId = "second", CompletedAt = DateTimeOffset.UtcNow };
        await store.WriteFallbackAsync(first, default); await store.WriteFallbackAsync(second, default);
        var history = await store.ReadLatestAsync(f.OwnerRoot, f.Paths.DiagnosticFallbackRoot, default, "fixture-user");
        Assert.AreEqual("fixture-user", history.Latest!.Owner);
    }

    [TestMethod]
    public async Task Standalone_check_releases_session_before_returning_to_idle()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        var coordinator = new RunCoordinator(); PreflightSnapshot? shown = null;
        await DesktopWorkflow.CheckAndReleaseAsync(coordinator, (_, _) => Task.FromResult(new PreflightCheckResult(f.Session.Snapshot, f.Session)),
            () => Task.CompletedTask, value => shown = value, default);
        Assert.IsNotNull(shown); Assert.IsTrue(shown.Report.CanStartBackup);
        Assert.IsFalse(coordinator.IsBusy);
        Assert.ThrowsExactly<ObjectDisposedException>(f.Session.Revalidate);
    }

    [TestMethod]
    public async Task Recheck_and_backup_share_slot_and_cleanup_failure_retains_session_and_owner()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        var coordinator = new RunCoordinator();
        f.Orchestrator = new(f.Git, f.Local.Paths, _ => long.MaxValue, f.Local.Audit, coordinator);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Http.FailDispose = true;
        var run = f.Orchestrator.RunWithPreflightAsync(BackupMode.Daily, f.Request(BackupMode.Daily).Settings,
            async (_, _) => { entered.SetResult(); await release.Task; return new(f.Session.Snapshot, f.Session); },
            () => Task.CompletedTask, null, default);
        await entered.Task;
        Assert.IsTrue(coordinator.IsBusy);
        Assert.ThrowsExactly<InvalidOperationException>(() => f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), default));
        release.SetResult(); var result = await run;
        Assert.IsTrue(result.HasPendingCleanup); Assert.IsTrue(coordinator.IsBusy);
        Assert.Throws<IOException>(() => OperationLocks.AcquireStorage(f.Local.OwnerRoot, "other"));
        f.Http.FailDispose = false; await coordinator.RetryCleanupAsync();
        Assert.IsFalse(coordinator.IsBusy); Assert.ThrowsExactly<ObjectDisposedException>(f.Session.Revalidate);
        using var available = OperationLocks.AcquireStorage(f.Local.OwnerRoot, "other");
    }

    [TestMethod]
    public async Task Cancellation_during_preflight_waits_for_cleanup_and_prevents_backup()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = f.Orchestrator.RunWithPreflightAsync(BackupMode.Daily, f.Request(BackupMode.Daily).Settings,
            async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new AssertFailedException(); },
            async () => { cleaning.SetResult(); await release.Task; }, null, default);
        await entered.Task; var cancel = f.Orchestrator.CancelAsync(); await cleaning.Task;
        Assert.IsFalse(cancel.IsCompleted); Assert.IsTrue(f.Orchestrator.IsBusy);
        Assert.HasCount(0, f.Git.Requests);
        release.SetResult();
        Assert.AreEqual(RunStatus.Cancelled, (await run).Summary.Status);
        await cancel;
        var history = await new SummaryStore(f.Local.Paths).ReadLatestAsync(f.Local.OwnerRoot, f.Local.Paths.DiagnosticFallbackRoot, default);
        Assert.AreEqual(RunStatus.Cancelled, history.Latest!.Status);
        Assert.IsFalse(f.Orchestrator.IsBusy);
    }

    [TestMethod]
    public async Task Progress_reports_real_repository_ordinals_for_each_stage()
    {
        await using var f = await OrchestrationFixture.CreateAsync(repositoryNames: ["first", "second"]);
        var values = new List<BackupProgress>();
        var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), default, new CollectedProgress(values));
        Assert.AreEqual(RunStatus.Pass, result.Summary.Status);
        CollectionAssert.AreEqual(new[] { "first:1/2", "second:2/2" }, values.Where(p => p.Phase == "mirror").Select(p => $"{p.Repository}:{p.Current}/{p.Total}").ToArray());
        Assert.IsTrue(values.Any(p => p.Phase == "metadata"));
        Assert.IsTrue(values.Any(p => p.LogTail?.Any(line => line.Contains("BACKUP_STARTED")) == true));
        Assert.IsTrue(values.Any(p => p.LogTail?.Any(line => line.Contains("PASS")) == true));
        Assert.IsTrue(values.All(p => p.LogTail is null || p.LogTail.Count <= 2000 && p.LogTail.All(line => line.Length <= 8192)));
        Assert.IsTrue(f.Git.Requests.Any(request => request.FilePath.EndsWith("git-lfs.exe", StringComparison.OrdinalIgnoreCase)
            && request.Arguments.Contains("fetch")), "Daily also fetches LFS; only Release attachments are Full-only.");
    }

    private sealed class CollectedProgress(List<BackupProgress> values) : IProgress<BackupProgress>
    { public void Report(BackupProgress value) => values.Add(value); }
}
