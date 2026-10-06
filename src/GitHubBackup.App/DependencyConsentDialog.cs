namespace GitHubBackup.App;

internal sealed class DependencyConsentDialog : Form
{
    internal readonly Dictionary<DependencyId, CheckBox> Confirmations = [];
    internal readonly Dictionary<DependencyId, Button> InstallButtons = [];
    internal readonly Button DeclineButton = new();
    internal readonly TextBox ManualInstructions = new();
    internal DependencyId? SelectedDependency { get; private set; }
    internal static string ManualGuidance => UiLanguageState.IsEnglish
        ? "App Installer (winget): https://apps.microsoft.com/detail/9nblggh4nns1\r\nGit: https://git-scm.com/install/windows\r\nGitHub CLI: https://cli.github.com/\r\nGit LFS: https://git-lfs.com/"
        : "应用安装程序（winget）：https://apps.microsoft.com/detail/9nblggh4nns1\r\nGit：https://git-scm.com/install/windows\r\nGitHub CLI：https://cli.github.com/\r\nGit LFS：https://git-lfs.com/";

    internal DependencyConsentDialog(ToolInventory tools)
    {
        bool english = UiLanguageState.IsEnglish;
        string T(string chinese, string translated) => english ? translated : chinese;
        Text = T("检测与安装依赖", "Check and install tools"); AutoScaleMode = AutoScaleMode.Dpi; AutoScroll = true;
        Size = new(720, 620); MinimumSize = new(480, 400); StartPosition = FormStartPosition.CenterParent;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(16) };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new(640, 0), Text = T("每次仅安装一个已勾选的软件包。安装会联网下载、接受 winget 来源及软件许可协议，可能请求管理员权限；拒绝或取消不保证回滚已发生的安装。不会授予应用 GitHub 凭据访问许可。", "Only one selected package is installed at a time. Installation downloads files, accepts winget source and software license terms, and may request administrator permission. Declining or cancelling does not guarantee rollback of an installation already in progress. This does not grant the app access to GitHub credentials.") });
        string wingetStatus = tools.Winget switch
        {
            { IsSupported: true } winget => T($"已检测到 winget {winget.RawVersion}，可以安装下方未达标的软件。", $"winget {winget.RawVersion} is available. You can install any tools below that do not meet the minimum version."),
            { } winget => T($"已检测到 winget {winget.RawVersion}，但版本未达要求（最低 1.29.290）；安装按钮不可用。请使用下方官方入口安装或更新“应用安装程序”。", $"winget {winget.RawVersion} is too old (minimum 1.29.290), so installation is unavailable. Use the official link below to install or update App Installer."),
            _ => T("未检测到可用的 winget；安装按钮不可用。请使用下方 Microsoft Store 官方入口安装“应用安装程序”（包含 winget），再重新检测。", "winget was not found, so installation is unavailable. Use the official Microsoft Store link below to install App Installer (which includes winget), then check again.")
        };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new(640, 0), Text = wingetStatus });
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new(640, 0), Text = T("勾选只表示同意该软件的下载和许可；还需点旁边的“安装”按钮才会开始。已满足最低版本的软件无需再次安装。", "Checking a box only accepts that package's download and license terms. Select its adjacent Install button to begin. Tools that already meet the minimum version do not need reinstalling.") });
        foreach (var (id, package, floor) in new[] { (DependencyId.Git, "Git.Git", "2.55.0.windows.3"), (DependencyId.GitHubCli, "GitHub.cli", "2.100.0"), (DependencyId.GitLfs, "GitHub.GitLFS", "3.7.1") })
        {
            var tool = tools.Get(id);
            string version = tool is null ? T("未检测到", "not detected") : System.Text.RegularExpressions.Regex.Match(tool.RawVersion,
                @"\A(?:git version |gh version |git-lfs/)([0-9]+\.[0-9]+\.[0-9]+(?:\.windows\.[0-9]+)?)", System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)).Groups[1].Value;
            if (version.Length == 0) version = T("版本无法识别", "unrecognized version");
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            bool alreadySupported = tool?.IsSupported == true;
            string state = alreadySupported ? T("已满足最低版本，无需安装", "meets minimum; no install needed") : tool is null ? T("未检测到", "not detected") : T("已检测到，但版本或格式未达要求", "detected, but version or format is unsupported");
            row.Controls.Add(new Label { AutoSize = true, Text = english ? $"{package} · detected {version} · minimum {floor} · {state}" : $"{package} · 检测 {version} · 最低 {floor} · {state}" });
            if (!alreadySupported)
            {
                var consent = new CheckBox { AutoSize = true, Text = T($"同意下载、许可与可能的管理员请求：{package}", $"Allow download, license terms, and possible administrator request: {package}"), AccessibleName = T("确认安装 ", "Confirm installation of ") + package, TabIndex = 0 };
                var install = new Button { AutoSize = true, Text = T("安装 ", "Install ") + package, AccessibleName = T("安装已确认的软件包 ", "Install approved package ") + package, Enabled = false, TabIndex = 1 };
                consent.CheckedChanged += (_, _) => install.Enabled = consent.Checked && tools.Winget?.IsSupported == true;
                install.Click += (_, _) => { SelectedDependency = id; DialogResult = DialogResult.OK; Close(); };
                Confirmations.Add(id, consent); InstallButtons.Add(id, install); row.Controls.Add(consent); row.Controls.Add(install);
            }
            layout.Controls.Add(row);
        }
        ManualInstructions.Text = ManualGuidance; ManualInstructions.Multiline = true; ManualInstructions.ReadOnly = true;
        ManualInstructions.Width = 640; ManualInstructions.Height = 85; ManualInstructions.AccessibleName = T("官方手动安装地址，可选择复制", "Official installation links; select and copy as needed"); layout.Controls.Add(ManualInstructions);
        bool hasInstallableItems = new[] { DependencyId.Git, DependencyId.GitHubCli, DependencyId.GitLfs }.Any(id => tools.Get(id)?.IsSupported != true);
        DeclineButton.Text = hasInstallableItems ? T("关闭(&C)", "Close(&C)") : T("完成(&C)", "Done(&C)"); DeclineButton.AutoSize = true; DeclineButton.AccessibleName = hasInstallableItems ? T("关闭依赖窗口", "Close tools window") : T("完成依赖检查", "Finish tools check"); DeclineButton.DialogResult = DialogResult.Cancel;
        layout.Controls.Add(DeclineButton); Controls.Add(layout); CancelButton = DeclineButton; AcceptButton = DeclineButton;
        for (int i = 0; i < layout.Controls.Count; i++) layout.Controls[i].TabIndex = i;
    }
}

internal sealed class RootConsentDialog : LogViewerDialog
{
    internal readonly CheckBox ConfirmCheckBox = new(), CreateCheckBox = new();
    internal readonly Button SaveButton = new(), DeclineButton = new();
    internal RootConsentDialog(string root, bool exists) : base(new(UiLanguageState.Text("确认备份目录", "Confirm backup folder"), [root,
        exists ? UiLanguageState.Text("验证所示现有目录，保存设置并重新检查环境。", "Validate the existing folder shown, save it, and check setup again.") : UiLanguageState.Text("目录尚不存在。仅创建所示的准确目录，父目录必须已存在；新目录使用受限权限。失败时可能保留已创建的空目录。", "This folder does not exist. Only the exact folder shown will be created; its parent must already exist. The new folder uses restricted permissions. An empty folder may remain if creation fails."), UiLanguageState.Text("此操作不会开始备份或授予 GitHub 凭据访问许可。", "This does not start a backup or grant access to GitHub credentials.")]))
    {
        CloseViewButton.Visible = false; CloseViewButton.TabStop = false;
        LogTextBox.AccessibleName = UiLanguageState.Text("准确备份目录与操作范围", "Exact backup folder and operation scope");
        LogTextBox.Text = root + "\r\n" + string.Join("\r\n", LogTextBox.Lines.Skip(1));
        ConfirmCheckBox.Text = UiLanguageState.Text("确认验证并保存此目录(&A)", "Confirm validation and save this folder(&A)"); ConfirmCheckBox.AccessibleName = UiLanguageState.Text("确认所示备份目录", "Confirm the backup folder shown");
        CreateCheckBox.Text = UiLanguageState.Text("同意创建所示的准确目录(&N)", "Allow creation of this exact folder(&N)"); CreateCheckBox.AccessibleName = UiLanguageState.Text("单独确认创建备份目录", "Separately confirm creation of the backup folder"); CreateCheckBox.Visible = !exists;
        SaveButton.Text = UiLanguageState.Text("验证并保存(&S)", "Validate and save(&S)"); SaveButton.AccessibleName = UiLanguageState.Text("保存已确认的备份目录", "Save the confirmed backup folder"); SaveButton.DialogResult = DialogResult.OK; SaveButton.Enabled = false;
        DeclineButton.Text = UiLanguageState.Text("取消(&C)", "Cancel(&C)"); DeclineButton.AccessibleName = UiLanguageState.Text("取消目录选择", "Cancel folder selection"); DeclineButton.DialogResult = DialogResult.Cancel;
        foreach (Control control in new Control[] { ConfirmCheckBox, CreateCheckBox, SaveButton, DeclineButton })
        { control.AutoSize = true; control.TabIndex = Commands.Controls.Count; Commands.Controls.Add(control); }
        void Update() => SaveButton.Enabled = ConfirmCheckBox.Checked && (exists || CreateCheckBox.Checked);
        ConfirmCheckBox.CheckedChanged += (_, _) => Update(); CreateCheckBox.CheckedChanged += (_, _) => Update();
        CancelButton = DeclineButton; AcceptButton = DeclineButton;
    }
}

internal sealed class RepairConsentDialog : LogViewerDialog
{
    internal readonly CheckedListBox Entries = new();
    internal readonly CheckBox ConfirmCheckBox = new();
    internal readonly Button RepairButton = new(), DeclineButton = new();
    private readonly SensitivePathAssessment[] candidates;
    internal IReadOnlyList<SensitivePathAssessment> SelectedEntries => Entries.CheckedIndices.Cast<int>().Select(i => candidates[i]).ToArray();
    internal RepairConsentDialog(EnvironmentStatus status) : base(new(UiLanguageState.Text("确认修复权限", "Confirm permission repair"), [
        UiLanguageState.Text("仅修复下方勾选的准确路径，不递归遍历或批量修复子项。", "Only the exact paths selected below will be repaired. Child items will not be traversed or changed in bulk."), UiLanguageState.Text("将所选项的所有者设为当前用户，并替换为受保护 DACL，仅允许当前用户、SYSTEM 和管理员完全控制。目录的继承规则供其子项继承。", "The selected item's owner will be set to the current user and its permissions replaced with a protected ACL that grants full control only to the current user, SYSTEM, and administrators. Folders pass their inheritance rules to children."), UiLanguageState.Text("请确认当前账号、目录与检查结果仍匹配；修复后重新检查，不自动开始备份。", "Confirm the account, folder, and check results still match. Check again after repair; a backup will not start automatically.")]))
    {
        candidates = status.UnsafeSensitivePaths.Where(e => RepairPolicy.IsRepairable(e.ErrorCode)).ToArray();
        CloseViewButton.Visible = false; CloseViewButton.TabStop = false;
        LogTextBox.AccessibleName = UiLanguageState.Text("非递归权限修复范围", "Non-recursive permission repair scope");
        Entries.CheckOnClick = true; Entries.Width = 680; Entries.Height = 150; Entries.HorizontalScrollbar = true;
        Entries.AccessibleName = UiLanguageState.Text("选择需要修复的准确路径", "Select exact paths to repair");
        foreach (var entry in candidates) Entries.Items.Add(entry.FullPath + " · " + entry.ErrorCode, false);
        ConfirmCheckBox.Text = UiLanguageState.Text("确认仅修复勾选项的所有者与 DACL(&A)", "Confirm changing the owner and permissions only for selected items(&A)"); ConfirmCheckBox.AccessibleName = UiLanguageState.Text("确认非递归权限修复", "Confirm non-recursive permission repair");
        RepairButton.Text = UiLanguageState.Text("修复并重新检查(&R)", "Repair and check again(&R)"); RepairButton.AccessibleName = UiLanguageState.Text("修复已确认的路径", "Repair the confirmed paths"); RepairButton.DialogResult = DialogResult.OK; RepairButton.Enabled = false;
        DeclineButton.Text = UiLanguageState.Text("取消(&C)", "Cancel(&C)"); DeclineButton.AccessibleName = UiLanguageState.Text("取消权限修复", "Cancel permission repair"); DeclineButton.DialogResult = DialogResult.Cancel;
        foreach (Control control in new Control[] { Entries, ConfirmCheckBox, RepairButton, DeclineButton })
        { if (control != Entries) control.AutoSize = true; control.TabIndex = Commands.Controls.Count; Commands.Controls.Add(control); }
        ConfirmCheckBox.CheckedChanged += (_, _) => RepairButton.Enabled = ConfirmCheckBox.Checked && Entries.CheckedItems.Count > 0;
        Entries.ItemCheck += (_, e) => RepairButton.Enabled = ConfirmCheckBox.Checked && Entries.CheckedItems.Count + (e.NewValue == CheckState.Checked ? 1 : -1) > 0;
        CancelButton = DeclineButton; AcceptButton = DeclineButton;
    }
}
