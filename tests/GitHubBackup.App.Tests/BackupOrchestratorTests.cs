using System.Net;
using System.Text;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class BackupOrchestratorTests
{
    [TestMethod]
    [DataRow(false, "clone")][DataRow(true, "clone")]
    [DataRow(false, "fetch")][DataRow(true, "fetch")]
    [DataRow(false, "core-fsck")][DataRow(true, "core-fsck")]
    public async Task Failed_core_preserves_optional_artifacts_and_continues_next_repository(bool full, string fault)
    {
        await using var f = await OrchestrationFixture.CreateAsync(repositoryNames: ["repo", "healthy"]);
        f.Local.MakeDirectory("manifests");
        await new ManifestStore().WriteAsync(f.Local.OwnerRoot, "old", [PreflightFixture.Repository], default);
        string mirror = Path.Combine(f.Local.OwnerRoot, "mirrors", "repo.git");
        if (fault != "clone")
        {
            f.Local.MakeDirectory(Path.Combine("mirrors", "repo.git"));
            MirrorSafeCopyTests.Write(mirror, "HEAD", "ref: refs/heads/main\n");
            MirrorSafeCopyTests.Write(mirror, "marker", "old mirror");
        }
        string wiki = f.Local.MakeDirectory(Path.Combine("wikis", "repo.wiki.git"));
        MirrorSafeCopyTests.Write(wiki, "HEAD", "ref: refs/heads/main\n");
        MirrorSafeCopyTests.Write(wiki, "marker", "old wiki");
        string metadata = f.Local.MakeDirectory(Path.Combine("metadata", "repo"));
        f.Local.Write(Path.Combine(metadata, "repository-summary.json"), "{\"old\":true}");
        f.Local.Write(Path.Combine(metadata, "repository.json"), "{\"old\":true}");
        f.Local.Write(Path.Combine(metadata, "releases.pages.json"), "[[]]");
        f.Local.MakeDirectory("releases");
        string releaseRoot = Path.Combine(f.Local.OwnerRoot, "releases", "repo");
        byte[] oldBytes = "OLD!"u8.ToArray(), newBytes = "NEW!"u8.ToArray();
        f.Http.Override = (path, _) => Task.FromResult<HttpResponseMessage?>(path.Contains("/releases/assets/")
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(oldBytes) } : null);
        var releases = new ReleaseAssetService(f.Session.HttpTransport!);
        var oldInventory = new ReleaseInventory([new(10, "v1", "old", false, false, null,
            [new(100, "asset.bin", 4, "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(oldBytes)), DateTimeOffset.Parse("2026-09-20T00:00:00Z"))])]);
        var oldPlan = await releases.PlanAsync(releaseRoot, "fixture-user", "repo", oldInventory, default);
        await releases.MaterializeAsync(oldPlan, () => long.MaxValue, default);
        await releases.CommitIndexAsync(oldPlan, default);
        string[] preserved = fault == "clone" ? [wiki, metadata, releaseRoot] : [mirror, wiki, metadata, releaseRoot];
        var before = preserved.ToDictionary(root => root, Snapshot);
        f.Http.Paths.Clear();
        string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(newBytes));
        f.Http.ReleaseJson = "[{\"id\":20,\"tag_name\":\"v2\",\"name\":\"new\",\"draft\":false,\"prerelease\":false,\"published_at\":null,\"assets\":[{\"id\":200,\"name\":\"asset.bin\",\"size\":4,\"digest\":\"sha256:" + digest + "\",\"updated_at\":\"2026-09-21T00:00:00Z\"}]}]";
        f.Http.Override = (path, _) => Task.FromResult<HttpResponseMessage?>(path.Contains("/releases/assets/")
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(newBytes) } : null);
        bool injected = false;
        f.Git.BeforeRun = request =>
        {
            bool firstCore = request.Arguments.Contains("https://github.com/fixture-user/repo.git")
                || Path.GetFileName(request.WorkingDirectory).StartsWith(".repo.git.staging-", StringComparison.Ordinal);
            f.Git.Fault = firstCore ? fault : "";
            if (firstCore && (request.Arguments.Contains(fault) || fault == "core-fsck"
                && request.Arguments.Contains("fsck") && !request.FilePath.EndsWith("git-lfs.exe")))
            {
                injected = true;
                // The next repository fits only after removing the skipped Release allocation.
                f.FreeBytes = 2 * DiskSpacePolicy.SafetyReserveBytes + (full ? 4 : 0);
            }
        };

        var result = await f.Orchestrator.RunAsync(f.Request(full ? BackupMode.Full : BackupMode.Daily), default);
        Assert.IsTrue(injected); Assert.AreEqual(RunStatus.Fail, result.Summary.Status);
        CollectionAssert.AreEqual(new[] { "repo" }, result.Summary.FailedRepositories.ToArray());
        Assert.AreEqual(2, result.Summary.RepositoryCount); Assert.AreEqual(1, result.Summary.SkippedWikiCount);
        Assert.AreEqual(0, result.Summary.WarningCount); Assert.IsFalse(result.HasPendingCleanup);
        foreach (string root in preserved)
        {
            var after = Snapshot(root); CollectionAssert.AreEquivalent(before[root].Keys.ToArray(), after.Keys.ToArray(), root);
            foreach (var (path, prior) in before[root])
            {
                Assert.AreEqual(prior.Modified, after[path].Modified, path);
                CollectionAssert.AreEqual(prior.Bytes, after[path].Bytes, path);
            }
        }
        if (fault == "clone") Assert.IsFalse(Directory.Exists(mirror));
        Assert.IsFalse(f.Git.Requests.Any(r => r.Arguments.Contains("https://github.com/fixture-user/repo.wiki.git")
            || r.WorkingDirectory.Contains(".repo.wiki.git.staging-", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(full ? new[] { "/repos/fixture-user/repo/releases" } : Array.Empty<string>(),
            f.Http.Paths.Where(path => path == "/repos/fixture-user/repo" || path.StartsWith("/repos/fixture-user/repo/", StringComparison.Ordinal)).ToArray());
        Assert.IsTrue(Directory.Exists(Path.Combine(f.Local.OwnerRoot, "mirrors", "healthy.git")));
        Assert.IsTrue(File.Exists(Path.Combine(f.Local.OwnerRoot, "metadata", "healthy", "releases.pages.json")));
        if (full)
        {
            Assert.Contains("new", File.ReadAllText(Path.Combine(f.Local.OwnerRoot, "releases", "healthy", "releases.json")));
            CollectionAssert.AreEqual(newBytes, File.ReadAllBytes(Directory.GetFiles(Path.Combine(f.Local.OwnerRoot, "releases", "healthy"), "asset.bin-*", SearchOption.AllDirectories).Single()));
        }

        static Dictionary<string, (byte[] Bytes, DateTime Modified)> Snapshot(string root) =>
            Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root)
                .ToDictionary(path => path, path => (Directory.Exists(path) ? Array.Empty<byte>() : File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)));
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task Prior_marker_requires_matching_durable_terminal_summary_and_no_unresolved_cleanup(bool pending)
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        using (var owner = OperationLocks.AcquireStorage(f.Local.OwnerRoot, "old"))
        {
            await RunningMarkerStore.WriteAsync(owner, new("old", BackupMode.Daily, "fixture-user", started), default);
            var summary = BackupSummary.PreflightFailure(BackupMode.Daily, "fixture-user", "old", f.Local.OwnerRoot,
                "cleanup", pending ? "BACKUP_CLEANUP_PENDING" : "OLD_FAILURE") with { StartedAt = started };
            await new SummaryStore(f.Local.Paths).WriteAsync(f.Local.OwnerRoot, summary, f.Local.Paths.DiagnosticFallbackRoot, default);
        }
        var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), default);
        Assert.AreEqual(pending ? RunStatus.Fail : RunStatus.Pass, result.Summary.Status, result.Summary.ErrorCode);
        Assert.AreEqual(pending, File.Exists(Path.Combine(f.Local.OwnerRoot, "RUNNING.json")));
        if (pending) { Assert.HasCount(0, f.Git.Requests); Assert.HasCount(0, f.Http.Paths); }
    }

    [TestMethod]
    public async Task Process_death_is_followed_by_locked_orchestrator_recovery_before_disk_failure()
    {
        using var root = new StorageTestRoot();
        string safe = root.Child("safe"); AclPolicy.CreateRestrictedDirectory(safe, root.User);
        string spec = Path.Combine(safe, "crash.json"), signal = Path.Combine(safe, "boundary");
        File.WriteAllText(spec, System.Text.Json.JsonSerializer.Serialize(new MirrorPromotionCrashProcessHarness.CrashSpec(safe, 1, "crash")));
        try
        {
            using (var process = MirrorPromotionCrashProcessHarness.StartWorker(spec))
            {
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (!File.Exists(signal))
                    {
                        if (process.HasExited) Assert.Fail(await output + await error);
                        if (watch.Elapsed > TimeSpan.FromSeconds(40)) Assert.Fail("Crash fixture timed out.");
                        await Task.Delay(25);
                    }
                }
                finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
                await Task.WhenAll(output, error);
            }
            await using var f = await OrchestrationFixture.CreateAsync(Path.Combine(safe, "backup"), emptyDiscovery: true, realGit: true);
            f.Local.MakeDirectory("manifests");
            await new ManifestStore().WriteAsync(f.Local.OwnerRoot, "old", [PreflightFixture.Repository], default);
            f.Orchestrator = new BackupOrchestrator(new ProcessRunner(), f.Local.Paths, _ => 0, f.Local.Audit);
            var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Full), default);
            Assert.AreEqual("BACKUP_INSUFFICIENT_FREE_SPACE", result.Summary.ErrorCode);
            Assert.IsFalse(result.HasPendingCleanup);
            Assert.HasCount(0, Directory.GetFiles(f.Local.OwnerRoot, "*.swap-*.json", SearchOption.AllDirectories));
            Assert.IsTrue(Directory.Exists(Path.Combine(f.Local.OwnerRoot, "mirrors", "repo.git")));
            Assert.HasCount(1, Directory.GetFiles(Path.Combine(f.Local.OwnerRoot, "manifests")));
            Assert.IsFalse(Directory.Exists(Path.Combine(f.Local.OwnerRoot, "logs")));
        }
        finally { MirrorFixture.ClearReadOnly(root.Path); }
    }

    [TestMethod]
    public async Task Ignored_header_cancellation_keeps_owner_and_ui_until_handler_finishes()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Http.BeforeReply = async (path, _) => { if (path.EndsWith("/releases")) { entered.SetResult(); await release.Task; } };
        var run = f.Orchestrator.RunAsync(f.Request(BackupMode.Full), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancel = f.Orchestrator.CancelAsync();
        Assert.IsFalse(cancel.IsCompleted); Assert.IsTrue(f.Orchestrator.IsBusy);
        Assert.Throws<IOException>(() => OperationLocks.AcquireStorage(f.Local.OwnerRoot, "other"));
        release.SetResult(); await cancel;
        Assert.AreEqual(RunStatus.Cancelled, (await run).Summary.Status);
    }

    [TestMethod]
    public async Task Daily_reaudits_all_history_under_owner_lock_after_preflight()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        string history = f.Local.MakeFile(Path.Combine("releases", "deleted", "asset.bin"));
        f.Local.DescriptorOverrides[history] = PreflightFixture.Descriptor(f.Local.User, System.Security.AccessControl.FileSystemRights.ReadData);
        var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), default);
        Assert.AreEqual(RunStatus.Fail, result.Summary.Status); Assert.AreEqual("audit", result.Summary.FailurePhase);
        Assert.HasCount(0, f.Git.Requests); Assert.HasCount(0, f.Http.Paths);
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Local.OwnerRoot, "manifests")));
    }

    [TestMethod]
    public async Task Deleted_history_recovers_before_insufficient_disk_gate_without_new_transfer_or_artifacts()
    {
        await using var f = await OrchestrationFixture.CreateAsync(emptyDiscovery: true);
        f.Local.MakeDirectory("manifests");
        await new ManifestStore().WriteAsync(f.Local.OwnerRoot, "old", [PreflightFixture.Repository], default);
        string mirror = f.Local.MakeDirectory(Path.Combine("mirrors", "repo.git"));
        MirrorSafeCopyTests.Write(mirror, "HEAD", "ref: refs/heads/main\n");
        MirrorSafeCopyTests.Write(mirror, "marker", "old");
        string journal = Path.Combine(f.Local.OwnerRoot, "mirrors", ".repo.git.swap-old.json");
        f.Local.Write(journal, "{\"Version\":1,\"LocalName\":\"repo\",\"RunId\":\"old\",\"Wiki\":false,\"Promoted\":false}");
        f.FreeBytes = 0;
        var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Full), default);
        Assert.AreEqual("BACKUP_INSUFFICIENT_FREE_SPACE", result.Summary.ErrorCode);
        Assert.IsFalse(File.Exists(journal)); Assert.AreEqual("old", File.ReadAllText(Path.Combine(mirror, "marker")));
        Assert.IsNotEmpty(f.Git.Requests);
        Assert.IsTrue(f.Git.Requests.All(r => r.Arguments.Contains("rev-parse") || r.Arguments.Contains("fsck")));
        Assert.HasCount(0, f.Http.Paths);
        Assert.HasCount(1, Directory.GetFiles(Path.Combine(f.Local.OwnerRoot, "manifests")));
        Assert.IsFalse(File.Exists(Path.Combine(f.Local.OwnerRoot, "RUNNING.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Local.OwnerRoot, "logs")));
    }

    [TestMethod]
    public async Task Backup_root_cannot_change_after_session_preflight()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        string unapproved = Path.Combine(f.Local.Root.Path, "other");
        var request = f.Request(BackupMode.Daily);
        var result = await f.Orchestrator.RunAsync(request with { Settings = request.Settings with { BackupRoot = unapproved } }, default);
        Assert.AreEqual(RunStatus.Fail, result.Summary.Status);
        Assert.IsFalse(Directory.Exists(unapproved)); Assert.HasCount(0, f.Git.Requests);
    }

    [TestMethod]
    public async Task Cancelled_atomic_cleanup_failure_retains_owned_file_and_owner_until_retry()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FileStream? held = null;
        f.Http.Override = (path, _) => Task.FromResult<HttpResponseMessage?>(path.EndsWith("/issues")
            ? new(HttpStatusCode.OK) { Content = new StreamContent(new PausedReadStream(async token =>
            {
                string temporary = Directory.GetFiles(Path.Combine(f.Local.OwnerRoot, "metadata", "repo"), ".atomic-*.tmp").Single();
                held = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                entered.SetResult(); await Task.Delay(Timeout.Infinite, token);
            })) } : null);
        var run = f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Orchestrator.CancelAsync(); var result = await run;
        try
        {
            Assert.IsTrue(result.HasPendingCleanup); Assert.IsTrue(result.Summary.WasCancelled);
            Assert.Throws<IOException>(() => OperationLocks.AcquireStorage(f.Local.OwnerRoot, "other"));
        }
        finally { held?.Dispose(); }
        await f.Orchestrator.RetryCleanupAsync();
        Assert.IsFalse(f.Orchestrator.HasPendingCleanup);
        Assert.HasCount(0, Directory.GetFiles(f.Local.OwnerRoot, ".atomic-*.tmp", SearchOption.AllDirectories));
    }

    [TestMethod]
    [DataRow("headers")][DataRow("body")][DataRow("backoff")]
    public async Task Cancellation_drains_native_http_and_releases_owner_only_after_completion(string stage)
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Http.Override = async (path, token) =>
        {
            if (!path.EndsWith("/releases")) return null;
            if (stage == "headers") { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            if (stage == "body") return new(HttpStatusCode.OK) { Content = new StreamContent(new PausedReadStream(async ct =>
                { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); })) };
            entered.TrySetResult(); return new(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) };
        };
        var run = f.Orchestrator.RunAsync(f.Request(BackupMode.Full), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (stage == "backoff") await Task.Delay(25);
        await f.Orchestrator.CancelAsync(); var result = await run;
        Assert.AreEqual(RunStatus.Cancelled, result.Summary.Status, result.Summary.ErrorCode);
        Assert.IsFalse(result.HasPendingCleanup); Assert.IsFalse(f.Orchestrator.IsBusy);
        Assert.HasCount(0, f.Git.Requests);
        using var owner = OperationLocks.AcquireStorage(f.Local.OwnerRoot, "next");
    }

    [TestMethod]
    public async Task Cleanup_of_owned_staging_after_cancel_keeps_independent_recovery_capability()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        using var cancel = new CancellationTokenSource();
        f.Orchestrator = new BackupOrchestrator(f.Git, f.Local.Paths, _ => long.MaxValue, f.Local.Audit,
            promotionBoundary: boundary => { if (boundary == PromotionBoundary.AfterJournal) cancel.Cancel(); });
        var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), cancel.Token);
        Assert.AreEqual(RunStatus.Cancelled, result.Summary.Status);
        Assert.IsFalse(result.HasPendingCleanup);
        Assert.IsTrue(f.Git.Requests.Any(r => r.WorkingDirectory.Contains(".recovery-")));
        Assert.HasCount(0, Directory.GetFiles(f.Local.OwnerRoot, "*.swap-*.json", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task Cancellation_at_release_commit_finishes_atomic_commit_before_unlocking()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        f.Http.ReleaseJson = "[{\"id\":10,\"tag_name\":\"v1\",\"name\":\"one\",\"draft\":false,\"prerelease\":false,\"published_at\":null,\"assets\":[]}]";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Orchestrator = new BackupOrchestrator(f.Git, f.Local.Paths, _ => long.MaxValue, f.Local.Audit,
            releaseCommitHooks: new(CommitStarted: async () => { entered.SetResult(); await release.Task; }));
        var run = f.Orchestrator.RunAsync(f.Request(BackupMode.Full), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task cancellation = f.Orchestrator.CancelAsync();
        Assert.IsFalse(cancellation.IsCompleted); Assert.IsTrue(f.Orchestrator.IsBusy);
        Assert.Throws<IOException>(() => OperationLocks.AcquireStorage(f.Local.OwnerRoot, "other"));
        release.SetResult(); await cancellation; var result = await run;
        Assert.AreEqual(RunStatus.Cancelled, result.Summary.Status);
        using var index = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Local.OwnerRoot, "releases", "repo", "releases.json")));
        Assert.AreEqual(10, index.RootElement[0].GetProperty("releaseId").GetInt32());
        Assert.HasCount(0, Directory.GetFiles(f.Local.OwnerRoot, ".atomic-*.tmp", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task Daily_uses_reconciled_names_and_updates_raw_release_pages_without_changing_release_history()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        f.Local.MakeDirectory("manifests");
        await new ManifestStore().WriteAsync(f.Local.OwnerRoot, "old", [PreflightFixture.Repository with { LocalName = "kept" }], default);
        string history = f.Local.MakeFile(Path.Combine("releases", "history", "opaque.txt"));
        DateTime timestamp = File.GetLastWriteTimeUtc(history);
        var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), default);
        Assert.AreEqual(RunStatus.Pass, result.Summary.Status, result.Summary.ErrorCode);
        Assert.IsTrue(Directory.Exists(Path.Combine(f.Local.OwnerRoot, "mirrors", "kept.git")));
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Local.OwnerRoot, "mirrors", "repo.git")));
        Assert.AreEqual(1, f.Http.Paths.Count(p => p.EndsWith("/releases")));
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(history));
        Assert.AreEqual("opaque fixture", File.ReadAllText(history));
        Assert.IsTrue(File.Exists(Path.Combine(f.Local.OwnerRoot, "metadata", "kept", "releases.pages.json")));
        Assert.IsTrue(f.Local.DescriptorsRead.Any(p => p.EndsWith("opaque.txt")));
        var mappings = await new ManifestStore().ReadLatestAsync(f.Local.OwnerRoot, default);
        Assert.AreEqual("kept", mappings.Single().LocalName);
    }

    [TestMethod]
    public async Task Running_marker_is_present_during_transfer_and_removed_after_durable_summary()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        string marker = Path.Combine(f.Local.OwnerRoot, "RUNNING.json");
        bool observed = false;
        f.Git.BeforeRun = _ => { Assert.IsTrue(File.Exists(marker)); observed = true; };
        var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), default);
        Assert.IsTrue(observed); Assert.AreEqual(RunStatus.Pass, result.Summary.Status);
        Assert.IsFalse(File.Exists(marker));
        Assert.IsTrue(File.Exists(Path.Combine(f.Local.OwnerRoot, "manifests", "summary-" + result.Summary.StartedRunId + ".json")));
    }

    [TestMethod]
    public async Task Unmatched_stale_marker_is_preserved_and_blocks_new_owner_work()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        string marker = Path.Combine(f.Local.OwnerRoot, "RUNNING.json");
        f.Local.Write(marker, "{\"runId\":\"old\",\"mode\":\"daily\",\"owner\":\"fixture-user\",\"startedAt\":\"2026-09-20T00:00:00Z\"}");
        byte[] before = File.ReadAllBytes(marker);
        var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Full), default);
        Assert.AreEqual(RunStatus.Fail, result.Summary.Status);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(marker));
        Assert.HasCount(0, f.Git.Requests); Assert.HasCount(0, f.Http.Paths);
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Local.OwnerRoot, "manifests")));
    }

    [TestMethod]
    public async Task Insufficient_space_after_locked_fresh_planning_writes_no_run_artifacts()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        f.FreeBytes = 0;
        var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Full), default);
        Assert.AreEqual(RunStatus.Fail, result.Summary.Status);
        Assert.AreEqual("BACKUP_INSUFFICIENT_FREE_SPACE", result.Summary.ErrorCode);
        Assert.IsTrue(f.Http.Paths.Any(p => p.EndsWith("/releases")));
        Assert.IsTrue(File.Exists(Path.Combine(f.Local.Paths.DiagnosticFallbackRoot, "summary-" + result.Summary.StartedRunId + ".json")));
        Assert.HasCount(0, f.Git.Requests);
        foreach (string name in new[] { "manifests", "logs", "mirrors", "metadata", "releases" })
            Assert.IsFalse(Directory.Exists(Path.Combine(f.Local.OwnerRoot, name)), name);
    }

    [TestMethod]
    public async Task Competing_full_run_cannot_plan_or_commit_until_first_cleanup_releases_owner()
    {
        await using var first = await OrchestrationFixture.CreateAsync();
        await using var second = await OrchestrationFixture.CreateAsync(first.Local.BackupRoot);
        first.Http.ReleaseJson = "[{\"id\":10,\"tag_name\":\"v1\",\"name\":\"first\",\"draft\":false,\"prerelease\":false,\"published_at\":null,\"assets\":[]}]";
        second.Http.ReleaseJson = "[{\"id\":20,\"tag_name\":\"v2\",\"name\":\"second\",\"draft\":false,\"prerelease\":false,\"published_at\":null,\"assets\":[]}]";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Http.BeforeReply = async (path, token) => { if (path.EndsWith("/releases")) { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        Task<BackupRunResult> firstRun = first.Orchestrator.RunAsync(first.Request(BackupMode.Full), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondResult = await second.Orchestrator.RunAsync(second.Request(BackupMode.Full), default);
        Assert.AreEqual(RunStatus.Fail, secondResult.Summary.Status);
        Assert.IsFalse(second.Http.Paths.Any(p => p.Contains("/releases")));
        Assert.HasCount(0, second.Git.Requests);
        release.SetResult(); var result = await firstRun;
        Assert.AreEqual(RunStatus.Pass, result.Summary.Status, result.Summary.ErrorCode);
        string index = File.ReadAllText(Path.Combine(first.Local.OwnerRoot, "releases", "repo", "releases.json"));
        Assert.Contains("first", index); Assert.DoesNotContain("second", index);
    }

    [TestMethod]
    public async Task Session_cleanup_failure_retains_owner_until_explicit_retry()
    {
        await using var f = await OrchestrationFixture.CreateAsync();
        f.Http.FailDispose = true;
        var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), default);
        Assert.IsTrue(result.HasPendingCleanup); Assert.IsTrue(f.Orchestrator.HasPendingCleanup);
        Assert.IsTrue(File.Exists(Path.Combine(f.Local.OwnerRoot, "RUNNING.json")));
        using (var summary = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Local.OwnerRoot, "manifests", "summary-" + result.Summary.StartedRunId + ".json"))))
            Assert.AreEqual("FAIL", summary.RootElement.GetProperty("status").GetString());
        Assert.Throws<IOException>(() => OperationLocks.AcquireStorage(f.Local.OwnerRoot, "other"));
        f.Http.FailDispose = false; await f.Orchestrator.RetryCleanupAsync();
        Assert.IsFalse(f.Orchestrator.HasPendingCleanup);
        using var next = OperationLocks.AcquireStorage(f.Local.OwnerRoot, "other");
    }
}

internal sealed class OrchestrationFixture : IAsyncDisposable
{
    internal PreflightFixture Local { get; }
    internal OrchestrationHttp Http { get; } = new();
    internal MirrorRunner Git { get; } = new();
    internal PreflightSession Session = null!;
    internal BackupOrchestrator Orchestrator = null!;
    internal long FreeBytes = long.MaxValue;
    private OrchestrationFixture(string? backupRoot) => Local = new(backupRoot);
    internal BackupRequest Request(BackupMode mode) => new(mode, new("fixture-user", Local.BackupRoot, NetworkMode.Auto), Session);
    internal static async Task<OrchestrationFixture> CreateAsync(string? backupRoot = null, bool emptyDiscovery = false, bool realGit = false, string[]? repositoryNames = null,
        Action<OrchestrationHttp>? configureHttp = null, IGitHubCredentialReader? reader = null)
    {
        var f = new OrchestrationFixture(backupRoot);
        f.Http.EmptyDiscovery = emptyDiscovery;
        if (repositoryNames is not null) f.Http.RepositoryNames = repositoryNames;
        configureHttp?.Invoke(f.Http);
        if (realGit) f.Local.Tools = f.Local.Tools with { Git = LocalGitRunner.Git };
        var settings = new AppSettings("fixture-user", f.Local.BackupRoot, NetworkMode.Auto)
            { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        await new SettingsStore(f.Local.Paths).SaveAsync(settings, default);
        var service = new PreflightService(f.Local.Auth, f.Local.Runner, (_, _) => Task.FromResult(f.Local.Tools), f.Local.Environment,
            new ProxyScope(new NetworkProbe(f.Local.Runner), new Dictionary<string, string?>(), origin => origin), f.Local.Audit,
            new(_ => DriveType.Fixed, path => File.Exists(path) || Directory.Exists(path) ? File.GetAttributes(path) : null, (_, _) => Task.CompletedTask), _ => long.MaxValue);
        var result = await service.CheckNativeAsync(BackupMode.Full, settings, f.Local.Job, reader ?? new Reader(), default, _ => f.Http);
        Assert.IsNotNull(result.LiveSession, string.Join(',', result.Snapshot.Report.Issues.Select(i => i.ErrorCode)));
        f.Session = result.LiveSession;
        f.Orchestrator = new BackupOrchestrator(f.Git, f.Local.Paths, _ => f.FreeBytes, f.Local.Audit);
        f.Http.Paths.Clear(); f.Local.DescriptorsRead.Clear();
        return f;
    }
    public async ValueTask DisposeAsync()
    {
        Http.FailDispose = false;
        await Orchestrator.RetryCleanupAsync(); await Session.DisposeAsync();
        MirrorFixture.ClearReadOnly(Local.Root.Path); Local.Dispose();
    }
    private sealed class Reader : IGitHubCredentialReader
    { public GitHubCredentialLease ReadExact(string login) => new(login, "SYNTHETIC_ORCHESTRATION_SECRET"u8.ToArray()); }
}

internal sealed class OrchestrationHttp : HttpMessageHandler
{
    internal List<string> Paths { get; } = [];
    internal Func<string, CancellationToken, Task>? BeforeReply;
    internal Func<string, CancellationToken, Task<HttpResponseMessage?>>? Override;
    internal Action<HttpRequestMessage>? ObserveRequest;
    internal bool FailDispose;
    internal bool EmptyDiscovery;
    internal string[] RepositoryNames = ["repo"];
    internal string ReleaseJson = "[]";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string path = request.RequestUri!.AbsolutePath; Paths.Add(path);
        ObserveRequest?.Invoke(request);
        if (BeforeReply is not null) await BeforeReply(path, token);
        if (Override is not null && await Override(path, token) is { } custom) return custom;
        string json = path == "/user" ? "{\"login\":\"fixture-user\",\"id\":7}" : path == "/user/repos"
            ? EmptyDiscovery ? "[]" : System.Text.Json.JsonSerializer.Serialize(RepositoryNames.Select((name, index) => new
                { id = index + 1, name, full_name = "fixture-user/" + name, html_url = "https://github.com/fixture-user/" + name,
                    owner = new { login = "fixture-user", id = 7 }, size = 0, @private = false, archived = false, fork = false, has_wiki = true }))
            : path.EndsWith("/releases") ? ReleaseJson : RepositoryNames.Any(name => path == "/repos/fixture-user/" + name) || path.EndsWith("/workflows") ? "{}" : "[]";
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)) };
    }
    protected override void Dispose(bool disposing)
    {
        if (FailDispose) throw new IOException("PRIVATE_RAW_EXCEPTION");
        base.Dispose(disposing);
    }
}

internal sealed class PausedReadStream(Func<CancellationToken, Task> read) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { await read(token); return 0; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
