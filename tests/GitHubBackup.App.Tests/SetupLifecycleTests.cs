using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace GitHubBackup.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SetupLifecycleTests
{
    [TestMethod]
    public void Install_and_uninstall_manage_only_owned_files_and_preserve_user_data()
    {
        var f = new Fixture();
        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);
        CollectionAssert.AreEquivalent(new[] { SetupLifecycle.AppName, SetupLifecycle.UninstallerName,
            SetupLifecycle.NoticeName, SetupLifecycle.ReceiptName }, Directory.GetFiles(f.Context.Root).Select(Path.GetFileName).ToArray());
        Assert.IsTrue(File.Exists(f.Context.Shortcut));
        Assert.IsTrue(File.Exists(f.Context.DesktopShortcut));
        var uninstallEntry = f.Registry.Entries.Values.Single();
        Assert.AreEqual("GitHub 备份工具", uninstallEntry["DisplayName"]);
        Assert.AreEqual(f.Manifest.Version, uninstallEntry["DisplayVersion"]);
        Assert.AreEqual("\"" + Path.Combine(f.Context.Root, SetupLifecycle.AppName) + "\",0", uninstallEntry["DisplayIcon"]);
        Assert.AreEqual("\"" + Path.Combine(f.Context.Root, SetupLifecycle.UninstallerName) + "\"", uninstallEntry["UninstallString"]);
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Context.Root, SetupLifecycle.StateName)));
        string extra = Path.Combine(f.Context.Root, "my-notes.txt");
        File.WriteAllText(extra, "keep my file");
        SetupLifecycle.Uninstall(f.Context);
        Assert.AreEqual("keep my file", File.ReadAllText(extra));
        Assert.IsFalse(File.Exists(f.Context.Shortcut));
        Assert.IsFalse(File.Exists(f.Context.DesktopShortcut));
        Assert.IsEmpty(f.Registry.Entries);
        Assert.AreEqual("backup must survive", File.ReadAllText(f.Sentinel));
        Assert.IsFalse(File.Exists(Path.Combine(f.Context.Root, SetupLifecycle.AppName)));
    }

    [TestMethod]
    public void Existing_unowned_root_is_never_overwritten()
    {
        var f = new Fixture();
        AclPolicy.CreateRestrictedDirectory(f.Context.Root, f.Context.User, requireNew: true);
        string existing = Path.Combine(f.Context.Root, SetupLifecycle.AppName);
        File.WriteAllText(existing, "foreign file");
        Assert.Throws<IOException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest));
        Assert.AreEqual("foreign file", File.ReadAllText(existing));
    }

    [TestMethod]
    [DataRow("PREPARED")]
    [DataRow("APP_WRITTEN")]
    [DataRow("SHORTCUT_WRITTEN")]
    [DataRow("DESKTOP_SHORTCUT_WRITTEN")]
    [DataRow("REGISTRY_PENDING")]
    [DataRow("REGISTRY_WRITTEN")]
    [DataRow("RECEIPT_WRITTEN")]
    [DataRow("COMMITTED")]
    public void Interrupted_install_reconciles_the_persisted_ledger_before_retry(string phase)
    {
        var f = new Fixture();
        Assert.Throws<SetupInterruptedException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest,
            current => { if (current == phase) throw new SetupInterruptedException(); }));
        Assert.IsTrue(Directory.Exists(Path.Combine(f.Context.Root, SetupLifecycle.StateName)));
        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);
        Assert.IsTrue(File.Exists(Path.Combine(f.Context.Root, SetupLifecycle.ReceiptName)));
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Context.Root, SetupLifecycle.StateName)));
        Assert.AreEqual("backup must survive", File.ReadAllText(f.Sentinel));
    }

    [TestMethod]
    public void Changed_file_after_interruption_blocks_recovery_without_deleting_it()
    {
        var f = new Fixture();
        Assert.Throws<SetupInterruptedException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest,
            phase => { if (phase == "APP_WRITTEN") throw new SetupInterruptedException(); }));
        string app = Path.Combine(f.Context.Root, SetupLifecycle.AppName);
        File.WriteAllText(app, "changed bytes");
        Assert.Throws<IOException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest));
        Assert.AreEqual("changed bytes", File.ReadAllText(app));
    }

    [TestMethod]
    public void Occupied_shortcut_is_preserved_and_fresh_install_rolls_back()
    {
        var f = new Fixture();
        File.WriteAllText(f.Context.Shortcut, "existing shortcut");
        Assert.Throws<IOException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest));
        Assert.AreEqual("existing shortcut", File.ReadAllText(f.Context.Shortcut));
        Assert.IsFalse(File.Exists(Path.Combine(f.Context.Root, SetupLifecycle.AppName)));
    }

    [TestMethod]
    public void Occupied_desktop_shortcut_is_preserved_and_fresh_install_rolls_back()
    {
        var f = new Fixture();
        File.WriteAllText(f.Context.DesktopShortcut, "existing desktop shortcut");
        Assert.Throws<IOException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest));
        Assert.AreEqual("existing desktop shortcut", File.ReadAllText(f.Context.DesktopShortcut));
        Assert.IsFalse(File.Exists(Path.Combine(f.Context.Root, SetupLifecycle.AppName)));
        Assert.IsEmpty(f.Registry.Entries);
    }

    [TestMethod]
    public void Partial_registry_write_is_rolled_back_without_leaving_installed_files()
    {
        var f = new Fixture();
        f.Registry.FailAfterFirstValue = true;

        Assert.Throws<IOException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest));

        Assert.IsEmpty(f.Registry.Entries);
        Assert.IsFalse(Directory.Exists(f.Context.Root));
        Assert.IsFalse(File.Exists(f.Context.Shortcut));
        Assert.IsFalse(File.Exists(f.Context.DesktopShortcut));
        Assert.AreEqual("backup must survive", File.ReadAllText(f.Sentinel));
    }

    [TestMethod]
    public void Empty_foreign_registry_key_appearing_after_preflight_is_preserved_during_rollback()
    {
        var f = new Fixture();
        f.Registry.OccupyWithEmptyKeyOnCreate = true;

        Assert.Throws<IOException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest));

        Assert.HasCount(1, f.Registry.Entries);
        Assert.IsEmpty(f.Registry.Entries.Values.Single());
        Assert.IsFalse(Directory.Exists(f.Context.Root));
        Assert.IsFalse(File.Exists(f.Context.Shortcut));
        Assert.IsFalse(File.Exists(f.Context.DesktopShortcut));
        Assert.AreEqual("backup must survive", File.ReadAllText(f.Sentinel));
    }

    [TestMethod]
    public void Missing_uninstall_registry_entry_does_not_strand_verified_installation()
    {
        var f = new Fixture();
        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);
        f.Registry.RemoveEntry(f.Registry.Entries.Keys.Single());

        SetupLifecycle.Uninstall(f.Context);

        Assert.IsFalse(Directory.Exists(f.Context.Root));
        Assert.IsFalse(File.Exists(f.Context.Shortcut));
        Assert.IsFalse(File.Exists(f.Context.DesktopShortcut));
        Assert.IsEmpty(f.Registry.Entries);
        Assert.AreEqual("backup must survive", File.ReadAllText(f.Sentinel));
    }

    [TestMethod]
    public void Schema_two_install_is_refused_for_upgrade_but_its_owned_files_remain_uninstallable()
    {
        var f = new Fixture();
        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);
        f.Registry.RemoveEntry(f.Registry.Entries.Keys.Single());
        File.Delete(f.Context.DesktopShortcut);
        WriteSchemaTwoReceipt(Path.Combine(f.Context.Root, SetupLifecycle.ReceiptName));

        IOException error = Assert.Throws<IOException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest));
        Assert.AreEqual("SETUP_OLD_VERSION_MUST_BE_UNINSTALLED", error.Message);

        SetupLifecycle.Uninstall(f.Context);

        Assert.IsFalse(Directory.Exists(f.Context.Root));
        Assert.IsFalse(File.Exists(f.Context.Shortcut));
        Assert.IsFalse(File.Exists(f.Context.DesktopShortcut));
        Assert.AreEqual("backup must survive", File.ReadAllText(f.Sentinel));
    }

    private static void WriteSchemaTwoReceipt(string path)
    {
        var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        record["Schema"] = 2;
        record.Remove("DesktopShortcut");
        record.Remove("RegistryInstallId");
        JsonArray entries = record["Entries"]!.AsArray();
        for (int index = entries.Count - 1; index >= 0; index--)
            if (entries[index]!["Role"]!.GetValue<string>() == "desktop-shortcut") entries.RemoveAt(index);
        File.WriteAllText(path, record.ToJsonString());
    }

    [TestMethod]
    [DataRow("foreign")]
    [DataRow("changed")]
    public void Registry_conflicts_refuse_uninstall_before_removing_any_files(string fault)
    {
        var f = new Fixture();
        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);
        string installId = f.Registry.Entries.Keys.Single();
        if (fault == "foreign") f.Registry.AddValue(installId, "ForeignValue", "preserve me");
        else f.Registry.AddValue(installId, "DisplayName", "changed by another program");
        SetupIdentity shortcut = Fixture.Identity(f, f.Context.Shortcut);
        SetupIdentity desktop = Fixture.Identity(f, f.Context.DesktopShortcut);

        Assert.Throws<IOException>(() => SetupLifecycle.Uninstall(f.Context));

        Assert.IsTrue(File.Exists(Path.Combine(f.Context.Root, SetupLifecycle.AppName)));
        Assert.AreEqual(shortcut, Fixture.Identity(f, f.Context.Shortcut));
        Assert.AreEqual(desktop, Fixture.Identity(f, f.Context.DesktopShortcut));
        Assert.AreEqual("backup must survive", File.ReadAllText(f.Sentinel));
        Assert.IsTrue(f.Registry.Entries.ContainsKey(installId));
    }

    [TestMethod]
    public void Uninstall_checks_every_owned_file_before_deleting_any()
    {
        var f = new Fixture();
        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);
        string notice = Path.Combine(f.Context.Root, SetupLifecycle.NoticeName);
        File.WriteAllText(notice, "changed notice");
        Assert.Throws<IOException>(() => SetupLifecycle.Uninstall(f.Context));
        Assert.IsTrue(File.Exists(f.Context.Shortcut));
        Assert.IsTrue(File.Exists(f.Context.DesktopShortcut));
        Assert.HasCount(1, f.Registry.Entries);
        Assert.IsTrue(File.Exists(Path.Combine(f.Context.Root, SetupLifecycle.AppName)));
        Assert.AreEqual("changed notice", File.ReadAllText(notice));
    }

    internal sealed class Fixture
    {
        internal SetupContext Context { get; }
        internal string Payload { get; }
        internal SetupManifest Manifest { get; }
        internal string Sentinel { get; }
        internal MemorySetupRegistryStore Registry { get; } = new();
        internal Fixture([CallerFilePath] string source = "")
        {
            var user = WindowsIdentity.GetCurrent().User!;
            string artifacts = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "artifacts", "setup"));
            Directory.CreateDirectory(artifacts);
            string fixture = Path.Combine(artifacts, "managed-lifecycle-" + Guid.NewGuid().ToString("N"));
            AclPolicy.CreateRestrictedDirectory(fixture, user, requireNew: true);
            Payload = Path.Combine(fixture, "payload");
            AclPolicy.CreateRestrictedDirectory(Payload, user, requireNew: true);
            string programs = Path.Combine(fixture, "LocalData", "Programs");
            AclPolicy.CreateRestrictedDirectory(programs, user);
            string menu = Path.Combine(fixture, "StartMenu");
            AclPolicy.CreateRestrictedDirectory(menu, user, requireNew: true);
            string desktop = Path.Combine(fixture, "Desktop");
            AclPolicy.CreateRestrictedDirectory(desktop, user, requireNew: true);
            Context = new(Path.Combine(programs, "GitHubBackupTool"), Path.Combine(menu, SetupLifecycle.ShortcutName),
                Path.Combine(desktop, SetupLifecycle.ShortcutName), Registry, user);
            File.WriteAllBytes(Path.Combine(Payload, SetupLifecycle.AppName), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(Payload, SetupLifecycle.UninstallerName), [5, 6, 7, 8]);
            File.WriteAllText(Path.Combine(Payload, SetupLifecycle.NoticeName), "synthetic notice");
            Manifest = new("1.0.0.0", new string('0', 40), Hash(SetupLifecycle.AppName), Hash(SetupLifecycle.UninstallerName), Hash(SetupLifecycle.NoticeName));
            Sentinel = Path.Combine(fixture, "backups.txt");
            File.WriteAllText(Sentinel, "backup must survive");
        }
        private string Hash(string name) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Payload, name))));
        internal static SetupIdentity Identity(Fixture f, string path)
        {
            using var handle = SetupNative.Open(path, false, f.Context.User);
            return SetupNative.Inspect(handle, path, false, f.Context.User);
        }
    }
}

internal sealed class MemorySetupRegistryStore : ISetupRegistryStore
{
    internal Dictionary<string, Dictionary<string, object>> Entries { get; } = new(StringComparer.Ordinal);
    internal bool FailAfterFirstValue { get; set; }
    internal bool OccupyWithEmptyKeyOnCreate { get; set; }

    public void EnsureAbsent(string installId)
    { if (Entries.ContainsKey(installId)) throw new IOException("SETUP_REGISTRY_ENTRY_OCCUPIED"); }

    public void CreateNew(SetupRegistryEntry entry)
    {
        if (OccupyWithEmptyKeyOnCreate)
        {
            OccupyWithEmptyKeyOnCreate = false;
            Entries.Add(entry.InstallId, new(StringComparer.OrdinalIgnoreCase));
            throw new IOException("SETUP_REGISTRY_ENTRY_OCCUPIED");
        }
        EnsureAbsent(entry.InstallId);
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        Entries.Add(entry.InstallId, values);
        foreach (var pair in entry.Values)
        {
            values.Add(pair.Key, pair.Value);
            if (FailAfterFirstValue)
            {
                FailAfterFirstValue = false;
                throw new IOException("SETUP_REGISTRY_SYNTHETIC_PARTIAL_WRITE");
            }
        }
    }

    public void VerifyExact(SetupRegistryEntry entry) => Verify(entry, allowPartial: false);

    public void RemoveOwned(SetupRegistryEntry entry, bool allowPartial)
    {
        if (!Entries.ContainsKey(entry.InstallId) && allowPartial) return;
        if (allowPartial && Entries[entry.InstallId].Count == 0) return;
        Verify(entry, allowPartial);
        Entries.Remove(entry.InstallId);
    }

    internal void RemoveEntry(string installId) => Entries.Remove(installId);

    internal void AddValue(string installId, string name, object value) => Entries[installId][name] = value;

    private void Verify(SetupRegistryEntry entry, bool allowPartial)
    {
        if (!Entries.TryGetValue(entry.InstallId, out var actual)) throw new IOException("SETUP_REGISTRY_ENTRY_MISSING");
        if (actual.Any(pair => !entry.Values.TryGetValue(pair.Key, out object? expected) || !Equals(pair.Value, expected)))
            throw new IOException("SETUP_REGISTRY_ENTRY_CONFLICT");
        if (!allowPartial && actual.Count != entry.Values.Count) throw new IOException("SETUP_REGISTRY_ENTRY_INCOMPLETE");
    }
}
