using GitHubBackup.App;
using System.Windows.Forms;

namespace GitHubBackup.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainFormInteractionTests
{
    [TestMethod]
    public void Initial_window_view_stays_at_top_and_does_not_focus_live_log() => UiTest.Run(async () =>
    {
        var fixture = new UiFixture(); using var form = new MainForm(fixture.Actions);
        form.Show();
        await form.CurrentOperation;
        Application.DoEvents();

        Assert.IsFalse(form.LiveLogTextBox.ContainsFocus, "The read-only live log should not receive initial focus.");
        Assert.AreEqual(0, form.AutoScrollPosition.Y, "The initial view should show the first content, not the bottom of the form.");
    });

    [TestMethod]
    [DataRow("AUTH_KEYRING_TARGET_MISSING", "条目缺失")]
    [DataRow("AUTH_KEYRING_ENUMERATION_FAILED", "无法确认")]
    public void Auth_postcondition_warning_survives_workflow_cleanup_failure_and_retry(string code, string message) => UiTest.Run(async () =>
    {
        using var root = new StorageTestRoot();
        var environment = ChildEnvironmentBuilder.CreateBase(new Dictionary<string, string?>
            { ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"), ["TEMP"] = root.Path }, []);
        var context = await GitRuntimeContext.CreatePublicProbeAsync(environment, default);
        using var pinned = NativeFileSystem.Open(context.Environment["GIT_CONFIG_GLOBAL"]!);
        var error = new AuthCleanupException(new(false, "", code), new GitRuntimeCleanupException(context));
        var runner = new ScriptedProcessRunner();
        var actual = new DesktopWorkflow(AppPaths.Create(root.Path), runner, detectTools: (_, _) => throw error).Actions;
        var actions = actual with { ReadHistory = (_, _) => Task.FromResult(new SummaryReadResult(null, null, [])) };
        using var form = new MainForm(actions); await form.LoadSettingsAsync();
        try
        {
            await form.LoginConfirmedAsync(true);
            Assert.IsTrue(actual.HasPendingCleanup()); Assert.IsTrue(form.RetryCleanupButton.Enabled);
            Assert.Contains(message, form.StatusLabel.Text); Assert.Contains("重试清理", form.StatusLabel.Text);
            Assert.IsFalse(form.StartButton.Enabled); Assert.IsFalse(form.LoginButton.Enabled);
            await form.RetryCleanupAsync();
            Assert.Contains(message, form.StatusLabel.Text); Assert.IsTrue(form.RetryCleanupButton.Enabled);
            pinned.Dispose(); await form.RetryCleanupAsync();
            Assert.Contains(message, form.StatusLabel.Text); Assert.Contains("清理完成", form.StatusLabel.Text);
            Assert.IsFalse(form.RetryCleanupButton.Enabled); Assert.IsFalse(actual.HasPendingCleanup());
            Assert.HasCount(0, runner.Requests);
        }
        finally { pinned.Dispose(); await actual.RetryCleanup(); await context.DisposeAsync(); }
    });

    [TestMethod]
    [DataRow("throw")][DataRow("cancel")][DataRow("cleanup")][DataRow("reload")][DataRow("warnings")]
    public void Committed_root_is_reconciled_after_later_failure_before_any_settings_can_overwrite_it(string fault) => UiTest.Run(async () =>
    {
        using var root = new StorageTestRoot(); var paths = AppPaths.Create(root.Path); var store = new SettingsStore(paths);
        var old = new AppSettings("fixture-user", root.Path, NetworkMode.Auto); await store.SaveAsync(old, default);
        string target = root.Child("confirmed-root"); bool failCleanup = fault == "cleanup", failReload = false;
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actual = new DesktopWorkflow(paths, new ScriptedProcessRunner(), check: async (_, _, _, token) =>
        {
            checking.TrySetResult();
            if (fault == "cancel") await Task.Delay(Timeout.Infinite, token);
            if (fault == "cleanup") return DesktopSetupTests.Snapshot();
            failReload = fault is "reload" or "warnings";
            throw new IOException("PRIVATE_CHECK_FAILURE");
        }, cleanupPreflight: () => failCleanup ? Task.FromException(new IOException("PRIVATE_CLEANUP_FAILURE")) : Task.CompletedTask).Actions;
        var actions = actual with { LoadSettings = token => failReload ? fault == "warnings"
                ? Task.FromResult(new SettingsLoadResult(AppSettings.Default, ["SETTINGS_INVALID_DEFAULTS_USED"]))
                : throw new IOException("PRIVATE_RELOAD_FAILURE") : store.LoadAsync(token),
            ReadHistory = (_, _) => Task.FromResult(new SummaryReadResult(null, null, [])) };
        using var form = new MainForm(actions); await form.LoadSettingsAsync();
        try
        {
            Task operation = form.SelectRootConfirmedAsync(target, true, true);
            await checking.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (fault == "cancel") await form.CancelOperationAsync();
            await operation;
            Assert.AreEqual(target, (await store.LoadAsync(default)).Settings.BackupRoot);
            Assert.IsFalse(form.StartButton.Enabled); Assert.DoesNotContain("PRIVATE", form.StatusLabel.Text);
            if (fault is "cleanup" or "reload" or "warnings")
            {
                Assert.IsTrue(form.RetryCleanupButton.Enabled); Assert.IsFalse(form.ConsentCheckBox.Enabled);
                form.ConsentCheckBox.Checked = true; await form.CurrentOperation;
                await form.CheckEnvironmentAsync();
                Assert.AreEqual(target, (await store.LoadAsync(default)).Settings.BackupRoot);
                form.ConsentCheckBox.Checked = false;
                failCleanup = failReload = false; await form.RetryCleanupAsync();
            }
            Assert.AreEqual(target, form.BackupRootTextBox.Text);
            Assert.IsFalse(form.StartButton.Enabled); Assert.IsFalse(form.RetryCleanupButton.Enabled);
            form.ConsentCheckBox.Checked = true; await form.CurrentOperation;
            Assert.AreEqual(target, (await store.LoadAsync(default)).Settings.BackupRoot);
            Assert.IsTrue((await store.LoadAsync(default)).Settings.HasApiCredentialConsentFor("fixture-user"));
        }
        finally { failCleanup = failReload = false; await actual.RetryCleanup(); }
    });

    [TestMethod]
    public void Explicit_detection_uses_injected_workflow_and_safe_boundary() => UiTest.Run(async () =>
    {
        var f = new UiFixture(); int detections = 0;
        using var form = new MainForm(f.Actions with { DetectTools = _ => { detections++; throw new IOException("PRIVATE environment"); } });
        await form.LoadSettingsAsync(); Assert.AreEqual(0, detections);
        await form.DetectAndInstallAsync(); Assert.AreEqual(1, detections);
        Assert.DoesNotContain("PRIVATE", form.StatusLabel.Text); Assert.IsFalse(form.StartButton.Enabled);
    });

    [TestMethod]
    public void Setup_actions_require_confirmation_and_failures_disable_backup_without_exposing_raw_errors() => UiTest.Run(async () =>
    {
        var f = new UiFixture(); int installs = 0, roots = 0, repairs = 0;
        var status = new EnvironmentStatus(UiFixture.Ready, ToolInventory.Empty, "Direct", [], []);
        var actions = f.Actions with
        {
            Install = (_, selected, id, confirmed, _) => { installs++; Assert.IsTrue(confirmed); Assert.AreEqual(DependencyId.GitLfs, id); Assert.IsFalse(selected.HasApiCredentialConsentFor(selected.Owner)); throw new IOException("PRIVATE secret"); },
            SelectRoot = (_, selected, confirmed, create, _) => { roots++; Assert.IsTrue(confirmed && create); Assert.AreEqual(@"C:\new-root", selected.BackupRoot); throw new IOException("PRIVATE secret"); },
            Repair = (_, _, snapshot, entries, confirmed, _) => { repairs++; Assert.AreSame(status, snapshot); Assert.IsTrue(confirmed); Assert.HasCount(1, entries); throw new IOException("PRIVATE secret"); }
        };
        using var form = new MainForm(actions); await form.LoadSettingsAsync();
        await form.InstallConfirmedAsync(DependencyId.GitLfs, false);
        await form.SelectRootConfirmedAsync(@"C:\new-root", false, true);
        await form.RepairConfirmedAsync(status, [], false);
        Assert.AreEqual(0, installs + roots + repairs); Assert.AreEqual(0, f.Checks + f.Runs); Assert.IsNull(f.Saved);
        await form.InstallConfirmedAsync(DependencyId.GitLfs, true);
        Assert.Contains("https://git-lfs.com/", form.StatusLabel.Text); Assert.DoesNotContain("PRIVATE", form.StatusLabel.Text);
        await form.SelectRootConfirmedAsync(@"C:\new-root", true, true);
        Assert.AreEqual(@"C:\fixture", form.BackupRootTextBox.Text); Assert.IsNull(f.Saved);
        await form.RepairConfirmedAsync(status, [new(@"C:\new-root", AclRisk.Block, "PREFLIGHT_PRIVATE_ROOT_ACL_UNSAFE")], true);
        Assert.AreEqual(1, installs); Assert.AreEqual(1, roots); Assert.AreEqual(1, repairs);
        Assert.DoesNotContain("PRIVATE", form.StatusLabel.Text); Assert.IsFalse(form.StartButton.Enabled);
    });

    [TestMethod]
    public void Confirmed_root_updates_display_without_text_event_persistence_and_missing_D_is_suggestion_only() => UiTest.Run(async () =>
    {
        var f = new UiFixture(); var settings = new AppSettings("fixture-user", @"C:\fixture", NetworkMode.Auto);
        var actions = f.Actions with { SelectRoot = (_, selected, _, _, _) => { settings = selected; return Task.FromResult(new EnvironmentStatus(UiFixture.Ready, ToolInventory.Empty, "Direct", [], [])); } };
        using var form = new MainForm(actions); await form.LoadSettingsAsync();
        await form.SelectRootConfirmedAsync(@"C:\new-root", true, true);
        Assert.AreEqual(@"C:\new-root", settings.BackupRoot); Assert.AreEqual(settings.BackupRoot, form.BackupRootTextBox.Text);
        Assert.IsNull(f.Saved); Assert.IsFalse(form.ConsentCheckBox.Checked); Assert.IsFalse(form.StartButton.Enabled);
        Assert.AreEqual(@"C:\profile\GitHub-Backups", MainForm.SuggestedRoot(AppSettings.Default, @"C:\profile", _ => false));
        Assert.IsNull(MainForm.SuggestedRoot(AppSettings.Default, @"C:\profile", _ => true));
        Assert.IsNull(MainForm.SuggestedRoot(settings, @"C:\profile", _ => false));
    });

    [TestMethod]
    public void Edited_root_cannot_be_persisted_indirectly_by_consent_or_check() => UiTest.Run(async () =>
    {
        var f = new UiFixture(); using var form = new MainForm(f.Actions); await form.LoadSettingsAsync();
        form.BackupRootTextBox.Text = @"C:\unvalidated-candidate";
        form.ConsentCheckBox.Checked = true; await form.CurrentOperation;
        Assert.AreEqual(@"C:\fixture", f.Saved!.BackupRoot);
        await form.CheckEnvironmentAsync(); Assert.AreEqual(0, f.Checks);
        Assert.AreEqual(@"C:\fixture", f.Saved.BackupRoot); Assert.IsFalse(form.StartButton.Enabled);
    });

    [TestMethod]
    public void Live_tail_is_read_only_bounded_reset_and_ignores_old_run_callbacks() => UiTest.Run(async () =>
    {
        var f = new UiFixture(); IProgress<BackupProgress>? previous = null; var finish = new TaskCompletionSource<BackupRunResult>();
        var actions = f.Actions with { Run = (_, _, progress, _) => { previous = progress; return finish.Task; } };
        using var form = new MainForm(actions); await form.LoadSettingsAsync();
        form.ConsentCheckBox.Checked = true; await form.CurrentOperation; await form.CheckEnvironmentAsync();
        var run = form.StartBackupAsync(); await Task.Yield();
        previous!.Report(new("mirror", "repo", 1, 2, Enumerable.Range(0, 2100).Select(i => "safe-" + i).ToArray())); await Task.Yield();
        Assert.IsTrue(form.LiveLogTextBox.ReadOnly); Assert.HasCount(2000, form.LiveLogTextBox.Lines);
        Assert.IsFalse(form.LatestLogButton.Enabled); Assert.IsFalse(form.OpenFolderButton.Enabled); Assert.IsFalse(form.DiagnosticsButton.Enabled);
        finish.SetResult(RunCoordinatorTests.Result()); await run;
        var old = previous; finish = new(); await form.CheckEnvironmentAsync(); run = form.StartBackupAsync(); await Task.Yield();
        Assert.AreEqual("", form.LiveLogTextBox.Text);
        old!.Report(new("mirror", "repo", 1, 2, ["old-run"])); await Task.Yield(); Assert.AreEqual("", form.LiveLogTextBox.Text);
        finish.SetResult(RunCoordinatorTests.Result()); await run;
    });

    [TestMethod]
    public void Safe_view_actions_fail_inside_boundary_and_lock_during_pending_cleanup() => UiTest.Run(async () =>
    {
        var f = new UiFixture(); bool pending = false;
        var actions = f.Actions with { OpenBackupFolder = (_, _) => throw new IOException("PRIVATE C:\\secret"), HasPendingCleanup = () => pending };
        using var form = new MainForm(actions); await form.LoadSettingsAsync();
        Assert.IsTrue(form.OpenFolderButton.Enabled); Assert.IsTrue(form.DiagnosticsButton.Enabled);
        await form.OpenBackupFolderAsync(); Assert.DoesNotContain("PRIVATE", form.StatusLabel.Text);
        pending = true; await form.OpenBackupFolderAsync(); await form.RetryCleanupAsync();
        Assert.IsFalse(form.OpenFolderButton.Enabled); Assert.IsFalse(form.LatestLogButton.Enabled); Assert.IsFalse(form.DiagnosticsButton.Enabled);
    });

    [TestMethod]
    public void Rejected_check_has_error_icon_alongside_safe_status_text() => UiTest.Run(async () =>
    {
        var f = new UiFixture { Report = UiFixture.Ready with { StorageReady = false, Issues = [new("STORAGE_FIXED_VOLUME_REQUIRED", "ignored", true)] } };
        using var form = new MainForm(f.Actions); await form.LoadSettingsAsync();
        form.ConsentCheckBox.Checked = true; await form.CurrentOperation; await form.CheckEnvironmentAsync();
        Assert.Contains("失败", form.StatusIcon.AccessibleName!); Assert.Contains("未通过", form.StatusLabel.Text);
    });

    [TestMethod]
    public void Secondary_activation_marshals_to_form_thread_without_starting_work() => UiTest.Run(async () =>
    {
        var fixture = new UiFixture(); using var form = new MainForm(fixture.Actions);
        _ = form.Handle; form.WindowState = FormWindowState.Minimized;
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.Run(() =>
        {
            form.ActivateFromSecondaryInstance();
            form.BeginInvoke(() => dispatched.SetResult());
        });
        await dispatched.Task;
        Assert.AreEqual(FormWindowState.Normal, form.WindowState);
        Assert.IsFalse(form.Visible); // Own hidden native handle only; no UI launch or visual acceptance.
        Assert.AreEqual(0, fixture.Checks); Assert.AreEqual(0, fixture.Runs); Assert.IsNull(fixture.Saved);
    });

    [TestMethod]
    public void Secondary_activation_does_not_create_a_handle_before_startup_or_after_disposal() => UiTest.Run(() =>
    {
        using var form = new MainForm(new UiFixture().Actions);
        form.ActivateFromSecondaryInstance(); Assert.IsFalse(form.IsHandleCreated);
        form.Dispose(); form.ActivateFromSecondaryInstance(); Assert.IsTrue(form.IsDisposed);
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Legacy_settings_require_explicit_consent_and_owner_changes_revoke_it() => UiTest.Run(async () =>
    {
        var f = new UiFixture(); using var form = new MainForm(f.Actions);
        await form.LoadSettingsAsync();
        Assert.IsFalse(form.ConsentCheckBox.Checked);
        await form.CheckEnvironmentAsync(); Assert.AreEqual(0, f.Checks);
        form.ConsentCheckBox.Checked = true; await form.CurrentOperation;
        await form.CheckEnvironmentAsync(); Assert.IsTrue(form.StartButton.Enabled);
        form.OwnerTextBox.Text = "another-user"; await form.CurrentOperation;
        Assert.IsFalse(form.ConsentCheckBox.Checked); Assert.IsFalse(form.StartButton.Enabled);
        Assert.IsFalse(f.Saved!.HasApiCredentialConsentFor("fixture-user"));
    });

    [TestMethod]
    public void Owner_mismatch_blocks_start_and_untrusted_text_is_never_displayed() => UiTest.Run(async () =>
    {
        var f = new UiFixture();
        f.Report = UiFixture.Ready with { OwnerMatches = false, Issues = [new("AUTH_OWNER_MISMATCH", "Authorization SECRET https://evil/?token=SECRET", true)] };
        using var form = new MainForm(f.Actions); await form.LoadSettingsAsync();
        form.ConsentCheckBox.Checked = true; await form.CurrentOperation;
        await form.CheckEnvironmentAsync(); await form.StartBackupAsync();
        Assert.IsFalse(form.StartButton.Enabled); Assert.AreEqual(0, f.Runs);
        Assert.Contains("账号", form.StatusLabel.Text); Assert.DoesNotContain("SECRET", form.StatusLabel.Text);
    });

    [TestMethod]
    public void Empty_repository_check_is_actionable_and_configuration_changes_invalidate_check() => UiTest.Run(async () =>
    {
        var f = new UiFixture(); using var form = new MainForm(f.Actions); await form.LoadSettingsAsync();
        form.ConsentCheckBox.Checked = true; await form.CurrentOperation;
        await form.CheckEnvironmentAsync();
        Assert.Contains("没有仓库", form.StatusLabel.Text); Assert.IsTrue(form.StartButton.Enabled);
        form.BackupRootTextBox.Text = @"C:\another-fixture";
        Assert.IsFalse(form.StartButton.Enabled);
    });

    [TestMethod]
    public void Cancel_keeps_buttons_locked_until_cleanup_then_retry_only_cleans() => UiTest.Run(async () =>
    {
        var f = new UiFixture { HoldRun = true, FailCleanup = true };
        using var form = new MainForm(f.Actions); await form.LoadSettingsAsync();
        form.ConsentCheckBox.Checked = true; await form.CurrentOperation; await form.CheckEnvironmentAsync();
        Task run = form.StartBackupAsync(); await f.Entered.Task;
        await Task.Yield(); // Progress<T> posts to the UI queue.
        Assert.IsFalse(form.OwnerTextBox.Enabled); Assert.IsTrue(form.CancelOperationButton.Enabled);
        Assert.Contains("1/2", form.ProgressLabel.Text);
        Assert.IsFalse(await form.RequestCloseAsync(CloseChoice.ContinueRunning));
        Task cancel = form.CancelOperationAsync(); await f.Cancelled.Task;
        Assert.IsFalse(cancel.IsCompleted); Assert.IsFalse(form.CheckButton.Enabled);
        f.Release.SetResult(); await cancel; await run;
        Assert.IsTrue(form.RetryCleanupButton.Enabled); Assert.IsFalse(form.StartButton.Enabled);
        Assert.IsFalse(await form.RequestCloseAsync(CloseChoice.CancelAndWait));
        int checks = f.Checks, runs = f.Runs; f.FailCleanup = false;
        await form.RetryCleanupAsync();
        Assert.AreEqual(checks, f.Checks); Assert.AreEqual(runs, f.Runs);
        Assert.IsFalse(form.RetryCleanupButton.Enabled); Assert.IsTrue(form.CheckButton.Enabled);
    });

    [TestMethod]
    public void Exceptions_and_unknown_codes_have_fixed_safe_messages() => UiTest.Run(async () =>
    {
        var f = new UiFixture { ThrowOnCheck = true }; using var form = new MainForm(f.Actions);
        await form.LoadSettingsAsync(); form.ConsentCheckBox.Checked = true; await form.CurrentOperation;
        await form.CheckEnvironmentAsync();
        Assert.DoesNotContain("SECRET", form.StatusLabel.Text); Assert.DoesNotContain("C:\\", form.StatusLabel.Text);
        Assert.IsFalse(form.StartButton.Enabled); Assert.IsTrue(form.CheckButton.Enabled);
    });

    [TestMethod]
    public void Editing_does_not_lock_input_and_revocation_persists_even_with_invalid_path() => UiTest.Run(async () =>
    {
        var f = new UiFixture(); using var form = new MainForm(f.Actions); await form.LoadSettingsAsync();
        form.ConsentCheckBox.Checked = true; await form.CurrentOperation;
        form.BackupRootTextBox.Text = "invalid";
        Assert.IsTrue(form.BackupRootTextBox.Enabled);
        form.ConsentCheckBox.Checked = false; await form.CurrentOperation;
        Assert.IsFalse(f.Saved!.HasApiCredentialConsentFor("fixture-user"));
    });

    [TestMethod]
    public void Login_requires_separate_confirmation_and_only_displays_ephemeral_device_code() => UiTest.Run(async () =>
    {
        var f = new UiFixture(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var actions = f.Actions with { Login = async (progress, _) =>
        {
            progress.Report("Authorization SECRET https://evil/?token=SECRET\n");
            progress.Report("! First copy your one-time code: ABCD-1234\n");
            entered.SetResult(); await release.Task; return new(false, "", "AUTH_LOGIN_FAILED_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED") { BrowserOpenFailed = true };
        } };
        using var form = new MainForm(actions); await form.LoadSettingsAsync();
        await form.LoginConfirmedAsync(false); Assert.IsFalse(entered.Task.IsCompleted);
        var login = form.LoginConfirmedAsync(true); await entered.Task; await Task.Yield();
        Assert.Contains("ABCD-1234", form.ProgressLabel.Text); Assert.DoesNotContain("SECRET", form.ProgressLabel.Text);
        Assert.Contains("通常会自动打开", form.StatusLabel.Text);
        Assert.Contains("https://github.com/login/device", form.StatusLabel.Text);
        Assert.Contains("当前界面显示", form.StatusLabel.Text);
        Assert.DoesNotContain("ABCD-1234", form.StatusLabel.Text);
        release.SetResult(); await login;
        Assert.DoesNotContain("ABCD-1234", form.ProgressLabel.Text);
        Assert.Contains("共享", form.StatusLabel.Text);
        Assert.Contains("https://github.com/login/device", form.StatusLabel.Text);
        Assert.Contains("新显示的一次性代码", form.StatusLabel.Text);
    });

    [TestMethod]
    public void History_is_visible_without_consent_and_latest_failure_does_not_replace_core_success() => UiTest.Run(async () =>
    {
        var success = RunCoordinatorTests.Result().Summary with { Status = RunStatus.Pass, CompletedAt = DateTimeOffset.Parse("2026-09-20T10:00:00Z"), RepositoryCount = 2 };
        var failure = success with { Status = RunStatus.Fail, CompletedAt = DateTimeOffset.Parse("2026-09-21T10:00:00Z"), WarningCount = 3, ErrorCode = "SECRET", Log = "SECRET" };
        var f = new UiFixture { History = new(failure, success, []) }; using var form = new MainForm(f.Actions);
        await form.LoadSettingsAsync();
        Assert.IsFalse(form.ConsentCheckBox.Checked); Assert.AreEqual(0, f.Checks);
        Assert.Contains("FAIL", form.HistoryLabel.Text); Assert.Contains("2026-09-20", form.HistoryLabel.Text);
        Assert.Contains("3", form.HistoryLabel.Text); Assert.DoesNotContain("SECRET", form.HistoryLabel.Text);
        f.History = new(null, null, []); form.BackupRootTextBox.Text = @"C:\different-fixture";
        await form.RefreshHistoryAsync(); Assert.Contains("暂无", form.HistoryLabel.Text);
    });

    [TestMethod]
    public void Token_shaped_repository_progress_is_redacted() => UiTest.Run(async () =>
    {
        var f = new UiFixture { HoldRun = true, ProgressRepository = "ghp_SYNTHETIC_SECRET" };
        using var form = new MainForm(f.Actions); await form.LoadSettingsAsync();
        form.ConsentCheckBox.Checked = true; await form.CurrentOperation; await form.CheckEnvironmentAsync();
        var run = form.StartBackupAsync(); await f.Entered.Task; await Task.Yield();
        try { Assert.DoesNotContain("SYNTHETIC_SECRET", form.ProgressLabel.Text); }
        finally { f.Release.TrySetResult(); await form.CancelOperationAsync(); await run; }
    });

    [TestMethod]
    public void Closing_then_disposing_twice_releases_owned_history_lifetime() => UiTest.Run(async () =>
    {
        var form = new MainForm(new UiFixture().Actions); await form.LoadSettingsAsync();
        form.Dispose(); form.Dispose();
        Assert.IsTrue(form.IsDisposed);
    });

    [TestMethod]
    public void Settings_atomic_cleanup_blocks_operations_and_close_until_owned_retry_and_revocation_save() => UiTest.Run(async () =>
    {
        using var root = new StorageTestRoot(); string temporary = root.Child("owned-settings.tmp"), other = root.Child("unrelated.txt");
        File.WriteAllText(other, "preserve"); NativeFileIdentity identity;
        using (var file = AclPolicy.CreateRestrictedFile(temporary, root.User)) identity = NativeFileSystem.Inspect(file.SafeFileHandle, temporary, false);
        using var pinned = NativeFileSystem.Open(temporary);
        var f = new UiFixture(); bool fail = true;
        var consented = new AppSettings("fixture-user", @"C:\fixture", NetworkMode.Auto) { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        var actions = f.Actions with
        {
            LoadSettings = _ => Task.FromResult(new SettingsLoadResult(consented, [])),
            SaveSettings = (selected, _) => { if (fail) throw new AtomicFileCleanupException(temporary, identity); f.Saved = selected; return Task.CompletedTask; }
        };
        using var form = new MainForm(actions); await form.LoadSettingsAsync();
        form.ConsentCheckBox.Checked = false; await form.CurrentOperation;
        Assert.IsTrue(form.RetryCleanupButton.Enabled); Assert.IsFalse(form.CheckButton.Enabled); Assert.IsFalse(form.LoginButton.Enabled);
        Assert.IsFalse(await form.RequestCloseAsync(CloseChoice.CancelAndWait));
        await form.RetryCleanupAsync(); Assert.IsTrue(form.RetryCleanupButton.Enabled); Assert.IsTrue(File.Exists(temporary));
        fail = false; pinned.Dispose(); await form.RetryCleanupAsync();
        Assert.IsFalse(File.Exists(temporary)); Assert.AreEqual("preserve", File.ReadAllText(other));
        Assert.IsFalse(form.RetryCleanupButton.Enabled); Assert.IsTrue(form.LoginButton.Enabled);
        Assert.IsNotNull(f.Saved); Assert.IsFalse(f.Saved.HasApiCredentialConsentFor("fixture-user"));
        Assert.AreEqual(0, f.Checks); Assert.AreEqual(0, f.Runs);
    });

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void Login_code_survives_safe_text_multi_line_and_split_delivery(bool split) => UiTest.Run(async () =>
    {
        var f = new UiFixture(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var actions = f.Actions with { Login = async (progress, _) =>
        {
            var stream = new SafeTextStream();
            string output = stream.Push("Authorization: Bearer SYNTHETIC_PRIVATE\n" + new string('x', 300) + "\n! First copy your one-time code: ABCD-1234\nOpen a browser\n");
            if (split) foreach (char character in output) progress.Report(character.ToString());
            else progress.Report(output);
            entered.SetResult(); await release.Task; return new(false, "", "AUTH_CANCELLED");
        } };
        using var form = new MainForm(actions); await form.LoadSettingsAsync();
        var login = form.LoginConfirmedAsync(true); await entered.Task; await Task.Yield();
        try { Assert.Contains("ABCD-1234", form.ProgressLabel.Text); Assert.DoesNotContain("SYNTHETIC_PRIVATE", form.ProgressLabel.Text); }
        finally { release.SetResult(); await login; }
        Assert.DoesNotContain("ABCD-1234", form.ProgressLabel.Text);
    });

    [TestMethod]
    [DataRow("io")] [DataRow("inaccessible")] [DataRow("cancelled")]
    public void Failed_consent_revocation_stays_pending_until_retry_saves_it(string failure) => UiTest.Run(async () =>
    {
        var f = new UiFixture(); bool fail = true;
        var persisted = new AppSettings("fixture-user", @"C:\fixture", NetworkMode.Auto) { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        var actions = f.Actions with
        {
            LoadSettings = _ => Task.FromResult(new SettingsLoadResult(persisted, [])),
            SaveSettings = (selected, _) =>
            {
                if (fail) throw failure switch
                {
                    "inaccessible" => new UnauthorizedAccessException("PRIVATE C:\\secret\\settings.json"),
                    "cancelled" => new OperationCanceledException("PRIVATE"),
                    _ => new IOException("PRIVATE C:\\secret\\settings.json")
                };
                persisted = selected; return Task.CompletedTask;
            }
        };
        using var form = new MainForm(actions); await form.LoadSettingsAsync();
        form.ConsentCheckBox.Checked = false; await form.CurrentOperation;
        Assert.IsTrue(persisted.HasApiCredentialConsentFor("fixture-user"));
        Assert.IsTrue(form.RetryCleanupButton.Enabled); Assert.IsFalse(form.OwnerTextBox.Enabled);
        Assert.IsFalse(form.LoginButton.Enabled); Assert.IsFalse(form.CheckButton.Enabled); Assert.IsFalse(form.StartButton.Enabled);
        Assert.IsFalse(await form.RequestCloseAsync(CloseChoice.CancelAndWait));
        Assert.DoesNotContain("PRIVATE", form.StatusLabel.Text); Assert.DoesNotContain("secret", form.StatusLabel.Text);
        await form.RetryCleanupAsync(); Assert.IsTrue(form.RetryCleanupButton.Enabled);
        fail = false; await form.RetryCleanupAsync();
        Assert.IsFalse(persisted.HasApiCredentialConsentFor("fixture-user")); Assert.IsFalse(form.RetryCleanupButton.Enabled);
        Assert.IsTrue(form.OwnerTextBox.Enabled); Assert.IsTrue(await form.RequestCloseAsync(CloseChoice.CancelAndWait));
        Assert.AreEqual(0, f.Checks); Assert.AreEqual(0, f.Runs);
    });

    [TestMethod]
    public void Latest_fallback_receipt_shows_only_the_fixed_safe_location() => UiTest.Run(async () =>
    {
        var receipt = RunCoordinatorTests.Result().Summary with { BackupRoot = @"C:\PRIVATE\secret", Log = "https://evil/?token=PRIVATE" };
        var f = new UiFixture { History = new(receipt, null, []) { LatestIsFallback = true } };
        using var form = new MainForm(f.Actions); await form.LoadSettingsAsync();
        Assert.Contains(@"%LOCALAPPDATA%\GitHubBackupTool\diagnostics\fallback-summaries", form.HistoryLabel.Text);
        Assert.DoesNotContain("PRIVATE", form.HistoryLabel.Text); Assert.DoesNotContain("token=", form.HistoryLabel.Text);
        f.History = new(receipt, null, []); await form.RefreshHistoryAsync();
        Assert.DoesNotContain("%LOCALAPPDATA%", form.HistoryLabel.Text);
        f.History = new(null, null, []); await form.RefreshHistoryAsync();
        Assert.DoesNotContain("%LOCALAPPDATA%", form.HistoryLabel.Text);
    });
}

internal sealed class UiFixture
{
    internal static readonly PreflightReport Ready = new(true, true, true, true, true, true, true, true, []);
    internal PreflightReport Report = Ready;
    internal int Checks, Runs;
    internal bool HoldRun, FailCleanup, ThrowOnCheck;
    internal AppSettings? Saved;
    internal SummaryReadResult History = new(null, null, []);
    internal string ProgressRepository = "repo";
    internal readonly TaskCompletionSource Entered = new(), Cancelled = new(), Release = new();
    private readonly RunCoordinator coordinator = new();
    internal DesktopActions Actions => new(
        _ => Task.FromResult(new SettingsLoadResult(new("fixture-user", @"C:\fixture", NetworkMode.Auto), [])),
        (settings, _) => { Saved = settings; return Task.CompletedTask; },
        (_, _, _) => { Checks++; if (ThrowOnCheck) throw new IOException("SECRET C:\\secret https://evil/?token=SECRET"); return Task.FromResult(new EnvironmentStatus(Report, ToolInventory.Empty, "Direct", [], [])); },
        (_, _, progress, token) =>
        {
            Runs++; var job = OperationJob.Create();
            return coordinator.RunAsync(job, async cancellation =>
            {
                progress.Report(new("mirror", ProgressRepository, 1, 2)); Entered.SetResult();
                if (HoldRun)
                {
                    try { await Task.Delay(Timeout.Infinite, cancellation); }
                    catch (OperationCanceledException) { Cancelled.SetResult(); }
                    await Release.Task;
                }
                return RunCoordinatorTests.Result() with { Summary = RunCoordinatorTests.Result().Summary with { Status = cancellation.IsCancellationRequested ? RunStatus.Cancelled : RunStatus.Pass } };
            }, () => { if (FailCleanup) throw new IOException("SECRET"); job.Dispose(); return Task.CompletedTask; }, token);
        },
        (_, _) => Task.FromResult(new AuthResult(true, "fixture-user", "")),
        coordinator.CancelAsync, coordinator.RetryCleanupAsync, () => coordinator.HasPendingCleanup,
        (_, _) => Task.FromResult(History),
        (_, _) => Task.FromResult(new ValidatedLogDocument("日志", ["safe"])),
        (_, _) => Task.FromResult(new SafeOpenResult(true, "")),
        (_, _) => Task.FromResult(new DiagnosticDisplayPreview("fixture", ["safe"])),
        (_, _, _, confirmed, _) => Task.FromResult(confirmed ? DiagnosticSaveStatus.Saved : DiagnosticSaveStatus.Cancelled),
        _ => Task.FromResult(ToolInventory.Empty),
        (_, _, _, _, _) => Task.FromResult(new EnvironmentStatus(Report, ToolInventory.Empty, "Direct", [], [])),
        (_, _, _, _, _) => Task.FromResult(new EnvironmentStatus(Report, ToolInventory.Empty, "Direct", [], [])),
        (_, _, _, _, _, _) => Task.FromResult(new EnvironmentStatus(Report, ToolInventory.Empty, "Direct", [], [])));
}

internal static class UiTest
{
    internal static void Run(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Task task = test(); var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!task.IsCompleted)
                {
                    if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("UI test timed out.");
                    Application.DoEvents(); Thread.Sleep(1);
                }
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
