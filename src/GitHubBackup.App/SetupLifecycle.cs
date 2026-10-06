using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

internal sealed record SetupContext(string Root, string Shortcut, string DesktopShortcut, ISetupRegistryStore Registry, SecurityIdentifier User);
internal sealed record SetupManifest(string Version, string SourceCommit, string AppHash, string UninstallerHash, string NoticeHash);
internal sealed class SetupInterruptedException : IOException;

internal static class SetupLifecycle
{
    internal const string AppName = "GitHubBackup.exe";
    internal const string UninstallerName = "Uninstall.exe";
    internal const string NoticeName = "NOTICE.txt";
    internal const string ReceiptName = "install.ini";
    internal const string StateName = ".GitHubBackupTool.state";
    internal const string ShortcutName = "GitHub 备份工具.lnk";
    private const string JournalName = "journal.jsonl";
    private const string Product = "GitHubBackupTool";
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static void Install(SetupContext context, string payload, SetupManifest manifest, Action<string>? checkpoint = null)
    {
        ValidateManifest(manifest);
        using var tree = new Tree(context);
        if (tree.Root is not null)
        {
            if (Exists(tree.StatePath))
            {
                using var previous = Journal.Open(tree);
                if (previous.Record.Phase == "COMMITTED")
                {
                    using var verified = VerifyReceipt(tree, manifest);
                    previous.Remove();
                    return;
                }
                if (previous.Record.Phase == "UNINSTALLING")
                {
                    using var files = VerifyEntries(tree, previous.Record, allowMissing: true);
                    RemoveInstallation(tree, previous, files, null);
                }
                else Rollback(tree, previous);
            }
            else
            {
                using var verified = VerifyReceipt(tree);
                if (verified.Record.Schema != 3)
                    throw new IOException("SETUP_OLD_VERSION_MUST_BE_UNINSTALLED");
                if (verified.Record.Manifest != manifest) throw new IOException("SETUP_INSTALLATION_VERSION_CONFLICT");
                return;
            }
        }
        string installId = Guid.NewGuid().ToString("N");
        SetupRegistryEntry registryEntry = SetupRegistryEntry.ForInstall(context, manifest, installId);
        context.Registry.EnsureAbsent(installId);
        if (Exists(context.Shortcut)) throw new IOException("SETUP_SHORTCUT_ALREADY_EXISTS");
        if (Exists(context.DesktopShortcut)) throw new IOException("SETUP_DESKTOP_SHORTCUT_ALREADY_EXISTS");
        tree.CreateRoot();
        using var journal = Journal.Create(tree, manifest, installId);
        try
        {
            Mark(journal, "PREPARED", checkpoint);
            AddPayload(tree, journal, payload, "app", manifest.AppHash);
            Mark(journal, "APP_WRITTEN", checkpoint);
            AddPayload(tree, journal, payload, "uninstaller", manifest.UninstallerHash);
            AddPayload(tree, journal, payload, "notice", manifest.NoticeHash);
            byte[] shortcut = SetupShortcut.Create(Path.Combine(context.Root, AppName), context.Root);
            SetupShortcut.Validate(shortcut, Path.Combine(context.Root, AppName), context.Root);
            AddFile(tree, journal, "shortcut", handle => SetupNative.Write(handle, shortcut), Hash(shortcut));
            Mark(journal, "SHORTCUT_WRITTEN", checkpoint);
            AddFile(tree, journal, "desktop-shortcut", handle => SetupNative.Write(handle, shortcut), Hash(shortcut));
            Mark(journal, "DESKTOP_SHORTCUT_WRITTEN", checkpoint);
            Mark(journal, "REGISTRY_PENDING", checkpoint);
            context.Registry.CreateNew(registryEntry);
            Mark(journal, "REGISTRY_WRITTEN", checkpoint);
            AddFile(tree, journal, "receipt", handle =>
            {
                journal.Record.ReceiptId = SetupNative.Inspect(handle, journal.StagePath("receipt"), false, context.User);
                var receipt = journal.Record with { Phase = "COMMITTED", Entries = journal.Record.Entries.Where(e => e.Role != "receipt").ToList() };
                SetupNative.Write(handle, JsonSerializer.SerializeToUtf8Bytes(receipt, Json));
            });
            Mark(journal, "RECEIPT_WRITTEN", checkpoint);
            using (var verified = VerifyReceipt(tree, manifest)) { }
            Mark(journal, "COMMITTED", checkpoint);
            journal.Remove();
        }
        catch (SetupInterruptedException) { throw; }
        catch
        {
            // Never turn a conflict into permission to remove a substituted object.
            Rollback(tree, journal, checkpoint);
            throw;
        }
    }

    internal static void Uninstall(SetupContext context, Action<string>? checkpoint = null)
    {
        using var tree = new Tree(context);
        if (tree.Root is null) throw new IOException("SETUP_INSTALLATION_MISSING");
        Journal journal;
        VerifiedFiles files;
        if (Exists(tree.StatePath))
        {
            journal = Journal.Open(tree);
            try
            {
                if (journal.Record.Phase != "UNINSTALLING") throw new IOException("SETUP_INSTALL_INCOMPLETE");
                files = VerifyEntries(tree, journal.Record, allowMissing: true);
            }
            catch { journal.Dispose(); throw; }
        }
        else
        {
            files = VerifyReceipt(tree, allowMissingRegistryEntry: true);
            try { journal = Journal.Create(tree, files.Record.Manifest, installed: files.Record); }
            catch { files.Dispose(); throw; }
        }
        using (journal)
        using (files)
            RemoveInstallation(tree, journal, files, checkpoint);
    }

    private static void RemoveInstallation(Tree tree, Journal journal, VerifiedFiles files, Action<string>? checkpoint)
    {
        Mark(journal, "UNINSTALLING", checkpoint);
        if (journal.Record.Schema == 3)
        {
            tree.Context.Registry.RemoveOwned(RegistryEntry(tree.Context, journal.Record), allowPartial: true);
            checkpoint?.Invoke("REGISTRY_REMOVED");
        }
        // Receipt and native uninstaller remain available until all other removals.
        foreach (string role in new[] { "shortcut", "desktop-shortcut", "app", "notice", "uninstaller", "receipt" })
        {
            if (files.Handles.TryGetValue(role, out var handle))
            {
                NativeFileSystem.DeleteByHandle(handle);
                handle.Dispose();
                files.Handles.Remove(role);
            }
            journal.Save();
            checkpoint?.Invoke("REMOVED_" + role.ToUpperInvariant());
        }
        journal.Remove();
        tree.RemoveRootIfEmpty();
    }

    private static void AddPayload(Tree tree, Journal journal, string payload, string role, string expected)
    {
        string source = NativeFileSystem.CanonicalPath(Path.Combine(payload, Leaf(role)));
        using var parents = NativeFileSystem.PinDirectories(Path.GetDirectoryName(source)!);
        using var input = NativeFileSystem.Open(source, 0x80020080, shareWrite: false);
        NativeFileSystem.Inspect(input, source, false);
        if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(input), tree.Context.User, true) != AclRisk.Safe)
            throw new IOException("SETUP_SOURCE_ACL_INVALID");
        if (SetupNative.Hash(input) != expected) throw new IOException("SETUP_PAYLOAD_HASH_MISMATCH");
        AddFile(tree, journal, role, output => SetupNative.Copy(input, output), expected);
    }

    private static void AddFile(Tree tree, Journal journal, string role, Action<SafeFileHandle> write, string? expected = null)
    {
        string stage = Path.Combine(tree.Parent(role), ".GitHubBackupTool-" + Guid.NewGuid().ToString("N") + ".stage");
        using var handle = SetupNative.Create(stage, false, tree.Context.User);
        var entry = new Entry { Role = role, Stage = Path.GetFileName(stage), Id = SetupNative.Inspect(handle, stage, false, tree.Context.User) };
        journal.Record.Entries.Add(entry);
        journal.Save(); // Persist authority before any bytes are written.
        write(handle);
        string hash = SetupNative.Hash(handle);
        if (expected is not null && hash != expected) throw new IOException("SETUP_WRITTEN_HASH_MISMATCH");
        entry.Hash = hash;
        journal.Save(); // Persist validated bytes before the no-replace publication.
        // Both complete ancestor chains and the destination parent stay pinned.
        SetupNative.Rename(handle, tree.Path(role));
        if (SetupNative.Inspect(handle, tree.Path(role), false, tree.Context.User) != entry.Id)
            throw new IOException("SETUP_RENAME_IDENTITY_CHANGED");
        entry.Published = true;
        journal.Save();
    }

    private static void Rollback(Tree tree, Journal journal, Action<string>? checkpoint = null)
    {
        using var files = VerifyEntries(tree, journal.Record, allowMissing: journal.Record.Phase == "ROLLING_BACK");
        var expected = files.Paths.Where(p => System.IO.Path.GetDirectoryName(p) == tree.Context.Root)
            .Select(System.IO.Path.GetFileName).Append(StateName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.EnumerateFileSystemEntries(tree.Context.Root).Any(p => !expected.Contains(Path.GetFileName(p))))
            throw new IOException("SETUP_RECOVERY_UNKNOWN_CONTENT");
        if (journal.Record.Schema == 3)
            tree.Context.Registry.RemoveOwned(RegistryEntry(tree.Context, journal.Record), allowPartial: true);
        Mark(journal, "ROLLING_BACK", checkpoint);
        foreach (var pair in files.Handles.Reverse())
        {
            NativeFileSystem.DeleteByHandle(pair.Value);
            pair.Value.Dispose();
            checkpoint?.Invoke("ROLLBACK_REMOVED_" + pair.Key.ToUpperInvariant());
        }
        files.Handles.Clear();
        journal.Remove();
        tree.RemoveRootIfEmpty();
        if (tree.Root is not null) throw new IOException("SETUP_RECOVERY_ROOT_NOT_EMPTY");
    }

    private static VerifiedFiles VerifyReceipt(Tree tree, SetupManifest? expected = null, bool allowMissingRegistryEntry = false)
    {
        string receiptPath = tree.Path("receipt");
        var handle = SetupNative.Open(receiptPath, false, tree.Context.User);
        VerifiedFiles? files = null;
        try
        {
            var record = Parse<Ledger>(SetupNative.Read(handle, 65536));
            ValidateRecord(tree, record);
            string[] roles = record.Schema == 3
                ? ["app", "uninstaller", "notice", "shortcut", "desktop-shortcut"]
                : ["app", "uninstaller", "notice", "shortcut"];
            if (record.Phase != "COMMITTED" || record.ReceiptId != SetupNative.Inspect(handle, receiptPath, false, tree.Context.User)
                || record.Entries.Count != roles.Length || record.Entries.Any(e => !e.Published || e.Hash is null)
                || !roles.All(r => record.Entries.Any(e => e.Role == r))
                || (expected is not null && record.Manifest != expected)) throw new IOException("SETUP_RECEIPT_MISMATCH");
            files = VerifyEntries(tree, record, allowMissing: false);
            if (record.Schema == 3)
            {
                try { tree.Context.Registry.VerifyExact(RegistryEntry(tree.Context, record)); }
                catch (IOException ex) when (allowMissingRegistryEntry && ex.Message == "SETUP_REGISTRY_ENTRY_MISSING") { }
            }
            files.Handles.Add("receipt", handle);
            record.Entries.Add(new Entry { Role = "receipt", Stage = "", Id = record.ReceiptId!, Hash = SetupNative.Hash(handle), Published = true });
            VerifiedFiles verified = files;
            files = null;
            return verified;
        }
        catch { files?.Dispose(); handle.Dispose(); throw; }
    }

    private static VerifiedFiles VerifyEntries(Tree tree, Ledger record, bool allowMissing)
    {
        ValidateRecord(tree, record);
        var result = new VerifiedFiles(record);
        try
        {
            foreach (var entry in record.Entries)
            {
                string stage = Path.Combine(tree.Parent(entry.Role), entry.Stage);
                if (!entry.Published && entry.Stage.Length != 0 && Exists(stage) && Exists(tree.Path(entry.Role)))
                    throw new IOException("SETUP_RECOVERY_BOTH_NAMES_PRESENT");
                // A crash can occur after rename but before its next journal snapshot.
                string path = !entry.Published && entry.Stage.Length != 0 && Exists(stage) ? stage : tree.Path(entry.Role);
                SafeFileHandle handle;
                try { handle = SetupNative.Open(path, false, tree.Context.User); }
                catch (FileNotFoundException) when (allowMissing) { continue; }
                result.Handles.Add(entry.Role, handle);
                result.Paths.Add(path);
                if (SetupNative.Inspect(handle, path, false, tree.Context.User) != entry.Id
                    || (entry.Hash is not null && SetupNative.Hash(handle) != entry.Hash)) throw new IOException("SETUP_OWNED_FILE_CHANGED");
                if (entry.Role is "shortcut" or "desktop-shortcut" && entry.Hash is not null)
                    SetupShortcut.Validate(SetupNative.Read(handle, SetupShortcut.MaximumBytes), tree.Path("app"), tree.Context.Root);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static void ValidateRecord(Tree tree, Ledger record)
    {
        if (record.Schema is not (2 or 3) || record.Product != Product || record.User != tree.Context.User.Value
            || record.Root != tree.Context.Root || record.Shortcut != tree.Context.Shortcut
            || record.RootId != tree.RootId || record.RootId is null || !IsIdentity(record.RootId)
            || record.StateId is null || record.JournalId is null
            || !IsIdentity(record.StateId) || !IsIdentity(record.JournalId)
            || record.Manifest is null || record.Entries is null || record.Entries.Count > 6
            || record.Entries.Select(e => e.Role).Distinct().Count() != record.Entries.Count
            || record.Phase is not ("PREPARED" or "APP_WRITTEN" or "SHORTCUT_WRITTEN" or "DESKTOP_SHORTCUT_WRITTEN"
                or "REGISTRY_PENDING" or "REGISTRY_WRITTEN" or "RECEIPT_WRITTEN" or "COMMITTED" or "UNINSTALLING" or "ROLLING_BACK"))
            throw new IOException("SETUP_RECORD_INVALID");
        if (record.Schema == 2)
        {
            if (record.DesktopShortcut is not null || record.RegistryInstallId is not null
                || record.Entries.Any(e => e.Role == "desktop-shortcut")) throw new IOException("SETUP_RECORD_INVALID");
        }
        else if (record.DesktopShortcut != tree.Context.DesktopShortcut || record.RegistryInstallId is null
            || !Guid.TryParseExact(record.RegistryInstallId, "N", out var registryId)
            || registryId.ToString("N") != record.RegistryInstallId)
            throw new IOException("SETUP_RECORD_INVALID");
        ValidateManifest(record.Manifest);
        if (record.Phase == "UNINSTALLING")
        {
            string[] roles = record.Schema == 3
                ? ["app", "uninstaller", "notice", "shortcut", "desktop-shortcut", "receipt"]
                : ["app", "uninstaller", "notice", "shortcut", "receipt"];
            if (record.Entries.Count != roles.Length || record.ReceiptId is null || !IsIdentity(record.ReceiptId)
                || record.Entries.Any(e => !e.Published || e.Hash is null)
                || !roles.All(r => record.Entries.Any(e => e.Role == r))
                || record.Entries.Single(e => e.Role == "receipt").Id != record.ReceiptId)
                throw new IOException("SETUP_UNINSTALL_RECORD_INCOMPLETE");
        }
        if (record.Phase == "COMMITTED")
        {
            string[] roles = record.Schema == 3
                ? ["app", "uninstaller", "notice", "shortcut", "desktop-shortcut"]
                : ["app", "uninstaller", "notice", "shortcut"];
            bool journalReceiptPresent = record.Entries.Any(e => e.Role == "receipt");
            if (record.Entries.Count != roles.Length + (journalReceiptPresent ? 1 : 0) || record.ReceiptId is null || !IsIdentity(record.ReceiptId)
                || record.Entries.Any(e => !e.Published || e.Hash is null)
                || !roles.All(r => record.Entries.Any(e => e.Role == r))
                || (journalReceiptPresent && record.Entries.Single(e => e.Role == "receipt").Id != record.ReceiptId))
                throw new IOException("SETUP_RECEIPT_MISMATCH");
        }
        foreach (var entry in record.Entries)
        {
            if (record.Schema == 2 && entry.Role == "desktop-shortcut") throw new IOException("SETUP_ENTRY_INVALID");
            _ = Leaf(entry.Role);
            string? manifestHash = entry.Role switch { "app" => record.Manifest.AppHash, "uninstaller" => record.Manifest.UninstallerHash,
                "notice" => record.Manifest.NoticeHash, _ => null };
            if (entry.Id is null || !IsIdentity(entry.Id) || (entry.Hash is not null && (!IsHash(entry.Hash)
                    || (manifestHash is not null && entry.Hash != manifestHash))) || (entry.Published && entry.Hash is null)
                || (entry.Stage != "" && (entry.Stage.Length != 56 || !entry.Stage.StartsWith(".GitHubBackupTool-", StringComparison.Ordinal)
                    || !entry.Stage.EndsWith(".stage", StringComparison.Ordinal) || !Guid.TryParseExact(entry.Stage[18..50], "N", out _)))
                || (!entry.Published && entry.Stage == "")) throw new IOException("SETUP_ENTRY_INVALID");
        }
    }

    internal static void ValidateManifest(SetupManifest manifest)
    {
        if (manifest is null || !Version.TryParse(manifest.Version, out _) || manifest.SourceCommit is null
            || manifest.SourceCommit.Length != 40 || !manifest.SourceCommit.All(Uri.IsHexDigit)
            || !IsHash(manifest.AppHash) || !IsHash(manifest.UninstallerHash) || !IsHash(manifest.NoticeHash))
            throw new IOException("SETUP_MANIFEST_INVALID");
    }

    internal static T Parse<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 20 });
            CheckDuplicates(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new IOException("SETUP_RECORD_NULL");
        }
        catch (JsonException error) { throw new IOException("SETUP_RECORD_JSON_INVALID", error); }
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new IOException("SETUP_RECORD_DUPLICATE_FIELD"); CheckDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) CheckDuplicates(value);
    }

    private static bool IsIdentity(SetupIdentity id) => id.FileId is not null && id.FileId.Length == 32 && id.FileId.All(Uri.IsHexDigit);
    private static bool IsHash(string? value) => value is not null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static SetupRegistryEntry RegistryEntry(SetupContext context, Ledger record) =>
        record.Schema == 3 && record.RegistryInstallId is not null
            ? SetupRegistryEntry.ForInstall(context, record.Manifest, record.RegistryInstallId)
            : throw new IOException("SETUP_REGISTRY_ID_INVALID");
    private static void Mark(Journal journal, string phase, Action<string>? checkpoint)
    { journal.Record.Phase = phase; journal.Save(); checkpoint?.Invoke(phase); }
    private static string Leaf(string role) => role switch
    { "app" => AppName, "uninstaller" => UninstallerName, "notice" => NoticeName, "receipt" => ReceiptName,
        "shortcut" or "desktop-shortcut" => ShortcutName, _ => throw new IOException("SETUP_ROLE_INVALID") };
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private sealed class Tree : IDisposable
    {
        internal SetupContext Context { get; }
        private readonly PathLease parents;
        internal SafeFileHandle? Root { get; private set; }
        internal SetupIdentity? RootId { get; private set; }
        internal string StatePath => System.IO.Path.Combine(Context.Root, StateName);
        internal Tree(SetupContext context)
        {
            if (NativeFileSystem.CanonicalPath(context.Root) != context.Root || NativeFileSystem.CanonicalPath(context.Shortcut) != context.Shortcut
                || NativeFileSystem.CanonicalPath(context.DesktopShortcut) != context.DesktopShortcut || context.Registry is null
                || System.IO.Path.GetFileName(context.Root) != Product || System.IO.Path.GetFileName(context.Shortcut) != Leaf("shortcut")
                || System.IO.Path.GetFileName(context.DesktopShortcut) != Leaf("desktop-shortcut")
                || string.Equals(context.Shortcut, context.DesktopShortcut, StringComparison.OrdinalIgnoreCase)
                || System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(context.Root)) != "Programs"
                || context.Root.Length >= 200 || context.Shortcut.Length >= 259 || context.DesktopShortcut.Length >= 259)
                throw new IOException("SETUP_CONTEXT_INVALID");
            Context = context;
            parents = new PathLease();
            try
            {
                string programs = System.IO.Path.GetDirectoryName(context.Root)!;
                foreach (string segment in NativeFileSystem.Segments(programs))
                {
                    SafeFileHandle pin;
                    try { pin = NativeFileSystem.Open(segment); }
                    catch (FileNotFoundException) when (segment == programs)
                    { pin = SetupNative.CreateSharedPrograms(programs, context.User); }
                    parents.Add(pin); NativeFileSystem.Inspect(pin, segment, true);
                    if (segment == programs) SetupNative.VerifySharedParent(pin, context.User);
                }
                foreach (string directory in new[] { System.IO.Path.GetDirectoryName(context.Shortcut)!, System.IO.Path.GetDirectoryName(context.DesktopShortcut)! })
                {
                    SafeFileHandle? folder = null;
                    foreach (string segment in NativeFileSystem.Segments(directory))
                    {
                        folder = NativeFileSystem.Open(segment); parents.Add(folder); NativeFileSystem.Inspect(folder, segment, true);
                    }
                    SetupNative.VerifySharedParent(folder ?? throw new IOException("SETUP_SHORTCUT_PARENT_MISSING"), context.User);
                }
                try { Root = SetupNative.Open(context.Root, true, context.User); RootId = SetupNative.Inspect(Root, context.Root, true, context.User); }
                catch (FileNotFoundException) { }
            }
            catch { Root?.Dispose(); parents.Dispose(); throw; }
        }
        internal void CreateRoot()
        { Root = SetupNative.Create(Context.Root, true, Context.User); RootId = SetupNative.Inspect(Root, Context.Root, true, Context.User); }
        internal string Path(string role) => role switch
        {
            "shortcut" => Context.Shortcut,
            "desktop-shortcut" => Context.DesktopShortcut,
            _ => System.IO.Path.Combine(Context.Root, Leaf(role))
        };
        internal string Parent(string role) => role switch
        {
            "shortcut" => System.IO.Path.GetDirectoryName(Context.Shortcut)!,
            "desktop-shortcut" => System.IO.Path.GetDirectoryName(Context.DesktopShortcut)!,
            _ => Context.Root
        };
        internal void RemoveRootIfEmpty()
        {
            if (Directory.EnumerateFileSystemEntries(Context.Root).Any()) return;
            if (SetupNative.Inspect(Root!, Context.Root, true, Context.User) != RootId) throw new IOException("SETUP_ROOT_CHANGED");
            NativeFileSystem.DeleteByHandle(Root!); Root!.Dispose(); Root = null;
        }
        public void Dispose() { Root?.Dispose(); parents.Dispose(); }
    }

    private sealed record Entry
    {
        public required string Role { get; init; }
        public required string Stage { get; init; }
        public required SetupIdentity Id { get; init; }
        public string? Hash { get; set; }
        public bool Published { get; set; }
    }
    private sealed record Ledger
    {
        public required int Schema { get; init; }
        public required string Product { get; init; }
        public required string User { get; init; }
        public required string Root { get; init; }
        public required string Shortcut { get; init; }
        public string? DesktopShortcut { get; init; }
        public string? RegistryInstallId { get; init; }
        public required SetupIdentity RootId { get; init; }
        public required SetupIdentity StateId { get; set; }
        public required SetupIdentity JournalId { get; set; }
        public SetupIdentity? ReceiptId { get; set; }
        public required SetupManifest Manifest { get; init; }
        public required string Phase { get; set; }
        public required List<Entry> Entries { get; init; }
    }
    private sealed record Envelope(string Data, string Sha256);
    private sealed class VerifiedFiles(Ledger record) : IDisposable
    {
        internal Ledger Record { get; } = record;
        internal Dictionary<string, SafeFileHandle> Handles { get; } = [];
        internal List<string> Paths { get; } = [];
        public void Dispose() { foreach (var handle in Handles.Values) handle.Dispose(); }
    }
    private sealed class Journal : IDisposable
    {
        internal Ledger Record { get; }
        private readonly Tree tree;
        private readonly SafeFileHandle state;
        private readonly SafeFileHandle file;
        private bool removed;
        private long? completeLength;
        private Journal(Tree tree, SafeFileHandle state, SafeFileHandle file, Ledger record)
        { this.tree = tree; this.state = state; this.file = file; Record = record; }
        internal static Journal Create(Tree tree, SetupManifest manifest, string? registryInstallId = null, Ledger? installed = null)
        {
            var state = SetupNative.Create(tree.StatePath, true, tree.Context.User);
            SafeFileHandle? file = null;
            try
            {
                file = SetupNative.Create(System.IO.Path.Combine(tree.StatePath, JournalName), false, tree.Context.User);
                var record = installed is null ? new Ledger { Schema = 3, Product = Product, Phase = "PREPARED", Entries = [],
                    User = tree.Context.User.Value, Root = tree.Context.Root,
                    Shortcut = tree.Context.Shortcut, DesktopShortcut = tree.Context.DesktopShortcut, RegistryInstallId = registryInstallId,
                    RootId = tree.RootId!, Manifest = manifest,
                    StateId = SetupNative.Inspect(state, tree.StatePath, true, tree.Context.User),
                    JournalId = SetupNative.Inspect(file, System.IO.Path.Combine(tree.StatePath, JournalName), false, tree.Context.User) }
                    : installed with { Phase = "UNINSTALLING", StateId = SetupNative.Inspect(state, tree.StatePath, true, tree.Context.User),
                        JournalId = SetupNative.Inspect(file, System.IO.Path.Combine(tree.StatePath, JournalName), false, tree.Context.User) };
                var journal = new Journal(tree, state, file, record);
                journal.Save(); return journal;
            }
            catch { file?.Dispose(); state.Dispose(); throw; }
        }
        internal static Journal Open(Tree tree)
        {
            var state = SetupNative.Open(tree.StatePath, true, tree.Context.User);
            SafeFileHandle? file = null;
            try
            {
                file = SetupNative.Open(System.IO.Path.Combine(tree.StatePath, JournalName), false, tree.Context.User, writable: true);
                byte[] bytes = SetupNative.Read(file, 1024 * 1024);
                int start = 0; Ledger? record = null;
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (bytes[i] != 10) continue;
                    var envelope = Parse<Envelope>(bytes[start..i]);
                    byte[] data;
                    try { data = Convert.FromBase64String(envelope.Data); }
                    catch (FormatException error) { throw new IOException("SETUP_JOURNAL_DATA_INVALID", error); }
                    if (Hash(data) != envelope.Sha256) throw new IOException("SETUP_JOURNAL_CHECKSUM_INVALID");
                    record = Parse<Ledger>(data); ValidateRecord(tree, record); start = i + 1;
                }
                if (record is null || record.StateId != SetupNative.Inspect(state, tree.StatePath, true, tree.Context.User)
                    || record.JournalId != SetupNative.Inspect(file, System.IO.Path.Combine(tree.StatePath, JournalName), false, tree.Context.User)
                    || Directory.EnumerateFileSystemEntries(tree.StatePath).Count() != 1)
                    throw new IOException("SETUP_JOURNAL_IDENTITY_INVALID");
                // Do not change even an incomplete append until ownership preflight
                // has succeeded. Rollback can delete the verified journal directly.
                return new(tree, state, file, record) { completeLength = start == bytes.Length ? null : start };
            }
            catch { file?.Dispose(); state.Dispose(); throw; }
        }
        internal string StagePath(string role) => System.IO.Path.Combine(tree.Parent(role), Record.Entries.Single(e => e.Role == role).Stage);
        internal void Save()
        {
            if (completeLength is long complete) { RandomAccess.SetLength(file, complete); completeLength = null; }
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(Record, Json);
            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Envelope(Convert.ToBase64String(data), Hash(data)), Json) + "\n");
            long length = RandomAccess.GetLength(file);
            if (length + line.Length > 1024 * 1024) throw new IOException("SETUP_JOURNAL_LIMIT");
            SetupNative.Write(file, line, length);
        }
        internal void Remove()
        {
            if (removed) return;
            if (SetupNative.Inspect(file, System.IO.Path.Combine(tree.StatePath, JournalName), false, tree.Context.User) != Record.JournalId
                || SetupNative.Inspect(state, tree.StatePath, true, tree.Context.User) != Record.StateId
                || Directory.EnumerateFileSystemEntries(tree.StatePath).Count() != 1) throw new IOException("SETUP_JOURNAL_CHANGED");
            NativeFileSystem.DeleteByHandle(file); file.Dispose();
            NativeFileSystem.DeleteByHandle(state); state.Dispose(); removed = true;
        }
        public void Dispose() { file.Dispose(); state.Dispose(); }
    }
}
