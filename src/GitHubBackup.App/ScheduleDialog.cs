namespace GitHubBackup.App;

internal sealed class ScheduleDialog : Form
{
    internal CheckBox EnabledCheckBox { get; } = new();
    internal ComboBox FrequencyComboBox { get; } = new();
    internal NumericUpDown HourControl { get; } = new();
    internal NumericUpDown MinuteControl { get; } = new();
    internal ComboBox DayComboBox { get; } = new();
    internal Button SaveButton { get; } = new();
    internal new Button CancelButton { get; } = new();
    internal BackupSchedule SelectedSchedule => new(EnabledCheckBox.Checked,
        FrequencyComboBox.SelectedIndex == 1 ? BackupScheduleFrequency.Weekly : FrequencyComboBox.SelectedIndex == 2 ? BackupScheduleFrequency.Daily : BackupScheduleFrequency.Disabled,
        (int)HourControl.Value, (int)MinuteControl.Value, (DayOfWeek)Math.Clamp(DayComboBox.SelectedIndex, 0, 6));

    internal ScheduleDialog(BackupSchedule current)
    {
        Text = "自动备份设置"; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; ClientSize = new Size(430, 250);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        EnabledCheckBox.Text = "启用自动备份"; EnabledCheckBox.AccessibleName = EnabledCheckBox.Text; EnabledCheckBox.AutoSize = true;
        FrequencyComboBox.DropDownStyle = ComboBoxStyle.DropDownList; FrequencyComboBox.AccessibleName = "备份频率";
        FrequencyComboBox.Items.AddRange(["不自动备份", "每周", "每天"]);
        HourControl.Minimum = 0; HourControl.Maximum = 23; HourControl.AccessibleName = "小时";
        MinuteControl.Minimum = 0; MinuteControl.Maximum = 59; MinuteControl.AccessibleName = "分钟";
        DayComboBox.DropDownStyle = ComboBoxStyle.DropDownList; DayComboBox.AccessibleName = "每周星期";
        DayComboBox.Items.AddRange(["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"]);
        layout.Controls.Add(EnabledCheckBox, 0, 0); layout.SetColumnSpan(EnabledCheckBox, 2);
        layout.Controls.Add(new Label { Text = "频率", AutoSize = true }, 0, 1); layout.Controls.Add(FrequencyComboBox, 1, 1);
        layout.Controls.Add(new Label { Text = "时间（本机）", AutoSize = true }, 0, 2);
        var time = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        time.Controls.Add(HourControl); time.Controls.Add(new Label { Text = "时", AutoSize = true, Margin = new Padding(4, 6, 4, 0) });
        time.Controls.Add(MinuteControl); time.Controls.Add(new Label { Text = "分", AutoSize = true, Margin = new Padding(4, 6, 4, 0) });
        layout.Controls.Add(time, 1, 2);
        layout.Controls.Add(new Label { Text = "每周星期", AutoSize = true }, 0, 3); layout.Controls.Add(DayComboBox, 1, 3);
        var hint = new Label { Text = "应用启动时会检查是否错过计划；不会修改 Windows 系统代理。", AutoSize = true, MaximumSize = new Size(390, 0) };
        layout.Controls.Add(hint, 0, 4); layout.SetColumnSpan(hint, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        SaveButton.Text = "保存"; SaveButton.AccessibleName = SaveButton.Text; SaveButton.DialogResult = DialogResult.OK; SaveButton.AutoSize = true;
        CancelButton.Text = "取消"; CancelButton.AccessibleName = CancelButton.Text; CancelButton.DialogResult = DialogResult.Cancel; CancelButton.AutoSize = true;
        buttons.Controls.Add(CancelButton); buttons.Controls.Add(SaveButton); layout.Controls.Add(buttons, 0, 5); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout); AcceptButton = SaveButton; this.CancelButton = CancelButton;
        EnabledCheckBox.Checked = current.Enabled;
        FrequencyComboBox.SelectedIndex = current.Frequency switch { BackupScheduleFrequency.Weekly => 1, BackupScheduleFrequency.Daily => 2, _ => 0 };
        HourControl.Value = Math.Clamp(current.Hour, 0, 23); MinuteControl.Value = Math.Clamp(current.Minute, 0, 59);
        DayComboBox.SelectedIndex = Math.Clamp((int)current.DayOfWeek, 0, 6);
        EnabledCheckBox.CheckedChanged += (_, _) => UpdateEnabled(); FrequencyComboBox.SelectedIndexChanged += (_, _) => UpdateEnabled(); UpdateEnabled();
    }

    private void UpdateEnabled()
    {
        bool enabled = EnabledCheckBox.Checked;
        FrequencyComboBox.Enabled = enabled; HourControl.Enabled = enabled; MinuteControl.Enabled = enabled;
        DayComboBox.Enabled = enabled && FrequencyComboBox.SelectedIndex == 1;
    }
}
