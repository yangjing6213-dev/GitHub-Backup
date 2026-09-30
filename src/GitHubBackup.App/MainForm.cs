namespace GitHubBackup.App;

internal enum CloseChoice { ContinueRunning, CancelAndWait }

public partial class MainForm : Form
{
    private readonly DesktopActions actions;
    private bool loading = true, busy, ready, cancelling, awaitingClose;
    private CancellationTokenSource? cancellation;
    private AppSettings savedSettings = AppSettings.Default;
    private AppSettings? pendingSettings;
    private AtomicFileCleanupException? settingsCleanup;
    private bool rootSettingsReloadPending;
    private string? authSafetyError;
    private bool HasPendingCleanup => rootSettingsReloadPending || pendingSettings is not null || settingsCleanup is not null || actions.HasPendingCleanup();
    private int historyRevision;
    private readonly CancellationTokenSource historyCancellation = new();
    internal Task CurrentOperation { get; private set; } = Task.CompletedTask;
    private int progressRevision;
    private EnvironmentStatus? environmentStatus;

    public MainForm() : this(new DesktopWorkflow(AppPaths.Create(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))).Actions) { }
    internal MainForm(DesktopActions actions)
    {
        this.actions = actions; InitializeComponent(); UpdateButtons();
        Shown += async (_, _) => await LoadSettingsAsync();
    }

    internal void ActivateFromSecondaryInstance()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(ActivateFromSecondaryInstance); return; }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    internal Task LoadSettingsAsync() => OperateAsync(async token =>
    {
        loading = true;
        try
        {
            var loaded = await actions.LoadSettings(token);
            savedSettings = loaded.Settings;
            OwnerTextBox.Text = loaded.Settings.Owner; BackupRootTextBox.Text = loaded.Settings.BackupRoot;
            ConsentCheckBox.Checked = loaded.Settings.HasApiCredentialConsentFor(loaded.Settings.Owner);
            StatusLabel.Text = loaded.Warnings.Count != 0 ? "设置无法读取，请确认账号和目录。" : ConsentCheckBox.Checked ? "尚未检查环境。" : "需要允许应用访问已登录账号。";
            string? suggestion = SuggestedRoot(loaded.Settings, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory.Exists);
            if (suggestion is not null) StatusLabel.Text += " 默认 D: 不可用，可选择：" + suggestion + "（尚未创建）。";
        }
        finally { loading = false; }
        await RefreshHistoryAsync();
    });

    private AppSettings SelectedSettings() => new(OwnerTextBox.Text.Trim(), BackupRootTextBox.Text.Trim(), NetworkMode.Auto)
    {
        ApiCredentialConsentVersion = ConsentCheckBox.Checked ? 1 : null,
        ApiCredentialConsentLogin = ConsentCheckBox.Checked ? OwnerTextBox.Text.Trim().ToLowerInvariant() : null
    };
    private BackupMode SelectedMode => ModeComboBox.SelectedIndex == 1 ? BackupMode.Full : BackupMode.Daily;
    private bool ValidInputs => AuthConfigLease.IsLogin(OwnerTextBox.Text.Trim()) && Path.IsPathFullyQualified(BackupRootTextBox.Text.Trim());
    private bool RootSaved => string.Equals(BackupRootTextBox.Text.Trim(), savedSettings.BackupRoot, StringComparison.OrdinalIgnoreCase);
    internal static string? SuggestedRoot(AppSettings settings, string profile, Func<string, bool> exists) =>
        settings.BackupRoot == AppSettings.Default.BackupRoot && !exists(@"D:\") ? AppPaths.SelectInitialBackupRoot(profile, exists) : null;

    private void SettingsChanged(bool ownerChanged, bool consentChanged = false)
    {
        if (loading || busy || rootSettingsReloadPending) return;
        progressRevision++; LiveLogTextBox.Clear();
        ready = false; environmentStatus = null;
        bool revoke = ownerChanged && ConsentCheckBox.Checked;
        if (ownerChanged) { loading = true; ConsentCheckBox.Checked = false; loading = false; }
        StatusLabel.Text = ConsentCheckBox.Checked ? "设置已更改，请重新检查环境。" : "需要允许应用访问已登录账号。";
        UpdateButtons();
        _ = RefreshHistoryAsync();
        if (revoke || consentChanged)
        {
            var selected = ConsentCheckBox.Checked && AuthConfigLease.IsLogin(OwnerTextBox.Text.Trim()) ? SelectedSettings() with { BackupRoot = savedSettings.BackupRoot }
                : savedSettings with { ApiCredentialConsentVersion = null, ApiCredentialConsentLogin = null };
            _ = OperateAsync(token => SaveSettingsAsync(selected, token));
        }
    }

    internal Task CheckEnvironmentAsync()
    {
        if (busy || HasPendingCleanup || !ValidInputs || !RootSaved || !ConsentCheckBox.Checked) return Task.CompletedTask;
        return OperateAsync(async token =>
        {
            ready = false; environmentStatus = null; StatusLabel.Text = "正在检查登录、网络和备份目录…";
            var selected = SelectedSettings(); await SaveSettingsAsync(selected, token);
            var snapshot = await actions.Check(SelectedMode, selected, token);
            token.ThrowIfCancellationRequested(); ShowEnvironment(snapshot);
        });
    }

    private void ShowEnvironment(EnvironmentStatus status)
    {
        environmentStatus = status; ready = status.Report.CanStartBackup && ConsentCheckBox.Checked && RootSaved;
        var issue = status.Report.Issues.FirstOrDefault(i => i.BlocksBackup);
        StatusLabel.Text = ready ? status.Repositories.Count == 0 ? "检查通过：当前账号没有仓库，可以记录一次空备份。" : $"检查通过：发现 {status.Repositories.Count} 个仓库，可以开始备份。"
            : ErrorText(issue?.ErrorCode, issue?.NetworkFailure?.RateLimitReset);
        SetStatusIcon(ready ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    internal async Task DetectAndInstallAsync()
    {
        if (busy || HasPendingCleanup) return;
        DependencyId? selected = null;
        await OperateAsync(async token =>
        {
            ready = false; environmentStatus = null;
            var tools = await actions.DetectTools(token); token.ThrowIfCancellationRequested();
            using var dialog = new DependencyConsentDialog(tools);
            if (dialog.ShowDialog(this) == DialogResult.OK) selected = dialog.SelectedDependency;
            else StatusLabel.Text = selected is null ? "依赖窗口已关闭。已达标的软件无需再次安装；如需安装缺少或过旧的软件，请按窗口内提示检查 winget。" : "已取消安装。可按官方地址手动安装后重新检测。";
        });
        if (selected is { } id) await InstallConfirmedAsync(id, true);
    }

    internal Task InstallConfirmedAsync(DependencyId id, bool confirmed)
    {
        if (busy || HasPendingCleanup) return Task.CompletedTask;
        ready = false; environmentStatus = null; UpdateButtons();
        return !confirmed ? Task.CompletedTask : OperateAsync(token => InstallCoreAsync(id, token));
    }

    private async Task InstallCoreAsync(DependencyId id, CancellationToken token)
    {
        try { var status = await actions.Install(SelectedMode, SelectedSettings() with { BackupRoot = savedSettings.BackupRoot }, id, true, token); token.ThrowIfCancellationRequested(); ShowEnvironment(status); }
        catch (Exception) when (!token.IsCancellationRequested)
        { ready = false; StatusLabel.Text = "安装或重新检查未完成，可能已有部分安装；请重新检测，或使用官方手动安装地址：\n" + DependencyConsentDialog.ManualGuidance; }
    }

    internal Task SelectRootConfirmedAsync(string candidate, bool confirmed, bool createConfirmed)
    {
        if (busy || HasPendingCleanup) return Task.CompletedTask;
        ready = false; environmentStatus = null; UpdateButtons();
        return !confirmed ? Task.CompletedTask : OperateAsync(token => SelectRootCoreAsync(candidate, createConfirmed, token));
    }

    private async Task SelectRootCoreAsync(string candidate, bool createConfirmed, CancellationToken token)
    {
        var selected = SelectedSettings() with { BackupRoot = NativeFileSystem.CanonicalPath(candidate) };
        rootSettingsReloadPending = true;
        try
        {
            var status = await actions.SelectRoot(SelectedMode, selected, true, createConfirmed, token);
            token.ThrowIfCancellationRequested(); savedSettings = selected;
            loading = true; try { BackupRootTextBox.Text = selected.BackupRoot; } finally { loading = false; }
            rootSettingsReloadPending = false;
            ShowEnvironment(status); await RefreshHistoryAsync();
        }
        catch
        {
            ready = false; environmentStatus = null;
            // Save may have committed before recheck/cancellation failed. Reconcile only after owned cleanup.
            if (!actions.HasPendingCleanup()) await ReconcileRootSettingsAsync();
            throw;
        }
    }

    private async Task ReconcileRootSettingsAsync()
    {
        var loaded = await actions.LoadSettings(CancellationToken.None);
        if (loaded.Warnings.Count != 0) throw new IOException("SETTINGS_RECONCILIATION_REQUIRED");
        loading = true;
        try { savedSettings = loaded.Settings; BackupRootTextBox.Text = loaded.Settings.BackupRoot; }
        finally { loading = false; }
        rootSettingsReloadPending = false; ready = false; environmentStatus = null;
    }

    private async Task ChooseRootAsync(bool browse)
    {
        if (busy || HasPendingCleanup) return;
        string? selected = null; bool create = false;
        await OperateAsync(_ =>
        {
            ready = false; environmentStatus = null; string candidate = BackupRootTextBox.Text.Trim();
            if (browse)
            {
                using var picker = new FolderBrowserDialog { ShowNewFolderButton = false, Description = "选择现有目录；新目录请在主窗口输入后验证并保存。" };
                if (picker.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
                candidate = picker.SelectedPath;
            }
            candidate = NativeFileSystem.CanonicalPath(candidate);
            using var dialog = new RootConsentDialog(candidate, Directory.Exists(candidate));
            if (dialog.ShowDialog(this) == DialogResult.OK) { selected = candidate; create = dialog.CreateCheckBox.Checked; }
            else StatusLabel.Text = "已取消目录选择。";
            return Task.CompletedTask;
        });
        if (selected is not null) await SelectRootConfirmedAsync(selected, true, create);
    }

    internal Task RepairConfirmedAsync(EnvironmentStatus snapshot, IReadOnlyList<SensitivePathAssessment> selected, bool confirmed)
    {
        if (busy || HasPendingCleanup) return Task.CompletedTask;
        ready = false; UpdateButtons();
        return !confirmed ? Task.CompletedTask : OperateAsync(async token =>
        {
            environmentStatus = null;
            var result = await actions.Repair(SelectedMode, SelectedSettings(), snapshot, selected, true, token);
            token.ThrowIfCancellationRequested(); ShowEnvironment(result);
        });
    }

    private async Task ChooseRepairAsync()
    {
        if (busy || HasPendingCleanup || environmentStatus is null) return;
        var snapshot = environmentStatus; IReadOnlyList<SensitivePathAssessment>? selected = null;
        await OperateAsync(_ =>
        {
            ready = false; environmentStatus = null;
            using var dialog = new RepairConsentDialog(snapshot);
            if (dialog.ShowDialog(this) == DialogResult.OK) selected = dialog.SelectedEntries;
            else StatusLabel.Text = "已取消权限修复。";
            return Task.CompletedTask;
        });
        if (selected is not null) await RepairConfirmedAsync(snapshot, selected, true);
    }

    internal Task StartBackupAsync()
    {
        if (busy || !ready || !ConsentCheckBox.Checked || HasPendingCleanup) return Task.CompletedTask;
        return OperateAsync(async token =>
        {
            ready = false; StatusLabel.Text = "正在备份；结束前请保持窗口打开。";
            LiveLogTextBox.Clear(); int revision = ++progressRevision;
            var result = await actions.Run(SelectedMode, SelectedSettings(), new Progress<BackupProgress>(value =>
            { if (revision == progressRevision) ShowProgress(value); }), token);
            var summary = result.Summary;
            StatusLabel.Text = summary.Status switch
            {
                RunStatus.Pass => $"PASS · 备份完成，共 {summary.RepositoryCount} 个仓库。",
                RunStatus.Partial => $"PARTIAL · 备份完成，有 {summary.WarningCount} 项警告，请检查备份结果。",
                RunStatus.Cancelled => "CANCELLED · 已取消，清理完成。",
                _ => "FAIL · " + ErrorText(summary.ErrorCode, result.NetworkFailure?.RateLimitReset)
            };
            if (result.NetworkFailure is { FailureKind: NetworkFailureKind.RateLimited } limited && summary.ErrorCode != "HTTP_RATE_LIMITED")
                StatusLabel.Text += " " + ErrorText("HTTP_RATE_LIMITED", limited.RateLimitReset);
            SetStatusIcon(summary.Status == RunStatus.Fail ? MessageBoxIcon.Error : summary.Status == RunStatus.Partial ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            await RefreshHistoryAsync();
        });
    }

    internal Task OpenBackupFolderAsync()
    {
        if (busy || HasPendingCleanup || !ValidInputs) return Task.CompletedTask;
        return OperateAsync(async token =>
        {
            var result = await actions.OpenBackupFolder(SelectedSettings(), token);
            StatusLabel.Text = result.Opened ? "已请求打开备份目录。" : "备份目录无法安全打开，请确认目录存在且权限正确。";
        });
    }

    private Task ShowLatestLogAsync()
    {
        if (busy || HasPendingCleanup || !ValidInputs) return Task.CompletedTask;
        return OperateAsync(async token =>
        {
            var document = await actions.ReadLatestLog(SelectedSettings(), token); token.ThrowIfCancellationRequested();
            using var dialog = new LogViewerDialog(document); dialog.ShowDialog(this);
        });
    }

    private Task ExportDiagnosticsAsync()
    {
        if (busy || HasPendingCleanup || !ValidInputs) return Task.CompletedTask;
        return OperateAsync(async token =>
        {
            var selected = SelectedSettings(); DiagnosticDisplayPreview? preview = null; bool consumed = false;
            try
            {
                preview = await actions.PreviewDiagnostics(selected, token); token.ThrowIfCancellationRequested();
                using var dialog = new DiagnosticExportDialog(preview);
                if (dialog.ShowDialog(this) != DialogResult.OK) { StatusLabel.Text = "已取消诊断导出。"; return; }
                using var picker = new SaveFileDialog { Title = "保存已检查的诊断", Filter = "文本文件 (*.txt)|*.txt", DefaultExt = "txt", AddExtension = true, FileName = "backup-diagnostics.txt", OverwritePrompt = true };
                if (picker.ShowDialog(this) != DialogResult.OK) { StatusLabel.Text = "已取消诊断导出。"; return; }
                var status = await actions.SaveDiagnostics(selected, preview.PreviewId, picker.FileName, true, token); consumed = true;
                StatusLabel.Text = status switch
                {
                    DiagnosticSaveStatus.Saved => "诊断已保存。",
                    DiagnosticSaveStatus.Cancelled => "已取消诊断导出。",
                    _ => "诊断来源或预览已改变，未保存；请重新预览。"
                };
            }
            finally
            {
                if (preview is not null && !consumed && !actions.HasPendingCleanup())
                    await actions.SaveDiagnostics(selected, preview.PreviewId, "", false, CancellationToken.None);
            }
        });
    }

    internal async Task CancelOperationAsync()
    {
        if (!busy || cancelling) return;
        cancelling = true; StatusLabel.Text = "正在取消，请等待当前操作和清理完成…"; UpdateButtons();
        cancellation?.Cancel();
        try { await actions.Cancel(); await CurrentOperation; }
        catch (Exception) { StatusLabel.Text = "操作未完成，请重新检查；若提示清理待完成，请重试清理。"; }
        finally { cancelling = false; UpdateButtons(); }
    }

    internal Task RetryCleanupAsync()
    {
        if (busy || !HasPendingCleanup) return Task.CompletedTask;
        return OperateAsync(async _ =>
        {
            ready = false; StatusLabel.Text = "正在重试清理…";
            if (settingsCleanup is not null) { settingsCleanup.RetryCleanup(); settingsCleanup = null; }
            if (pendingSettings is not null) await SaveSettingsAsync(pendingSettings, CancellationToken.None);
            if (actions.HasPendingCleanup()) await actions.RetryCleanup();
            if (rootSettingsReloadPending) await ReconcileRootSettingsAsync();
            StatusLabel.Text = "清理完成，请重新检查环境。" + (authSafetyError is null ? "" : " " + ErrorText(authSafetyError));
        });
    }

    private async Task LoginAsync()
    {
        if (busy || HasPendingCleanup) return;
        if (MessageBox.Show(this, "浏览器登录会写入当前 Windows 用户的 GitHub 共享凭据库，可能改变该用户其他 GitHub CLI 实例的账号凭据或当前账号。是否继续登录？",
            "确认浏览器登录", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        await LoginConfirmedAsync(true);
    }

    internal Task LoginConfirmedAsync(bool confirmed)
    {
        if (!confirmed || busy || HasPendingCleanup) return Task.CompletedTask;
        return OperateAsync(async token =>
        {
            ready = false; authSafetyError = null; StatusLabel.Text = "请在浏览器中完成 GitHub 登录。";
            bool acceptingCode = true;
            var loginLine = new System.Text.StringBuilder(100);
            bool discardLine = false;
            try
            {
                var result = await actions.Login(new Progress<string>(chunk =>
                {
                    if (!acceptingCode || IsDisposed) return;
                    foreach (char character in chunk)
                    {
                        if (character == '\n')
                        {
                            if (!discardLine)
                            {
                                var match = System.Text.RegularExpressions.Regex.Match(loginLine.ToString().Trim(), @"^! First copy your one-time code: ([A-Z0-9]{4}-[A-Z0-9]{4})$");
                                if (match.Success) ProgressLabel.Text = "在 GitHub 浏览器页面输入一次性代码：" + match.Groups[1].Value;
                            }
                            loginLine.Clear(); discardLine = false;
                        }
                        else if (loginLine.Length < 100) loginLine.Append(character);
                        else discardLine = true;
                    }
                }), token);
                StatusLabel.Text = result.AuthReady ? "登录完成，共享账号可能已更新；请确认账号并重新检查环境。" : ErrorText(result.ErrorCode);
            }
            finally { acceptingCode = false; loginLine.Clear(); ProgressLabel.Text = "登录操作已结束。"; }
        });
    }

    private Task OperateAsync(Func<CancellationToken, Task> work)
    {
        if (busy) return CurrentOperation;
        busy = true; cancellation = new(); UpdateButtons();
        return CurrentOperation = ExecuteAsync();
        async Task ExecuteAsync()
        {
            await Task.Yield();
            try { await work(cancellation.Token); }
            catch (AuthCleanupException ex)
            {
                ready = false;
                authSafetyError = ex.Outcome.ErrorCode is "AUTH_KEYRING_TARGET_MISSING" or "AUTH_KEYRING_ENUMERATION_FAILED" ? ex.Outcome.ErrorCode : null;
                StatusLabel.Text = ErrorText(ex.Outcome.ErrorCode);
            }
            catch (OperationCanceledException) { ready = false; StatusLabel.Text = "CANCELLED · 已取消，清理完成。"; }
            catch (Exception) { ready = false; StatusLabel.Text = "操作未完成，请检查设置后重试。"; }
            finally
            {
                cancellation.Dispose(); cancellation = null; busy = false;
                if (HasPendingCleanup) { ready = false; StatusLabel.Text = (authSafetyError is null ? "" : ErrorText(authSafetyError) + " ")
                    + "保存或清理尚未完成，请重试清理。完成前不能开始新任务或关闭窗口。"; }
                UpdateButtons();
            }
        }
    }

    private void ShowProgress(BackupProgress progress)
    {
        if (!busy || IsDisposed) return;
        string phase = progress.Phase switch { "preflight" => "重新检查环境", "planning" => "准备备份", "mirror" => "备份仓库", "wiki" => "备份 Wiki", "metadata" => "备份仓库资料", "release" => "备份发布附件", "cleanup" => "完成并清理", _ => "正在处理" };
        string repository = progress.Repository is { Length: > 0 and <= 100 } name && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? name : "";
        var redactor = new SecretRedactor(); redactor.Push(repository); repository = redactor.Complete();
        ProgressLabel.Text = progress.Total > 0 ? $"{phase} · {progress.Current}/{progress.Total} · {repository}" : phase;
        if (progress.LogTail is not null) LiveLogTextBox.Lines = LogViewerDialog.BoundedLines(progress.LogTail);
    }

    private void UpdateButtons()
    {
        bool idle = !busy && !HasPendingCleanup;
        OwnerTextBox.Enabled = BackupRootTextBox.Enabled = BrowseButton.Enabled = ModeComboBox.Enabled = ConsentCheckBox.Enabled = idle;
        LoginButton.Enabled = idle;
        CheckButton.Enabled = idle && ValidInputs && RootSaved && ConsentCheckBox.Checked;
        DependencyButton.Enabled = idle;
        SaveRootButton.Enabled = idle && ValidInputs;
        RepairButton.Enabled = idle && environmentStatus?.UnsafeSensitivePaths.Any(e => RepairPolicy.IsRepairable(e.ErrorCode)) == true;
        StartButton.Enabled = idle && ready && ConsentCheckBox.Checked;
        CancelOperationButton.Enabled = busy && !cancelling;
        RetryCleanupButton.Enabled = !busy && HasPendingCleanup;
        LatestLogButton.Enabled = OpenFolderButton.Enabled = DiagnosticsButton.Enabled = idle && ValidInputs;
        ActivityBar.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        ActivityBar.MarqueeAnimationSpeed = busy ? 30 : 0;
    }

    private void SetStatusIcon(MessageBoxIcon status)
    {
        var previous = StatusIcon.Image;
        StatusIcon.Image = (status == MessageBoxIcon.Error ? SystemIcons.Error : status == MessageBoxIcon.Warning ? SystemIcons.Warning : SystemIcons.Information).ToBitmap();
        StatusIcon.AccessibleName = "操作状态：" + (status == MessageBoxIcon.Error ? "失败" : status == MessageBoxIcon.Warning ? "警告" : "信息");
        StatusIcon.AccessibleDescription = StatusLabel.Text; previous?.Dispose();
    }

    internal async Task<bool> RequestCloseAsync(CloseChoice choice)
    {
        if (!busy && !HasPendingCleanup) return true;
        if (choice == CloseChoice.ContinueRunning) return false;
        await CancelOperationAsync();
        return !busy && !HasPendingCleanup;
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!busy && !HasPendingCleanup) return;
        e.Cancel = true; if (awaitingClose) return;
        if (HasPendingCleanup) { StatusLabel.Text = "请先重试清理，完成后再关闭窗口。"; return; }
        awaitingClose = true;
        try
        {
            using var choice = new CloseOperationDialog();
            if (choice.ShowDialog(this) == DialogResult.OK && await RequestCloseAsync(CloseChoice.CancelAndWait)) BeginInvoke(Close);
        }
        finally { awaitingClose = false; }
    }

    private static string ErrorText(string? code, DateTimeOffset? rateLimitReset = null) => code switch
    {
        "AUTH_OWNER_MISMATCH" or "AUTH_LOGIN_MISMATCH" or "BACKUP_OWNER_SESSION_MISMATCH" => "所选账号与登录账号不一致，请确认账号后重新检查。",
        "AUTH_API_CONSENT_REQUIRED" => "需要允许应用访问已登录账号。",
        "AUTH_APP_LOGIN_REQUIRED" or "AUTH_API_CREDENTIAL_MISSING" or "AUTH_API_CREDENTIAL_READ_FAILED" or "AUTH_API_CREDENTIAL_FORMAT_UNSUPPORTED" => "请先点击浏览器登录，再重新检查环境。",
        "AUTH_KEYRING_TARGET_MISSING" => "登录后检测到原有共享凭据条目缺失，请检查其他账号的登录状态。",
        "AUTH_KEYRING_ENUMERATION_FAILED" => "登录后无法确认原有共享凭据条目是否完整，请检查其他账号的登录状态。",
        "PREFLIGHT_TOOLS_REQUIRED" or "AUTH_TOOL_UNAVAILABLE" => "需要安装受支持的 Git、GitHub CLI 和 Git LFS 后重新检查。",
        "DEPENDENCY_SETUP_INCOMPLETE" => "安装未完成，可能已有部分安装。请重新检测或使用官方手动安装地址：\n" + DependencyConsentDialog.ManualGuidance,
        "AUTH_CANCELLED_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED" or "AUTH_LOGIN_TIMEOUT_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED" or "AUTH_LOGIN_FAILED_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED" => "登录未完成；当前 Windows 用户的共享账号可能已改变，请重新检查登录。",
        "HTTP_RATE_LIMITED" => "GitHub 请求次数已受限，" + (rateLimitReset is { } reset
            ? "预计重置时间：" + reset.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture)
            : "重置时间未知") + "，请稍后手动重新检查。",
        "STORAGE_FREE_SPACE_REQUIRED" or "BACKUP_INSUFFICIENT_FREE_SPACE" => "备份磁盘空间不足，请释放空间或选择其他目录。",
        "OWNER_INVALID" => "请输入有效的 GitHub 账号。",
        "PREFLIGHT_CANCELLED" or "BACKUP_CANCELLED" or "AUTH_CANCELLED" => "已取消，请重新检查环境。",
        "BACKUP_CLEANUP_PENDING" or "PREFLIGHT_CLEANUP_FAILED" => "清理尚未完成，请重试清理。",
        "BACKUP_OWNER_LOCK_UNAVAILABLE" => "该备份目录正在被其他任务使用，请稍后重试。",
        "BACKUP_SOURCE_UNSAFE" or "PROMOTION_RECOVERY_REQUIRED" => "备份目录需要检查，请保留现有文件并联系支持。",
        _ => "检查或备份未通过，请确认登录、网络和本地目录后重试。"
    };

    internal async Task RefreshHistoryAsync()
    {
        int revision = ++historyRevision;
        if (!ValidInputs) { HistoryLabel.Text = "请填写有效账号和目录以查看已有备份状态。"; return; }
        try
        {
            var history = await actions.ReadHistory(SelectedSettings(), historyCancellation.Token);
            if (IsDisposed || revision != historyRevision) return;
            string latest = history.Latest is { } run
                ? $"{StatusText(run.Status)} · {run.CompletedAt.ToLocalTime():yyyy-MM-dd HH:mm} · 仓库 {run.RepositoryCount} · 警告 {run.WarningCount} · 失败 {run.FailedRepositories.Count}"
                : "暂无运行记录";
            string success = history.LatestCoreSuccess is { } core ? core.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "暂无成功记录";
            if (history.Warnings.Contains("RUNNING_MARKER_ACTIVE")) latest = "运行中，请等待当前任务完成。";
            else if (history.Warnings.Contains("RUNNING_MARKER_INCOMPLETE")) latest = "上次运行未完成，请保留现有文件并检查。";
            else if (history.Warnings.Contains("RUNNING_MARKER_UNRESOLVED")) latest = "运行记录需要检查，请保留现有文件。";
            HistoryLabel.Text = $"最近一次运行：{latest}\n最近核心成功：{success}" + (history.Warnings.Any(w => !w.StartsWith("RUNNING_MARKER_", StringComparison.Ordinal)) ? "\n部分历史记录无法读取。" : "");
            if (history.Warnings.Contains("RUNNING_MARKER_COMPLETED_RESIDUE")) HistoryLabel.Text += "\n运行已完成，仍有待清理的完成标记。";
            if (history.Latest is not null && history.LatestIsFallback)
                HistoryLabel.Text += "\n本次运行记录保存在备用位置：" + @"%LOCALAPPDATA%\GitHubBackupTool\diagnostics\fallback-summaries";
        }
        catch (Exception)
        {
            if (!IsDisposed && revision == historyRevision) HistoryLabel.Text = "历史状态暂不可用，请确认本地目录。";
        }
    }

    private static string StatusText(RunStatus status) => status switch
    { RunStatus.Pass => "PASS", RunStatus.Partial => "PARTIAL", RunStatus.Cancelled => "CANCELLED", _ => "FAIL" };

    private async Task SaveSettingsAsync(AppSettings selected, CancellationToken token)
    {
        try
        {
            await actions.SaveSettings(selected, token);
            savedSettings = selected; pendingSettings = null;
        }
        catch (Exception ex)
        {
            pendingSettings = selected;
            if (ex is AtomicFileCleanupException owned) settingsCleanup = owned;
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed) { historyCancellation.Cancel(); historyCancellation.Dispose(); StatusIcon.Image?.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class CloseOperationDialog : Form
{
    internal CloseOperationDialog()
    {
        Text = "任务仍在运行"; AutoScaleMode = AutoScaleMode.Dpi; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var content = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new(16) };
        content.Controls.Add(new Label { AutoSize = true, Text = "关闭前需要等待任务和清理结束。" });
        var keep = new Button { AutoSize = true, Text = "继续运行(&K)", DialogResult = DialogResult.Cancel };
        var stop = new Button { AutoSize = true, Text = "取消并等待清理(&C)", DialogResult = DialogResult.OK };
        content.Controls.Add(keep); content.Controls.Add(stop); Controls.Add(content); AcceptButton = keep; CancelButton = keep;
    }
}
