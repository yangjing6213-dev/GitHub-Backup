namespace GitHubBackup.App;

partial class MainForm
{
    internal TextBox OwnerTextBox = new(), BackupRootTextBox = new();
    internal ComboBox ModeComboBox = new();
    internal CheckBox ConsentCheckBox = new();
    internal Button BrowseButton = new(), LoginButton = new(), CheckButton = new(), StartButton = new(), CancelOperationButton = new(), RetryCleanupButton = new();
    internal Label StatusLabel = new(), ProgressLabel = new(), HistoryLabel = new();
    internal Button LatestLogButton = new(), OpenFolderButton = new(), DiagnosticsButton = new(), ReleaseDownloadButton = new();
    internal Button DependencyButton = new(), SaveRootButton = new(), RepairButton = new();
    internal TextBox LiveLogTextBox = new();
    internal Label PrivacyLabel = new();
    internal PictureBox StatusIcon = new();
    private ProgressBar ActivityBar = new();

    private void InitializeComponent()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(780, 650); MinimumSize = new Size(480, 480); AutoScroll = true;
        StartPosition = FormStartPosition.CenterScreen; Text = "GitHub 备份工具 · 内部测试版（未签名）";
        BackColor = SystemColors.Control; ForeColor = SystemColors.ControlText;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new(20) };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Add(Control control) { control.Margin = new Padding(0, 0, 0, 10); control.TabIndex = layout.Controls.Count; layout.Controls.Add(control); }
        void Labelled(string label, Control control)
        {
            Add(new Label { Text = label, AutoSize = true, UseMnemonic = true });
            control.AccessibleName = label.Replace("(&U)", "").Replace("(&D)", "").Replace("(&M)", "");
            control.Dock = DockStyle.Top; Add(control);
        }
        Add(new Label { Text = "GitHub 备份工具", AutoSize = true, Font = new Font(Font.FontFamily, 16, FontStyle.Bold) });
        Add(new Label { Text = "内部测试版（未签名）", AutoSize = true });
        ReleaseDownloadButton.Text = "正式版下载地址未配置"; ReleaseDownloadButton.AutoSize = true;
        ReleaseDownloadButton.Enabled = false; ReleaseDownloadButton.AccessibleName = "正式版下载地址未配置"; Add(ReleaseDownloadButton);
        HistoryLabel.AutoSize = true; HistoryLabel.MaximumSize = new Size(720, 0); HistoryLabel.Text = "正在读取已有备份状态…"; Add(HistoryLabel);
        Labelled("GitHub 账号(&U)", OwnerTextBox);
        Labelled("备份目录(&D)", BackupRootTextBox);
        void Button(Button button, string text)
        { button.Text = text; button.AccessibleName = text; button.AutoSize = true; Add(button); }
        Button(BrowseButton, "选择目录(&B)…");
        Button(SaveRootButton, "验证并保存目录(&P)…");
        ModeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        ModeComboBox.Items.AddRange(["日常备份（仓库、LFS、Wiki 和资料）", "完整备份（另含发布附件）"]); ModeComboBox.SelectedIndex = 0;
        Labelled("备份方式(&M)", ModeComboBox);
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
            SetStatusIcon(StatusLabel.Text.StartsWith("FAIL", StringComparison.Ordinal) ? MessageBoxIcon.Error
                : StatusLabel.Text.Contains("未完成") || StatusLabel.Text.Contains("警告") ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        };
        ProgressLabel.AutoSize = true; ProgressLabel.MaximumSize = new Size(720, 0); ProgressLabel.Text = "尚未开始备份。"; Add(ProgressLabel);
        ActivityBar.Dock = DockStyle.Top; ActivityBar.AccessibleName = "当前操作活动状态"; Add(ActivityBar);
        Add(new Label { Text = "实时脱敏日志（最多保留末尾 2,000 行）", AutoSize = true });
        LiveLogTextBox.Multiline = true; LiveLogTextBox.ReadOnly = true; LiveLogTextBox.WordWrap = false;
        LiveLogTextBox.ScrollBars = ScrollBars.Both; LiveLogTextBox.Dock = DockStyle.Top; LiveLogTextBox.Height = 160;
        LiveLogTextBox.AccessibleName = "实时脱敏日志"; LiveLogTextBox.BackColor = SystemColors.Window; LiveLogTextBox.ForeColor = SystemColors.WindowText; Add(LiveLogTextBox);
        Controls.Add(layout);
        layout.SizeChanged += (_, _) =>
        {
            int width = Math.Max(200, layout.ClientSize.Width - layout.Padding.Horizontal);
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
        ResumeLayout(true);
    }
}
