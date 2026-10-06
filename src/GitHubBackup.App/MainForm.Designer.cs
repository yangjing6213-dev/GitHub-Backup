namespace GitHubBackup.App;

partial class MainForm
{
    internal TextBox OwnerTextBox = new(), BackupRootTextBox = new();
    internal ComboBox ModeComboBox = new(), LanguageComboBox = new();
    internal CheckBox ConsentCheckBox = new();
    internal Button BrowseButton = new(), LoginButton = new(), CheckButton = new(), StartButton = new(), CancelOperationButton = new(), RetryCleanupButton = new();
    internal Label StatusLabel = new(), ProgressLabel = new(), HistoryLabel = new();
    internal Label LanguageLabel = new(), AppTitleLabel = new(), BuildNoticeLabel = new(), OwnerFieldLabel = new(), BackupRootFieldLabel = new(), ModeFieldLabel = new(), LiveLogTitleLabel = new();
    internal Label AboutNameLabel = new(), AboutRoleLabel = new(), AboutTaglineLabel = new();
    internal Button LatestLogButton = new(), OpenFolderButton = new(), DiagnosticsButton = new(), ReleaseDownloadButton = new();
    internal Button DependencyButton = new(), SaveRootButton = new(), RepairButton = new();
    internal TextBox LiveLogTextBox = new();
    internal Label PrivacyLabel = new();
    internal PictureBox StatusIcon = new(), AuthorPictureBox = new();
    internal TabControl WorkspaceTabs = new();
    internal TabPage BackupTab = new(), GuideTab = new(), AboutTab = new();
    internal RichTextBox GuideTextBox = new(), AuthorContactText = new();
    private ProgressBar ActivityBar = new();

    private void InitializeComponent()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(780, 650); MinimumSize = new Size(480, 480);
        StartPosition = FormStartPosition.CenterScreen; Text = "GitHub 备份工具 · 内部测试版（未签名）";
        BackColor = SystemColors.Control; ForeColor = SystemColors.ControlText;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new(8) };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100));
        var languageBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new(4, 0, 4, 6) };
        LanguageLabel.Text = "界面语言："; LanguageLabel.AutoSize = true; LanguageLabel.Margin = new(0, 7, 8, 0);
        LanguageComboBox.DropDownStyle = ComboBoxStyle.DropDownList; LanguageComboBox.Width = 132; LanguageComboBox.Items.AddRange(["简体中文", "English"]); LanguageComboBox.SelectedIndex = 0;
        languageBar.Controls.Add(LanguageLabel); languageBar.Controls.Add(LanguageComboBox);

        WorkspaceTabs.Dock = DockStyle.Fill;
        BackupTab.Text = "备份"; GuideTab.Text = "操作指导"; AboutTab.Text = "关于作者";
        BackupTab.AutoScroll = true;
        WorkspaceTabs.TabPages.AddRange([BackupTab, GuideTab, AboutTab]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new(16) };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Add(Control control) { control.Margin = new Padding(0, 0, 0, 10); control.TabIndex = layout.Controls.Count; layout.Controls.Add(control); }
        void Labelled(Label label, string text, Control control)
        {
            label.Text = text; label.AutoSize = true; label.UseMnemonic = true; Add(label);
            control.AccessibleName = text.Replace("(&U)", "").Replace("(&D)", "").Replace("(&M)", "");
            control.Dock = DockStyle.Top; Add(control);
        }
        Add(AppTitleLabel);
        AppTitleLabel.Text = "GitHub 备份工具"; AppTitleLabel.AutoSize = true; AppTitleLabel.Font = new Font(Font.FontFamily, 16, FontStyle.Bold);
        Add(BuildNoticeLabel); BuildNoticeLabel.Text = "内部测试版（未签名）"; BuildNoticeLabel.AutoSize = true;
        ReleaseDownloadButton.Text = "正式版下载地址未配置"; ReleaseDownloadButton.AutoSize = true;
        ReleaseDownloadButton.Enabled = false; ReleaseDownloadButton.AccessibleName = "正式版下载地址未配置"; Add(ReleaseDownloadButton);
        HistoryLabel.AutoSize = true; HistoryLabel.MaximumSize = new Size(720, 0); HistoryLabel.Text = "正在读取已有备份状态…"; Add(HistoryLabel);
        Labelled(OwnerFieldLabel, "GitHub 账号(&U)", OwnerTextBox);
        Labelled(BackupRootFieldLabel, "备份目录(&D)", BackupRootTextBox);
        void Button(Button button, string text)
        { button.Text = text; button.AccessibleName = text; button.AutoSize = true; Add(button); }
        Button(BrowseButton, "选择目录(&B)…");
        Button(SaveRootButton, "验证并保存目录(&P)…");
        ModeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        ModeComboBox.Items.AddRange(["日常备份（仓库、LFS、Wiki 和资料）", "完整备份（另含发布附件）"]); ModeComboBox.SelectedIndex = 0;
        Labelled(ModeFieldLabel, "备份方式(&M)", ModeComboBox);
        ConsentCheckBox.Text = "允许本应用读取此 Windows 用户凭据库中所选 GitHub 账号的凭据，仅在内存中用于 GitHub 身份验证和只读备份请求。本应用不另存、显示、记录或导出该凭据。";
        ConsentCheckBox.AccessibleName = "允许应用访问已登录账号";
        ConsentCheckBox.AutoSize = true; ConsentCheckBox.MaximumSize = new Size(720, 0); Add(ConsentCheckBox);
        PrivacyLabel.Text = "备份原始内容按原样复制、不脱敏：源内容中已有的秘密也会保留，并由受限文件夹权限保护。应用凭据另行保护，应用生成的控制日志会脱敏。";
        PrivacyLabel.AutoSize = true; PrivacyLabel.MaximumSize = new(720, 0); Add(PrivacyLabel);
        Button(LoginButton, "浏览器登录(&L)…"); Button(CheckButton, "重新检查环境(&E)");
        Button(DependencyButton, "检测与安装依赖(&T)…"); Button(RepairButton, "检查并修复权限(&F)…");
        Button(StartButton, "开始备份(&S)"); Button(CancelOperationButton, "取消当前任务(&C)");
        Button(RetryCleanupButton, "重试清理(&R)");
        Button(LatestLogButton, "查看最近日志(&V)…"); Button(OpenFolderButton, "打开备份目录(&O)"); Button(DiagnosticsButton, "预览并导出诊断(&X)…");
        StatusIcon.Size = new(24, 24); StatusIcon.SizeMode = PictureBoxSizeMode.Zoom; StatusIcon.TabStop = false;
        StatusIcon.AccessibleName = "操作状态"; StatusIcon.Image = SystemIcons.Information.ToBitmap(); Add(StatusIcon);
        StatusLabel.AutoSize = true; StatusLabel.MaximumSize = new Size(720, 0); StatusLabel.Text = "需要允许应用访问已登录账号。"; Add(StatusLabel);
        StatusLabel.TextChanged += (_, _) =>
        {
            string text = StatusLabel.Text;
            SetStatusIcon(text.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase) || text.Contains("失败", StringComparison.Ordinal) || text.Contains("failed", StringComparison.OrdinalIgnoreCase)
                ? MessageBoxIcon.Error
                : text.Contains("未完成", StringComparison.Ordinal) || text.Contains("警告", StringComparison.Ordinal) || text.Contains("pending", StringComparison.OrdinalIgnoreCase) || text.Contains("warning", StringComparison.OrdinalIgnoreCase)
                    ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        };
        ProgressLabel.AutoSize = true; ProgressLabel.MaximumSize = new Size(720, 0); ProgressLabel.Text = "尚未开始备份。"; Add(ProgressLabel);
        ActivityBar.Dock = DockStyle.Top; ActivityBar.AccessibleName = "当前操作活动状态"; Add(ActivityBar);
        Add(LiveLogTitleLabel); LiveLogTitleLabel.Text = "实时脱敏日志（最多保留末尾 2,000 行）"; LiveLogTitleLabel.AutoSize = true;
        LiveLogTextBox.Multiline = true; LiveLogTextBox.ReadOnly = true; LiveLogTextBox.WordWrap = false; LiveLogTextBox.TabStop = false;
        LiveLogTextBox.ScrollBars = ScrollBars.Both; LiveLogTextBox.Dock = DockStyle.Top; LiveLogTextBox.Height = 160;
        LiveLogTextBox.AccessibleName = "实时脱敏日志"; LiveLogTextBox.BackColor = SystemColors.Window; LiveLogTextBox.ForeColor = SystemColors.WindowText; Add(LiveLogTextBox);
        BackupTab.Controls.Add(layout);

        GuideTextBox.Dock = DockStyle.Fill; GuideTextBox.ReadOnly = true; GuideTextBox.DetectUrls = true; GuideTextBox.Multiline = true;
        GuideTextBox.WordWrap = true; GuideTextBox.ScrollBars = RichTextBoxScrollBars.Vertical; GuideTextBox.BorderStyle = BorderStyle.None;
        GuideTextBox.BackColor = SystemColors.Window; GuideTextBox.Font = new Font(Font.FontFamily, 10); GuideTab.Controls.Add(GuideTextBox);

        var about = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 2, RowCount = 4 };
        about.ColumnStyles.Add(new(SizeType.Absolute, 195)); about.ColumnStyles.Add(new(SizeType.Percent, 100));
        about.RowStyles.Add(new(SizeType.AutoSize)); about.RowStyles.Add(new(SizeType.AutoSize)); about.RowStyles.Add(new(SizeType.AutoSize)); about.RowStyles.Add(new(SizeType.Percent, 100));
        AuthorPictureBox.Dock = DockStyle.Fill; AuthorPictureBox.SizeMode = PictureBoxSizeMode.Zoom; AuthorPictureBox.Margin = new(0, 0, 18, 0);
        about.Controls.Add(AuthorPictureBox, 0, 0); about.SetRowSpan(AuthorPictureBox, 4);
        AboutNameLabel.AutoSize = true; AboutNameLabel.Font = new Font(Font.FontFamily, 18, FontStyle.Bold); AboutNameLabel.Text = "Enhe（恩禾）"; AboutNameLabel.Margin = new(0, 0, 0, 10); about.Controls.Add(AboutNameLabel, 1, 0);
        AboutRoleLabel.AutoSize = true; AboutRoleLabel.Text = "产品设计师 · 一人公司实践者 · AI Builder"; AboutRoleLabel.Margin = new(0, 0, 0, 10); about.Controls.Add(AboutRoleLabel, 1, 1);
        AboutTaglineLabel.AutoSize = true; AboutTaglineLabel.Text = "用 AI 打造一个人公司。"; AboutTaglineLabel.Margin = new(0, 0, 0, 16); about.Controls.Add(AboutTaglineLabel, 1, 2);
        AuthorContactText.Dock = DockStyle.Fill; AuthorContactText.ReadOnly = true; AuthorContactText.DetectUrls = true; AuthorContactText.BorderStyle = BorderStyle.None;
        AuthorContactText.BackColor = SystemColors.Control; AuthorContactText.Font = new Font(Font.FontFamily, 10); about.Controls.Add(AuthorContactText, 1, 3);
        AboutTab.Controls.Add(about);

        root.Controls.Add(languageBar, 0, 0); root.Controls.Add(WorkspaceTabs, 0, 1); Controls.Add(root);
        root.SizeChanged += (_, _) =>
        {
            int width = Math.Max(200, BackupTab.ClientSize.Width - layout.Padding.Horizontal - 24);
            ConsentCheckBox.MaximumSize = PrivacyLabel.MaximumSize = StatusLabel.MaximumSize = ProgressLabel.MaximumSize = HistoryLabel.MaximumSize = new Size(width, 0);
        };
        OwnerTextBox.TextChanged += (_, _) => SettingsChanged(true);
        BackupRootTextBox.TextChanged += (_, _) => SettingsChanged(false);
        ModeComboBox.SelectedIndexChanged += (_, _) => SettingsChanged(false);
        ConsentCheckBox.CheckedChanged += (_, _) => SettingsChanged(false, true);
        BrowseButton.Click += async (_, _) => await ChooseRootAsync(true);
        SaveRootButton.Click += async (_, _) => await ChooseRootAsync(false);
        DependencyButton.Click += async (_, _) => await DetectAndInstallAsync();
        RepairButton.Click += async (_, _) => await ChooseRepairAsync();
        LoginButton.Click += async (_, _) => await LoginAsync();
        CheckButton.Click += async (_, _) => await CheckEnvironmentAsync();
        StartButton.Click += async (_, _) => await StartBackupAsync();
        CancelOperationButton.Click += async (_, _) => await CancelOperationAsync();
        RetryCleanupButton.Click += async (_, _) => await RetryCleanupAsync();
        LatestLogButton.Click += async (_, _) => await ShowLatestLogAsync();
        OpenFolderButton.Click += async (_, _) => await OpenBackupFolderAsync();
        DiagnosticsButton.Click += async (_, _) => await ExportDiagnosticsAsync();
        LanguageComboBox.SelectedIndexChanged += (_, _) => ApplyLanguage();
        AuthorContactText.LinkClicked += (_, e) => OpenAuthorLink(e.LinkText);
        ResumeLayout(true);
    }
}
