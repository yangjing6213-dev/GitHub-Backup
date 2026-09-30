using GitHubBackup.App;
using System.Security.AccessControl;
using System.Security.Principal;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class ToolingTests
{
    [TestMethod]
    public async Task Deadline_refresh_waits_for_original_detection_finally_and_observes_completion()
    {
        using var job = OperationJob.Create(); job.BeginCancellation();
        var detection = new DelayedDetectionCompletion();
        Task<ToolInventory> refresh = ToolInventoryRefresh.AfterActionAsync(detection.RunAsync, job, default);
        await detection.AssertStillOwnedThenReleaseAsync(refresh);
        await Assert.ThrowsAsync<OperationCanceledException>(() => refresh);
        Assert.IsTrue(detection.Running!.IsCompleted); Assert.AreEqual(1, detection.CompletionWrites);
        Assert.IsTrue(detection.Job!.IsCancellationRequested);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task Native_start_failure_is_typed_and_next_trusted_candidate_can_succeed(bool hasFallback)
    {
        using var fixture = await TestToolBuilder.CreateAsync(); using var job = OperationJob.Create();
        string fallback = Path.Combine(fixture.Root, "gh.exe");
        var runner = new ScriptedProcessRunner();
        runner.Results.Enqueue(_ => throw new System.ComponentModel.Win32Exception(193, "UNSAFE_NATIVE_ERROR_BODY"));
        runner.Results.Enqueue(_ => new(0, false, false, ["gh version 2.100.0"], []));
        var detector = new ToolDetector(runner, fixture.Environment,
            id => id != DependencyId.GitHubCli ? [] : hasFallback ? [fixture.Executable, fallback] : [fixture.Executable]);
        ToolInventory result = await detector.DetectAsync(job, default);
        if (hasFallback) Assert.AreEqual(fallback, result.GitHubCli?.AbsolutePath);
        else Assert.IsNull(result.GitHubCli);
        Assert.AreEqual("TOOL_VERSION_PROBE_FAILED", detector.Errors[DependencyId.GitHubCli]);
        Assert.HasCount(hasFallback ? 2 : 1, runner.Requests);
    }

    [TestMethod]
    [DataRow(DependencyId.Git, "git version 2.55.0.windows.2", false)]
    [DataRow(DependencyId.Git, "git version 2.55.0.windows.3", true)]
    [DataRow(DependencyId.Git, "git version 2.55.0.windows.4", true)]
    [DataRow(DependencyId.Git, "git version 2.56.0.windows.1", true)]
    [DataRow(DependencyId.Git, "git version 2.55.0", false)]
    [DataRow(DependencyId.GitHubCli, "gh version 2.99.9", false)]
    [DataRow(DependencyId.GitHubCli, "gh version 2.100.0 (2026-09-03)", true)]
    [DataRow(DependencyId.GitHubCli, "gh version 2.100.1", true)]
    [DataRow(DependencyId.GitLfs, "git-lfs/3.7.0", false)]
    [DataRow(DependencyId.GitLfs, "git-lfs/3.7.1 (GitHub; windows amd64; go)", true)]
    [DataRow(DependencyId.GitLfs, "git-lfs/3.7.2", true)]
    [DataRow(DependencyId.Winget, "v1.29.289", false)]
    [DataRow(DependencyId.Winget, "v1.29.290", true)]
    [DataRow(DependencyId.Winget, "v1.29.291", true)]
    [DataRow(DependencyId.GitHubCli, "gh version 2.100.0-rc1", false)]
    [DataRow(DependencyId.GitHubCli, "unexpected 2.100.0", false)]
    public void Version_floor_is_exact(object id, string output, bool expected) =>
        Assert.AreEqual(expected, VersionPolicy.IsSupported((DependencyId)id, output));

    [TestMethod]
    public async Task Trusted_detection_is_identity_bound_and_gh_config_is_empty_and_isolated()
    {
        using var fixture = await TestToolBuilder.CreateAsync();
        using var job = OperationJob.Create(); var runner = new ScriptedProcessRunner();
        runner.Results.Enqueue(request =>
        {
            Assert.AreEqual("false", request.Environment["GH_TELEMETRY"]);
            Assert.AreEqual("1", request.Environment["GH_NO_UPDATE_NOTIFIER"]);
            Assert.AreNotEqual(fixture.Root, request.WorkingDirectory);
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(request.Environment["GH_CONFIG_DIR"]!).Any());
            using var pin = SummaryStore.RequirePrivateDirectory(request.Environment["GH_CONFIG_DIR"]!);
            Assert.IsTrue(request.ExpectedExecutableIdentity == ExecutableTrust.CaptureTrustedIdentity(fixture.Executable));
            return new(0, false, false, ["gh version 2.100.0"], []);
        });
        var detector = new ToolDetector(runner, fixture.Environment,
            id => id == DependencyId.GitHubCli ? [fixture.Executable] : []);
        ToolInventory result = await detector.DetectAsync(job, default);
        Assert.IsTrue(result.GitHubCli?.IsSupported);
        Assert.HasCount(1, runner.Requests);
        Assert.IsFalse(Directory.Exists(runner.Requests[0].WorkingDirectory));
    }

    [TestMethod]
    public async Task Unexpected_probe_state_never_returns_a_detected_tool()
    {
        using var fixture = await TestToolBuilder.CreateAsync(); using var job = OperationJob.Create();
        var runner = new ScriptedProcessRunner();
        runner.Results.Enqueue(request => { File.WriteAllText(Path.Combine(request.WorkingDirectory, "unexpected"), "fixture"); return new(0,false,false,["gh version 2.100.0"],[]); });
        var detector = new ToolDetector(runner, fixture.Environment, id => id == DependencyId.GitHubCli ? [fixture.Executable] : []);
        var failure = await Assert.ThrowsAsync<GitRuntimeCleanupException>(() => detector.DetectAsync(job, default));
        string sentinel = Path.Combine(runner.Requests.Single().WorkingDirectory, "unexpected");
        File.Delete(sentinel); // Only the exact fixture-created sentinel; the context owns all other cleanup.
        await failure.Context.DisposeAsync();
    }

    [TestMethod]
    [DataRow("valid", true)] [DataRow("ambiguous", false)] [DataRow("unsafe", false)] [DataRow("manifest", false)]
    public async Task Supported_winget_package_alias_is_resolved_then_trusted(string variant, bool allowed)
    {
        using var fixture = await TestToolBuilder.CreateAsync();
        string installation = Path.Combine(fixture.Root, "package"); AclPolicy.CreateRestrictedDirectory(installation, WindowsIdentity.GetCurrent().User!);
        File.Copy(fixture.Executable, Path.Combine(installation, "winget.exe"));
        File.WriteAllText(Path.Combine(installation, "AppxManifest.xml"), """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10/5">
            <Identity Name="Microsoft.DesktopAppInstaller" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" Version="1.29.379.0" ProcessorArchitecture="x64"/>
            <Applications><Application Id="winget" Executable="winget.exe"><Extensions><uap:Extension Category="windows.appExecutionAlias"><uap:AppExecutionAlias><uap:ExecutionAlias Alias="winget.exe"/></uap:AppExecutionAlias></uap:Extension></Extensions></Application></Applications></Package>
            """.Replace("Alias=\"winget.exe\"", variant == "manifest" ? "Alias=\"evil.exe\"" : "Alias=\"winget.exe\""));
        if (variant == "unsafe") StorageTestRoot.Grant(installation, FileSystemRights.CreateFiles);
        var package = new RegisteredWingetPackage("Microsoft.DesktopAppInstaller_1.29.379.0_x64__8wekyb3d8bbwe", installation);
        var resolver = new WingetPackageResolver(() => variant == "ambiguous" ? [package, package] : [package]);
        if (allowed) Assert.AreEqual(Path.Combine(installation, "winget.exe"), resolver.Resolve());
        else Assert.ThrowsExactly<IOException>(() => resolver.Resolve());
    }

    [TestMethod]
    [DataRow("directory", "TOOL_PATH_WRITE_ACL_UNSAFE")]
    [DataRow("file", "TOOL_FILE_WRITE_ACL_UNSAFE")]
    [DataRow("junction", "TOOL_PATH_REPARSE_POINT_REJECTED")]
    [DataRow("current", "TOOL_PATH_LOCAL_CANDIDATE_REJECTED")]
    [DataRow("backup", "TOOL_PATH_LOCAL_CANDIDATE_REJECTED")]
    public async Task Unsafe_candidate_is_rejected_before_probe(string fault, string error)
    {
        using var fixture = await TestToolBuilder.CreateAsync();
        string installation = Path.Combine(fixture.Root, "tools");
        AclPolicy.CreateRestrictedDirectory(installation, WindowsIdentity.GetCurrent().User!);
        string executable = Path.Combine(installation, "gh.exe"); File.Copy(fixture.Executable, executable);
        if (fault == "directory") StorageTestRoot.Grant(installation, FileSystemRights.CreateFiles);
        if (fault == "file")
        {
            var info = new FileInfo(executable); var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), FileSystemRights.WriteData, AccessControlType.Allow)); info.SetAccessControl(acl);
        }
        string? junction = null;
        if (fault == "junction") { junction = Path.Combine(fixture.Root, "link"); StorageTestRoot.CreateJunction(junction, installation); executable = Path.Combine(junction, "gh.exe"); }
        try
        {
            using var job = OperationJob.Create(); var runner = new ScriptedProcessRunner();
            var policy = new ToolPathPolicy(fault == "current" ? installation : Environment.CurrentDirectory,
                fault == "backup" ? fixture.Root : @"D:\GitHub-Backups");
            var detector = new ToolDetector(runner, fixture.Environment, id => id == DependencyId.GitHubCli ? [executable] : [], policy);
            Assert.IsNull((await detector.DetectAsync(job, default)).GitHubCli);
            Assert.AreEqual(error, detector.Errors[DependencyId.GitHubCli]); Assert.HasCount(0, runner.Requests);
        }
        finally { if (junction is not null) Directory.Delete(junction); }
    }
}

// Models the managed part of the real runner/context finally after its process
// count reaches zero. No live process, user credential or external wait is used.
internal sealed class DelayedDetectionCompletion
{
    private readonly TaskCompletionSource finallyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource releaseFinally = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<ToolInventory>? Running;
    internal OperationJob? Job;
    internal int CompletionWrites;
    internal Task<ToolInventory> RunAsync(OperationJob job, CancellationToken token)
    {
        Job = job; return Running = CoreAsync(token);
    }
    private async Task<ToolInventory> CoreAsync(CancellationToken token)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return ToolInventory.Empty; }
        finally { finallyStarted.TrySetResult(); await releaseFinally.Task; CompletionWrites++; }
    }
    internal async Task AssertStillOwnedThenReleaseAsync(Task wrapper)
    {
        try
        {
            await finallyStarted.Task.WaitAsync(TimeSpan.FromSeconds(65));
            Task winner = await Task.WhenAny(wrapper, Task.Delay(150));
            Assert.AreNotSame(wrapper, winner, "The wrapper returned while the original detector still owned async cleanup.");
            Assert.IsTrue(Job!.IsCancellationRequested);
            Assert.AreEqual(0, CompletionWrites);
        }
        finally
        {
            releaseFinally.TrySetResult();
            if (Running is not null) try { await Running; } catch (OperationCanceledException) { }
            // Observe even a failing RED wrapper without leaving a task behind.
            try { await wrapper; } catch (OperationCanceledException) { }
        }
    }
}
