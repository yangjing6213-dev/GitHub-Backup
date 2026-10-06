using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitHubBackup.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SetupLifecycleBoundaryTests
{
    [TestMethod]
    [DataRow("app")]
    [DataRow("shortcut")]
    [DataRow("desktop-shortcut")]
    [DataRow("receipt")]
    public void Same_bytes_on_a_different_file_identity_cannot_authorize_uninstall(string role)
    {
        var f = new SetupLifecycleTests.Fixture();
        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);
        string path = RolePath(f, role);
        ReplaceWithSameBytes(f, path);
        var before = Snapshot(f);

        Assert.Throws<IOException>(() => SetupLifecycle.Uninstall(f.Context));

        AssertUnchanged(f, before);
        Assert.IsFalse(Directory.Exists(StatePath(f)), "Refusal must precede creation of an uninstall journal.");
    }

    [TestMethod]
    public void Same_journal_bytes_on_a_different_file_identity_cannot_authorize_recovery()
    {
        var f = new SetupLifecycleTests.Fixture();
        InterruptInstall(f, "APP_WRITTEN");
        ReplaceWithSameBytes(f, JournalPath(f));
        var before = Snapshot(f);

        Assert.Throws<IOException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest));

        AssertUnchanged(f, before);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("extra")]
    public void Malformed_receipt_fields_refuse_uninstall_before_any_file_is_deleted(string mutation)
    {
        var f = new SetupLifecycleTests.Fixture();
        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);
        string path = RolePath(f, "receipt");
        var receipt = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        string text;
        if (mutation == "missing")
        {
            Assert.IsTrue(receipt.Remove("User"));
            text = receipt.ToJsonString();
        }
        else if (mutation == "extra")
        {
            receipt["UnexpectedAuthority"] = true;
            text = receipt.ToJsonString();
        }
        else text = "{\"Schema\":2," + receipt.ToJsonString()[1..];
        File.WriteAllText(path, text, new UTF8Encoding(false));
        var before = Snapshot(f);

        Assert.Throws<IOException>(() => SetupLifecycle.Uninstall(f.Context));

        AssertUnchanged(f, before);
        Assert.IsFalse(Directory.Exists(StatePath(f)));
    }

    [TestMethod]
    [DataRow("empty_uninstall")]
    [DataRow("null_hash")]
    [DataRow("manifest_mismatch")]
    public void A_valid_checksum_does_not_authorize_invalid_journal_content(string mutation)
    {
        var f = new SetupLifecycleTests.Fixture();
        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);
        Assert.Throws<SetupInterruptedException>(() => SetupLifecycle.Uninstall(f.Context,
            phase => { if (phase == "UNINSTALLING") throw new SetupInterruptedException(); }));
        AppendJournalRecord(f, record =>
        {
            if (mutation == "empty_uninstall") record["Entries"] = new JsonArray();
            else if (mutation == "null_hash")
                record["Entries"]!.AsArray().Single(entry => entry!["Role"]!.GetValue<string>() == "app")!["Hash"] = null;
            else record["Manifest"]!["AppHash"] = new string('A', 64);
        });
        var before = Snapshot(f);

        Assert.Throws<IOException>(() => SetupLifecycle.Uninstall(f.Context));
        AssertUnchanged(f, before);
        Assert.Throws<IOException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest));
        AssertUnchanged(f, before);
    }

    [TestMethod]
    [DataRow("root")]
    [DataRow("state")]
    public void Unknown_objects_refuse_install_recovery_without_deleting_known_or_unknown_files(string location)
    {
        var f = new SetupLifecycleTests.Fixture();
        InterruptInstall(f, "APP_WRITTEN");
        string parent = location == "root" ? f.Context.Root : StatePath(f);
        string unknown = Path.Combine(parent, "foreign-object.txt");
        File.WriteAllText(unknown, "preserve the unknown object");
        var before = Snapshot(f);

        Assert.Throws<IOException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest));

        AssertUnchanged(f, before);
    }

    [TestMethod]
    [DataRow("app")]
    [DataRow("shortcut")]
    [DataRow("desktop-shortcut")]
    public void An_occupied_owned_file_blocks_uninstall_before_any_partial_removal(string role)
    {
        var f = new SetupLifecycleTests.Fixture();
        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);
        using var occupied = new FileStream(RolePath(f, role), FileMode.Open, FileAccess.Read, FileShare.Read);
        var before = Snapshot(f);

        Assert.Throws<IOException>(() => SetupLifecycle.Uninstall(f.Context));

        AssertUnchanged(f, before);
        Assert.IsFalse(Directory.Exists(StatePath(f)), "The occupied-file preflight must precede journal creation.");
    }

    [TestMethod]
    public void Repeated_install_preserves_existing_owned_file_identities_and_bytes()
    {
        var f = new SetupLifecycleTests.Fixture();
        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);
        var before = Snapshot(f);
        var identities = before.Keys.Where(path => path != f.Sentinel && !path.StartsWith("MEMORY_REGISTRY:", StringComparison.Ordinal))
            .ToDictionary(path => path, path => Identity(f, path));
        var rootIdentity = Identity(f, f.Context.Root, directory: true);

        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);

        AssertUnchanged(f, before);
        foreach (var pair in identities) Assert.AreEqual(pair.Value, Identity(f, pair.Key));
        Assert.AreEqual(rootIdentity, Identity(f, f.Context.Root, directory: true));
        Assert.IsFalse(Directory.Exists(StatePath(f)));
    }

    [TestMethod]
    [DataRow("REMOVED_APP", false)]
    [DataRow("REMOVED_UNINSTALLER", false)]
    [DataRow("REMOVED_DESKTOP-SHORTCUT", false)]
    [DataRow("REMOVED_RECEIPT", false)]
    [DataRow("REGISTRY_REMOVED", false)]
    [DataRow("REMOVED_APP", true)]
    [DataRow("REMOVED_UNINSTALLER", true)]
    [DataRow("REMOVED_DESKTOP-SHORTCUT", true)]
    [DataRow("REMOVED_RECEIPT", true)]
    [DataRow("REGISTRY_REMOVED", true)]
    public void Interrupted_uninstall_can_resume_or_reinstall_without_losing_user_data(string phase, bool reinstall)
    {
        var f = new SetupLifecycleTests.Fixture();
        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);
        Assert.Throws<SetupInterruptedException>(() => SetupLifecycle.Uninstall(f.Context,
            current => { if (current == phase) throw new SetupInterruptedException(); }));
        Assert.AreEqual("UNINSTALLING", ReadJournalRecord(f)["Phase"]!.GetValue<string>());

        if (reinstall)
        {
            SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);
            AssertInstalled(f);
        }
        else
        {
            SetupLifecycle.Uninstall(f.Context);
            Assert.IsFalse(Directory.Exists(f.Context.Root));
            Assert.IsFalse(File.Exists(f.Context.Shortcut));
            Assert.IsFalse(File.Exists(f.Context.DesktopShortcut));
            Assert.IsEmpty(f.Registry.Entries);
        }
        Assert.AreEqual("backup must survive", File.ReadAllText(f.Sentinel));
    }

    [TestMethod]
    [DataRow("ROLLING_BACK")]
    [DataRow("ROLLBACK_REMOVED_APP")]
    public void Interrupted_rollback_resumes_from_its_owned_ledger_before_reinstall(string phase)
    {
        var f = new SetupLifecycleTests.Fixture();
        Assert.Throws<SetupInterruptedException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest,
            current =>
            {
                if (current == "APP_WRITTEN") throw new IOException("Synthetic installation failure.");
                if (current == phase) throw new SetupInterruptedException();
            }));
        Assert.AreEqual("ROLLING_BACK", ReadJournalRecord(f)["Phase"]!.GetValue<string>());

        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);

        AssertInstalled(f);
        Assert.AreEqual("backup must survive", File.ReadAllText(f.Sentinel));
    }

    [TestMethod]
    public void Missing_fixed_Programs_parent_is_created_for_first_install()
    {
        var f = new SetupLifecycleTests.Fixture();
        string programs = Path.GetDirectoryName(f.Context.Root)!;
        Assert.AreEqual("Programs", Path.GetFileName(programs));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(programs));
        Directory.Delete(programs); // Fresh fixture only; nonrecursive, and proven empty.

        SetupLifecycle.Install(f.Context, f.Payload, f.Manifest);

        AssertInstalled(f);
        Assert.IsTrue(Directory.Exists(programs));
        Assert.AreEqual("backup must survive", File.ReadAllText(f.Sentinel));
    }

    [TestMethod]
    public void A_missing_non_Programs_ancestor_is_refused_without_creating_it()
    {
        var f = new SetupLifecycleTests.Fixture();
        string programs = Path.GetDirectoryName(f.Context.Root)!;
        string localData = Path.GetDirectoryName(programs)!;
        Assert.AreEqual("Programs", Path.GetFileName(programs));
        Assert.AreEqual("LocalData", Path.GetFileName(localData));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(programs));
        Directory.Delete(programs);
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(localData));
        Directory.Delete(localData); // Exact empty synthetic ancestor, never a real profile directory.

        Assert.Throws<IOException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest));

        Assert.IsFalse(Directory.Exists(localData));
        Assert.IsFalse(Directory.Exists(programs));
        Assert.IsFalse(Directory.Exists(f.Context.Root));
        Assert.IsFalse(File.Exists(f.Context.Shortcut));
        Assert.AreEqual("backup must survive", File.ReadAllText(f.Sentinel));
    }

    [TestMethod]
    public void Bad_source_hash_rolls_back_the_created_root_and_preserves_a_foreign_shortcut()
    {
        var f = new SetupLifecycleTests.Fixture();
        File.WriteAllBytes(Path.Combine(f.Payload, SetupLifecycle.AppName), [9, 9, 9]);
        bool prepared = false;

        var error = Assert.Throws<IOException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest,
            phase =>
            {
                if (phase != "PREPARED") return;
                prepared = true;
                Assert.IsTrue(Directory.Exists(f.Context.Root));
                Assert.IsTrue(File.Exists(JournalPath(f)));
                File.WriteAllText(f.Context.Shortcut, "foreign shortcut appeared after preparation");
            }));

        StringAssert.Contains(error.Message, "SETUP_PAYLOAD_HASH_MISMATCH");
        Assert.IsTrue(prepared, "The failure must occur after the fresh root and journal exist.");
        Assert.IsFalse(Directory.Exists(f.Context.Root));
        Assert.AreEqual("foreign shortcut appeared after preparation", File.ReadAllText(f.Context.Shortcut));
        Assert.AreEqual("backup must survive", File.ReadAllText(f.Sentinel));
    }

    private static void InterruptInstall(SetupLifecycleTests.Fixture f, string phase) =>
        Assert.Throws<SetupInterruptedException>(() => SetupLifecycle.Install(f.Context, f.Payload, f.Manifest,
            current => { if (current == phase) throw new SetupInterruptedException(); }));

    private static string RolePath(SetupLifecycleTests.Fixture f, string role) => role switch
    {
        "app" => Path.Combine(f.Context.Root, SetupLifecycle.AppName),
        "receipt" => Path.Combine(f.Context.Root, SetupLifecycle.ReceiptName),
        "shortcut" => f.Context.Shortcut,
        "desktop-shortcut" => f.Context.DesktopShortcut,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private static string StatePath(SetupLifecycleTests.Fixture f) => Path.Combine(f.Context.Root, SetupLifecycle.StateName);
    private static string JournalPath(SetupLifecycleTests.Fixture f) => Path.Combine(StatePath(f), "journal.jsonl");

    private static SetupIdentity Identity(SetupLifecycleTests.Fixture f, string path, bool directory = false)
    {
        using var handle = SetupNative.Open(path, directory, f.Context.User);
        return SetupNative.Inspect(handle, path, directory, f.Context.User);
    }

    private static void ReplaceWithSameBytes(SetupLifecycleTests.Fixture f, string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var previous = Identity(f, path);
        // Retain the previous object elsewhere in the fixture so its ID cannot be reused.
        File.Move(path, Path.Combine(f.Payload, "retained-" + Guid.NewGuid().ToString("N") + ".bin"));
        using (var replacement = AclPolicy.CreateRestrictedFile(path, f.Context.User))
        {
            replacement.Write(bytes);
            replacement.Flush(true);
        }
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.AreNotEqual(previous, Identity(f, path), "The test must substitute a real filesystem identity.");
    }

    private static Dictionary<string, byte[]> Snapshot(SetupLifecycleTests.Fixture f)
    {
        var paths = Directory.GetFiles(f.Context.Root, "*", SearchOption.AllDirectories).ToList();
        if (File.Exists(f.Context.Shortcut)) paths.Add(f.Context.Shortcut);
        if (File.Exists(f.Context.DesktopShortcut)) paths.Add(f.Context.DesktopShortcut);
        paths.Add(f.Sentinel);
        var result = paths.ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in f.Registry.Entries)
            result["MEMORY_REGISTRY:" + entry.Key] = JsonSerializer.SerializeToUtf8Bytes(entry.Value);
        return result;
    }

    private static void AssertUnchanged(SetupLifecycleTests.Fixture f, Dictionary<string, byte[]> before)
    {
        var after = Snapshot(f);
        CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray(), "No fixture object may be removed or added on refusal.");
        foreach (var pair in before) CollectionAssert.AreEqual(pair.Value, after[pair.Key], pair.Key);
    }

    private static JsonObject ReadJournalRecord(SetupLifecycleTests.Fixture f)
    {
        string last = File.ReadLines(JournalPath(f)).Last(line => line.Length != 0);
        var envelope = JsonNode.Parse(last)!;
        byte[] data = Convert.FromBase64String(envelope["Data"]!.GetValue<string>());
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(data)), envelope["Sha256"]!.GetValue<string>());
        return JsonNode.Parse(data)!.AsObject();
    }

    private static void AppendJournalRecord(SetupLifecycleTests.Fixture f, Action<JsonObject> mutate)
    {
        var record = ReadJournalRecord(f);
        mutate(record);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(record);
        string line = JsonSerializer.Serialize(new { Data = Convert.ToBase64String(data), Sha256 = Convert.ToHexString(SHA256.HashData(data)) });
        File.AppendAllText(JournalPath(f), line + "\n", new UTF8Encoding(false));
        _ = ReadJournalRecord(f); // Verify the mutation retained a correct envelope checksum.
    }

    private static void AssertInstalled(SetupLifecycleTests.Fixture f)
    {
        foreach (string name in new[] { SetupLifecycle.AppName, SetupLifecycle.UninstallerName, SetupLifecycle.NoticeName })
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(f.Payload, name)), File.ReadAllBytes(Path.Combine(f.Context.Root, name)));
        Assert.IsTrue(File.Exists(Path.Combine(f.Context.Root, SetupLifecycle.ReceiptName)));
        SetupShortcut.Validate(File.ReadAllBytes(f.Context.Shortcut), Path.Combine(f.Context.Root, SetupLifecycle.AppName), f.Context.Root);
        SetupShortcut.Validate(File.ReadAllBytes(f.Context.DesktopShortcut), Path.Combine(f.Context.Root, SetupLifecycle.AppName), f.Context.Root);
        Assert.HasCount(1, f.Registry.Entries);
        Assert.IsFalse(Directory.Exists(StatePath(f)));
    }
}
