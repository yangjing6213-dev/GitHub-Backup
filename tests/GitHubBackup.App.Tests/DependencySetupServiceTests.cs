using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class DependencySetupServiceTests
{
    [TestMethod]
    public async Task Cancelled_install_deadline_retains_operation_until_detection_finally_finishes()
    {
        using var fixture = await TestToolBuilder.CreateAsync(); using var job = OperationJob.Create();
        var runner = new ScriptedProcessRunner(); runner.Results.Enqueue(_ => { job.BeginCancellation(); return new(null,false,true,[],[]); });
        var tool = new ToolDetection(fixture.Executable, ExecutableTrust.CaptureTrustedIdentity(fixture.Executable), "v1.29.290", true);
        var detection = new DelayedDetectionCompletion(); int calls = 0;
        var service = new DependencySetupService(runner, (current, token) => ++calls == 1
            ? Task.FromResult(new ToolInventory(null,null,null,tool)) : detection.RunAsync(current, token));
        Task<DependencyInstallResult> install = service.InstallAsync(DependencyId.Git, true, job, default);
        await detection.AssertStillOwnedThenReleaseAsync(install);
        var result = await install;
        Assert.AreEqual(DependencyInstallStatus.Cancelled, result.Status); Assert.AreEqual(ToolInventory.Empty, result.Inventory);
        Assert.IsTrue(detection.Running!.IsCompleted); Assert.AreEqual(1, detection.CompletionWrites);
        Assert.IsTrue(detection.Job!.IsCancellationRequested); Assert.HasCount(1, runner.Requests);
    }

    [TestMethod]
    [DataRow(false, "DEPENDENCY_DETECTION_FAILED")]
    [DataRow(true, "DEPENDENCY_REDETECTION_FAILED")]
    public async Task Native_detection_failure_returns_typed_unknown_inventory(bool afterInstall, string expectedCode)
    {
        using var fixture = await TestToolBuilder.CreateAsync(); using var job = OperationJob.Create();
        var runner = new ScriptedProcessRunner(); runner.Results.Enqueue(_ => new(0, false, false, [], []));
        var tool = new ToolDetection(fixture.Executable, ExecutableTrust.CaptureTrustedIdentity(fixture.Executable), "v1.29.290", true);
        int detections = 0;
        var service = new DependencySetupService(runner, (_, _) =>
        {
            if (!afterInstall || ++detections > 1) throw new System.ComponentModel.Win32Exception(193, "UNSAFE_NATIVE_ERROR_BODY");
            return Task.FromResult(new ToolInventory(null, null, null, tool));
        });
        var result = await service.InstallAsync(DependencyId.Git, true, job, default);
        Assert.AreEqual(DependencyInstallStatus.Failed, result.Status);
        Assert.AreEqual(expectedCode, result.ErrorCode); Assert.AreEqual(ToolInventory.Empty, result.Inventory);
        Assert.HasCount(afterInstall ? 1 : 0, runner.Requests);
    }

    [TestMethod]
    public async Task Unconfirmed_install_starts_no_detection_or_process()
    {
        using var job = OperationJob.Create(); var runner = new ScriptedProcessRunner();
        var service = new DependencySetupService(runner, (_, _) => throw new AssertFailedException("Detection requires confirmation."));
        var result = await service.InstallAsync(DependencyId.Git, false, job, default);
        Assert.AreEqual(DependencyInstallStatus.Cancelled, result.Status); Assert.HasCount(0, runner.Requests);
    }

    [TestMethod]
    public async Task Precancelled_confirmation_returns_typed_cancel_without_detection()
    {
        using var job = OperationJob.Create(); job.BeginCancellation(); var runner = new ScriptedProcessRunner();
        var service = new DependencySetupService(runner, (_, _) => throw new OperationCanceledException());
        Assert.AreEqual(DependencyInstallStatus.Cancelled, (await service.InstallAsync(DependencyId.Git, true, job, default)).Status);
        Assert.HasCount(0, runner.Requests);
    }

    [TestMethod]
    [DataRow(DependencyId.Git, "Git.Git")]
    [DataRow(DependencyId.GitHubCli, "GitHub.cli")]
    [DataRow(DependencyId.GitLfs, "GitHub.GitLFS")]
    public async Task Install_uses_allowlist_and_redetection_controls_success(object dependency, string package)
    {
        var id = (DependencyId)dependency;
        using var fixture = await TestToolBuilder.CreateAsync(); using var job = OperationJob.Create();
        var runner = new ScriptedProcessRunner(); runner.Results.Enqueue(_ => new(0, false, false, [], []));
        var tool = new ToolDetection(fixture.Executable, ExecutableTrust.CaptureTrustedIdentity(fixture.Executable), "v1.29.290", true);
        int detections = 0;
        var service = new DependencySetupService(runner, (_, _) => { detections++; return Task.FromResult(new ToolInventory(null, null, null, tool)); });
        var result = await service.InstallAsync(id, true, job, default);
        CollectionAssert.AreEqual(new[] { "install", "--id", package, "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements" }, runner.Requests.Single().Arguments.ToArray());
        Assert.AreEqual(tool.AbsolutePath, runner.Requests[0].FilePath); Assert.IsTrue(runner.Requests[0].ExpectedExecutableIdentity == tool.Identity);
        Assert.AreEqual(2, detections); Assert.AreEqual(DependencyInstallStatus.Failed, result.Status);
        Assert.AreEqual("DEPENDENCY_NOT_DETECTED", result.ErrorCode);
    }

    [TestMethod]
    public async Task Cancelled_installer_redetects_using_new_bounded_job_without_second_install()
    {
        using var fixture = await TestToolBuilder.CreateAsync(); using var job = OperationJob.Create(); using var cancellation = new CancellationTokenSource();
        var runner = new ScriptedProcessRunner();
        runner.Results.Enqueue(_ => { job.BeginCancellation(); cancellation.Cancel(); return new(null,false,true,[],[]); });
        var tool = new ToolDetection(fixture.Executable, ExecutableTrust.CaptureTrustedIdentity(fixture.Executable), "v1.29.290", true);
        OperationJob? cleanup = null; CancellationToken cleanupToken = default; int detections = 0;
        var service = new DependencySetupService(runner, (current, token) =>
        {
            detections++;
            if (detections == 2) { cleanup = current; cleanupToken = token; Assert.AreNotSame(job, current); Assert.IsTrue(job.IsCancellationRequested); Assert.IsFalse(current.IsCancellationRequested); Assert.IsTrue(token.CanBeCanceled); Assert.IsFalse(token.IsCancellationRequested); }
            return Task.FromResult(new ToolInventory(detections == 2 ? tool : null, null, null, tool));
        });
        var result = await service.InstallAsync(DependencyId.Git, true, job, cancellation.Token);
        Assert.AreEqual(DependencyInstallStatus.Cancelled, result.Status); Assert.IsNotNull(result.Inventory.Git);
        Assert.HasCount(1, runner.Requests); Assert.AreEqual(2, detections); Assert.IsTrue(cleanup!.IsCancellationRequested);
    }

    [TestMethod]
    public async Task Cancelled_install_redetection_deadline_returns_unknown_inventory_and_drains_job()
    {
        using var fixture = await TestToolBuilder.CreateAsync(); using var job = OperationJob.Create();
        var runner = new ScriptedProcessRunner(); runner.Results.Enqueue(_ => { job.BeginCancellation(); return new(null,false,true,[],[]); });
        var tool = new ToolDetection(fixture.Executable, ExecutableTrust.CaptureTrustedIdentity(fixture.Executable), "v1.29.290", true);
        int calls = 0; OperationJob? cleanup = null;
        var service = new DependencySetupService(runner, async (current, token) =>
        {
            if (++calls == 1) return new ToolInventory(null,null,null,tool);
            cleanup = current; await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new AssertFailedException("Deadline did not cancel.");
        });
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.InstallAsync(DependencyId.Git, true, job, default).WaitAsync(TimeSpan.FromSeconds(70));
        Assert.AreEqual(DependencyInstallStatus.Cancelled, result.Status); Assert.AreEqual(ToolInventory.Empty, result.Inventory);
        Assert.AreEqual("DEPENDENCY_REDETECTION_FAILED", result.ErrorCode); Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(68));
        Assert.IsTrue(cleanup!.IsCancellationRequested); Assert.HasCount(1, runner.Requests);
    }
}
