namespace GitHubBackup.App;

internal sealed class DiagnosticExportDialog : LogViewerDialog
{
    internal Button SaveButton = new(), DeclineButton = new();
    internal CheckBox ConfirmCheckBox = new();
    internal DiagnosticExportDialog(DiagnosticDisplayPreview preview) : base(new(
        UiLanguageState.Text("诊断导出预览（已脱敏，最多显示末尾 2,000 行）", "Diagnostic preview (redacted; latest 2,000 lines)"), preview.DisplayLines))
    {
        CloseViewButton.Visible = false; CloseViewButton.TabStop = false;
        ConfirmCheckBox.Text = UiLanguageState.Text("我已检查脱敏预览，同意将诊断内容保存到所选文件(&A)", "I reviewed the redacted preview and agree to save it to the selected file(&A)");
        ConfirmCheckBox.AccessibleName = UiLanguageState.Text("确认已检查诊断预览", "Confirm diagnostic preview was reviewed"); ConfirmCheckBox.AutoSize = true; ConfirmCheckBox.MaximumSize = new(700, 0); ConfirmCheckBox.TabIndex = 0;
        SaveButton.Text = UiLanguageState.Text("选择位置并保存(&S)…", "Choose location and save(&S)…"); SaveButton.AccessibleName = UiLanguageState.Text("保存已确认的诊断", "Save reviewed diagnostic");
        SaveButton.AutoSize = true; SaveButton.Enabled = false; SaveButton.DialogResult = DialogResult.OK; SaveButton.TabIndex = 1;
        DeclineButton.Text = UiLanguageState.Text("取消(&C)", "Cancel(&C)"); DeclineButton.AccessibleName = UiLanguageState.Text("取消诊断导出", "Cancel diagnostic export");
        DeclineButton.AutoSize = true; DeclineButton.DialogResult = DialogResult.Cancel; DeclineButton.TabIndex = 2;
        Commands.Controls.Add(ConfirmCheckBox); Commands.Controls.Add(SaveButton); Commands.Controls.Add(DeclineButton);
        ConfirmCheckBox.CheckedChanged += (_, _) => SaveButton.Enabled = ConfirmCheckBox.Checked;
        Commands.SizeChanged += (_, _) => ConfirmCheckBox.MaximumSize = new(Math.Max(200, Commands.ClientSize.Width), 0);
        CancelButton = DeclineButton; AcceptButton = DeclineButton;
    }
}
