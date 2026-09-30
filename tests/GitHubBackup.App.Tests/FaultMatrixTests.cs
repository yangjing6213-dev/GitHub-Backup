using System.Net;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class FaultMatrixTests
{
    private static (byte[] Bytes, DateTime LastWriteTimeUtc, NativeFileIdentity Identity) CaptureFile(string path)
    {
        using var file = NativeFileSystem.Open(path);
        return (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path), NativeFileSystem.Inspect(file, path, directory: false));
    }

    private static void AssertFileUnchanged(string path, (byte[] Bytes, DateTime LastWriteTimeUtc, NativeFileIdentity Identity) original)
    {
        CollectionAssert.AreEqual(original.Bytes, File.ReadAllBytes(path));
        Assert.AreEqual(original.LastWriteTimeUtc, File.GetLastWriteTimeUtc(path));
        using var file = NativeFileSystem.Open(path);
        Assert.AreEqual(original.Identity, NativeFileSystem.Inspect(file, path, directory: false));
    }

    [TestMethod]
    public async Task Next_authorized_backup_clears_valid_fallback_residue_without_rewriting_receipt()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        using (var owner = OperationLocks.AcquireStorage(f.Local.OwnerRoot, "prior"))
            await RunningMarkerStore.WriteAsync(owner, new("prior", BackupMode.Daily, "fixture-user", started), default);
        var receipt = BackupSummary.PreflightFailure(BackupMode.Daily, "fixture-user", "prior", f.Local.OwnerRoot, "", "")
            with { Status = RunStatus.Pass, StartedAt = started };
        string fallback = await new SummaryStore(f.Local.Paths).WriteFallbackAsync(receipt, default);
        var terminal = CaptureFile(fallback);
        string marker = Path.Combine(f.Local.OwnerRoot, "RUNNING.json");
        var markerBefore = CaptureFile(marker);

        var history = await new SummaryStore(f.Local.Paths).ReadLatestAsync(f.Local.OwnerRoot, f.Local.Paths.DiagnosticFallbackRoot, default);
        CollectionAssert.Contains(history.Warnings.ToArray(), "RUNNING_MARKER_COMPLETED_RESIDUE");
        AssertFileUnchanged(marker, markerBefore);
        AssertFileUnchanged(fallback, terminal);
        Assert.IsTrue(File.Exists(marker));

        var next = await f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), default);

        Assert.AreEqual(RunStatus.Pass, next.Summary.Status);
        Assert.IsFalse(File.Exists(marker));
        AssertFileUnchanged(fallback, terminal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Fallback_terminal_receipt_reconciles_held_marker_without_changing_status_or_bytes(bool coreFailure)
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        FileStream? heldMarker = null, heldSummary = null;
        string? ownerSummary = null;
        f.Git.BeforeRun = _ =>
        {
            if (heldMarker is not null) return;
            string markerPath = Path.Combine(f.Local.OwnerRoot, "RUNNING.json");
            var marker = System.Text.Json.JsonSerializer.Deserialize<RunningMarker>(File.ReadAllBytes(markerPath), MetadataJson.Options)!;
            heldMarker = new(markerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            ownerSummary = Path.Combine(f.Local.OwnerRoot, "manifests", "summary-" + marker.RunId + ".json");
            f.Local.Write(ownerSummary, "{}");
            heldSummary = new(ownerSummary, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (coreFailure) f.Git.Fault = "clone";
        };
        try
        {
            var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), default);
            Assert.AreEqual(coreFailure ? RunStatus.Fail : RunStatus.Pass, result.Summary.Status);
            Assert.IsFalse(result.HasPendingCleanup || f.Orchestrator.IsBusy);
            string fallback = Path.Combine(f.Local.Paths.DiagnosticFallbackRoot, "summary-" + result.Summary.StartedRunId + ".json");
            byte[] terminal = await File.ReadAllBytesAsync(fallback);
            CollectionAssert.AreEqual(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result.Summary, MetadataJson.Options), terminal);
            Assert.AreEqual("{}", await File.ReadAllTextAsync(ownerSummary!));
            var residue = await new SummaryStore(f.Local.Paths).ReadLatestAsync(f.Local.OwnerRoot, f.Local.Paths.DiagnosticFallbackRoot, default);
            CollectionAssert.Contains(residue.Warnings.ToArray(), "RUNNING_MARKER_COMPLETED_RESIDUE");
            Assert.IsTrue(residue.LatestIsFallback);
            Assert.AreEqual(result.Summary.Status, residue.Latest!.Status);
            heldMarker!.Dispose(); heldMarker = null; heldSummary!.Dispose(); heldSummary = null;
            string marker = Path.Combine(f.Local.OwnerRoot, "RUNNING.json");
            var markerBefore = CaptureFile(marker);
            var ownerBefore = CaptureFile(ownerSummary!);
            var fallbackBefore = CaptureFile(fallback);
            for (int read = 0; read < 2; read++)
            {
                var history = await new SummaryStore(f.Local.Paths).ReadLatestAsync(f.Local.OwnerRoot, f.Local.Paths.DiagnosticFallbackRoot, default);
                CollectionAssert.Contains(history.Warnings.ToArray(), "RUNNING_MARKER_COMPLETED_RESIDUE");
                Assert.AreEqual(result.Summary.Status, history.Latest!.Status);
                AssertFileUnchanged(marker, markerBefore);
                AssertFileUnchanged(ownerSummary!, ownerBefore);
                AssertFileUnchanged(fallback, fallbackBefore);
            }
            using (var owner = OperationLocks.AcquireExistingStorage(f.Local.OwnerRoot))
                await RunningMarkerStore.RemoveCompletedAsync(owner, null, fallback, default);
            Assert.IsFalse(File.Exists(marker));
            AssertFileUnchanged(ownerSummary!, ownerBefore);
            AssertFileUnchanged(fallback, fallbackBefore);
        }
        finally { heldMarker?.Dispose(); heldSummary?.Dispose(); }
    }

    [TestMethod]
    public async Task Startup_history_reads_owner_completed_residue_without_changing_files()
    {
        using var f = new PreflightFixture();
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        using (var owner = OperationLocks.AcquireStorage(f.OwnerRoot, "completed"))
            await RunningMarkerStore.WriteAsync(owner, new("completed", BackupMode.Daily, "fixture-user", started), default);
        var receipt = BackupSummary.PreflightFailure(BackupMode.Daily, "fixture-user", "completed", f.OwnerRoot, "", "")
            with { Status = RunStatus.Pass, StartedAt = started };
        string summary = await new SummaryStore(f.Paths).WriteAsync(f.OwnerRoot, receipt, f.Paths.DiagnosticFallbackRoot, default);
        Assert.IsTrue(summary.StartsWith(Path.Combine(f.OwnerRoot, "manifests"), StringComparison.OrdinalIgnoreCase));
        string marker = Path.Combine(f.OwnerRoot, "RUNNING.json");
        var markerBefore = CaptureFile(marker);
        var summaryBefore = CaptureFile(summary);
        var store = new SummaryStore(f.Paths);

        UiTest.Run(async () =>
        {
            var actions = new UiFixture().Actions with
            {
                LoadSettings = _ => Task.FromResult(new SettingsLoadResult(new("fixture-user", f.BackupRoot, NetworkMode.Auto), [])),
                ReadHistory = (selected, token) => store.ReadLatestAsync(Path.Combine(selected.BackupRoot, selected.Owner),
                    f.Paths.DiagnosticFallbackRoot, token, selected.Owner)
            };
            using var form = new MainForm(actions);
            for (int read = 0; read < 2; read++)
            {
                await form.LoadSettingsAsync();
                Assert.Contains("运行已完成，仍有待清理的完成标记", form.HistoryLabel.Text);
                AssertFileUnchanged(marker, markerBefore);
                AssertFileUnchanged(summary, summaryBefore);
            }
        });
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("run")]
    [DataRow("mode")]
    [DataRow("start")]
    [DataRow("root")]
    [DataRow("cleanup-pending")]
    [DataRow("unapproved-fallback")]
    public async Task Fallback_receipt_mismatch_or_unapproved_root_cannot_authorize_marker_deletion(string mismatch)
    {
        using var f = new PreflightFixture();
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        using (var owner = OperationLocks.AcquireStorage(f.OwnerRoot, "unfinished"))
            await RunningMarkerStore.WriteAsync(owner, new("unfinished", BackupMode.Daily, "fixture-user", started), default);
        var receipt = BackupSummary.PreflightFailure(BackupMode.Daily, "fixture-user", "unfinished", f.OwnerRoot, "", "")
            with { Status = RunStatus.Pass, StartedAt = started };
        receipt = mismatch switch
        {
            "owner" => receipt with { Owner = "other" },
            "run" => receipt with { StartedRunId = "other" },
            "mode" => receipt with { Mode = BackupMode.Full },
            "start" => receipt with { StartedAt = started.AddSeconds(-1) },
            "root" => receipt with { BackupRoot = f.Root.Child("other") },
            "cleanup-pending" => receipt with { Status = RunStatus.Fail, FailurePhase = "cleanup", ErrorCode = "BACKUP_CLEANUP_PENDING" },
            _ => receipt
        };
        string fallback = await new SummaryStore(f.Paths).WriteFallbackAsync(receipt, default);
        byte[] before = await File.ReadAllBytesAsync(fallback);
        string marker = Path.Combine(f.OwnerRoot, "RUNNING.json");
        byte[] markerBefore = await File.ReadAllBytesAsync(marker);
        string selectedFallback = mismatch == "unapproved-fallback" ? f.Root.Child("unapproved") : f.Paths.DiagnosticFallbackRoot;
        var history = await new SummaryStore(f.Paths).ReadLatestAsync(f.OwnerRoot, selectedFallback, default);
        CollectionAssert.Contains(history.Warnings.ToArray(), "RUNNING_MARKER_INCOMPLETE");
        if (mismatch != "unapproved-fallback")
        {
            bool rejected = false;
            using var owner = OperationLocks.AcquireExistingStorage(f.OwnerRoot);
            try { await RunningMarkerStore.RemoveCompletedAsync(owner, null, null, default, f.Paths); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
            Assert.IsTrue(rejected);
        }
        CollectionAssert.AreEqual(markerBefore, await File.ReadAllBytesAsync(marker));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fallback));
        Assert.IsFalse(Directory.Exists(f.Root.Child("unapproved")));
    }

    [TestMethod]
    public async Task Pre_run_fallback_cannot_override_pending_owner_receipt()
    {
        using var f = new PreflightFixture();
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        using (var owner = OperationLocks.AcquireStorage(f.OwnerRoot, "prior"))
            await RunningMarkerStore.WriteAsync(owner, new("prior", BackupMode.Daily, "fixture-user", started), default);
        var receipt = BackupSummary.PreflightFailure(BackupMode.Daily, "fixture-user", "prior", f.OwnerRoot, "", "")
            with { Status = RunStatus.Pass, StartedAt = started };
        string ownerSummary = await new SummaryStore(f.Paths).WriteAsync(f.OwnerRoot,
            receipt with { Status = RunStatus.Fail, FailurePhase = "cleanup", ErrorCode = "BACKUP_CLEANUP_PENDING" },
            f.Paths.DiagnosticFallbackRoot, default);
        string fallback = await new SummaryStore(f.Paths).WriteFallbackAsync(receipt, default);
        string marker = Path.Combine(f.OwnerRoot, "RUNNING.json");
        var markerBefore = CaptureFile(marker);
        var ownerBefore = CaptureFile(ownerSummary);
        var fallbackBefore = CaptureFile(fallback);

        bool rejected = false;
        using (var owner = OperationLocks.AcquireExistingStorage(f.OwnerRoot))
            try { await RunningMarkerStore.RemoveCompletedAsync(owner, null, null, default, f.Paths); }
            catch (IOException) { rejected = true; }
        Assert.IsTrue(rejected);
        AssertFileUnchanged(marker, markerBefore);
        AssertFileUnchanged(ownerSummary, ownerBefore);
        AssertFileUnchanged(fallback, fallbackBefore);
    }

    [TestMethod]
    public async Task Pre_run_fallback_rejects_unsafe_receipt_file()
    {
        using var f = new PreflightFixture();
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        using (var owner = OperationLocks.AcquireStorage(f.OwnerRoot, "prior"))
            await RunningMarkerStore.WriteAsync(owner, new("prior", BackupMode.Daily, "fixture-user", started), default);
        var receipt = BackupSummary.PreflightFailure(BackupMode.Daily, "fixture-user", "prior", f.OwnerRoot, "", "")
            with { Status = RunStatus.Pass, StartedAt = started };
        string fallback = await new SummaryStore(f.Paths).WriteFallbackAsync(receipt, default);
        f.Root.SetNullDacl(fallback);
        string marker = Path.Combine(f.OwnerRoot, "RUNNING.json");
        var markerBefore = CaptureFile(marker);

        bool rejected = false;
        using (var owner = OperationLocks.AcquireExistingStorage(f.OwnerRoot))
            try { await RunningMarkerStore.RemoveCompletedAsync(owner, null, null, default, f.Paths); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
        Assert.IsTrue(rejected);
        AssertFileUnchanged(marker, markerBefore);
    }

    [TestMethod]
    [DataRow("missing-lock-directory")]
    [DataRow("missing-lock-file")]
    [DataRow("missing-owner")]
    public async Task History_keeps_marker_visible_without_creating_missing_lock_or_owner(string missing)
    {
        using var f = new PreflightFixture();
        string root = missing == "missing-owner" ? f.Root.Child("absent") : f.OwnerRoot;
        string marker = Path.Combine(root, "RUNNING.json");
        if (missing != "missing-owner")
            f.Write(marker, "{\"runId\":\"unfinished\",\"mode\":\"daily\",\"owner\":\"fixture-user\",\"startedAt\":\"2026-09-20T00:00:00Z\"}");
        if (missing == "missing-lock-file") f.MakeDirectory(StorageLayout.LockDirectoryName);
        var history = await new SummaryStore(f.Paths).ReadLatestAsync(root, f.Paths.DiagnosticFallbackRoot, default);
        if (missing == "missing-owner")
        {
            Assert.IsEmpty(history.Warnings);
            Assert.IsFalse(Directory.Exists(root));
        }
        else
        {
            CollectionAssert.Contains(history.Warnings.ToArray(), "RUNNING_MARKER_INCOMPLETE");
            Assert.IsTrue(File.Exists(marker));
            Assert.AreEqual(missing == "missing-lock-file", Directory.Exists(Path.Combine(root, StorageLayout.LockDirectoryName)));
            Assert.IsFalse(File.Exists(Path.Combine(root, StorageLayout.LockDirectoryName, "owner.lock")));
        }
    }

    [TestMethod]
    [DataRow("/user", "1700000000")]
    [DataRow("/user", "invalid")]
    [DataRow("/user", null)]
    [DataRow("/user/repos", "1700000000")]
    [DataRow("/user/repos", "invalid")]
    [DataRow("/user/repos", null)]
    public async Task Native_preflight_rate_limit_reaches_run_and_ui_without_retry_or_raw_text(string failedPath, string? reset)
    {
        using var f = new PreflightFixture();
        var settings = new AppSettings("fixture-user", f.BackupRoot, NetworkMode.Auto)
            { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        await new SettingsStore(f.Paths).SaveAsync(settings, default);
        int failures = 0;
        var handler = new OrchestrationHttp { Override = (path, _) =>
        {
            if (path != failedPath) return Task.FromResult<HttpResponseMessage?>(null);
            failures++; return Task.FromResult<HttpResponseMessage?>(Limited(reset));
        } };
        var service = PreflightServiceTests.NativeService(f);
        var preflight = await service.CheckNativeAsync(BackupMode.Full, settings, f.Job, new RateLimitReader(), default, _ => handler);
        Assert.IsNull(preflight.LiveSession);
        Assert.AreEqual("HTTP_RATE_LIMITED", preflight.Snapshot.Report.Issues.Single(issue => issue.BlocksBackup).ErrorCode);
        var orchestrator = new BackupOrchestrator(f.Runner, f.Paths);
        var result = await orchestrator.RunWithPreflightAsync(BackupMode.Full, settings, (_, _) => Task.FromResult(preflight),
            service.RetryCleanupAsync, null, default);
        Assert.AreEqual("HTTP_RATE_LIMITED", result.Summary.ErrorCode);
        Assert.AreEqual(1, failures);
        AssertRateLimitUi(reset, result, preflight.Snapshot);
    }

    [TestMethod]
    [DataRow("metadata", "1700000000")]
    [DataRow("metadata", "invalid")]
    [DataRow("metadata", null)]
    [DataRow("inventory", "1700000000")]
    [DataRow("inventory", "invalid")]
    [DataRow("inventory", null)]
    [DataRow("asset", "1700000000")]
    [DataRow("asset", "invalid")]
    [DataRow("asset", null)]
    public async Task Native_backup_rate_limit_reaches_ui_without_retry_or_raw_text(string stage, string? reset)
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        f.Http.ReleaseJson = "[{\"id\":10,\"tag_name\":\"v1\",\"name\":\"release\",\"draft\":false,\"prerelease\":false,\"published_at\":null,\"assets\":[{\"id\":100,\"name\":\"asset.bin\",\"size\":3,\"digest\":null,\"updated_at\":\"2026-09-20T00:00:00Z\"}]}]";
        string failedPath = stage switch { "metadata" => "/repos/fixture-user/repo/issues", "inventory" => "/repos/fixture-user/repo/releases", _ => "/repos/fixture-user/repo/releases/assets/100" };
        int failures = 0;
        f.Http.Override = (path, _) =>
        {
            if (path == failedPath) { failures++; return Task.FromResult<HttpResponseMessage?>(Limited(reset)); }
            return Task.FromResult<HttpResponseMessage?>(path.Contains("/releases/assets/")
                ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 27, 255]) } : null);
        };
        var result = await f.Orchestrator.RunAsync(f.Request(stage == "metadata" ? BackupMode.Daily : BackupMode.Full), default);
        Assert.AreEqual(stage == "inventory" ? RunStatus.Fail : RunStatus.Partial, result.Summary.Status);
        Assert.AreEqual(1, failures);
        AssertRateLimitUi(reset, result);
    }

    private static HttpResponseMessage Limited(string? reset)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("PRIVATE_TASK9_RATE_BODY") };
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        if (reset is not null) response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset);
        return response;
    }

    private static void AssertRateLimitUi(string? reset, BackupRunResult result, PreflightSnapshot? preflight = null) => UiTest.Run(async () =>
    {
        string expected = reset == "1700000000" ? "2023-11-14 22:13:20 UTC" : "重置时间未知";
        var fixture = new UiFixture();
        using var form = new MainForm(fixture.Actions with { Run = (_, _, _, _) => Task.FromResult(result) });
        await form.LoadSettingsAsync(); form.ConsentCheckBox.Checked = true; await form.CurrentOperation;
        await form.CheckEnvironmentAsync(); await form.StartBackupAsync();
        Assert.Contains(expected, form.StatusLabel.Text);
        Assert.DoesNotContain("PRIVATE", form.StatusLabel.Text);
        Assert.DoesNotContain("invalid", form.StatusLabel.Text);
        if (preflight is not null)
        {
            using var check = new MainForm(fixture.Actions with { Check = (_, _, _) => Task.FromResult(new EnvironmentStatus(preflight.Report, preflight.Tools,
                preflight.SelectedProxyProfile.DisplayName, preflight.Repositories, preflight.UnsafeSensitivePaths)) });
            await check.LoadSettingsAsync(); check.ConsentCheckBox.Checked = true; await check.CurrentOperation;
            await check.CheckEnvironmentAsync();
            Assert.Contains(expected, check.StatusLabel.Text);
            Assert.DoesNotContain("PRIVATE", check.StatusLabel.Text);
        }
    });

    private sealed class RateLimitReader : IGitHubCredentialReader
    { public GitHubCredentialLease ReadExact(string login) => new(login, "SYNTHETIC_TASK9_RATE_CREDENTIAL"u8.ToArray()); }

    [TestMethod]
    [DataRow("incomplete")]
    [DataRow("active")]
    [DataRow("completed")]
    [DataRow("corrupt")]
    [DataRow("foreign")]
    public async Task History_classifies_marker_without_hiding_newer_work_or_erasing_unknown_evidence(string state)
    {
        using var f = new PreflightFixture();
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var summary = BackupSummary.PreflightFailure(BackupMode.Daily, "fixture-user", "old", f.OwnerRoot, "", "")
            with { Status = RunStatus.Pass, StartedAt = started.AddDays(-1), CompletedAt = started.AddHours(-1) };
        await new SummaryStore(f.Paths).WriteAsync(f.OwnerRoot, summary, f.Paths.DiagnosticFallbackRoot, default);
        using var owner = OperationLocks.AcquireStorage(f.OwnerRoot, "new");
        await RunningMarkerStore.WriteAsync(owner, new("new", BackupMode.Daily, "fixture-user", started), default);
        if (state == "completed") await new SummaryStore(f.Paths).WriteAsync(f.OwnerRoot,
            summary with { StartedRunId = "new", StartedAt = started, CompletedAt = started.AddSeconds(10) }, f.Paths.DiagnosticFallbackRoot, default);
        string path = Path.Combine(f.OwnerRoot, "RUNNING.json");
        if (state == "corrupt") await File.WriteAllTextAsync(path, "{broken");
        if (state == "foreign") await File.WriteAllTextAsync(path,
            "{\"runId\":\"new\",\"mode\":\"daily\",\"owner\":\"other\",\"startedAt\":\"2026-09-01T00:00:00Z\"}");
        byte[] original = await File.ReadAllBytesAsync(path);
        if (state != "active") owner.Dispose();

        var history = await new SummaryStore(f.Paths).ReadLatestAsync(f.OwnerRoot, f.Paths.DiagnosticFallbackRoot, default);

        string expected = state switch { "active" => "RUNNING_MARKER_ACTIVE", "completed" => "RUNNING_MARKER_COMPLETED_RESIDUE",
            "incomplete" => "RUNNING_MARKER_INCOMPLETE", _ => "RUNNING_MARKER_UNRESOLVED" };
        CollectionAssert.Contains(history.Warnings.ToArray(), expected);
        Assert.IsNotNull(history.LatestCoreSuccess);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
        UiTest.Run(async () =>
        {
            var ui = new UiFixture { History = history };
            using var form = new MainForm(ui.Actions); await form.LoadSettingsAsync();
            Assert.Contains(state switch { "active" => "运行中", "completed" => "PASS", "incomplete" => "未完成", _ => "需要检查" }, form.HistoryLabel.Text);
        });
    }

    [TestMethod]
    public async Task Marker_delete_failure_keeps_durable_terminal_receipt_and_does_not_block_cleanup()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        FileStream? held = null;
        f.Git.BeforeRun = _ => held ??= new FileStream(Path.Combine(f.Local.OwnerRoot, "RUNNING.json"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), default);
            Assert.AreEqual(RunStatus.Pass, result.Summary.Status);
            Assert.IsFalse(result.HasPendingCleanup || f.Orchestrator.IsBusy);
            string summaryPath = Path.Combine(f.Local.OwnerRoot, "manifests", "summary-" + result.Summary.StartedRunId + ".json");
            byte[] terminal = await File.ReadAllBytesAsync(summaryPath);
            CollectionAssert.AreEqual(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result.Summary, MetadataJson.Options), terminal);
            Assert.IsTrue(File.Exists(Path.Combine(f.Local.OwnerRoot, "RUNNING.json")));
            var residue = await new SummaryStore(f.Local.Paths).ReadLatestAsync(f.Local.OwnerRoot, f.Local.Paths.DiagnosticFallbackRoot, default);
            CollectionAssert.Contains(residue.Warnings.ToArray(), "RUNNING_MARKER_COMPLETED_RESIDUE");
            held!.Dispose(); held = null;
            string marker = Path.Combine(f.Local.OwnerRoot, "RUNNING.json");
            var markerBefore = CaptureFile(marker);
            var summaryBefore = CaptureFile(summaryPath);
            for (int read = 0; read < 2; read++)
            {
                var reconciled = await new SummaryStore(f.Local.Paths).ReadLatestAsync(f.Local.OwnerRoot, f.Local.Paths.DiagnosticFallbackRoot, default);
                CollectionAssert.Contains(reconciled.Warnings.ToArray(), "RUNNING_MARKER_COMPLETED_RESIDUE");
                AssertFileUnchanged(marker, markerBefore);
                AssertFileUnchanged(summaryPath, summaryBefore);
            }
            using (var owner = OperationLocks.AcquireExistingStorage(f.Local.OwnerRoot))
                await RunningMarkerStore.RemoveCompletedAsync(owner, null, null, default);
            Assert.IsFalse(File.Exists(marker));
            AssertFileUnchanged(summaryPath, summaryBefore);
        }
        finally { held?.Dispose(); }
    }

    [TestMethod]
    public async Task Successful_no_digest_download_and_reuse_both_pass_with_limited_integrity_information()
    {
        using var root = new StorageTestRoot();
        for (int run = 0; run < 2; run++)
        {
            await using var f = await OrchestrationFixture.CreateAsync(backupRoot: root.Child("backup"), configureHttp: http =>
            {
                http.ReleaseJson = "[{\"id\":10,\"tag_name\":\"v1\",\"name\":\"release\",\"draft\":false,\"prerelease\":false,\"published_at\":null,\"assets\":[{\"id\":100,\"name\":\"asset.bin\",\"size\":3,\"digest\":null,\"updated_at\":\"2026-09-20T00:00:00Z\"}]}]";
                http.Override = (path, _) => Task.FromResult<HttpResponseMessage?>(path.Contains("/releases/assets/")
                    ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 27, 255]) } : null);
            });
            var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Full), default);
            Assert.AreEqual(RunStatus.Pass, result.Summary.Status);
            Assert.AreEqual(0, result.Summary.WarningCount);
            Assert.AreEqual(run == 0 ? 1 : 0, f.Http.Paths.Count(path => path.Contains("/releases/assets/")));
            Assert.Contains("RELEASE_SIZE_ONLY_INTEGRITY", await File.ReadAllTextAsync(result.Summary.Log));
        }
    }

    [TestMethod]
    [DataRow(204, "HTTP_STATUS_204", 1)]
    [DataRow(206, "HTTP_STATUS_206", 1)]
    [DataRow(304, "HTTP_STATUS_304", 1)]
    [DataRow(401, "HTTP_STATUS_401", 1)]
    [DataRow(403, "HTTP_STATUS_403", 1)]
    [DataRow(404, "HTTP_STATUS_404", 1)]
    [DataRow(407, "HTTP_STATUS_407", 1)]
    [DataRow(429, "HTTP_RATE_LIMITED", 1)]
    [DataRow(500, "HTTP_STATUS_500", 3)]
    [DataRow(503, "HTTP_STATUS_503", 3)]
    public async Task Rejected_status_preserves_metadata_and_release_generation(int status, string code, int attempts)
    {
        await PreservePriorArtifacts((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent("SYNTHETIC_TASK9_PRIVATE_ERROR_BODY") }), code, code, attempts);
    }

    [TestMethod]
    [DataRow("malformed-link", "HTTP_PAGINATION_REJECTED", "HTTP_REDIRECT_REJECTED")]
    [DataRow("hostile-host", "HTTP_PAGINATION_REJECTED", "HTTP_REDIRECT_REJECTED")]
    [DataRow("slow-headers", "HTTP_TIMEOUT", "HTTP_TIMEOUT")]
    [DataRow("slow-body", "HTTP_TIMEOUT", "HTTP_BODY_IDLE_TIMEOUT")]
    public async Task Invalid_navigation_and_stalled_transfer_preserve_prior_artifacts(string fault, string metadataCode, string assetCode)
    {
        await PreservePriorArtifacts(async (asset, token) =>
        {
            if (fault == "slow-headers") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (fault == "slow-body") return new(HttpStatusCode.OK)
                { Content = new StreamContent(new PausedReadStream(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct))) };
            if (asset) return new(HttpStatusCode.Found) { Headers = { Location = new(fault == "hostile-host"
                ? "https://objects.githubusercontent.com.evil.invalid/file?sig=SYNTHETIC_TASK9_PRIVATE_ERROR_BODY"
                : "https://api.github.com/repos/fixture-user/repo/releases/assets/999") } };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            response.Headers.TryAddWithoutValidation("Link", fault == "hostile-host"
                ? "<https://api.github.com.evil.invalid/repos/fixture-user/repo/issues?state=all&per_page=100&page=2>; rel=\"next\""
                : "<https://api.github.com/repos/fixture-user/repo/issues?state=all&per_page=100&page=2&page=3>; rel=\"next\"");
            return response;
        }, metadataCode, assetCode, fault == "slow-headers" ? 3 : 1);
    }

    private static async Task PreservePriorArtifacts(Func<bool, CancellationToken, Task<HttpResponseMessage>> fault,
        string metadataCode, string assetCode, int expectedAttempts)
    {
        using var root = new StorageTestRoot();
        string metadata = root.Child("metadata"), releases = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        AclPolicy.CreateRestrictedDirectory(releases, root.User);
        string target = Path.Combine(metadata, "issues.pages.json");
        byte[] previousPage = "[[{\"old\":true}]]"u8.ToArray();
        using (var file = AclPolicy.CreateRestrictedFile(target, root.User)) file.Write(previousPage);
        byte[] previousAsset = [0, 27, 255];
        bool fail = false;
        int calls = 0;
        var handler = new OrchestrationHttp
        {
            ObserveRequest = request => Assert.AreEqual("api.github.com", request.RequestUri!.Host),
            Override = async (path, token) =>
            {
                if (path == "/user") return null;
                calls++;
                if (fail) return await fault(path.Contains("/releases/assets/"), token);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(previousAsset) };
            }
        };
        byte[] secret = "SYNTHETIC_TASK9_FAULT_CREDENTIAL"u8.ToArray();
        using var transport = await GitHubHttpTransport.BindAsync(new("fixture-user", secret), "fixture-user",
            ProxyProfile.Direct, default, _ => handler,
            new(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250)));
        var service = new ReleaseAssetService(transport);
        ReleaseInventory Inventory(DateTimeOffset updated) => new([new(10, "v1", "release", false, false, null,
            [new(100, "asset.bin", 3, null, updated)])]);
        var oldPlan = await service.PlanAsync(releases, "fixture-user", "repo", Inventory(DateTimeOffset.UnixEpoch), default);
        await service.MaterializeAsync(oldPlan, () => long.MaxValue, default);
        await service.CommitIndexAsync(oldPlan, default);
        byte[] oldIndex = await File.ReadAllBytesAsync(Path.Combine(releases, "releases.json"));
        var changed = await service.PlanAsync(releases, "fixture-user", "repo", Inventory(DateTimeOffset.UnixEpoch.AddDays(1)), default);
        fail = true; calls = 0;
        var metadataError = await Assert.ThrowsExactlyAsync<HttpTransferException>(() => new RawPageStore(metadata)
            .SaveMetadataAsync("fixture-user", "repo", "issues.pages.json", transport, default));
        Assert.AreEqual(metadataCode, metadataError.Code);
        Assert.AreEqual(expectedAttempts, calls);
        calls = 0;
        var assetError = await Assert.ThrowsExactlyAsync<HttpTransferException>(() => service.MaterializeAsync(changed, () => long.MaxValue, default));
        Assert.AreEqual(assetCode, assetError.Code);
        Assert.AreEqual(expectedAttempts, calls);
        foreach (var error in new[] { metadataError, assetError })
            Assert.IsFalse(error.ToString().Contains("SYNTHETIC_TASK9_", StringComparison.Ordinal));
        CollectionAssert.AreEqual(previousPage, await File.ReadAllBytesAsync(target));
        CollectionAssert.AreEqual(oldIndex, await File.ReadAllBytesAsync(Path.Combine(releases, "releases.json")));
        CollectionAssert.AreEqual(previousAsset, await File.ReadAllBytesAsync(oldPlan.Generations[0].Assets[0].FullPath));
        Assert.IsFalse(File.Exists(changed.Generations[0].Assets[0].FullPath));
        Assert.IsEmpty(Directory.GetFiles(root.Path, ".atomic-*.tmp", SearchOption.AllDirectories));
        transport.Dispose();
        Assert.IsTrue(secret.All(value => value == 0));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Core_failure_before_later_cancellation_remains_fail_in_returned_and_saved_summary(bool full)
    {
        await using var f = await OrchestrationFixture.CreateAsync(repositoryNames: ["repo", "healthy"]);
        using var cancellation = new CancellationTokenSource();
        f.Git.BeforeRun = request =>
        {
            if (request.Arguments.Contains("https://github.com/fixture-user/repo.git")) f.Git.Fault = "clone";
            if (request.Arguments.Contains("https://github.com/fixture-user/healthy.git"))
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };

        var result = await f.Orchestrator.RunAsync(f.Request(full ? BackupMode.Full : BackupMode.Daily), cancellation.Token);

        Assert.AreEqual(RunStatus.Fail, result.Summary.Status);
        Assert.IsTrue(result.Summary.WasCancelled);
        CollectionAssert.Contains(result.Summary.FailedRepositories.ToArray(), "repo");
        var history = await new SummaryStore(f.Local.Paths).ReadLatestAsync(f.Local.OwnerRoot, f.Local.Paths.DiagnosticFallbackRoot, default);
        Assert.IsNotNull(history.Latest);
        Assert.AreEqual(RunStatus.Fail, history.Latest.Status);
        Assert.IsTrue(history.Latest.WasCancelled);
        CollectionAssert.Contains(history.Latest.FailedRepositories.ToArray(), "repo");
        Assert.IsNull(history.LatestCoreSuccess);
        Assert.IsFalse(result.HasPendingCleanup || f.Orchestrator.IsBusy);
        Assert.IsFalse(File.Exists(Path.Combine(f.Local.OwnerRoot, "RUNNING.json")));
    }
}
