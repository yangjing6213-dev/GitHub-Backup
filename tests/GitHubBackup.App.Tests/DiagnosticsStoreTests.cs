using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class DiagnosticsStoreTests
{
    [TestMethod]
    public async Task Large_allowlisted_run_log_streams_into_redacted_preview_and_export()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        string owner = root.Child("owner");
        string logs = Path.Combine(owner, "logs");
        AclPolicy.CreateRestrictedDirectory(logs, root.User);
        string source = Path.Combine(logs, "backup-large.log");
        byte[] block = System.Text.Encoding.UTF8.GetBytes(new string('x', 4095) + "\n");
        await using (FileStream file = AclPolicy.CreateRestrictedFile(source, root.User))
        {
            for (int i = 0; i < 2304; i++) await file.WriteAsync(block);
            await file.WriteAsync("https://example.test/path?sig='TESTONLY-SECRET'\nhttps://user:TESTONLY'SECRET@proxy.example/\nEND"u8.ToArray());
        }
        long sourceLength = new FileInfo(source).Length;
        var store = new DiagnosticsStore(paths, owner);
        DiagnosticExportPreview preview = await store.PreviewAsync([source], CancellationToken.None);
        string target = root.Child("large-export.txt");
        Assert.AreEqual(DiagnosticSaveStatus.Saved, await store.SaveAsync(preview, target, true, CancellationToken.None));
        Assert.IsGreaterThan(8L * 1024 * 1024, new FileInfo(target).Length);
        string exported = await File.ReadAllTextAsync(target);
        Assert.DoesNotContain("TESTONLY", exported);
        StringAssert.EndsWith(exported, "https://example.test/path\nhttps://proxy.example/\nEND\n");
        Assert.AreEqual(sourceLength, new FileInfo(source).Length);
    }

    [TestMethod]
    [DataRow("https://example.test/path?sig='TESTONLY-SECRET'")]
    [DataRow("https://user:TESTONLY'SECRET@proxy.example/")]
    public async Task Apostrophes_cannot_leak_from_raw_diagnostic_source_into_preview_or_export(string uri)
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.DiagnosticLogRoot, root.User);
        string source = Path.Combine(paths.DiagnosticLogRoot, "diagnostic-uri.log");
        await using (FileStream file = AclPolicy.CreateRestrictedFile(source, root.User))
            await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(uri));
        var store = new DiagnosticsStore(paths);
        DiagnosticExportPreview preview = await store.PreviewAsync([source], CancellationToken.None);
        string target = root.Child("export.txt");
        Assert.DoesNotContain("TESTONLY", string.Join('\n', preview.DisplayLines));
        Assert.AreEqual(DiagnosticSaveStatus.Saved, await store.SaveAsync(preview, target, true, CancellationToken.None));
        Assert.DoesNotContain("TESTONLY", await File.ReadAllTextAsync(preview.PreviewFilePath));
        Assert.DoesNotContain("TESTONLY", await File.ReadAllTextAsync(target));
    }

    [TestMethod]
    public async Task Preview_requires_confirmation_and_unchanged_source()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.DiagnosticLogRoot, root.User);
        string source = Path.Combine(paths.DiagnosticLogRoot, "diagnostic-run.log");
        await using (FileStream file = AclPolicy.CreateRestrictedFile(source, root.User))
            await file.WriteAsync("Authorization: Bearer TESTONLY-SECRET"u8.ToArray());
        var store = new DiagnosticsStore(paths);
        DiagnosticExportPreview preview = await store.PreviewAsync([source], CancellationToken.None);
        Assert.AreEqual(-1, string.Join('\n', preview.DisplayLines).IndexOf("TESTONLY", StringComparison.Ordinal));
        string target = root.Child("export.txt");
        Assert.AreEqual(DiagnosticSaveStatus.Cancelled,
            await store.SaveAsync(preview, target, false, CancellationToken.None));
        Assert.IsFalse(File.Exists(target));
        await File.AppendAllTextAsync(source, "changed");
        Assert.AreEqual(DiagnosticSaveStatus.Stale,
            await store.SaveAsync(preview, target, true, CancellationToken.None));
        Assert.IsFalse(File.Exists(target));
    }

    [TestMethod]
    public async Task Preview_rejects_arbitrary_text_and_tampered_preview()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.DiagnosticLogRoot, root.User);
        string arbitrary = root.Child("arbitrary.log");
        await File.WriteAllTextAsync(arbitrary, "secret");
        var store = new DiagnosticsStore(paths);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.PreviewAsync([arbitrary], CancellationToken.None));
        string source = Path.Combine(paths.DiagnosticLogRoot, "diagnostic-run.log");
        await using (FileStream file = AclPolicy.CreateRestrictedFile(source, root.User))
            await file.WriteAsync("safe"u8.ToArray());
        DiagnosticExportPreview preview = await store.PreviewAsync([source], CancellationToken.None);
        await File.AppendAllTextAsync(preview.PreviewFilePath, "tamper");
        string target = root.Child("export.txt");
        Assert.AreEqual(DiagnosticSaveStatus.Stale,
            await store.SaveAsync(preview, target, true, CancellationToken.None));
        Assert.IsFalse(File.Exists(target));
    }

    [TestMethod]
    public async Task Export_redacts_and_prune_does_not_touch_backup_logs()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.DiagnosticLogRoot, root.User);
        string owner = root.Child("owner");
        string logs = Path.Combine(owner, "logs");
        AclPolicy.CreateRestrictedDirectory(logs, root.User);
        string source = Path.Combine(logs, "backup-run.log");
        await using (FileStream file = AclPolicy.CreateRestrictedFile(source, root.User))
            await file.WriteAsync("https://storage.example/blob?sig=TESTONLY-SECRET"u8.ToArray());
        var store = new DiagnosticsStore(paths, owner);
        DiagnosticExportPreview preview = await store.PreviewAsync([source], CancellationToken.None);
        string target = root.Child("export.txt");
        Assert.AreEqual(DiagnosticSaveStatus.Saved,
            await store.SaveAsync(preview, target, true, CancellationToken.None));
        Assert.AreEqual(-1, (await File.ReadAllTextAsync(target)).IndexOf("TESTONLY", StringComparison.Ordinal));
        for (int i = 0; i < 25; i++)
        {
            string path = Path.Combine(paths.DiagnosticLogRoot, $"diagnostic-{i:D2}.log");
            await using FileStream file = AclPolicy.CreateRestrictedFile(path, root.User);
            await file.WriteAsync("test"u8.ToArray());
        }
        store.Prune();
        Assert.IsLessThanOrEqualTo(20, Directory.GetFiles(paths.DiagnosticLogRoot).Length);
        Assert.IsTrue(File.Exists(source));
    }

    [TestMethod]
    public async Task Prune_caps_total_app_diagnostics_and_keeps_backup_log()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        string appLogs = Path.Combine(paths.LocalAppDataRoot, "logs");
        AclPolicy.CreateRestrictedDirectory(appLogs, root.User);
        AclPolicy.CreateRestrictedDirectory(paths.DiagnosticLogRoot, root.User);
        string older = Path.Combine(appLogs, "diagnostic-old.log");
        string newer = Path.Combine(paths.DiagnosticLogRoot, "diagnostic-new.log");
        await using (FileStream file = AclPolicy.CreateRestrictedFile(older, root.User)) file.SetLength(60L * 1024 * 1024);
        await using (FileStream file = AclPolicy.CreateRestrictedFile(newer, root.User)) file.SetLength(60L * 1024 * 1024);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        string owner = root.Child("owner");
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, "logs"), root.User);
        string backup = Path.Combine(owner, "logs", "backup-old.log");
        await using (FileStream file = AclPolicy.CreateRestrictedFile(backup, root.User))
            await file.WriteAsync("backup"u8.ToArray());

        new DiagnosticsStore(paths, owner).Prune();

        Assert.IsFalse(File.Exists(older));
        Assert.IsTrue(File.Exists(newer));
        Assert.IsTrue(File.Exists(backup));
    }

    [TestMethod]
    public async Task Stale_and_cancelled_previews_preserve_existing_target_then_valid_preview_replaces_it()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.DiagnosticLogRoot, root.User);
        string source = Path.Combine(paths.DiagnosticLogRoot, "diagnostic-run.log");
        await using (FileStream file = AclPolicy.CreateRestrictedFile(source, root.User))
            await file.WriteAsync("safe content"u8.ToArray());
        var store = new DiagnosticsStore(paths);
        DiagnosticExportPreview preview = await store.PreviewAsync([source], CancellationToken.None);
        string target = root.Child("export.txt");
        await File.WriteAllTextAsync(target, "preserve");
        Assert.AreEqual(DiagnosticSaveStatus.Cancelled, await store.SaveAsync(preview, target, false, CancellationToken.None));
        Assert.AreEqual(DiagnosticSaveStatus.Stale, await store.SaveAsync(preview with { PreviewSha256 = "tampered" }, target, true, CancellationToken.None));
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(target));
        Assert.AreEqual(DiagnosticSaveStatus.Saved, await store.SaveAsync(preview, target, true, CancellationToken.None));
        Assert.AreEqual("safe content\n", await File.ReadAllTextAsync(target));
        Assert.AreEqual(DiagnosticSaveStatus.Stale, await store.SaveAsync(preview, target, true, CancellationToken.None));
    }

    [TestMethod]
    public async Task Payload_sources_and_unapproved_owner_are_rejected_with_safe_errors()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        string owner = root.Child("owner");
        foreach (string subtree in new[] { "mirrors", "metadata", "releases", "logs" })
        {
            string source = Path.Combine(owner, subtree, "backup-ghp_TESTONLY-SECRET.log");
            var store = new DiagnosticsStore(paths, subtree == "logs" ? null : owner);
            Exception error = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.PreviewAsync([source], CancellationToken.None));
            Assert.DoesNotContain("TESTONLY", error.Message);
        }
    }

    [TestMethod]
    public async Task Hardlinked_diagnostic_is_rejected_without_reading_or_pruning_external_content()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.DiagnosticLogRoot, root.User);
        string outside = root.Child("outside.txt");
        await using (FileStream file = AclPolicy.CreateRestrictedFile(outside, root.User))
            await file.WriteAsync("untouched"u8.ToArray());
        string source = Path.Combine(paths.DiagnosticLogRoot, "diagnostic-link.log");
        StorageTestRoot.CreateHardLink(source, outside);
        var store = new DiagnosticsStore(paths);
        await Assert.ThrowsExactlyAsync<PathBoundaryException>(() => store.PreviewAsync([source], CancellationToken.None));
        Assert.ThrowsExactly<PathBoundaryException>(() => store.Prune());
        Assert.AreEqual("untouched", await File.ReadAllTextAsync(outside));
    }
}
