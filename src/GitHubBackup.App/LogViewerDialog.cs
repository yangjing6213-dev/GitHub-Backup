namespace GitHubBackup.App;

internal class LogViewerDialog : Form
{
    internal TextBox LogTextBox = new();
    protected FlowLayoutPanel Commands = new() { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown };
    internal Button CloseViewButton = new();
    internal LogViewerDialog(ValidatedLogDocument document)
    {
        Text = document.DisplayName; AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new(760, 480); MinimumSize = new(420, 300);
        StartPosition = FormStartPosition.CenterParent; BackColor = SystemColors.Control; ForeColor = SystemColors.ControlText;
        Padding = new(12); MinimizeBox = false;
        LogTextBox.Multiline = true; LogTextBox.ReadOnly = true; LogTextBox.WordWrap = false;
        LogTextBox.ScrollBars = ScrollBars.Both; LogTextBox.Dock = DockStyle.Fill; LogTextBox.TabIndex = 0;
        LogTextBox.AccessibleName = "脱敏日志内容"; LogTextBox.BackColor = SystemColors.Window; LogTextBox.ForeColor = SystemColors.WindowText;
        LogTextBox.Lines = BoundedLines(document.Lines);
        Commands.TabIndex = 1;
        CloseViewButton.Text = "关闭(&C)"; CloseViewButton.AccessibleName = "关闭日志";
        CloseViewButton.AutoSize = true; CloseViewButton.DialogResult = DialogResult.Cancel;
        Commands.Controls.Add(CloseViewButton); Controls.Add(LogTextBox); Controls.Add(Commands);
        CancelButton = CloseViewButton; AcceptButton = CloseViewButton;
    }

    internal static string[] BoundedLines(IReadOnlyList<string> lines) => lines.TakeLast(2000)
        .Select(line => line.Length <= 8192 ? line : line[..8181] + "[TRUNCATED]").ToArray();
}
