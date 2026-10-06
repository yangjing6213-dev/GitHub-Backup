namespace GitHubBackup.App;

internal enum CloseChoice { ContinueRunning, CancelAndWait }

public partial class MainForm : Form
{
    private const string DeviceLoginUrl = "https://github.com/login/device";
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
    private bool changingLanguage;
    private string statusChinese = "需要允许应用访问已登录账号。", statusEnglish = "Allow the app to access the signed-in account.";
    private string progressChinese = "尚未开始备份。", progressEnglish = "No backup has started.";
    private string historyChinese = "正在读取已有备份状态…", historyEnglish = "Loading backup history…";
    internal Task CurrentOperation { get; private set; } = Task.CompletedTask;
    private int progressRevision;
    private EnvironmentStatus? environmentStatus;

    public MainForm() : this(new DesktopWorkflow(AppPaths.Create(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))).Actions) { }
    internal MainForm(DesktopActions actions)
    {
        UiLanguageState.Current = AppUiLanguage.SimplifiedChinese;
        this.actions = actions; InitializeComponent(); LoadAuthorPortrait(); ApplyLanguage();
        string? executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            try { Icon = Icon.ExtractAssociatedIcon(executable) ?? Icon; }
            catch (Exception) { }
        }
        UpdateButtons();
        Shown += async (_, _) => await LoadSettingsAsync();
    }

    private bool IsEnglish => UiLanguageState.IsEnglish;
    private static string T(string chinese, string english) => UiLanguageState.Text(chinese, english);

    private void LoadAuthorPortrait()
    {
        string? resource = typeof(MainForm).Assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".author-avatar.png", StringComparison.Ordinal));
        if (resource is null) return;
        using Stream? stream = typeof(MainForm).Assembly.GetManifestResourceStream(resource);
        if (stream is null) return;
        using var image = Image.FromStream(stream);
        AuthorPictureBox.Image = new Bitmap(image);
    }

    private void ApplyLanguage()
    {
        changingLanguage = true;
        try
        {
            UiLanguageState.Current = LanguageComboBox.SelectedIndex == 1 ? AppUiLanguage.English : AppUiLanguage.SimplifiedChinese;
            bool en = IsEnglish;
            Text = T("GitHub 备份工具 · 内部测试版（未签名）", "GitHub Backup Tool · Preview (Unsigned)");
            LanguageLabel.Text = T("界面语言：", "Language:");
            AppTitleLabel.Text = T("GitHub 备份工具", "GitHub Backup Tool");
            BuildNoticeLabel.Text = T("内部测试版（未签名）", "Preview build (unsigned)");
            WorkspaceTabs.TabPages[0].Text = T("备份", "Backup");
            WorkspaceTabs.TabPages[1].Text = T("操作指导", "Guide");
            WorkspaceTabs.TabPages[2].Text = T("关于作者", "About");
            ReleaseDownloadButton.Text = T("正式版下载地址未配置", "Official download link is not configured");
            ReleaseDownloadButton.AccessibleName = ReleaseDownloadButton.Text;
            HistoryLabel.Text = T(historyChinese, historyEnglish);
            OwnerFieldLabel.Text = T("GitHub 账号(&U)", "GitHub account(&U)");
            BackupRootFieldLabel.Text = T("备份目录(&D)", "Backup folder(&D)");
            ModeFieldLabel.Text = T("备份方式(&M)", "Backup type(&M)");
            string ownerName = OwnerFieldLabel.Text.Replace("(&U)", "", StringComparison.Ordinal);
            string rootName = BackupRootFieldLabel.Text.Replace("(&D)", "", StringComparison.Ordinal);
            OwnerTextBox.AccessibleName = ownerName; BackupRootTextBox.AccessibleName = rootName;
            ModeComboBox.Items.Clear();
            ModeComboBox.Items.AddRange(en
                ? ["Daily backup (repositories, LFS, wikis, and related data)", "Full backup (also includes release assets)"]
                : ["日常备份（仓库、LFS、Wiki 和资料）", "完整备份（另含发布附件）"]);
            if (ModeComboBox.Items.Count != 0) ModeComboBox.SelectedIndex = Math.Clamp(ModeComboBox.SelectedIndex, 0, ModeComboBox.Items.Count - 1);
            ConsentCheckBox.Text = T(
                "允许本应用读取此 Windows 用户凭据库中所选 GitHub 账号的凭据，仅在内存中用于 GitHub 身份验证和只读备份请求。本应用不另存、显示、记录或导出该凭据。",
                "Allow this app to read the selected GitHub account's credential from this Windows user's credential store. It is used only in memory for GitHub authentication and read-only backup requests. The app does not save, display, log, or export the credential.");
            ConsentCheckBox.AccessibleName = T("允许应用访问已登录账号", "Allow access to the signed-in account");
            PrivacyLabel.Text = T(
                "备份原始内容按原样复制、不脱敏：源内容中已有的秘密也会保留，并由受限文件夹权限保护。应用凭据另行保护，应用生成的控制日志会脱敏。",
                "Backup files are copied as-is and are not redacted; secrets already present in source content remain in the backup. Folder permissions protect the files. App credentials are protected separately, and app-generated control logs are redacted.");
            BrowseButton.Text = BrowseButton.AccessibleName = T("选择目录(&B)…", "Browse…(&B)");
            SaveRootButton.Text = SaveRootButton.AccessibleName = T("验证并保存目录(&P)…", "Validate and save folder(&P)…");
            LoginButton.Text = LoginButton.AccessibleName = T("浏览器登录(&L)…", "Sign in with browser(&L)…");
            CheckButton.Text = CheckButton.AccessibleName = T("重新检查环境(&E)", "Check setup again(&E)");
            DependencyButton.Text = DependencyButton.AccessibleName = T("检测与安装依赖(&T)…", "Check and install tools(&T)…");
            RepairButton.Text = RepairButton.AccessibleName = T("检查并修复权限(&F)…", "Check and fix permissions(&F)…");
            StartButton.Text = StartButton.AccessibleName = T("开始备份(&S)", "Start backup(&S)");
            CancelOperationButton.Text = CancelOperationButton.AccessibleName = T("取消当前任务(&C)", "Cancel current task(&C)");
            RetryCleanupButton.Text = RetryCleanupButton.AccessibleName = T("重试清理(&R)", "Retry cleanup(&R)");
            LatestLogButton.Text = LatestLogButton.AccessibleName = T("查看最近日志(&V)…", "View latest log(&V)…");
            OpenFolderButton.Text = OpenFolderButton.AccessibleName = T("打开备份目录(&O)", "Open backup folder(&O)");
            DiagnosticsButton.Text = DiagnosticsButton.AccessibleName = T("预览并导出诊断(&X)…", "Preview and export diagnostics(&X)…");
            LiveLogTitleLabel.Text = T("实时脱敏日志（最多保留末尾 2,000 行）", "Live redacted log (keeps the latest 2,000 lines)");
            LiveLogTextBox.AccessibleName = T("实时脱敏日志", "Live redacted log");
            StatusIcon.AccessibleName = T("操作状态", "Operation status");
            AboutNameLabel.Text = T("Enhe（恩禾）", "Enhe (恩禾)");
            AboutRoleLabel.Text = T("产品设计师 · 一人公司实践者 · AI Builder", "Product designer · One-person-company practitioner · AI Builder");
            AboutTaglineLabel.Text = T("用 AI 打造一个人公司。", "Building a one-person company with AI.");
            AuthorContactText.Text = en
                ? "GitHub: https://github.com/yangjing6213-dev\nX / Twitter: https://x.com/Amenenhe_ai\nWebsite: https://www.enhe-tech.com.cn/\nWeChat: ENHE-AI\nEmail: amen.enhe@gmail.com"
                : "GitHub： https://github.com/yangjing6213-dev\nX / Twitter： https://x.com/Amenenhe_ai\n网站： https://www.enhe-tech.com.cn/\n微信： ENHE-AI\n邮箱： amen.enhe@gmail.com";
            GuideTextBox.Text = en ? EnglishGuide : ChineseGuide;
            StatusLabel.Text = T(statusChinese, statusEnglish);
            ProgressLabel.Text = T(progressChinese, progressEnglish);
            PrivacyLabel.AccessibleName = PrivacyLabel.Text;
        }
        finally { changingLanguage = false; }
    }

    private static readonly string ChineseGuide = """
GitHub 备份工具 · 操作指导

开始前
• 需要 Windows 11 x64、网络连接，以及 Git、GitHub CLI 和 Git LFS。可点“检测与安装依赖”检查；安装软件前会先征求同意。
• 选择一个有足够空间、你有权限写入的备份目录。建议选新目录；程序不会清理或覆盖目录里与本次备份无关的文件。
• 备份是原样复制。源仓库里已有的敏感内容也会一同保存，请保护好备份盘和备份文件。

备份步骤
1. 在“GitHub 账号”填写要备份的账号，并选择“备份目录”。新目录请先验证并保存。
2. 如尚未登录，点“浏览器登录”。程序会显示一次性代码并尝试打开 GitHub 登录页：https://github.com/login/device 。在网页输入当前窗口中的代码并完成授权；不要把代码发给他人。
3. 勾选凭据访问许可，再点“重新检查环境”。只有出现“检查通过”后，“开始备份”才会启用。
4. 选择“日常备份”或“完整备份”，然后点“开始备份”。运行时保持窗口打开；结束后查看状态、最近运行记录和备份目录。

常见问题
• 浏览器没有打开：看窗口是否显示一次性代码；用浏览器访问上面的 GitHub 地址并输入该代码。如果没有代码，不要猜，重新点“浏览器登录”。
• 登录/网络检查失败：先核对账号框与浏览器登录的是同一账号；再检查网络、代理是否可访问 GitHub，稍后重新检查。新版会显示错误代码，可按代码查看日志或导出脱敏诊断。
• NETWORK_CONNECTIONRESET：连接被中途断开，不等于尚未登录。程序会在可重试的连接故障后尝试 Windows 已配置的代理，并重新验证整条连接；不会修改系统代理。若仍失败，确认代理正在正常运行，稍后重新检查；反馈时附上错误代码和界面显示的连接方式。
• MIRROR_GIT_*：仓库镜像命令未完成；错误代码可能指出超时、连接中断、证书、代理、请求受限或访问被拒绝，UNCLASSIFIED 表示原因尚未确定。请打开“查看最近日志”并提供其中的分类计数。
• MIRROR_LOCAL_*：本机备份文件读写或权限检查失败。确认备份磁盘可用、目录可写；不要手动删除镜像目录，必要时查看最近日志中的错误分类。
• 目录无法保存或空间不足：确认目录存在、磁盘可写且可用空间至少 1 GiB；通过“选择目录”选择其他目录后再验证保存。
• PREFLIGHT_PRIVATE_READ_ACL_UNSAFE：其他本机账户可以读取所选备份目录。点击“检查并修复权限”，只勾选你确认的准确目录；修复会收紧该目录的访问权限，不会更改目录内容。
• 缺少或版本过旧的软件：点“检测与安装依赖”，确认后安装；也可以按依赖窗口中的官方地址手动安装，再重新检查。
• GitHub 请求次数受限：等待提示的重置时间后再操作，不需要重复登录。
• 仍无法解决：点“查看最近日志”或“预览并导出诊断”。分享前确认诊断已脱敏，不要发送一次性代码、凭据或完整备份内容。

提示：状态中的“浏览器登录流程已结束”仅表示登录命令已停止；请以上方显示的失败原因和错误代码为准。
""";

    private static readonly string EnglishGuide = """
GitHub Backup Tool · Getting Started

Before you begin
• Requires Windows 11 x64, an internet connection, Git, GitHub CLI, and Git LFS. Select “Check and install tools” to verify; you will be asked before software is installed.
• Choose a writable backup folder with enough free space. A new folder is recommended. The app does not clean up or overwrite unrelated files in that folder.
• Backups are copied as-is. Sensitive content already in a source repository is included, so protect the backup drive and files.

Backup steps
1. Enter the GitHub account to back up and choose a backup folder. Validate and save a new folder first.
2. If you are not signed in, select “Sign in with browser.” The app shows a one-time code and tries to open GitHub: https://github.com/login/device . Enter the code shown in the app and finish authorization. Never share the code.
3. Allow credential access, then select “Check setup again.” “Start backup” is enabled only after the setup check passes.
4. Choose “Daily backup” or “Full backup,” then select “Start backup.” Keep the window open. When finished, check the status, recent run history, and backup folder.

Common problems
• Browser did not open: check whether a one-time code is shown. Open the GitHub link above and enter that code. If there is no code, do not guess; start browser sign-in again.
• Sign-in or network check failed: confirm the account in the app matches the account signed in through the browser. Check that your network/proxy can reach GitHub, then retry. The updated app shows an error code you can use with the latest log or a redacted diagnostic export.
• NETWORK_CONNECTIONRESET: the connection was interrupted; this does not mean you are signed out. After a retryable connection failure, the app tries the configured Windows proxy and validates the whole route again, without changing system proxy settings. If it still fails, check that the proxy is running and retry later. Include the error code and displayed connection route when asking for help.
• MIRROR_GIT_*: a repository mirror command did not finish. The code may identify a timeout, interrupted connection, certificate, proxy, rate limit, or denied access; UNCLASSIFIED means the cause is still unknown. Open “View latest log” and share the category counts.
• MIRROR_LOCAL_*: reading, writing, or permission checks for the local backup files failed. Confirm the backup drive is available and the folder is writable. Do not manually delete mirror folders; review the latest error category first.
• Folder cannot be saved or disk space is low: make sure the folder exists, is writable, and has at least 1 GiB free. Choose another folder and validate it.
• PREFLIGHT_PRIVATE_READ_ACL_UNSAFE: other local accounts can read the selected backup folder. Select “Check and fix permissions” and check only the exact folder you intend to secure. This changes folder permissions, not its contents.
• A required tool is missing or too old: select “Check and install tools,” review the prompt, and install; or use the official links in that dialog, then check again.
• GitHub rate limit reached: wait until the displayed reset time. Repeated sign-ins are not needed.
• Still stuck: select “View latest log” or “Preview and export diagnostics.” Confirm the diagnostic is redacted before sharing. Never share one-time codes, credentials, or full backup contents.

Note: “Browser sign-in flow ended” only means the sign-in command stopped. Use the status above it and its error code to understand the result.
""";

    private void SetStatus(string chinese, string english)
    {
        statusChinese = chinese; statusEnglish = english;
        StatusLabel.Text = T(chinese, english);
    }

    private void SetProgress(string chinese, string english)
    {
        progressChinese = chinese; progressEnglish = english;
        ProgressLabel.Text = T(chinese, english);
    }

    private void SetHistory(string chinese, string english)
    {
        historyChinese = chinese; historyEnglish = english;
        HistoryLabel.Text = T(chinese, english);
    }

    private void OpenAuthorLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? uri)) return;
        bool allowed = uri.Scheme == Uri.UriSchemeHttps && uri.Host is "github.com" or "x.com" or "www.enhe-tech.com.cn"
            || uri.Scheme == Uri.UriSchemeMailto && uri.OriginalString == "mailto:amen.enhe@gmail.com";
        if (!allowed) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { SetStatus("无法打开此链接。请复制链接到浏览器中打开。", "Could not open this link. Copy it into your browser and open it there."); }
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
            SetStatus(
                loaded.Warnings.Count != 0 ? "设置无法读取，请确认账号和目录。" : ConsentCheckBox.Checked ? "尚未检查环境。" : "需要允许应用访问已登录账号。",
                loaded.Warnings.Count != 0 ? "Settings could not be read. Check the account and folder." : ConsentCheckBox.Checked ? "Setup has not been checked yet." : "Allow the app to access the signed-in account.");
            string? suggestion = SuggestedRoot(loaded.Settings, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory.Exists);
            if (suggestion is not null)
                SetStatus(statusChinese + " 默认 D: 不可用，可选择：" + suggestion + "（尚未创建）。",
                    statusEnglish + " Default drive D: is unavailable. You can choose " + suggestion + " (it has not been created).");
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
        if (loading || busy || rootSettingsReloadPending || changingLanguage) return;
        progressRevision++; LiveLogTextBox.Clear();
        ready = false; environmentStatus = null;
        bool revoke = ownerChanged && ConsentCheckBox.Checked;
        if (ownerChanged) { loading = true; ConsentCheckBox.Checked = false; loading = false; }
        SetStatus(ConsentCheckBox.Checked ? "设置已更改，请重新检查环境。" : "需要允许应用访问已登录账号。",
            ConsentCheckBox.Checked ? "Settings changed. Check setup again." : "Allow the app to access the signed-in account.");
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
            ready = false; environmentStatus = null; SetStatus("正在检查登录、网络和备份目录…", "Checking sign-in, network, and backup folder…");
            var selected = SelectedSettings(); await SaveSettingsAsync(selected, token);
            var snapshot = await actions.Check(SelectedMode, selected, token);
            token.ThrowIfCancellationRequested(); ShowEnvironment(snapshot);
        });
    }

    private void ShowEnvironment(EnvironmentStatus status)
    {
        environmentStatus = status; ready = status.Report.CanStartBackup && ConsentCheckBox.Checked && RootSaved;
        var issue = status.Report.Issues.FirstOrDefault(i => i.BlocksBackup);
        string chinese = ready
            ? status.Repositories.Count == 0 ? "检查通过：当前账号没有仓库，可以记录一次空备份。" : $"检查通过：发现 {status.Repositories.Count} 个仓库，可以开始备份。"
            : "检查未通过：" + ErrorText(issue?.ErrorCode, issue?.NetworkFailure?.RateLimitReset, issue?.NetworkFailure, false);
        string english = ready
            ? status.Repositories.Count == 0 ? "Setup check passed. This account has no repositories; an empty backup record can be created." : $"Setup check passed. Found {status.Repositories.Count} repositories. You can start the backup."
            : "Setup check did not pass: " + ErrorText(issue?.ErrorCode, issue?.NetworkFailure?.RateLimitReset, issue?.NetworkFailure, true);
        if (!ready && issue?.NetworkFailure is not null)
        {
            var route = status.SelectedProxyDisplayName switch
            {
                "Direct" => (Chinese: "直接连接", English: "Direct"),
                "Windows system proxy" => (Chinese: "Windows 系统代理", English: "Windows system proxy"),
                "Existing environment" => (Chinese: "现有环境代理", English: "Existing environment proxy"),
                _ => (Chinese: "", English: "")
            };
            if (route.Chinese.Length != 0)
            {
                chinese += "\n本次连接：" + route.Chinese + "。";
                english += "\nLast attempted route: " + route.English + ".";
            }
            if (status.Report.AuthReady && issue.NetworkFailure.FailureKind is NetworkFailureKind.Timeout or NetworkFailureKind.ConnectionRefused or NetworkFailureKind.ConnectionReset)
            {
                chinese += "\nGitHub 登录验证已通过，本次失败发生在后续网络检查。";
                english += "\nSign-in was verified; a later network check failed.";
            }
        }
        SetStatus(chinese, english);
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
            else SetStatus(selected is null ? "依赖窗口已关闭。已达标的软件无需再次安装；如需安装缺少或过旧的软件，请按窗口内提示检查 winget。" : "已取消安装。可按官方地址手动安装后重新检测。",
                selected is null ? "The tools window was closed. Up-to-date tools do not need reinstalling. To install missing or old tools, follow the window's winget guidance." : "Installation cancelled. You can use the official links in the dialog, then check again.");
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
        { ready = false; SetStatus("安装或重新检查未完成，可能已有部分安装；请重新检测，或使用官方手动安装地址：\n" + DependencyConsentDialog.ManualGuidance,
            "Installation or recheck did not finish; some tools may already be installed. Check again or use the official manual installation links:\n" + DependencyConsentDialog.ManualGuidance); }
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
            else SetStatus("已取消目录选择。", "Folder selection cancelled.");
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
            else SetStatus("已取消权限修复。", "Permission repair cancelled.");
            return Task.CompletedTask;
        });
        if (selected is not null) await RepairConfirmedAsync(snapshot, selected, true);
    }

    internal Task StartBackupAsync()
    {
        if (busy || !ready || !ConsentCheckBox.Checked || HasPendingCleanup) return Task.CompletedTask;
        return OperateAsync(async token =>
        {
            ready = false; SetStatus("正在备份；结束前请保持窗口打开。", "Backup is running. Keep this window open until it finishes.");
            LiveLogTextBox.Clear(); int revision = ++progressRevision;
            var result = await actions.Run(SelectedMode, SelectedSettings(), new Progress<BackupProgress>(value =>
            { if (revision == progressRevision) ShowProgress(value); }), token);
            var summary = result.Summary;
            string chinese = summary.Status switch
            {
                RunStatus.Pass => $"PASS · 备份完成，共 {summary.RepositoryCount} 个仓库。",
                RunStatus.Partial => $"PARTIAL · 备份完成，有 {summary.WarningCount} 项警告，请检查备份结果。",
                RunStatus.Cancelled => "CANCELLED · 已取消，清理完成。",
                _ => "FAIL · " + ErrorText(summary.ErrorCode, result.NetworkFailure?.RateLimitReset, result.NetworkFailure, false)
            };
            string english = summary.Status switch
            {
                RunStatus.Pass => $"PASS · Backup complete. {summary.RepositoryCount} repositories processed.",
                RunStatus.Partial => $"PARTIAL · Backup finished with {summary.WarningCount} warnings. Review the result.",
                RunStatus.Cancelled => "CANCELLED · Cancelled and cleanup complete.",
                _ => "FAIL · " + ErrorText(summary.ErrorCode, result.NetworkFailure?.RateLimitReset, result.NetworkFailure, true)
            };
            if (result.NetworkFailure is { FailureKind: NetworkFailureKind.RateLimited } limited && summary.ErrorCode != "HTTP_RATE_LIMITED")
            {
                chinese += " " + ErrorText("HTTP_RATE_LIMITED", limited.RateLimitReset, limited, false);
                english += " " + ErrorText("HTTP_RATE_LIMITED", limited.RateLimitReset, limited, true);
            }
            SetStatus(chinese, english);
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
            SetStatus(result.Opened ? "已请求打开备份目录。" : "备份目录无法安全打开，请确认目录存在且权限正确。",
                result.Opened ? "The backup folder was opened." : "The backup folder could not be opened safely. Check that it exists and you have permission.");
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
                if (dialog.ShowDialog(this) != DialogResult.OK) { SetStatus("已取消诊断导出。", "Diagnostic export cancelled."); return; }
                using var picker = new SaveFileDialog { Title = T("保存已检查的诊断", "Save reviewed diagnostics"), Filter = T("文本文件 (*.txt)|*.txt", "Text files (*.txt)|*.txt"), DefaultExt = "txt", AddExtension = true, FileName = "backup-diagnostics.txt", OverwritePrompt = true };
                if (picker.ShowDialog(this) != DialogResult.OK) { SetStatus("已取消诊断导出。", "Diagnostic export cancelled."); return; }
                var status = await actions.SaveDiagnostics(selected, preview.PreviewId, picker.FileName, true, token); consumed = true;
                SetStatus(status switch
                {
                    DiagnosticSaveStatus.Saved => "诊断已保存。",
                    DiagnosticSaveStatus.Cancelled => "已取消诊断导出。",
                    _ => "诊断来源或预览已改变，未保存；请重新预览。"
                }, status switch
                {
                    DiagnosticSaveStatus.Saved => "Diagnostics saved.",
                    DiagnosticSaveStatus.Cancelled => "Diagnostic export cancelled.",
                    _ => "Diagnostic sources or preview changed. Nothing was saved; preview again."
                });
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
        cancelling = true; SetStatus("正在取消，请等待当前操作和清理完成…", "Cancelling. Please wait for the operation and cleanup to finish…"); UpdateButtons();
        cancellation?.Cancel();
        try { await actions.Cancel(); await CurrentOperation; }
        catch (Exception) { SetStatus("操作未完成，请重新检查；若提示清理待完成，请重试清理。", "The operation did not finish. Check setup again; if cleanup is pending, retry cleanup."); }
        finally { cancelling = false; UpdateButtons(); }
    }

    internal Task RetryCleanupAsync()
    {
        if (busy || !HasPendingCleanup) return Task.CompletedTask;
        return OperateAsync(async _ =>
        {
            ready = false; SetStatus("正在重试清理…", "Retrying cleanup…");
            if (settingsCleanup is not null) { settingsCleanup.RetryCleanup(); settingsCleanup = null; }
            if (pendingSettings is not null) await SaveSettingsAsync(pendingSettings, CancellationToken.None);
            if (actions.HasPendingCleanup()) await actions.RetryCleanup();
            if (rootSettingsReloadPending) await ReconcileRootSettingsAsync();
            SetStatus("清理完成，请重新检查环境。" + (authSafetyError is null ? "" : " " + ErrorText(authSafetyError)),
                "Cleanup complete. Check setup again." + (authSafetyError is null ? "" : " " + ErrorText(authSafetyError, english: true)));
        });
    }

    private async Task LoginAsync()
    {
        if (busy || HasPendingCleanup) return;
        if (MessageBox.Show(this, T("浏览器登录会写入当前 Windows 用户的 GitHub 共享凭据库，可能改变该用户其他 GitHub CLI 实例的账号凭据或当前账号。是否继续登录？",
                "Browser sign-in writes to this Windows user's shared GitHub credential store and may change the account used by other GitHub CLI instances for this user. Continue?"),
            T("确认浏览器登录", "Confirm browser sign-in"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        await LoginConfirmedAsync(true);
    }

    internal Task LoginConfirmedAsync(bool confirmed)
    {
        if (!confirmed || busy || HasPendingCleanup) return Task.CompletedTask;
        return OperateAsync(async token =>
        {
            ready = false; authSafetyError = null; SetStatus("正在启动 GitHub 浏览器登录…", "Starting GitHub browser sign-in…");
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
                                if (match.Success)
                                {
                                    string code = match.Groups[1].Value;
                                    SetProgress("在 GitHub 浏览器页面输入一次性代码：" + code, "Enter this one-time code on the GitHub sign-in page: " + code);
                                    SetStatus("GitHub 登录页通常会自动打开；如果没有打开，请手动访问 " + DeviceLoginUrl + "，再输入当前界面显示的一次性代码。",
                                        "The GitHub sign-in page should open automatically. If it does not, visit " + DeviceLoginUrl + " and enter the one-time code shown in this window.");
                                }
                            }
                            loginLine.Clear(); discardLine = false;
                        }
                        else if (loginLine.Length < 100) loginLine.Append(character);
                        else discardLine = true;
                    }
                }), token);
                string chinese = result.AuthReady ? "登录完成，共享账号可能已更新；请确认账号并重新检查环境。"
                    : ErrorText(result.ErrorCode, result.NetworkFailure?.RateLimitReset, result.NetworkFailure, false);
                string english = result.AuthReady ? "Sign-in finished. The shared account may have changed; confirm the account and check setup again."
                    : ErrorText(result.ErrorCode, result.NetworkFailure?.RateLimitReset, result.NetworkFailure, true);
                if (result.BrowserOpenFailed)
                {
                    chinese += " 浏览器未能自动打开。如需重试，请再次点击“浏览器登录”，再手动访问 " + DeviceLoginUrl + " 并输入新显示的一次性代码。";
                    english += " The browser did not open automatically. To retry, select “Sign in with browser” again, visit " + DeviceLoginUrl + " and enter the new one-time code shown in this window.";
                }
                SetStatus(chinese, english);
            }
            finally { acceptingCode = false; loginLine.Clear(); SetProgress("浏览器登录流程已结束，请以上方状态为准。", "Browser sign-in flow ended. See the status above."); }
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
                SetStatus(ErrorText(ex.Outcome.ErrorCode), ErrorText(ex.Outcome.ErrorCode, english: true));
            }
            catch (OperationCanceledException) { ready = false; SetStatus("CANCELLED · 已取消，清理完成。", "CANCELLED · Cancelled and cleanup complete."); }
            catch (Exception) { ready = false; SetStatus("操作未完成，请检查设置后重试。", "The operation did not finish. Check your settings and try again."); }
            finally
            {
                cancellation.Dispose(); cancellation = null; busy = false;
                if (HasPendingCleanup)
                {
                    ready = false;
                    SetStatus((authSafetyError is null ? "" : ErrorText(authSafetyError) + " ") + "保存或清理尚未完成，请重试清理。完成前不能开始新任务或关闭窗口。",
                        (authSafetyError is null ? "" : ErrorText(authSafetyError, english: true) + " ") + "Saving or cleanup is still pending. Retry cleanup before starting another task or closing the app.");
                }
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
        string message = progress.Total > 0 ? $"{phase} · {progress.Current}/{progress.Total} · {repository}" : phase;
        SetProgress(message, message);
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
        StatusIcon.AccessibleName = T("操作状态：" + (status == MessageBoxIcon.Error ? "失败" : status == MessageBoxIcon.Warning ? "警告" : "信息"),
            "Operation status: " + (status == MessageBoxIcon.Error ? "failed" : status == MessageBoxIcon.Warning ? "warning" : "information"));
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
        if (HasPendingCleanup) { SetStatus("请先重试清理，完成后再关闭窗口。", "Retry cleanup before closing the app."); return; }
        awaitingClose = true;
        try
        {
            using var choice = new CloseOperationDialog();
            if (choice.ShowDialog(this) == DialogResult.OK && await RequestCloseAsync(CloseChoice.CancelAndWait)) BeginInvoke(Close);
        }
        finally { awaitingClose = false; }
    }

    internal static string ErrorText(string? code, DateTimeOffset? rateLimitReset = null,
        NetworkCheckResult? networkFailure = null, bool english = false)
    {
        string T(string chinese, string translated) => english ? translated : chinese;
        string reset = rateLimitReset is { } value
            ? value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture)
            : english ? "unknown" : "未知";
        string message = code switch
        {
            "AUTH_OWNER_MISMATCH" or "AUTH_LOGIN_MISMATCH" or "BACKUP_OWNER_SESSION_MISMATCH" => T("所选账号与浏览器登录账号不一致，请确认后重新检查。", "The selected account does not match the account signed in through the browser. Confirm the account and check again."),
            "AUTH_API_CONSENT_REQUIRED" => T("请勾选凭据访问许可，然后重新检查环境。", "Allow credential access in the checkbox, then check setup again."),
            "AUTH_APP_LOGIN_REQUIRED" or "AUTH_API_CREDENTIAL_MISSING" or "AUTH_API_CREDENTIAL_READ_FAILED" or "AUTH_API_CREDENTIAL_FORMAT_UNSUPPORTED" => T("请点击“浏览器登录”，完成 GitHub 登录后重新检查。", "Select “Sign in with browser,” finish GitHub sign-in, then check setup again."),
            "AUTH_KEYRING_TARGET_MISSING" => T("登录后发现原有 Windows 凭据条目缺失，或无法确认它仍完整。请检查其他 GitHub CLI 登录状态；如果账号有变化，重新确认后再登录。", "After sign-in, a prior Windows credential entry is missing or could not be verified. Check other GitHub CLI sign-ins; if the account changed, confirm it before signing in again."),
            "AUTH_KEYRING_ENUMERATION_FAILED" => T("无法确认原有 Windows 凭据记录是否完整。请检查其他 GitHub CLI 登录状态；若仍发生，请导出脱敏诊断。", "The app could not verify the existing Windows credential entries. Check other GitHub CLI sign-ins; if this continues, export a redacted diagnostic."),
            "AUTH_TOOL_REDETECTION_FAILED" => T("登录后未能重新识别 GitHub CLI。请重新检测依赖；若仍失败，查看最近日志。", "GitHub CLI could not be detected again after sign-in. Check the required tools; if this continues, review the latest log."),
            "PREFLIGHT_TOOLS_REQUIRED" or "AUTH_TOOL_UNAVAILABLE" => T("需要安装受支持版本的 Git、GitHub CLI 和 Git LFS；请打开“检测与安装依赖”。", "Supported versions of Git, GitHub CLI, and Git LFS are required. Open “Check and install tools.”"),
            "GIT_RUNTIME_COMMAND_FAILED" or "GIT_RUNTIME_CONFIG_QUERY_FAILED" or "GIT_HELPER_INVALID" or "GIT_RUNTIME_GIT_PATH_INVALID" => T("Git 与 GitHub CLI 的连接配置未完成。请在“检测与安装依赖”中确认工具可用，然后重新检查；若仍失败，请反馈此错误代码。", "Git connection setup with GitHub CLI did not finish. Open “Check and install tools,” confirm the tools are available, then check again. If this continues, report this error code."),
            "PREFLIGHT_PRIVATE_READ_ACL_UNSAFE" => T("其他本机账户可以读取所选备份目录。点击“检查并修复权限”，只勾选你确认的准确目录；修复会收紧该目录访问权限，不会更改目录内容。", "Other local accounts can read the selected backup folder. Open “Check and fix permissions” and select only the exact folder you intend to secure. This changes folder permissions, not its contents."),
            "DEPENDENCY_SETUP_INCOMPLETE" => T("安装未完成，可能已有部分安装。请重新检测，或使用以下官方地址手动安装：\n", "Installation did not finish; some tools may already be installed. Check again or install manually from these official links:\n") + DependencyConsentDialog.ManualGuidance,
            "AUTH_CANCELLED_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED" or "AUTH_LOGIN_TIMEOUT_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED" or "AUTH_LOGIN_FAILED_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED" => T("登录未完成；当前 Windows 用户的 GitHub CLI 共享账号可能已变化。请先确认登录账号，再重新检查。", "Sign-in did not finish. The shared GitHub CLI account for this Windows user may have changed. Confirm the signed-in account before checking again."),
            "AUTH_API_TIMEOUT" or "AUTH_STATUS_TIMEOUT" => T("连接 GitHub 的请求超时。请检查网络或代理能否访问 GitHub，稍后再试。", "The request to GitHub timed out. Check whether your network or proxy can reach GitHub, then try again later."),
            "NETWORK_CONNECTIONRESET" => NetworkFailureText(NetworkFailureKind.ConnectionReset, english),
            "NETWORK_CONNECTIONREFUSED" => NetworkFailureText(NetworkFailureKind.ConnectionRefused, english),
            "NETWORK_TIMEOUT" or "HTTP_TIMEOUT" => NetworkFailureText(NetworkFailureKind.Timeout, english),
            "AUTH_API_FAILED" or "AUTH_STATUS_FAILED" => networkFailure is { FailureKind: not NetworkFailureKind.None and not NetworkFailureKind.Unknown }
                ? NetworkFailureText(networkFailure.FailureKind, english)
                : T("GitHub 登录状态检查没有成功。请确认浏览器登录完成、账号正确，并检查网络后重试。", "GitHub sign-in status could not be verified. Confirm browser sign-in is complete and the account is correct, check your network, then try again."),
            "AUTH_STATUS_NOT_READY" => T("GitHub 登录尚未就绪。请在浏览器完成授权，确认账号后重新检查。", "GitHub sign-in is not ready. Finish authorization in the browser, confirm the account, then check again."),
            "AUTH_STATUS_INVALID" => T("GitHub CLI 返回的登录状态无法识别。请重新登录；如果仍失败，查看脱敏诊断。", "The GitHub CLI returned an unrecognized sign-in status. Sign in again; if this continues, review a redacted diagnostic."),
            "AUTH_PLAINTEXT_STORAGE_REJECTED" => T("为保护凭据，应用拒绝了不安全的明文凭据保存方式。请停止重复尝试并查看脱敏诊断。", "For credential safety, the app rejected an insecure plaintext storage method. Stop retrying and review a redacted diagnostic."),
            "AUTH_CONFIG_CLEANUP_FAILED" => T("登录临时文件未能完成安全清理。请重试清理；完成前不要开始新任务。", "Temporary sign-in files could not be cleaned up safely. Retry cleanup and do not start another task until it completes."),
            "HTTP_RATE_LIMITED" => english ? $"GitHub has limited requests. The limit is expected to reset at {reset}; try again after that." : $"GitHub 请求次数已受限，预计重置时间：{reset}，请稍后重新检查。",
            "STORAGE_FREE_SPACE_REQUIRED" or "BACKUP_INSUFFICIENT_FREE_SPACE" => T("备份磁盘空间不足；请释放空间，或选择至少有 1 GiB 可用空间的目录。", "There is not enough free disk space. Free space or choose a folder with at least 1 GiB available."),
            "OWNER_INVALID" => T("请输入有效的 GitHub 账号名。", "Enter a valid GitHub account name."),
            "PREFLIGHT_CANCELLED" or "BACKUP_CANCELLED" or "AUTH_CANCELLED" => T("操作已取消。", "The operation was cancelled."),
            "BACKUP_CLEANUP_PENDING" or "PREFLIGHT_CLEANUP_FAILED" or "PREFLIGHT_CLEANUP_PENDING" => T("清理尚未完成，请点“重试清理”。完成前不能开始新任务。", "Cleanup is not finished. Select “Retry cleanup”; another task cannot start until cleanup completes."),
            "PREFLIGHT_RECOVERY_PENDING" or "PROMOTION_RECOVERY_REQUIRED" => T("上次操作需要恢复。请保留已有文件，重试清理并查看最近日志。", "The prior operation needs recovery. Keep existing files, retry cleanup, and review the latest log."),
            "PREFLIGHT_CHECK_FAILED" or "AUTH_CHECK_FAILED" => T("环境检查未完成。请确认登录、网络和备份目录，再重新检查；下方会显示错误代码。", "The setup check did not finish. Confirm sign-in, network, and backup folder, then check again. The error code is shown below."),
            "REPOSITORY_DISCOVERY_INVALID" => T("GitHub 返回的仓库列表无法安全识别。请重新检查；若再次出现，导出脱敏诊断。", "The repository list from GitHub could not be verified safely. Check again; if this repeats, export a redacted diagnostic."),
            "BACKUP_OWNER_LOCK_UNAVAILABLE" => T("该备份目录正在被另一个任务使用，请等它完成后再试。", "Another task is using this backup folder. Wait for it to finish, then try again."),
            "BACKUP_SOURCE_UNSAFE" => T("备份目录需要检查。请保留现有文件并查看最近日志。", "The backup folder needs attention. Keep the existing files and review the latest log."),
            "MIRROR_BACKUP_FAILED" or "MIRROR_CLONE_FAILED" or "MIRROR_FETCH_FAILED" or "MIRROR_FSCK_FAILED" or "MIRROR_IDENTITY_INVALID" or "LFS_OBJECT_INTEGRITY_UNRESOLVED" => T("仓库镜像没有完成。请打开“查看最近日志”，查看脱敏的失败分类和数量；不要删除已有备份。", "The repository mirror did not complete. Open “View latest log” to review the redacted failure categories and counts. Do not delete existing backups."),
            "MIRROR_LOCAL_ACCESS_DENIED" => T("本机拒绝读取或写入备份文件。检查备份目录权限、磁盘状态；不要手动更改已有镜像内容。", "Windows denied access to local backup files. Check the folder permissions and drive. Do not manually change existing mirror contents."),
            "MIRROR_LOCAL_DATA_INVALID" or "MIRROR_LOCAL_LFS_INTEGRITY_FAILED" => T("本地镜像文件未通过完整性检查。请保留现有文件并查看最近日志；不要手动删除或覆盖镜像。", "A local mirror file failed its integrity check. Keep existing files and review the latest log; do not manually delete or overwrite a mirror."),
            "MIRROR_LOCAL_PATH_INVALID" => T("本地备份路径无法安全使用。请在设置中重新选择备份目录，再检查环境。", "The local backup path could not be used safely. Choose the backup folder again in Settings, then check setup."),
            "MIRROR_AUTH_INVALID" or "BACKUP_AUTH_INVALID" => T("备份期间登录状态发生变化，或登录信息无法继续安全读取。请重新检查环境；若提示未登录，再点击浏览器登录。不要删除已有备份。", "Sign-in changed during backup or could no longer be read safely. Check setup again; use browser sign-in if requested. Do not delete existing backups."),
            "MIRROR_LOCAL_SYSTEM_CALL_FAILED" or "MIRROR_LOCAL_IO_FAILED" => T("本机备份操作未完成。请检查磁盘空间、目录权限和是否有其他程序占用，并查看最近日志中的错误分类。", "A local backup operation did not finish. Check disk space, folder permissions, and whether another program is using the files, then review the latest error categories."),
            "MIRROR_FAILURE_UNCLASSIFIED" => T("备份操作未完成，原因尚未确定。请保留已有备份并导出脱敏诊断，不要反复更改登录或网络设置。", "The backup operation did not finish and the cause is still unknown. Keep existing backups and export redacted diagnostics; avoid repeatedly changing sign-in or network settings."),
            _ when code is not null && code.StartsWith("MIRROR_GIT_", StringComparison.Ordinal) => MirrorGitFailureText(code, english),
            _ when networkFailure is { FailureKind: not NetworkFailureKind.None and not NetworkFailureKind.Unknown } => NetworkFailureText(networkFailure.FailureKind, english),
            _ => T("目前无法判断具体原因。请按“操作指导”中的方法检查；反馈时附上下面的错误代码。", "The specific cause could not be identified. Follow the Guide steps; include the error code below when asking for help.")
        };
        return IsSafeErrorCode(code) ? message + T(" 错误代码：", " Error code: ") + code : message;
    }

    private static string MirrorGitFailureText(string code, bool english)
    {
        string T(string chinese, string translated) => english ? translated : chinese;
        if (code.EndsWith("_CONNECTION_RESET", StringComparison.Ordinal))
            return NetworkFailureText(NetworkFailureKind.ConnectionReset, english);
        if (code.EndsWith("_CONNECTION_REFUSED", StringComparison.Ordinal))
            return NetworkFailureText(NetworkFailureKind.ConnectionRefused, english);
        if (code.EndsWith("_TIMEOUT", StringComparison.Ordinal))
            return NetworkFailureText(NetworkFailureKind.Timeout, english);
        if (code.EndsWith("_TLS_CERTIFICATE", StringComparison.Ordinal))
            return NetworkFailureText(NetworkFailureKind.TlsCertificate, english);
        if (code.EndsWith("_PROXY_AUTHENTICATION", StringComparison.Ordinal))
            return NetworkFailureText(NetworkFailureKind.ProxyAuthentication, english);
        if (code.EndsWith("_RATE_LIMITED", StringComparison.Ordinal))
            return NetworkFailureText(NetworkFailureKind.RateLimited, english);
        if (code.EndsWith("_HTTP_5XX", StringComparison.Ordinal))
            return NetworkFailureText(NetworkFailureKind.Http5xx, english);
        if (code.EndsWith("_UNAUTHORIZED", StringComparison.Ordinal))
            return T("GitHub 拒绝了 Git 镜像访问。请确认浏览器登录账号有权访问这些仓库；环境登录检查通过不代表 Git 镜像通道的授权也正常。", "GitHub denied Git mirror access. Confirm the browser-signed-in account can access these repositories; passing the setup sign-in check does not guarantee Git mirror authorization." );
        if (code.EndsWith("_FORBIDDEN", StringComparison.Ordinal) || code.EndsWith("_NOT_FOUND", StringComparison.Ordinal))
            return T("GitHub 拒绝或找不到 Git 镜像目标。请确认登录账号有权访问仓库，且仓库仍存在。", "GitHub denied or could not find the Git mirror target. Confirm the signed-in account can access the repository and that it still exists.");
        return T("Git 镜像命令没有成功，程序未保存原始命令输出。请检查错误代码，并反馈最近日志中的脱敏分类计数。", "The Git mirror command did not succeed. The app did not save raw command output. Check the error code and share the redacted category counts from the latest log.");
    }

    private static bool IsSafeErrorCode(string? code) => code is { Length: > 0 and <= 80 }
        && System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9_]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string NetworkFailureText(NetworkFailureKind kind, bool english)
    {
        string T(string chinese, string translated) => english ? translated : chinese;
        return kind switch
        {
        NetworkFailureKind.Timeout => T("连接 GitHub 超时。请检查网络或代理，稍后再试。", "The connection to GitHub timed out. Check your network or proxy, then try again later."),
        NetworkFailureKind.ConnectionReset => T("与 GitHub 的连接被中途断开。请检查网络；若使用代理，请确认代理正常运行，再重新检查环境。", "The connection to GitHub was interrupted. Check your network and, if you use a proxy, confirm it is running before checking setup again."),
        NetworkFailureKind.ConnectionRefused => T("无法建立到 GitHub 的连接。请检查网络或代理设置后再试。", "A connection to GitHub could not be established. Check your network or proxy settings, then try again."),
        NetworkFailureKind.TlsCertificate => T("连接的安全证书验证未通过。请检查系统时间和网络代理，不要关闭证书验证。", "The connection's security certificate could not be verified. Check your system clock and network proxy; do not disable certificate checks."),
        NetworkFailureKind.ProxyAuthentication => T("代理服务器要求登录。请先在 Windows/浏览器中完成代理认证，再重试。", "The proxy requires sign-in. Complete proxy authentication in Windows or your browser, then try again."),
        NetworkFailureKind.Unauthorized => T("GitHub 未接受当前登录状态。请使用浏览器重新登录，并确认账号正确。", "GitHub did not accept the current sign-in. Sign in again through the browser and confirm the account."),
        NetworkFailureKind.Forbidden => T("GitHub 拒绝了请求。请确认账号有权访问目标，并检查是否触发请求限制。", "GitHub denied the request. Confirm the account can access the target and check whether a request limit was reached."),
        NetworkFailureKind.NotFound => T("GitHub 没有找到请求的内容。请确认账号和目标仓库名称。", "GitHub could not find the requested content. Confirm the account and repository name."),
        NetworkFailureKind.Http5xx => T("GitHub 暂时无法处理请求，请稍后重新检查。", "GitHub could not process the request temporarily. Check again later."),
        NetworkFailureKind.RateLimited => T("GitHub 请求次数已受限，请等待限制重置后再试。", "GitHub has limited requests. Wait for the limit to reset, then try again."),
            _ => T("与 GitHub 通信未成功。请检查网络和代理后重试。", "Communication with GitHub did not succeed. Check your network and proxy, then try again.")
        };
    }

    internal async Task RefreshHistoryAsync()
    {
        int revision = ++historyRevision;
        if (!ValidInputs) { SetHistory("请填写有效账号和目录以查看已有备份状态。", "Enter a valid account and folder to view backup history."); return; }
        try
        {
            var history = await actions.ReadHistory(SelectedSettings(), historyCancellation.Token);
            if (IsDisposed || revision != historyRevision) return;
            string latest = history.Latest is { } run
                ? $"{StatusText(run.Status)} · {run.CompletedAt.ToLocalTime():yyyy-MM-dd HH:mm} · 仓库 {run.RepositoryCount} · 警告 {run.WarningCount} · 失败 {run.FailedRepositories.Count}"
                : "暂无运行记录";
            string success = history.LatestCoreSuccess is { } core ? core.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "暂无成功记录";
            string latestEnglish = history.Latest is { } latestRun
                ? $"{StatusText(latestRun.Status)} · {latestRun.CompletedAt.ToLocalTime():yyyy-MM-dd HH:mm} · repositories {latestRun.RepositoryCount} · warnings {latestRun.WarningCount} · failures {latestRun.FailedRepositories.Count}"
                : "No run recorded";
            string successEnglish = history.LatestCoreSuccess is { } coreRun ? coreRun.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "No successful backup recorded";
            if (history.Warnings.Contains("RUNNING_MARKER_ACTIVE")) { latest = "运行中，请等待当前任务完成。"; latestEnglish = "A backup is running. Wait for it to finish."; }
            else if (history.Warnings.Contains("RUNNING_MARKER_INCOMPLETE")) { latest = "上次运行未完成，请保留现有文件并检查。"; latestEnglish = "The previous run did not finish. Keep existing files and review them."; }
            else if (history.Warnings.Contains("RUNNING_MARKER_UNRESOLVED")) { latest = "运行记录需要检查，请保留现有文件。"; latestEnglish = "The run record needs review. Keep existing files."; }
            string chinese = $"最近一次运行：{latest}\n最近核心成功：{success}" + (history.Warnings.Any(w => !w.StartsWith("RUNNING_MARKER_", StringComparison.Ordinal)) ? "\n部分历史记录无法读取。" : "");
            string english = $"Latest run: {latestEnglish}\nLatest successful backup: {successEnglish}" + (history.Warnings.Any(w => !w.StartsWith("RUNNING_MARKER_", StringComparison.Ordinal)) ? "\nSome history records could not be read." : "");
            if (history.Warnings.Contains("RUNNING_MARKER_COMPLETED_RESIDUE")) { chinese += "\n运行已完成，仍有待清理的完成标记。"; english += "\nThe run finished, but a completion marker still needs cleanup."; }
            if (history.Latest is not null && history.LatestIsFallback)
            {
                chinese += "\n本次运行记录保存在备用位置：" + @"%LOCALAPPDATA%\GitHubBackupTool\diagnostics\fallback-summaries";
                english += "\nThis run record is stored in the fallback location: " + @"%LOCALAPPDATA%\GitHubBackupTool\diagnostics\fallback-summaries";
            }
            SetHistory(chinese, english);
        }
        catch (Exception)
        {
            if (!IsDisposed && revision == historyRevision) SetHistory("历史状态暂不可用，请确认本地目录。", "Backup history is temporarily unavailable. Check the local folder.");
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
        if (disposing && !IsDisposed) { historyCancellation.Cancel(); historyCancellation.Dispose(); StatusIcon.Image?.Dispose(); AuthorPictureBox.Image?.Dispose(); Icon?.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class CloseOperationDialog : Form
{
    internal CloseOperationDialog()
    {
        Text = UiLanguageState.Text("任务仍在运行", "Task is still running"); AutoScaleMode = AutoScaleMode.Dpi; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var content = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new(16) };
        content.Controls.Add(new Label { AutoSize = true, Text = UiLanguageState.Text("关闭前需要等待任务和清理结束。", "Wait for the task and cleanup to finish before closing.") });
        var keep = new Button { AutoSize = true, Text = UiLanguageState.Text("继续运行(&K)", "Keep running(&K)"), DialogResult = DialogResult.Cancel };
        var stop = new Button { AutoSize = true, Text = UiLanguageState.Text("取消并等待清理(&C)", "Cancel and wait for cleanup(&C)"), DialogResult = DialogResult.OK };
        content.Controls.Add(keep); content.Controls.Add(stop); Controls.Add(content); AcceptButton = keep; CancelButton = keep;
    }
}
