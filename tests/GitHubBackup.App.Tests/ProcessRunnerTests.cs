using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProcessRunnerTests
{
    private static TestToolBuilder tools = null!;
    [ClassInitialize] public static async Task Initialize(TestContext _) => tools = await TestToolBuilder.CreateAsync();
    [ClassCleanup] public static void Cleanup() => tools?.Dispose();
    private ProcessRequest Request(string[] args, TimeSpan? timeout = null, ProcessOutputMode mode = ProcessOutputMode.TextTail, string? output = null, long max = 8388608) =>
        new(tools.Executable, args, tools.Root, tools.Environment, timeout ?? TimeSpan.FromSeconds(10), mode, output, max, ExecutableTrust.CaptureTrustedIdentity(tools.Executable));

    [TestMethod]
    public async Task Captured_stdout_with_ephemeral_stderr_does_not_persist_stderr_or_return_tail()
    {
        string output = Path.Combine(tools.Root, Guid.NewGuid() + ".capture");
        string logs = Path.Combine(tools.Root, "logs");
        AclPolicy.CreateRestrictedDirectory(logs, System.Security.Principal.WindowsIdentity.GetCurrent().User!);
        string log = Path.Combine(logs, "backup-" + Guid.NewGuid() + ".log");
        using var job = OperationJob.Create();
        await using (var logger = await RunLogger.CreateAsync(log, default))
        {
            var request = Request(["stream", "out:{\"hosts\":{}}", "err:AUTH-STATUS-EPHEMERAL-ONLY\n"], mode: ProcessOutputMode.CapturedFile, output: output) with { EphemeralStandardError = true };
            ProcessResult result = await new ProcessRunner(logger: logger).RunAsync(request, job, null, default);
            Assert.AreEqual(0, result.ExitCode); Assert.HasCount(0, result.StandardError); Assert.HasCount(0, result.StandardOutput);
            Assert.AreEqual("{\"hosts\":{}}", File.ReadAllText(output));
            Assert.DoesNotContain("AUTH-STATUS-EPHEMERAL-ONLY", string.Join("", logger.GetTail()));
        }
        Assert.DoesNotContain("AUTH-STATUS-EPHEMERAL-ONLY", File.ReadAllText(log));
    }

    [TestMethod] public async Task Argument_array_round_trips_without_a_shell()
    {
        string[] expected = ["a b", "中文", "quote\"x", "a&b", "a|b", "%PATH%", @"C:\tail\", "", "a'\"b"];
        using var job = OperationJob.Create();
        var result = await new ProcessRunner().RunAsync(Request(["echo-args", ..expected]), job, null, default);
        CollectionAssert.AreEqual(expected, JsonSerializer.Deserialize<string[]>(string.Concat(result.StandardOutput))!);
    }

    [TestMethod] public async Task Request_timeout_leaves_operation_open()
    {
        using var job = OperationJob.Create();
        var runner = new ProcessRunner();
        var first = await runner.RunAsync(Request(["wait"], TimeSpan.FromMilliseconds(150)), job, null, default);
        Assert.IsTrue(first.TimedOut); Assert.IsFalse(first.Cancelled); Assert.IsFalse(job.IsCancellationRequested);
        Assert.AreEqual(0, (await runner.RunAsync(Request(["exit-code", "0"]), job, null, default)).ExitCode);
    }

    [TestMethod] public async Task Cancellation_kills_descendants_and_closes_operation()
    {
        string ready = Path.Combine(tools.Root, Guid.NewGuid()+".ready");
        using var job = OperationJob.Create(); using var cts = new CancellationTokenSource();
        var running = new ProcessRunner().RunAsync(Request(["spawn-child", ready]), job, null, cts.Token);
        await TestToolBuilder.UntilAsync(() => File.Exists(ready));
        int[] ids = File.ReadAllLines(ready).Select(int.Parse).ToArray();
        cts.Cancel();
        Assert.IsTrue((await running.WaitAsync(TimeSpan.FromSeconds(10))).Cancelled);
        Assert.IsTrue(job.IsCancellationRequested); Assert.AreEqual(0, job.ActiveLeaseCount);
        Assert.IsTrue(ids.All(id => !TestToolBuilder.ProcessExists(id)));
        Assert.ThrowsExactly<OperationCanceledException>(() => job.CreateRequestJob());
    }

    [TestMethod] public async Task Root_exit_with_descendant_holding_pipes_is_bounded()
    {
        using var job = OperationJob.Create();
        string ready = Path.Combine(tools.Root, Guid.NewGuid()+".ready");
        var result = await new ProcessRunner().RunAsync(Request(["orphan-pipe", ready], TimeSpan.FromSeconds(1)), job, null, default).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.IsTrue(result.TimedOut);
        Assert.IsTrue(File.ReadAllLines(ready).Select(int.Parse).All(id => !TestToolBuilder.ProcessExists(id)));
        Assert.AreEqual(0, job.ActiveLeaseCount);
    }

    [TestMethod] public async Task Assign_failure_terminates_unassigned_suspended_process()
    {
        string sentinel = Path.Combine(tools.Root, Guid.NewGuid()+".sentinel");
        int pid = 0;
        var runner = new ProcessRunner((_, process) => { pid = ProcessNative.GetProcessId(process); throw new Win32Exception(5, "TEST_ASSIGN_FAILURE"); });
        using var job = OperationJob.Create();
        await Assert.ThrowsExactlyAsync<Win32Exception>(() => runner.RunAsync(Request(["write-sentinel-then-wait", sentinel]), job, null, default));
        Assert.IsFalse(File.Exists(sentinel)); Assert.IsFalse(TestToolBuilder.ProcessExists(pid)); Assert.AreEqual(0, job.ActiveLeaseCount);
    }

    [TestMethod] public async Task Precancelled_and_assign_resume_races_never_execute()
    {
        for (int i = 0; i < 100; i++)
        {
            string sentinel = Path.Combine(tools.Root, Guid.NewGuid()+".sentinel");
            using var job = OperationJob.Create(); using var cts = new CancellationTokenSource();
            int pid = 0;
            Microsoft.Win32.SafeHandles.SafeProcessHandle? captured = null;
            var runner = new ProcessRunner((operation, process) =>
            {
                captured = process;
                pid = ProcessNative.GetProcessId(process);
                var lease = operation.CreateRequestJob(); lease.Assign(process); cts.Cancel(); return lease;
            });
            var result = await runner.RunAsync(Request(["write-sentinel-then-wait", sentinel]), job, null, cts.Token);
            Assert.IsTrue(result.Cancelled); Assert.IsFalse(File.Exists(sentinel)); Assert.IsFalse(TestToolBuilder.ProcessExists(pid)); Assert.AreEqual(0, job.ActiveLeaseCount);
            Assert.IsTrue(captured!.IsClosed);
        }
        using var closed = OperationJob.Create(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var pre = await new ProcessRunner((_, _) => throw new AssertFailedException("process created")).RunAsync(Request(["wait"]), closed, null, cancelled.Token);
        Assert.IsTrue(pre.Cancelled);
    }

    [TestMethod] public async Task Detection_identity_change_prevents_execution()
    {
        using var job = OperationJob.Create(); var request = Request(["wait"]);
        request = request with { ExpectedExecutableIdentity = request.ExpectedExecutableIdentity! with { Length = 0 } };
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => new ProcessRunner().RunAsync(request, job, null, default));
        Assert.AreEqual("PROCESS_EXECUTABLE_IDENTITY_CHANGED", error.Message);
    }

    [TestMethod] public async Task Actual_image_change_since_detection_is_rejected()
    {
        string image = Path.Combine(Path.GetDirectoryName(tools.Executable)!, "changed.exe");
        File.Copy(tools.Executable, image);
        var identity = ExecutableTrust.CaptureTrustedIdentity(image);
        await File.AppendAllTextAsync(image, "changed");
        using var job = OperationJob.Create();
        var request = Request(["exit-code", "0"]) with { FilePath = image, ExpectedExecutableIdentity = identity };
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => new ProcessRunner().RunAsync(request, job, null, default));
        Assert.AreEqual("PROCESS_EXECUTABLE_IDENTITY_CHANGED", error.Message); File.Delete(image);
    }

    [TestMethod] public void Reparse_and_writable_installation_are_rejected()
    {
        string installation = Path.Combine(tools.Root, "unsafe-installation");
        AclPolicy.CreateRestrictedDirectory(installation, System.Security.Principal.WindowsIdentity.GetCurrent().User!);
        string image = Path.Combine(installation, "fake.exe"); File.Copy(tools.Executable, image);
        StorageTestRoot.Grant(installation, System.Security.AccessControl.FileSystemRights.CreateFiles);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => ExecutableTrust.CaptureTrustedIdentity(image));
        string junction = Path.Combine(tools.Root, "junction"); StorageTestRoot.CreateJunction(junction, Path.GetDirectoryName(tools.Executable)!);
        Assert.ThrowsExactly<PathBoundaryException>(() => ExecutableTrust.CaptureTrustedIdentity(Path.Combine(junction, Path.GetFileName(tools.Executable))));
        Directory.Delete(junction);
    }

    [TestMethod] public void Launch_identity_lease_blocks_image_write_and_rename()
    {
        using var lease = ExecutableTrust.Acquire(tools.Executable, ExecutableTrust.CaptureTrustedIdentity(tools.Executable));
        Assert.ThrowsExactly<IOException>(() => { using var stream = new FileStream(tools.Executable, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
        Assert.ThrowsExactly<IOException>(() => File.Move(tools.Executable, tools.Executable + ".moved"));
    }

    [TestMethod] public void Executable_hardlink_is_readable_but_cannot_bypass_launch_lock_or_private_data_gate()
    {
        string alias = Path.Combine(Path.GetDirectoryName(tools.Executable)!, "alias.exe");
        StorageTestRoot.CreateHardLink(alias, tools.Executable);
        try
        {
            Assert.AreEqual(ExecutableTrust.CaptureTrustedIdentity(tools.Executable), ExecutableTrust.CaptureTrustedIdentity(alias));
            using var lease = ExecutableTrust.Acquire(tools.Executable, null);
            Assert.ThrowsExactly<IOException>(() => { using var stream = new FileStream(alias, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
            using var data = NativeFileSystem.Open(alias);
            Assert.ThrowsExactly<PathBoundaryException>(() => NativeFileSystem.Inspect(data, alias, false));
        }
        finally { File.Delete(alias); }
    }

    [TestMethod] public async Task Child_environment_is_the_frozen_allowlist_in_real_process()
    {
        using var job = OperationJob.Create();
        var result = await new ProcessRunner().RunAsync(Request(["dump-env"]), job, null, default);
        var actual = JsonSerializer.Deserialize<Dictionary<string,string>>(string.Concat(result.StandardOutput))!;
        CollectionAssert.AreEquivalent(tools.Environment.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var pair in tools.Environment) Assert.AreEqual(pair.Value, actual[pair.Key]);
    }

    [TestMethod] public async Task Context_cannot_be_deleted_while_request_uses_derived_environment()
    {
        var context = await GitRuntimeContext.CreatePublicProbeAsync(tools.Environment, default);
        string ready = Path.Combine(tools.Root, Guid.NewGuid()+".ready");
        using var job = OperationJob.Create(); using var cts = new CancellationTokenSource();
        var environment = ChildEnvironmentBuilder.Build(context.Environment, new Dictionary<string,string?> { ["LC_ALL"] = "C" });
        var running = new ProcessRunner().RunAsync(Request(["write-sentinel-then-wait", ready]) with { Environment = environment }, job, null, cts.Token);
        await TestToolBuilder.UntilAsync(() => File.Exists(ready));
        Assert.ThrowsExactly<InvalidOperationException>(() => context.DisposeAsync());
        cts.Cancel(); await running; await context.DisposeAsync();
    }

    [TestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)] [DataRow(3)]
    public async Task All_modes_redact_incremental_stderr(int modeValue)
    {
        var mode = (ProcessOutputMode)modeValue;
        using var job = OperationJob.Create();
        string output = Path.Combine(tools.Root, Guid.NewGuid()+".out");
        var progress = new InlineProgress();
        var result = await new ProcessRunner().RunAsync(Request(["stream", "out:ABCD-EFGH\n", "err:https://objects.githubusercontent.com/a?X-Amz-Signature=TEST", "err:ONLY-SECRET&X-Amz-Expires=300#fragment\n"], mode: mode, output: mode is ProcessOutputMode.BinaryFile or ProcessOutputMode.CapturedFile ? output : null), job, progress, default);
        string surfaced = string.Concat(result.StandardError.Concat(result.StandardOutput).Concat(progress.Values));
        Assert.DoesNotContain("TESTONLY-SECRET", surfaced); Assert.DoesNotContain("X-Amz-", surfaced);
        if (mode == ProcessOutputMode.EphemeralText) { Assert.HasCount(0, result.StandardOutput); Assert.HasCount(0, result.StandardError); Assert.Contains("ABCD-EFGH", surfaced); }
        if (File.Exists(output)) { Assert.AreEqual("ABCD-EFGH\n", File.ReadAllText(output)); File.Delete(output); }
    }

    [TestMethod] public async Task Binary_output_is_exact_and_limit_deletes_owned_file()
    {
        using var job = OperationJob.Create(); byte[] bytes = [0, 255, 1, 13, 10, 128];
        string output = Path.Combine(tools.Root, Guid.NewGuid()+".out");
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(Request(["write-bytes", Convert.ToBase64String(bytes)], mode: ProcessOutputMode.BinaryFile, output: output), job, null, default);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(output)); Assert.HasCount(0, result.StandardOutput); File.Delete(output);
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => runner.RunAsync(Request(["write-bytes", Convert.ToBase64String(bytes)], mode: ProcessOutputMode.BinaryFile, output: output, max: 2), job, null, default));
        Assert.AreEqual("PROCESS_STDOUT_LIMIT_EXCEEDED", error.Message); Assert.IsFalse(File.Exists(output)); Assert.IsFalse(job.IsCancellationRequested);
    }

    [TestMethod] public async Task Unrelated_inheritable_native_handle_is_not_passed_to_child()
    {
        string path = Path.Combine(tools.Root, "unrelated-handle.dat");
        using var file = File.Create(path);
        var id = NativeFileSystem.Inspect(file.SafeFileHandle, path, false);
        Assert.IsTrue(ProcessNative.SetHandleInformation(file.SafeFileHandle, 1, 1));
        using var job = OperationJob.Create();
        var result = await new ProcessRunner().RunAsync(Request(["probe-handle", file.SafeFileHandle.DangerousGetHandle().ToInt64().ToString(), id.FileIndex.ToString()]), job, null, default);
        Assert.AreEqual("NOT_INHERITED", string.Concat(result.StandardOutput));
    }

    [TestMethod] public async Task Ephemeral_output_never_logs_and_raw_stdout_never_surfaces()
    {
        string owner = Path.Combine(tools.Root, "logger-owner");
        string logs = Path.Combine(owner, "logs");
        AclPolicy.CreateRestrictedDirectory(logs, System.Security.Principal.WindowsIdentity.GetCurrent().User!);
        await using var logger = await RunLogger.CreateAsync(Path.Combine(logs, "backup-process.log"), default);
        using var job = OperationJob.Create(); var progress = new InlineProgress();
        var runner = new ProcessRunner(logger: logger);
        var ephemeral = await runner.RunAsync(Request(["stream", "out:ABCD-EFGH\n", "err:Authorization: Bearer TESTONLY-SECRET\n"], mode: ProcessOutputMode.EphemeralText), job, progress, default);
        Assert.HasCount(0, logger.GetTail()); Assert.HasCount(0, ephemeral.StandardError); Assert.HasCount(0, ephemeral.StandardOutput);
        Assert.Contains("ABCD-EFGH", string.Concat(progress.Values)); Assert.DoesNotContain("TESTONLY-SECRET", string.Concat(progress.Values));
        progress.Values.Clear(); string output = Path.Combine(tools.Root, "opaque-output.tmp");
        byte[] raw = System.Text.Encoding.UTF8.GetBytes("TESTONLY-RAW-BYTES");
        var captured = await runner.RunAsync(Request(["write-bytes", Convert.ToBase64String(raw)], mode: ProcessOutputMode.CapturedFile, output: output), job, progress, default);
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(output)); Assert.HasCount(0, captured.StandardOutput); Assert.HasCount(0, progress.Values); Assert.HasCount(0, logger.GetTail()); File.Delete(output);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void Descriptor_check_releases_identity_without_finalization(bool rejected)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var descriptor = new RawSecurityDescriptor($"O:{identity.User!.Value}D:P(A;;FA;;;{identity.User.Value})");
        if (rejected) descriptor.DiscretionaryAcl = null;
        using var current = Process.GetCurrentProcess();
        int baseline, after;
        Assert.IsTrue(GC.TryStartNoGCRegion(16 * 1024 * 1024, disallowFullBlockingGC: true), "DIAGNOSTIC_NO_GC_REGION_UNAVAILABLE");
        try
        {
            for (int i = 0; i < 5; i++) Check();
            current.Refresh(); baseline = current.HandleCount;
            for (int i = 0; i < 32; i++) Check();
            current.Refresh(); after = current.HandleCount;
        }
        finally { GC.EndNoGCRegion(); }
        Console.WriteLine($"Descriptor rejected={rejected}; handles baseline={baseline}, after={after}");
        Assert.IsLessThanOrEqualTo(baseline, after);

        void Check()
        {
            if (rejected) Assert.ThrowsExactly<UnauthorizedAccessException>(() => ExecutableTrust.CheckDescriptor(descriptor, true));
            else ExecutableTrust.CheckDescriptor(descriptor, true);
        }
    }

    [TestMethod] public async Task Repeated_launch_and_cleanup_returns_native_handle_count_to_baseline()
    {
        using var job = OperationJob.Create(); var runner = new ProcessRunner();
        // Prime diagnostic access before warmup, and defer output until measurement ends.
        using (var diagnostic = Process.GetCurrentProcess()) _ = Sample("prime", diagnostic);
        var samples = new List<string>();
        // Warm the synchronous anonymous-pipe reader workers before measuring the
        // request-owned handles; worker startup itself allocates runtime handles.
        for (int i = 0; i < 30; i++) await runner.RunAsync(Request(["exit-code", "0"]), job, null, default);
        GC.Collect(); GC.WaitForPendingFinalizers();
        using var current = Process.GetCurrentProcess(); current.Refresh(); int baseline = current.HandleCount;
        samples.Add(Sample("baseline", current));
        for (int i = 0; i < 30; i++)
        {
            await runner.RunAsync(Request(["exit-code", "0"]), job, null, default);
            if ((i + 1) % 5 == 0) samples.Add(Sample($"iteration-{i + 1}", current));
        }
        GC.Collect(); GC.WaitForPendingFinalizers();
        current.Refresh(); int after = current.HandleCount;
        samples.Add(Sample("after", current));
        foreach (string sample in samples) Console.WriteLine(sample);
        Console.WriteLine($"Native handles baseline={baseline}, after={after}; active request leases={job.ActiveLeaseCount}");
        Assert.IsLessThanOrEqualTo(baseline, after); Assert.AreEqual(0, job.ActiveLeaseCount);

        static string Sample(string label, Process process)
        {
            process.Refresh();
            int handles = process.HandleCount;
            ProcessThreadCollection threads = process.Threads;
            try
            {
                return $"{label}: handles={handles}; poolThreads={ThreadPool.ThreadCount}; processThreads={threads.Count}; pendingWork={ThreadPool.PendingWorkItemCount}; completedWork={ThreadPool.CompletedWorkItemCount}";
            }
            finally { foreach (ProcessThread thread in threads) thread.Dispose(); }
        }
    }
}

internal sealed class InlineProgress : IProgress<string>
{
    internal List<string> Values { get; } = [];
    public void Report(string value) { lock (Values) Values.Add(value); }
}
