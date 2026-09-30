namespace GitHubBackup.App;

internal sealed class DependencyConsentDialog : Form
{
    internal readonly Dictionary<DependencyId, CheckBox> Confirmations = [];
    internal readonly Dictionary<DependencyId, Button> InstallButtons = [];
    internal readonly Button DeclineButton = new();
    internal readonly TextBox ManualInstructions = new();
    internal DependencyId? SelectedDependency { get; private set; }
    internal static string ManualGuidance => "应用安装程序（winget）：https://apps.microsoft.com/detail/9nblggh4nns1\r\nGit：https://git-scm.com/install/windows\r\nGitHub CLI：https://cli.github.com/\r\nGit LFS：https://git-lfs.com/";

    internal DependencyConsentDialog(ToolInventory tools)
    {
        Text = "检测与安装依赖"; AutoScaleMode = AutoScaleMode.Dpi; AutoScroll = true;
        Size = new(720, 620); MinimumSize = new(480, 400); StartPosition = FormStartPosition.CenterParent;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(16) };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new(640, 0), Text = "每次仅安装一个已勾选的软件包。安装会联网下载、接受 winget 来源及软件许可协议，可能请求管理员权限；拒绝或取消不保证回滚已发生的安装。不会授予应用 GitHub 凭据访问许可。" });
        string wingetStatus = tools.Winget switch
        {
            { IsSupported: true } winget => $"已检测到 winget {winget.RawVersion}，可以安装下方未达标的软件。",
            { } winget => $"已检测到 winget {winget.RawVersion}，但版本未达要求（最低 1.29.290）；安装按钮不可用。请使用下方官方入口安装或更新“应用安装程序”。",
            _ => "未检测到可用的 winget；安装按钮不可用。请使用下方 Microsoft Store 官方入口安装“应用安装程序”（包含 winget），再重新检测。"
        };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new(640, 0), Text = wingetStatus });
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new(640, 0), Text = "勾选只表示同意该软件的下载和许可；还需点旁边的“安装”按钮才会开始。已满足最低版本的软件无需再次安装。" });
        foreach (var (id, package, floor) in new[] { (DependencyId.Git, "Git.Git", "2.55.0.windows.3"), (DependencyId.GitHubCli, "GitHub.cli", "2.100.0"), (DependencyId.GitLfs, "GitHub.GitLFS", "3.7.1") })
        {
            var tool = tools.Get(id);
            string version = tool is null ? "未检测到" : System.Text.RegularExpressions.Regex.Match(tool.RawVersion,
                @"\A(?:git version |gh version |git-lfs/)([0-9]+\.[0-9]+\.[0-9]+(?:\.windows\.[0-9]+)?)", System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)).Groups[1].Value;
            if (version.Length == 0) version = "版本无法识别";
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            bool alreadySupported = tool?.IsSupported == true;
            string state = alreadySupported ? "已满足最低版本，无需安装" : tool is null ? "未检测到" : "已检测到，但版本或格式未达要求";
            row.Controls.Add(new Label { AutoSize = true, Text = $"{package} · 检测 {version} · 最低 {floor} · {state}" });
            if (!alreadySupported)
            {
                var consent = new CheckBox { AutoSize = true, Text = $"同意下载、许可与可能的管理员请求：{package}", AccessibleName = "确认安装 " + package, TabIndex = 0 };
                var install = new Button { AutoSize = true, Text = "安装 " + package, AccessibleName = "安装已确认的软件包 " + package, Enabled = false, TabIndex = 1 };
                consent.CheckedChanged += (_, _) => install.Enabled = consent.Checked && tools.Winget?.IsSupported == true;
                install.Click += (_, _) => { SelectedDependency = id; DialogResult = DialogResult.OK; Close(); };
                Confirmations.Add(id, consent); InstallButtons.Add(id, install); row.Controls.Add(consent); row.Controls.Add(install);
            }
            layout.Controls.Add(row);
        }
        ManualInstructions.Text = ManualGuidance; ManualInstructions.Multiline = true; ManualInstructions.ReadOnly = true;
        ManualInstructions.Width = 640; ManualInstructions.Height = 85; ManualInstructions.AccessibleName = "官方手动安装地址，可选择复制"; layout.Controls.Add(ManualInstructions);
        bool hasInstallableItems = new[] { DependencyId.Git, DependencyId.GitHubCli, DependencyId.GitLfs }.Any(id => tools.Get(id)?.IsSupported != true);
        DeclineButton.Text = hasInstallableItems ? "关闭(&C)" : "完成(&C)"; DeclineButton.AutoSize = true; DeclineButton.AccessibleName = hasInstallableItems ? "关闭依赖窗口" : "完成依赖检查"; DeclineButton.DialogResult = DialogResult.Cancel;
        layout.Controls.Add(DeclineButton); Controls.Add(layout); CancelButton = DeclineButton; AcceptButton = DeclineButton;
        for (int i = 0; i < layout.Controls.Count; i++) layout.Controls[i].TabIndex = i;
    }
}

internal sealed class RootConsentDialog : LogViewerDialog
{
    internal readonly CheckBox ConfirmCheckBox = new(), CreateCheckBox = new();
    internal readonly Button SaveButton = new(), DeclineButton = new();
    internal RootConsentDialog(string root, bool exists) : base(new("确认备份目录", [root,
        exists ? "验证所示现有目录，保存设置并重新检查环境。" : "目录尚不存在。仅创建所示的准确目录，父目录必须已存在；新目录使用受限权限。失败时可能保留已创建的空目录。", "此操作不会开始备份或授予 GitHub 凭据访问许可。"]))
    {
        CloseViewButton.Visible = false; CloseViewButton.TabStop = false;
        LogTextBox.AccessibleName = "准确备份目录与操作范围";
        LogTextBox.Text = root + "\r\n" + string.Join("\r\n", LogTextBox.Lines.Skip(1));
        ConfirmCheckBox.Text = "确认验证并保存此目录(&A)"; ConfirmCheckBox.AccessibleName = "确认所示备份目录";
        CreateCheckBox.Text = "同意创建所示的准确目录(&N)"; CreateCheckBox.AccessibleName = "单独确认创建备份目录"; CreateCheckBox.Visible = !exists;
        SaveButton.Text = "验证并保存(&S)"; SaveButton.AccessibleName = "保存已确认的备份目录"; SaveButton.DialogResult = DialogResult.OK; SaveButton.Enabled = false;
        DeclineButton.Text = "取消(&C)"; DeclineButton.AccessibleName = "取消目录选择"; DeclineButton.DialogResult = DialogResult.Cancel;
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
    internal RepairConsentDialog(EnvironmentStatus status) : base(new("确认修复权限", [
        "仅修复下方勾选的准确路径，不递归遍历或批量修复子项。", "将所选项的所有者设为当前用户，并替换为受保护 DACL，仅允许当前用户、SYSTEM 和管理员完全控制。目录的继承规则供其子项继承。", "请确认当前账号、目录与检查结果仍匹配；修复后重新检查，不自动开始备份。"]))
    {
        candidates = status.UnsafeSensitivePaths.Where(e => RepairPolicy.IsRepairable(e.ErrorCode)).ToArray();
        CloseViewButton.Visible = false; CloseViewButton.TabStop = false;
        LogTextBox.AccessibleName = "非递归权限修复范围";
        Entries.CheckOnClick = true; Entries.Width = 680; Entries.Height = 150; Entries.HorizontalScrollbar = true;
        Entries.AccessibleName = "选择需要修复的准确路径";
        foreach (var entry in candidates) Entries.Items.Add(entry.FullPath + " · " + entry.ErrorCode, false);
        ConfirmCheckBox.Text = "确认仅修复勾选项的所有者与 DACL(&A)"; ConfirmCheckBox.AccessibleName = "确认非递归权限修复";
        RepairButton.Text = "修复并重新检查(&R)"; RepairButton.AccessibleName = "修复已确认的路径"; RepairButton.DialogResult = DialogResult.OK; RepairButton.Enabled = false;
        DeclineButton.Text = "取消(&C)"; DeclineButton.AccessibleName = "取消权限修复"; DeclineButton.DialogResult = DialogResult.Cancel;
        foreach (Control control in new Control[] { Entries, ConfirmCheckBox, RepairButton, DeclineButton })
        { if (control != Entries) control.AutoSize = true; control.TabIndex = Commands.Controls.Count; Commands.Controls.Add(control); }
        ConfirmCheckBox.CheckedChanged += (_, _) => RepairButton.Enabled = ConfirmCheckBox.Checked && Entries.CheckedItems.Count > 0;
        Entries.ItemCheck += (_, e) => RepairButton.Enabled = ConfirmCheckBox.Checked && Entries.CheckedItems.Count + (e.NewValue == CheckState.Checked ? 1 : -1) > 0;
        CancelButton = DeclineButton; AcceptButton = DeclineButton;
    }
}
