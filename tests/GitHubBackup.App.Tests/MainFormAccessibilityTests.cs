using GitHubBackup.App;
using System.Drawing;
using System.Windows.Forms;

namespace GitHubBackup.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainFormAccessibilityTests
{
    [TestMethod]
    public void Setup_dialogs_start_unchecked_and_offer_keyboard_accessible_exact_scope() => UiTest.Run(async () =>
    {
        using var form = new MainForm(new UiFixture().Actions); await form.LoadSettingsAsync();
        foreach (Control control in new[] { form.DependencyButton, form.SaveRootButton, form.RepairButton })
        { Assert.IsTrue(control.TabStop); Assert.IsFalse(string.IsNullOrWhiteSpace(control.AccessibleName)); Assert.Contains("&", control.Text); }
        var tool = new ToolDetection(@"C:\synthetic\winget.exe", new(1, 2, 3, 4), "v1.29.290", true);
        using var dependency = new DependencyConsentDialog(new(null, null, null, tool));
        Assert.AreSame(dependency.DeclineButton, dependency.AcceptButton); Assert.AreSame(dependency.DeclineButton, dependency.CancelButton);
        Assert.IsTrue(dependency.Confirmations.Values.All(c => !c.Checked && c.TabStop && !string.IsNullOrEmpty(c.AccessibleName)));
        Assert.IsTrue(dependency.InstallButtons.Values.All(b => !b.Enabled && b.TabStop));
        dependency.Confirmations[DependencyId.Git].Checked = true;
        Assert.IsTrue(dependency.InstallButtons[DependencyId.Git].Enabled); Assert.IsFalse(dependency.InstallButtons[DependencyId.GitLfs].Enabled);
        Assert.IsTrue(dependency.ManualInstructions.ReadOnly); Assert.Contains("https://git-scm.com/install/windows", dependency.ManualInstructions.Text);
        using var blocked = new DependencyConsentDialog(ToolInventory.Empty); blocked.Confirmations[DependencyId.Git].Checked = true;
        Assert.IsFalse(blocked.InstallButtons[DependencyId.Git].Enabled);
        using var root = new RootConsentDialog(@"C:\selected", false);
        Assert.IsFalse(root.SaveButton.Enabled); root.ConfirmCheckBox.Checked = true; Assert.IsFalse(root.SaveButton.Enabled);
        root.CreateCheckBox.Checked = true; Assert.IsTrue(root.SaveButton.Enabled); Assert.AreSame(root.DeclineButton, root.AcceptButton);
        using var repair = new RepairConsentDialog(new(UiFixture.Ready, ToolInventory.Empty, "Direct", [], [new(@"C:\exact", AclRisk.Block, "MANIFEST_READ_ACL_UNSAFE")]));
        Assert.IsFalse(repair.ConfirmCheckBox.Checked); Assert.HasCount(0, repair.SelectedEntries); Assert.Contains("不递归", repair.LogTextBox.Text);
        repair.ConfirmCheckBox.Checked = true; Assert.IsFalse(repair.RepairButton.Enabled); repair.Entries.SetItemChecked(0, true);
        Assert.IsTrue(repair.RepairButton.Enabled); Assert.AreEqual(@"C:\exact", repair.SelectedEntries.Single().FullPath);
        Assert.AreSame(repair.DeclineButton, repair.AcceptButton);
    });

    [TestMethod]
    public void Ordinary_views_and_diagnostics_have_explicit_confirmation_accessible_controls_and_disclosure() => UiTest.Run(async () =>
    {
        using var form = new MainForm(new UiFixture().Actions); await form.LoadSettingsAsync();
        Control[] views = [form.LatestLogButton, form.OpenFolderButton, form.DiagnosticsButton, form.LiveLogTextBox];
        Assert.IsTrue(views.All(c => c.TabStop && !string.IsNullOrWhiteSpace(c.AccessibleName)));
        CollectionAssert.AreEqual(views, views.OrderBy(c => c.TabIndex).ToArray());
        Assert.Contains("内部测试版（未签名）", form.Text); Assert.IsFalse(form.ReleaseDownloadButton.Enabled);
        Assert.AreEqual("正式版下载地址未配置", form.ReleaseDownloadButton.Text);
        Assert.Contains("不脱敏", form.PrivacyLabel.Text); Assert.Contains("权限", form.PrivacyLabel.Text);
        Assert.IsNotNull(form.StatusIcon.Image);
        using var log = new LogViewerDialog(new("最近一次运行日志", ["safe"]));
        Assert.IsTrue(log.LogTextBox.ReadOnly); Assert.AreEqual("safe", log.LogTextBox.Text);
        using var diagnostic = new DiagnosticExportDialog(new("opaque", ["[REDACTED]"]));
        Assert.IsFalse(diagnostic.SaveButton.Enabled); Assert.IsTrue(diagnostic.LogTextBox.ReadOnly);
        Assert.Contains("[REDACTED]", diagnostic.LogTextBox.Text);
        diagnostic.ConfirmCheckBox.Checked = true; Assert.IsTrue(diagnostic.SaveButton.Enabled);
        Assert.AreSame(diagnostic.DeclineButton, diagnostic.CancelButton);
        Assert.IsTrue(new Control[] { diagnostic.LogTextBox, diagnostic.ConfirmCheckBox, diagnostic.SaveButton, diagnostic.DeclineButton }
            .All(c => c.TabStop && !string.IsNullOrWhiteSpace(c.AccessibleName)));
    });

    [TestMethod]
    public void Field_mnemonics_target_their_corresponding_inputs() => UiTest.Run(async () =>
    {
        using var form = new MainForm(new UiFixture().Actions); await form.LoadSettingsAsync();
        var layout = form.OwnerTextBox.Parent!;
        foreach (var (mnemonic, expected) in new (string, Control)[] { ("(&U)", form.OwnerTextBox), ("(&D)", form.BackupRootTextBox), ("(&M)", form.ModeComboBox) })
        {
            var label = layout.Controls.OfType<Label>().Single(control => control.Text.Contains(mnemonic));
            Assert.AreSame(expected, layout.GetNextControl(label, true), mnemonic);
        }
    });

    [TestMethod]
    public void Native_controls_expose_keyboard_order_and_system_colors_at_supported_scales() => UiTest.Run(async () =>
    {
        using var form = new MainForm(new UiFixture().Actions); await form.LoadSettingsAsync();
        Assert.AreEqual(AutoScaleMode.Dpi, form.AutoScaleMode);
        Control[] inputs = [form.OwnerTextBox, form.BackupRootTextBox, form.BrowseButton, form.ModeComboBox,
            form.ConsentCheckBox, form.LoginButton, form.CheckButton, form.StartButton, form.CancelOperationButton, form.RetryCleanupButton];
        Assert.IsTrue(inputs.All(c => c.TabStop && !string.IsNullOrWhiteSpace(c.AccessibleName)));
        CollectionAssert.AreEqual(inputs, inputs.OrderBy(control => control.TabIndex).ToArray());
        Assert.IsTrue(form.ConsentCheckBox.AutoSize); Assert.IsTrue(form.StatusLabel.AutoSize);
        Assert.AreEqual(SystemColors.ControlText, form.StatusLabel.ForeColor);
        Assert.AreEqual(SystemColors.Control, form.BackColor);
        Assert.Contains("&", form.CheckButton.Text); Assert.Contains("&", form.StartButton.Text);
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        {
            using var scaled = new MainForm(new UiFixture().Actions);
            scaled.Scale(new SizeF(scale, scale)); scaled.PerformLayout();
            Assert.IsTrue(scaled.AutoScroll); Assert.IsGreaterThan(0, scaled.ConsentCheckBox.MaximumSize.Width);
            Assert.IsLessThanOrEqualTo(scaled.ConsentCheckBox.Height, scaled.ConsentCheckBox.PreferredSize.Height);
        }
    });
}
