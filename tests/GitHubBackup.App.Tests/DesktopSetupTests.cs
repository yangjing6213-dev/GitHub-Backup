using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class DesktopSetupTests
{
    [TestMethod]
    [DataRow("detect")][DataRow("install")][DataRow("login")][DataRow("check")]
    public async Task Incoming_runtime_cleanup_carrier_stays_owned_through_each_workflow_entry(string kind)
    {
        using var f = new PreflightFixture();
        var context = await GitRuntimeContext.CreatePublicProbeAsync(f.Environment, default);
        using var pinned = NativeFileSystem.Open(context.Environment["GIT_CONFIG_GLOBAL"]!);
        var carrier = new GitRuntimeCleanupException(context);
        var actions = new DesktopWorkflow(f.Paths, f.Runner, detectTools: (_, _) => throw carrier,
            check: (_, _, _, _) => throw carrier).Actions;
        try
        {
            await Assert.ThrowsAsync<GitRuntimeCleanupException>(async () =>
            {
                switch (kind)
                {
                    case "detect": await actions.DetectTools(default); break;
                    case "install": await actions.Install(BackupMode.Daily, new("fixture-user", f.BackupRoot, NetworkMode.Auto), DependencyId.Git, true, default); break;
                    case "login": await actions.Login(new Progress<string>(), default); break;
                    default: await actions.Check(BackupMode.Daily, new("fixture-user", f.BackupRoot, NetworkMode.Auto), default); break;
                }
            });
            Assert.IsTrue(actions.HasPendingCleanup());
            await Assert.ThrowsAsync<IOException>(actions.RetryCleanup);
        }
        finally { pinned.Dispose(); await actions.RetryCleanup(); await context.DisposeAsync(); }
        Assert.IsFalse(actions.HasPendingCleanup()); Assert.HasCount(0, f.Runner.Requests);
    }

    [TestMethod]
    public async Task Preflight_service_retains_propagated_detector_context_and_returns_only_failed_snapshot()
    {
        using var f = new PreflightFixture();
        var context = await GitRuntimeContext.CreatePublicProbeAsync(f.Environment, default);
        using var pinned = NativeFileSystem.Open(context.Environment["GIT_CONFIG_GLOBAL"]!);
        var service = new PreflightService(f.Auth, f.Runner, (_, _) => throw new GitRuntimeCleanupException(context), f.Environment,
            new ProxyScope(new NetworkProbe(f.Runner), new Dictionary<string, string?>(), origin => origin), f.Audit,
            new(_ => DriveType.Fixed, _ => FileAttributes.Directory, (_, _) => Task.CompletedTask), _ => long.MaxValue);
        try
        {
            var result = await service.CheckAsync(BackupMode.Daily, new("fixture-user", f.BackupRoot, NetworkMode.Auto), f.Job,
                (_, _, _, _, _) => throw new AssertFailedException("No discovery after detection cleanup failure"), default);
            Assert.IsNull(result.LiveSession); Assert.IsFalse(result.Snapshot.Report.CanStartBackup);
            Assert.IsTrue(service.HasPendingCleanup); Assert.HasCount(0, result.Snapshot.ChildEnvironment); Assert.IsNull(result.Snapshot.EmptyHooksDirectory);
            await Assert.ThrowsAsync<IOException>(service.RetryCleanupAsync);
        }
        finally { pinned.Dispose(); await service.RetryCleanupAsync(); }
        Assert.IsFalse(service.HasPendingCleanup); Assert.HasCount(0, f.Runner.Requests);
    }

    [TestMethod]
    [DataRow(0)][DataRow(1)][DataRow(-2147023673)]
    public async Task Install_routes_only_one_package_and_rechecks_even_when_installer_fails(int exit)
    {
        using var root = new StorageTestRoot(); using var tools = await TestToolBuilder.CreateAsync(); var runner = new ScriptedProcessRunner();
        var tool = new ToolDetection(tools.Executable, ExecutableTrust.CaptureTrustedIdentity(tools.Executable), "v1.29.290", true);
        int detections = 0, checks = 0; OperationJob? operation = null;
        runner.Results.Enqueue(request => new(exit, false, false, [], []));
        var actions = new DesktopWorkflow(AppPaths.Create(root.Path), runner,
            detectTools: (job, _) => { detections++; if (operation is null) operation = job; else Assert.AreSame(operation, job); return Task.FromResult(new ToolInventory(tool, tool, tool, tool)); },
            check: (_, selected, job, _) => { checks++; Assert.AreSame(operation, job); Assert.IsFalse(selected.HasApiCredentialConsentFor("owner")); return Task.FromResult(Snapshot()); }).Actions;
        var result = await actions.Install(BackupMode.Daily, new("owner", root.Path, NetworkMode.Auto), DependencyId.GitLfs, true, default);
        Assert.AreEqual(exit == 0, result.Report.CanStartBackup); Assert.AreEqual(2, detections); Assert.AreEqual(1, checks);
        CollectionAssert.AreEqual(new[] { "install", "--id", "GitHub.GitLFS", "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements" }, runner.Requests.Single().Arguments.ToArray());
        Assert.IsFalse(Directory.Exists(runner.Requests.Single().WorkingDirectory));
    }

    [TestMethod]
    public async Task Root_save_failure_and_reparse_preserve_previous_settings()
    {
        using var root = new StorageTestRoot(); var paths = AppPaths.Create(root.Path); var old = new AppSettings("owner", root.Path, NetworkMode.Auto);
        await new SettingsStore(paths).SaveAsync(old, default); string target = root.Child("target"); AclPolicy.CreateRestrictedDirectory(target, root.User);
        var actions = new DesktopWorkflow(paths, new ScriptedProcessRunner(), check: (_, _, _, _) => throw new AssertFailedException("No recheck after failed save")).Actions;
        using (var pinned = NativeFileSystem.Open(paths.SettingsFile))
            await Assert.ThrowsAsync<IOException>(() => actions.SelectRoot(BackupMode.Daily, old with { BackupRoot = target }, true, false, default));
        Assert.AreEqual(old, (await new SettingsStore(paths).LoadAsync(default)).Settings); Assert.IsFalse(actions.HasPendingCleanup());
        string junction = root.Child("junction"); StorageTestRoot.CreateJunction(junction, target);
        try
        {
            await Assert.ThrowsAsync<Exception>(() => actions.SelectRoot(BackupMode.Daily, old with { BackupRoot = junction }, true, false, default));
            Assert.AreEqual(old, (await new SettingsStore(paths).LoadAsync(default)).Settings);
            Assert.HasCount(0, Directory.GetFileSystemEntries(target));
        }
        finally { Directory.Delete(junction); }
    }

    [TestMethod]
    public async Task Setup_atomic_failure_keeps_owned_cleanup_and_never_delivers_or_rechecks()
    {
        using var root = new StorageTestRoot(); string owned = root.Child("owned.tmp"); NativeFileIdentity identity;
        using (var file = AclPolicy.CreateRestrictedFile(owned, root.User)) identity = NativeFileSystem.Inspect(file.SafeFileHandle, owned, false);
        using var pinned = NativeFileSystem.Open(owned); var coordinator = new RunCoordinator(); bool received = false;
        try
        {
            await Assert.ThrowsAsync<AtomicFileCleanupException>(() => DesktopWorkflow.CheckAndReleaseAsync(coordinator,
                (_, _) => throw new AssertFailedException("No recheck"), () => Task.CompletedTask, _ => received = true, default,
                (_, _) => throw new AtomicFileCleanupException(owned, identity)));
            Assert.IsTrue(coordinator.HasPendingCleanup); Assert.IsFalse(received);
            await Assert.ThrowsAsync<IOException>(coordinator.RetryCleanupAsync);
        }
        finally { pinned.Dispose(); await coordinator.RetryCleanupAsync(); }
        Assert.IsFalse(File.Exists(owned)); Assert.IsFalse(coordinator.IsBusy);
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task Completed_tool_probe_or_install_disposal_failure_retains_context_until_retry(bool install)
    {
        using var root = new StorageTestRoot(); using var tools = await TestToolBuilder.CreateAsync();
        var runner = new ScriptedProcessRunner(); Microsoft.Win32.SafeHandles.SafeFileHandle? pinned = null;
        string? runtimeDirectory = null;
        runner.Results.Enqueue(request =>
        {
            pinned = NativeFileSystem.Open(request.Environment["GIT_CONFIG_GLOBAL"]!);
            runtimeDirectory = request.WorkingDirectory;
            return new(0, false, false, ["gh version 2.100.0"], []);
        });
        var tool = new ToolDetection(tools.Executable, ExecutableTrust.CaptureTrustedIdentity(tools.Executable), "v1.29.290", true);
        var detector = new ToolDetector(runner, tools.Environment, id => id == DependencyId.GitHubCli ? [tools.Executable] : []);
        var actions = new DesktopWorkflow(AppPaths.Create(root.Path), runner,
            detectTools: install ? (_, _) => Task.FromResult(new ToolInventory(tool, null, null, tool)) : detector.DetectAsync,
            check: (_, _, _, _) => Task.FromResult(Snapshot())).Actions;
        try
        {
            await Assert.ThrowsAsync<GitRuntimeCleanupException>(async () =>
            {
                if (install) await actions.Install(BackupMode.Daily, new("owner", root.Path, NetworkMode.Auto), DependencyId.Git, true, default);
                else await actions.DetectTools(default);
            });
            Assert.IsTrue(actions.HasPendingCleanup()); Assert.IsTrue(Directory.Exists(runtimeDirectory));
            await Assert.ThrowsAsync<InvalidOperationException>(() => actions.DetectTools(default));
            await Assert.ThrowsAsync<IOException>(actions.RetryCleanup);
        }
        finally { pinned?.Dispose(); await actions.RetryCleanup(); }
        Assert.IsFalse(actions.HasPendingCleanup()); Assert.IsFalse(Directory.Exists(runtimeDirectory));
    }

    [TestMethod]
    public async Task Root_confirmation_validates_saves_then_rechecks_with_same_job_and_no_api_consent()
    {
        using var root = new StorageTestRoot(); var paths = AppPaths.Create(root.Path);
        var old = new AppSettings("owner", root.Path, NetworkMode.Auto); await new SettingsStore(paths).SaveAsync(old, default);
        string target = root.Child("selected"); int checks = 0;
        var actions = new DesktopWorkflow(paths, new ScriptedProcessRunner(), check: async (_, selected, _, _) =>
        {
            checks++; Assert.IsTrue(Directory.Exists(target)); Assert.AreEqual(selected, (await new SettingsStore(paths).LoadAsync(default)).Settings);
            Assert.IsFalse(selected.HasApiCredentialConsentFor("owner")); return Snapshot();
        }).Actions;
        await Assert.ThrowsAsync<OperationCanceledException>(() => actions.SelectRoot(BackupMode.Daily, old with { BackupRoot = target }, false, true, default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => actions.SelectRoot(BackupMode.Daily, old with { BackupRoot = target }, true, false, default));
        Assert.IsFalse(Directory.Exists(target)); Assert.AreEqual(old, (await new SettingsStore(paths).LoadAsync(default)).Settings);
        var result = await actions.SelectRoot(BackupMode.Daily, old with { BackupRoot = target }, true, true, default);
        Assert.AreEqual(1, checks); Assert.IsNotNull(result);
        await actions.SelectRoot(BackupMode.Daily, old with { BackupRoot = target }, true, false, default);
        Assert.AreEqual(2, checks);
    }

    [TestMethod]
    [DataRow("relative")][DataRow("volume")][DataRow("missing-parent")][DataRow("file")]
    public async Task Rejected_root_preserves_saved_settings_without_recheck(string kind)
    {
        using var root = new StorageTestRoot(); var paths = AppPaths.Create(root.Path);
        var old = new AppSettings("owner", root.Path, NetworkMode.Auto); await new SettingsStore(paths).SaveAsync(old, default);
        string file = root.Child("file"); File.WriteAllText(file, "preserve");
        string candidate = kind switch { "relative" => "relative", "volume" => Path.GetPathRoot(root.Path)!, "missing-parent" => root.Child("missing\\child"), _ => file };
        var actions = new DesktopWorkflow(paths, new ScriptedProcessRunner(), check: (_, _, _, _) => throw new AssertFailedException("No recheck after rejected root")).Actions;
        await Assert.ThrowsAsync<Exception>(() => actions.SelectRoot(BackupMode.Daily, old with { BackupRoot = candidate }, true, true, default));
        Assert.AreEqual(old, (await new SettingsStore(paths).LoadAsync(default)).Settings);
        Assert.AreEqual("preserve", File.ReadAllText(file)); Assert.IsFalse(Directory.Exists(root.Child("missing")));
    }

    [TestMethod]
    public async Task Declined_install_does_not_detect_or_run_or_grant_consent()
    {
        using var root = new StorageTestRoot(); var runner = new ScriptedProcessRunner();
        var actions = new DesktopWorkflow(AppPaths.Create(root.Path), runner,
            detectTools: (_, _) => throw new AssertFailedException("No detection"), check: (_, _, _, _) => throw new AssertFailedException("No recheck")).Actions;
        await Assert.ThrowsAsync<OperationCanceledException>(() => actions.Install(BackupMode.Daily, new("owner", root.Path, NetworkMode.Auto), DependencyId.Git, false, default));
        Assert.HasCount(0, runner.Requests);
    }

    [TestMethod]
    public void Repair_allowlist_is_closed_ordinal_and_accepts_all_24_authorized_codes()
    {
        string[] codes = ["PREFLIGHT_PRIVATE_ROOT_ACL_UNSAFE", "PREFLIGHT_PRIVATE_OWNER_UNSAFE", "PREFLIGHT_PRIVATE_NULL_DACL_UNSAFE", "PREFLIGHT_PRIVATE_READ_ACL_UNSAFE",
            "LOCK_PATH_OWNER_UNSAFE", "LOCK_PATH_NULL_DACL_UNSAFE", "LOCK_PATH_READ_ACL_UNSAFE", "LOCK_PATH_WRITE_ACL_UNSAFE",
            "MIRROR_SOURCE_OWNER_UNSAFE", "MIRROR_SOURCE_NULL_DACL_UNSAFE", "MIRROR_SOURCE_READ_ACL_UNSAFE", "MIRROR_SOURCE_WRITE_ACL_UNSAFE",
            "METADATA_SOURCE_OWNER_UNSAFE", "METADATA_SOURCE_NULL_DACL_UNSAFE", "METADATA_SOURCE_READ_ACL_UNSAFE", "METADATA_SOURCE_WRITE_ACL_UNSAFE",
            "RELEASE_SOURCE_OWNER_UNSAFE", "RELEASE_SOURCE_NULL_DACL_UNSAFE", "RELEASE_SOURCE_READ_ACL_UNSAFE", "RELEASE_SOURCE_WRITE_ACL_UNSAFE",
            "MANIFEST_OWNER_UNSAFE", "MANIFEST_NULL_DACL_UNSAFE", "MANIFEST_READ_ACL_UNSAFE", "MANIFEST_WRITE_ACL_UNSAFE"];
        foreach (string code in codes)
        {
            Assert.IsTrue(RepairPolicy.IsRepairable(code), code);
            foreach (string rejected in new[] { code.ToLowerInvariant(), code + "_EXTRA", " " + code, code + " ", code[..^1] })
                Assert.IsFalse(RepairPolicy.IsRepairable(rejected), rejected);
        }
        foreach (string rejected in new[] { "", "MANIFEST_REPARSE_UNSAFE", "UNKNOWN_OWNER_UNSAFE" })
            Assert.IsFalse(RepairPolicy.IsRepairable(rejected), rejected);
    }

    [TestMethod]
    [DataRow("declined")][DataRow("settings")][DataRow("snapshot")][DataRow("forged")][DataRow("unknown")][DataRow("duplicate")]
    public async Task Repair_rejects_unconfirmed_stale_or_forged_selection_before_any_acl_write(string kind)
    {
        using var root = new StorageTestRoot(); string target = root.Child("entry"); Directory.CreateDirectory(target);
        var entry = new SensitivePathAssessment(target, AclRisk.Block, "PREFLIGHT_PRIVATE_ROOT_ACL_UNSAFE");
        int checks = 0; var actions = new DesktopWorkflow(AppPaths.Create(root.Path), new ScriptedProcessRunner(),
            check: (_, _, _, _) => { checks++; return Task.FromResult(Snapshot([entry])); }).Actions;
        var settings = new AppSettings("owner", root.Path, NetworkMode.Auto);
        var shown = await actions.Check(BackupMode.Daily, settings, default);
        if (kind == "snapshot") await actions.Check(BackupMode.Daily, settings, default);
        if (kind == "forged") shown = shown with { };
        var selected = kind == "unknown" ? entry with { ErrorCode = "UNKNOWN" } : entry;
        var before = AclPolicy.ReadDescriptor(target);
        int priorChecks = checks;
        await Assert.ThrowsAsync<Exception>(() => actions.Repair(BackupMode.Daily,
            kind == "settings" ? settings with { Owner = "other" } : settings, shown,
            kind == "duplicate" ? [selected, selected] : [selected], kind != "declined", default));
        Assert.AreEqual(priorChecks, checks); var after = AclPolicy.ReadDescriptor(target);
        Assert.AreEqual(before.OwnerSid, after.OwnerSid); Assert.AreEqual(before.AreAccessRulesProtected, after.AreAccessRulesProtected);
        CollectionAssert.AreEqual(before.Entries.ToArray(), after.Entries.ToArray());
    }

    [TestMethod]
    public async Task Check_and_each_setup_hold_the_backup_slot_until_recheck_and_cleanup_complete()
    {
        foreach (string kind in new[] { "check", "root", "repair", "install" })
        {
            using var root = new StorageTestRoot(); string target = root.Child("selected"); AclPolicy.CreateRestrictedDirectory(target, root.User);
            var entry = new SensitivePathAssessment(target, AclRisk.Block, "PREFLIGHT_PRIVATE_ROOT_ACL_UNSAFE");
            TaskCompletionSource entered = new(), release = new(), cleaning = new(), clean = new();
            bool hold = false;
            var runner = new ScriptedProcessRunner(); runner.Results.Enqueue(_ => new(0, false, false, [], []));
            var tool = new ToolDetection(@"C:\synthetic\winget.exe", new(1, 2, 3, 4), "v1.29.290", true);
            var actions = new DesktopWorkflow(AppPaths.Create(root.Path), runner,
                detectTools: (_, _) => Task.FromResult(new ToolInventory(tool, null, null, tool)),
                check: async (_, _, _, _) => { if (hold) { entered.SetResult(); await release.Task; } return Snapshot([entry]); },
                cleanupPreflight: async () => { if (hold) { cleaning.SetResult(); await clean.Task; } }).Actions;
            var settings = new AppSettings("owner", target, NetworkMode.Auto);
            var shown = await actions.Check(BackupMode.Daily, settings, default); hold = true;
            Task<EnvironmentStatus> pending = kind switch
            {
                "root" => actions.SelectRoot(BackupMode.Daily, settings, true, false, default),
                "repair" => actions.Repair(BackupMode.Daily, settings, shown, [entry], true, default),
                "install" => actions.Install(BackupMode.Daily, settings, DependencyId.Git, true, default),
                _ => actions.Check(BackupMode.Daily, settings, default)
            };
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Assert.ThrowsAsync<InvalidOperationException>(() => actions.Run(BackupMode.Daily, settings, new Progress<BackupProgress>(), default));
                release.SetResult(); await cleaning.Task; Assert.IsFalse(pending.IsCompleted);
                await Assert.ThrowsAsync<InvalidOperationException>(() => actions.Run(BackupMode.Daily, settings, new Progress<BackupProgress>(), default));
            }
            finally { release.TrySetResult(); clean.TrySetResult(); await pending; }
        }
    }

    [TestMethod]
    public async Task Confirmed_repair_hardens_only_selected_exact_entries_and_rechecks_once()
    {
        using var root = new StorageTestRoot(); string selected = root.Child("selected"), other = root.Child("other"), child = Path.Combine(selected, "child");
        Directory.CreateDirectory(child); Directory.CreateDirectory(other);
        StorageTestRoot.Grant(selected, System.Security.AccessControl.FileSystemRights.Read);
        StorageTestRoot.Grant(other, System.Security.AccessControl.FileSystemRights.Read);
        StorageTestRoot.Grant(child, System.Security.AccessControl.FileSystemRights.Read);
        var entries = new[] { new SensitivePathAssessment(selected, AclRisk.Block, "PREFLIGHT_PRIVATE_READ_ACL_UNSAFE"), new SensitivePathAssessment(other, AclRisk.Block, "MANIFEST_READ_ACL_UNSAFE") };
        int checks = 0; var actions = new DesktopWorkflow(AppPaths.Create(root.Path), new ScriptedProcessRunner(),
            check: (_, _, _, _) => { checks++; return Task.FromResult(Snapshot(entries)); }).Actions;
        var settings = new AppSettings("owner", root.Path, NetworkMode.Auto); var shown = await actions.Check(BackupMode.Daily, settings, default);
        await actions.Repair(BackupMode.Daily, settings, shown, [entries[0]], true, default);
        Assert.AreEqual(2, checks); Assert.AreEqual(AclRisk.Safe, AclPolicy.Evaluate(AclPolicy.ReadDescriptor(selected), root.User, true));
        Assert.AreEqual(AclRisk.Block, AclPolicy.Evaluate(AclPolicy.ReadDescriptor(other), root.User, true));
        Assert.AreEqual(AclRisk.Block, AclPolicy.Evaluate(AclPolicy.ReadDescriptor(child), root.User, true));
    }

    internal static PreflightCheckResult Snapshot(IReadOnlyList<SensitivePathAssessment>? entries = null) => new(new(
        UiFixture.Ready, ToolInventory.Empty, ProxyProfile.Direct, new Dictionary<string, string?> { ["PRIVATE"] = "SYNTHETIC_SECRET" },
        "PRIVATE_HOOKS", [], entries ?? []), null);

    [TestMethod]
    public void Check_contract_does_not_export_environment_hooks_proxy_or_session()
    {
        var result = typeof(DesktopActions).GetProperty("Check")!.PropertyType.GetGenericArguments().Last().GetGenericArguments().Single();
        CollectionAssert.AreEquivalent(new[] { "Report", "Tools", "SelectedProxyDisplayName", "Repositories", "UnsafeSensitivePaths" },
            result.GetProperties().Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public async Task Check_delivers_no_usable_snapshot_before_cleanup_succeeds()
    {
        var coordinator = new RunCoordinator(); bool received = false, fail = true;
        var snapshot = new PreflightSnapshot(UiFixture.Ready, ToolInventory.Empty, ProxyProfile.Direct,
            new Dictionary<string, string?> { ["PRIVATE"] = "SYNTHETIC_SECRET" }, "PRIVATE_HOOKS", [], []);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => DesktopWorkflow.CheckAndReleaseAsync(coordinator, (_, _) => Task.FromResult(new PreflightCheckResult(snapshot, null)),
                () => { if (fail) throw new IOException("fixture cleanup"); return Task.CompletedTask; }, _ => received = true, default));
            Assert.IsFalse(received, "A UI result must remain unavailable while cleanup owns the operation.");
        }
        finally { fail = false; await coordinator.RetryCleanupAsync(); }
    }
}
