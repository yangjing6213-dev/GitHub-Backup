using System.Text.Json;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    [TestMethod]
    public void Missing_D_drive_suggests_the_profile_backup_folder() =>
        Assert.AreEqual(@"C:\Users\Test\GitHub-Backups", AppPaths.SelectInitialBackupRoot(@"C:\Users\Test", _ => false));

    [TestMethod]
    public async Task Corrupt_settings_use_defaults_and_one_warning_without_rewriting()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.LocalAppDataRoot, root.User);
        await using (FileStream stream = AclPolicy.CreateRestrictedFile(paths.SettingsFile, root.User))
            await stream.WriteAsync("{"u8.ToArray());
        SettingsLoadResult result = await new SettingsStore(paths).LoadAsync(CancellationToken.None);
        Assert.AreEqual(AppSettings.Default, result.Settings);
        Assert.HasCount(1, result.Warnings);
        Assert.AreEqual("{", await File.ReadAllTextAsync(paths.SettingsFile));
    }

    [TestMethod]
    public async Task Missing_settings_return_defaults_without_a_warning()
    {
        using var root = new StorageTestRoot();
        var result = await new SettingsStore(AppPaths.Create(root.Path)).LoadAsync(CancellationToken.None);
        Assert.AreEqual(AppSettings.Default, result.Settings);
        Assert.IsEmpty(result.Warnings);
    }

    [TestMethod]
    public async Task Settings_persist_only_allowlisted_properties_and_roundtrip()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        var settings = new AppSettings("test-owner", root.Child("backups"), NetworkMode.Auto);
        var store = new SettingsStore(paths);
        await store.SaveAsync(settings, CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile));
        CollectionAssert.AreEquivalent(new[] { "owner", "backupRoot", "networkMode" }, json.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual(settings, (await store.LoadAsync(CancellationToken.None)).Settings);
        Assert.IsTrue(AppDataPathPolicy.Validate(paths, paths.SettingsFile, AppDataEntryKind.File).Allowed);
    }

    [TestMethod]
    public async Task Consent_roundtrips_without_persisting_a_credential()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        var settings = new AppSettings("Test-Owner", root.Child("backups"), NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "test-owner" };
        var store = new SettingsStore(paths);
        await store.SaveAsync(settings, CancellationToken.None);
        SettingsLoadResult loaded = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual(1, loaded.Settings.ApiCredentialConsentVersion);
        Assert.AreEqual("test-owner", loaded.Settings.ApiCredentialConsentLogin);
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile));
        CollectionAssert.AreEquivalent(new[] { "owner", "backupRoot", "networkMode", "apiCredentialConsentVersion", "apiCredentialConsentLogin" },
            json.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.IsFalse((await File.ReadAllTextAsync(paths.SettingsFile)).Contains("TEST_CANARY", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Actions_options_roundtrip_only_when_explicitly_enabled()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        var store = new SettingsStore(paths);
        var settings = new AppSettings("fixture-user", root.Child("backups"), NetworkMode.Auto)
        {
            IncludeActionsArtifacts = true,
            ActionsMaxBytes = 64L * 1024 * 1024
        };

        await store.SaveAsync(settings, CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile));
        Assert.IsTrue(json.RootElement.GetProperty("includeActionsArtifacts").GetBoolean());
        Assert.AreEqual(64L * 1024 * 1024, json.RootElement.GetProperty("actionsMaxBytes").GetInt64());
        Assert.AreEqual(settings, (await store.LoadAsync(CancellationToken.None)).Settings);
    }

    [TestMethod]
    public async Task Invalid_actions_limit_is_rejected_before_writing()
    {
        using var root = new StorageTestRoot();
        var store = new SettingsStore(AppPaths.Create(root.Path));
        var settings = new AppSettings("fixture-user", root.Child("backups"), NetworkMode.Auto)
        {
            IncludeActionsArtifacts = true,
            ActionsMaxBytes = 1
        };

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(settings, CancellationToken.None));
    }

    [TestMethod]
    public async Task Old_settings_and_changed_account_or_version_have_no_consent()
    {
        using var root = new StorageTestRoot();
        var store = new SettingsStore(AppPaths.Create(root.Path));
        var granted = new AppSettings("test-owner", root.Child("backups"), NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "test-owner" };
        await store.SaveAsync(granted, CancellationToken.None);
        await store.SaveAsync(granted with { Owner = "other-owner" }, CancellationToken.None);
        Assert.IsNull((await store.LoadAsync(CancellationToken.None)).Settings.ApiCredentialConsentVersion);
        await store.SaveAsync(granted with { ApiCredentialConsentVersion = 0 }, CancellationToken.None);
        Assert.IsNull((await store.LoadAsync(CancellationToken.None)).Settings.ApiCredentialConsentLogin);
        await store.SaveAsync(granted with { ApiCredentialConsentVersion = null, ApiCredentialConsentLogin = null }, CancellationToken.None);
        Assert.IsNull((await store.LoadAsync(CancellationToken.None)).Settings.ApiCredentialConsentVersion);
    }

    [TestMethod]
    public async Task Unsupported_consent_field_is_ignored_without_rewriting_existing_settings()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        var store = new SettingsStore(paths);
        await store.SaveAsync(new AppSettings("fixture-user", root.Child("backups"), NetworkMode.Auto), CancellationToken.None);
        string prior = await File.ReadAllTextAsync(paths.SettingsFile);
        string changed = prior.TrimEnd('}') + ",\"apiCredentialConsentVersion\":999999999999999999999,\"apiCredentialConsentLogin\":\"fixture-user\"}";
        await File.WriteAllTextAsync(paths.SettingsFile, changed);
        AppSettings loaded = (await store.LoadAsync(CancellationToken.None)).Settings;
        Assert.IsNull(loaded.ApiCredentialConsentVersion);
        Assert.AreEqual(changed, await File.ReadAllTextAsync(paths.SettingsFile));
    }

    [TestMethod]
    public async Task Unsafe_existing_settings_are_blocked_not_repaired()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        Directory.CreateDirectory(paths.LocalAppDataRoot);
        StorageTestRoot.Grant(paths.LocalAppDataRoot, System.Security.AccessControl.FileSystemRights.Read);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => new SettingsStore(paths).SaveAsync(AppSettings.Default, CancellationToken.None));
        Assert.IsFalse(File.Exists(paths.SettingsFile));
    }

    [TestMethod]
    public async Task Replacing_settings_keeps_private_boundary()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        var store = new SettingsStore(paths);
        await store.SaveAsync(AppSettings.Default, CancellationToken.None);
        var changed = new AppSettings("second-owner", root.Child("backups"), NetworkMode.Auto);
        await store.SaveAsync(changed, CancellationToken.None);
        Assert.AreEqual(changed, (await store.LoadAsync(CancellationToken.None)).Settings);
        Assert.AreEqual(AclRisk.Safe, AclPolicy.Evaluate(AclPolicy.ReadDescriptor(paths.SettingsFile), root.User, false));
    }

    [TestMethod]
    public async Task Broad_read_file_is_blocked_before_read_or_overwrite()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        var store = new SettingsStore(paths);
        await store.SaveAsync(AppSettings.Default, CancellationToken.None);
        string original = await File.ReadAllTextAsync(paths.SettingsFile);
        var info = new FileInfo(paths.SettingsFile);
        var security = info.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier("S-1-1-0"), System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.SaveAsync(AppSettings.Default, CancellationToken.None));
        Assert.AreEqual(original, await File.ReadAllTextAsync(paths.SettingsFile));
    }
}
