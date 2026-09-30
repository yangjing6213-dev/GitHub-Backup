using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class RunLoggerTests
{
    [TestMethod]
    public async Task Canary_cannot_reach_tail_log_preview_or_export()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        string owner = root.Child("owner");
        string logs = Path.Combine(owner, "logs");
        AclPolicy.CreateRestrictedDirectory(logs, root.User);
        string path = Path.Combine(logs, "backup-canary.log");
        const string message = "github_pat_TESTONLY_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789\n"
            + "ghp_TESTONLYABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789\nAuthorization: Bearer TESTONLY-SECRET\n"
            + "Cookie: session=TESTONLY-SECRET\nhttps://user:TESTONLY-SECRET@proxy.example:8443/\n"
            + "GIT_ASKPASS=TESTONLY-SECRET\nhttps://objects.githubusercontent.com/path/asset.bin?X-Amz-Signature=TESTONLY-SECRET&X-Amz-Expires=300#fragment\n"
            + "https://storage.example/blob?sig=TESTONLY-SECRET&se=2099-01-01\n"
            + "https://example.test/path?sig='TESTONLY-SECRET'\nhttps://user:TESTONLY'SECRET@proxy.example/\n"
            + "prefixGHp_TESTONLYABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789\nprefix_github_pat_TESTONLY_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        await using (var logger = await RunLogger.CreateAsync(path, CancellationToken.None))
        {
            await logger.WriteAsync(LogLevel.Error, "backup", null, message, CancellationToken.None);
            Assert.DoesNotContain("TESTONLY", string.Join('\n', logger.GetTail()));
        }
        var store = new DiagnosticsStore(paths, owner);
        DiagnosticExportPreview preview = await store.PreviewAsync([path], CancellationToken.None);
        string target = root.Child("export.txt");
        Assert.AreEqual(DiagnosticSaveStatus.Saved, await store.SaveAsync(preview, target, true, CancellationToken.None));
        Assert.DoesNotContain("TESTONLY", string.Join('\n', preview.DisplayLines));
        foreach (string output in new[] { path, preview.PreviewFilePath, target })
        {
            string text = await File.ReadAllTextAsync(output);
            Assert.DoesNotContain("TESTONLY", text);
            // Retain only sanitized outputs for the filename-only post-test canary scan.
            string artifacts = Path.Combine(AppContext.BaseDirectory, "TestResults", "task5-runtime");
            AclPolicy.CreateRestrictedDirectory(artifacts, root.User);
            await AtomicFile.WriteAsync(Path.Combine(artifacts, Path.GetFileName(output)),
                async (file, token) => await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), token), CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task Active_logger_pins_parent_directory_until_disposed()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("owner");
        string logs = Path.Combine(owner, "logs");
        AclPolicy.CreateRestrictedDirectory(logs, root.User);
        await using (var logger = await RunLogger.CreateAsync(Path.Combine(logs, "backup-run.log"), CancellationToken.None))
        {
            Assert.ThrowsExactly<IOException>(() => Directory.Move(logs, Path.Combine(owner, "moved")));
            await logger.WriteAsync(LogLevel.Info, "backup", null, "safe", CancellationToken.None);
        }
        Directory.Move(logs, Path.Combine(owner, "moved"));
    }

    [TestMethod]
    public async Task Sanitizes_caps_and_retains_newest_2000_lines()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("owner");
        string logs = Path.Combine(owner, "logs");
        AclPolicy.CreateRestrictedDirectory(logs, root.User);
        string path = Path.Combine(logs, "backup-run1.log");
        await using (var logger = await RunLogger.CreateAsync(path, CancellationToken.None))
        {
            for (int i = 0; i < 2001; i++)
                await logger.WriteAsync(LogLevel.Info, "backup", null,
                    $"\u001b[31m{i:D4}{new string('x', 9000)} Authorization: Bearer TESTONLY-SECRET", CancellationToken.None);
            IReadOnlyList<string> tail = logger.GetTail();
            Assert.HasCount(2000, tail);
            StringAssert.Contains(tail[0], "0001");
            Assert.IsTrue(tail.All(line => line.Length <= 8192 && !line.Contains('\u001b')));
        }
        string persisted = await File.ReadAllTextAsync(path);
        Assert.AreEqual(-1, persisted.IndexOf("TESTONLY", StringComparison.Ordinal));
        Assert.DoesNotContain('\u001b', persisted);
    }

    [TestMethod]
    public async Task Rejects_nonprivate_log_directory()
    {
        using var root = new StorageTestRoot();
        string logs = root.Child("logs");
        Directory.CreateDirectory(logs);
        StorageTestRoot.Grant(logs, System.Security.AccessControl.FileSystemRights.Read);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            RunLogger.CreateAsync(Path.Combine(logs, "backup-run.log"), CancellationToken.None));
    }
}
