using System.Security.AccessControl;
using System.Security.Principal;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class AclPolicyTests
{
    private static readonly SecurityIdentifier User = new("S-1-5-21-1-2-3-1001");

    [TestMethod]
    [DataRow("S-1-1-0", FileSystemRights.Read)]
    [DataRow("S-1-5-11", FileSystemRights.Write)]
    [DataRow("S-1-5-32-545", FileSystemRights.Delete)]
    public void Unapproved_allow_blocks_all_backups_regardless_visibility(string sid, FileSystemRights rights)
    {
        var descriptor = new AclDescriptor(User, true, false, true, [new(new(sid), rights, AccessControlType.Allow)]);
        Assert.AreEqual(AclRisk.Block, AclPolicy.Evaluate(descriptor, User, true));
        Assert.AreEqual(AclRisk.Block, AclPolicy.Evaluate(descriptor, User, false));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public void Missing_and_NULL_DACL_block(bool present, bool isNull) =>
        Assert.AreEqual(AclRisk.Block, AclPolicy.Evaluate(new(User, present, isNull, true, []), User, true));

    [TestMethod]
    public void Empty_deny_all_DACL_is_distinct_from_NULL_and_allowed_owner_is_required()
    {
        Assert.AreEqual(AclRisk.Safe, AclPolicy.Evaluate(new(User, true, false, true, []), User, true));
        Assert.AreEqual(AclRisk.Block, AclPolicy.Evaluate(new(new("S-1-5-21-9-8-7-1002"), true, false, true, []), User, true));
    }

    [TestMethod]
    public void Owner_system_administrators_and_non_effective_denies_are_safe()
    {
        var entries = new[] { new AclEntry(User, FileSystemRights.FullControl, AccessControlType.Allow),
            new(new("S-1-5-18"), FileSystemRights.FullControl, AccessControlType.Allow),
            new(new("S-1-5-32-544"), FileSystemRights.FullControl, AccessControlType.Allow),
            new(new("S-1-1-0"), FileSystemRights.Read, AccessControlType.Deny) };
        Assert.AreEqual(AclRisk.Safe, AclPolicy.Evaluate(new(User, true, false, true, entries), User, true));
    }

    [TestMethod]
    public void Native_creation_sets_owner_and_protected_acl_at_every_missing_segment()
    {
        using var root = new StorageTestRoot();
        StorageTestRoot.Grant(root.Path, FileSystemRights.ReadAndExecute);
        string parent = root.Child("private");
        string child = Path.Combine(parent, "nested");
        AclPolicy.CreateRestrictedDirectory(child, root.User);
        using (AclPolicy.CreateRestrictedFile(Path.Combine(child, "file"), root.User)) { }
        foreach (string path in new[] { parent, child, Path.Combine(child, "file") })
        {
            AclDescriptor descriptor = AclPolicy.ReadDescriptor(path);
            Assert.AreEqual(root.User, descriptor.OwnerSid);
            Assert.IsTrue(descriptor.AreAccessRulesProtected);
            Assert.IsTrue(descriptor.DaclPresent);
            Assert.IsFalse(descriptor.IsNullDacl);
            Assert.AreEqual(AclRisk.Safe, AclPolicy.Evaluate(descriptor, root.User, true));
        }
    }

    [TestMethod]
    public void Hardening_requires_exact_confirmation_and_never_recurses()
    {
        using var root = new StorageTestRoot();
        string parent = root.Child("repair");
        Directory.CreateDirectory(parent);
        string child = Path.Combine(parent, "unapproved-child");
        Directory.CreateDirectory(child);
        StorageTestRoot.Grant(parent, FileSystemRights.Read);
        StorageTestRoot.Grant(child, FileSystemRights.Read);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => AclPolicy.HardenExisting(parent, root.User, []));
        AclPolicy.HardenExisting(parent, root.User, [parent]);
        Assert.AreEqual(AclRisk.Safe, AclPolicy.Evaluate(AclPolicy.ReadDescriptor(parent), root.User, true));
        Assert.AreEqual(AclRisk.Block, AclPolicy.Evaluate(AclPolicy.ReadDescriptor(child), root.User, true));
    }

    [TestMethod]
    [DataRow(FileSystemRights.Write, "APPDATA_PATH_WRITE_ACL_UNSAFE")]
    [DataRow(FileSystemRights.Read, "APPDATA_PATH_READ_ACL_UNSAFE")]
    public void Application_subtree_rejects_broad_access(FileSystemRights rights, string code)
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.LocalAppDataRoot, root.User);
        StorageTestRoot.Grant(paths.LocalAppDataRoot, rights);
        AppDataPathValidation result = AppDataPathPolicy.Validate(paths, paths.LocalAppDataRoot, AppDataEntryKind.Directory);
        Assert.IsFalse(result.Allowed);
        Assert.AreEqual(code, result.ErrorCode);
    }

    [TestMethod]
    public void Ordinary_inherited_anchor_with_restricted_app_child_is_allowed()
    {
        using var root = new StorageTestRoot();
        StorageTestRoot.Grant(root.Path, FileSystemRights.Read);
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.LocalAppDataRoot, root.User);
        Assert.IsTrue(AppDataPathPolicy.Validate(paths, paths.LocalAppDataRoot, AppDataEntryKind.Directory).Allowed);
        Assert.IsFalse(AppDataPathPolicy.Validate(paths, paths.LocalAppDataRoot + "-other", AppDataEntryKind.Directory).Allowed);
        Assert.IsFalse(AppDataPathPolicy.Validate(paths, root.Path, AppDataEntryKind.Directory).Allowed);
    }

    [TestMethod]
    public void Wrong_entry_kind_blocks()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.LocalAppDataRoot, root.User);
        Assert.AreEqual("APPDATA_PATH_TYPE_MISMATCH", AppDataPathPolicy.Validate(paths, paths.LocalAppDataRoot, AppDataEntryKind.File).ErrorCode);
    }

    [TestMethod]
    public void Native_NULL_DACL_is_identified_and_blocked_at_application_boundary()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.LocalAppDataRoot, root.User);
        root.SetNullDacl(paths.LocalAppDataRoot);
        AclDescriptor descriptor = AclPolicy.ReadDescriptor(paths.LocalAppDataRoot);
        Assert.IsTrue(descriptor.IsNullDacl);
        Assert.AreEqual("APPDATA_PATH_NULL_DACL_UNSAFE", AppDataPathPolicy.Validate(paths, paths.LocalAppDataRoot, AppDataEntryKind.Directory).ErrorCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Junction_at_app_root_or_descendant_is_never_followed(bool nested)
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        string external = root.Child("external");
        Directory.CreateDirectory(external);
        string sentinel = Path.Combine(external, "sentinel");
        File.WriteAllText(sentinel, "untouched");
        string junction = nested ? paths.DiagnosticLogRoot : paths.LocalAppDataRoot;
        if (nested) AclPolicy.CreateRestrictedDirectory(paths.LocalAppDataRoot, root.User);
        StorageTestRoot.CreateJunction(junction, external);
        try
        {
            var result = AppDataPathPolicy.Validate(paths, junction, AppDataEntryKind.Directory);
            Assert.AreEqual("APPDATA_PATH_REPARSE_POINT_REJECTED", result.ErrorCode);
            Assert.ThrowsExactly<PathBoundaryException>(() => AclPolicy.CreateRestrictedDirectory(Path.Combine(junction, "new"), root.User));
            Assert.ThrowsExactly<PathBoundaryException>(() => AclPolicy.HardenExisting(junction, root.User, [junction]));
            Assert.AreEqual("untouched", File.ReadAllText(sentinel));
            Assert.IsFalse(Directory.Exists(Path.Combine(external, "new")));
        }
        finally { Directory.Delete(junction); }
    }

    [TestMethod]
    public void Lease_pins_directory_identity_until_operation_finishes()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.LocalAppDataRoot, root.User);
        using (AppDataPathLease lease = AppDataPathPolicy.Acquire(paths, paths.LocalAppDataRoot, AppDataEntryKind.Directory))
        {
            Assert.IsNotNull(lease.Identity);
            Assert.ThrowsExactly<IOException>(() => Directory.Move(paths.LocalAppDataRoot, root.Child("replaced")));
        }
        Directory.Move(paths.LocalAppDataRoot, root.Child("replaced"));
        Assert.IsTrue(Directory.Exists(root.Child("replaced")));
    }

    [TestMethod]
    public void Hardlinked_file_cannot_enter_the_private_boundary()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.LocalAppDataRoot, root.User);
        using (AclPolicy.CreateRestrictedFile(paths.SettingsFile, root.User)) { }
        StorageTestRoot.CreateHardLink(root.Child("alias"), paths.SettingsFile);
        Assert.AreEqual("APPDATA_PATH_HARDLINK_REJECTED", AppDataPathPolicy.Validate(paths, paths.SettingsFile, AppDataEntryKind.File).ErrorCode);
    }

    [TestMethod]
    public void Existing_file_is_not_overwritten_by_restricted_creation()
    {
        using var root = new StorageTestRoot();
        string file = root.Child("file");
        File.WriteAllText(file, "keep");
        Assert.ThrowsExactly<IOException>(() => AclPolicy.CreateRestrictedFile(file, root.User));
        Assert.AreEqual("keep", File.ReadAllText(file));
    }

    [TestMethod]
    public void Confirmation_of_parent_does_not_authorize_child_repair()
    {
        using var root = new StorageTestRoot();
        string child = root.Child("child");
        Directory.CreateDirectory(child);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => AclPolicy.HardenExisting(child, root.User, [root.Path]));
    }

    [TestMethod]
    public void Hardening_one_file_preserves_content()
    {
        using var root = new StorageTestRoot();
        string file = root.Child("file");
        File.WriteAllText(file, "keep");
        AclPolicy.HardenExisting(file, root.User, [file]);
        Assert.AreEqual("keep", File.ReadAllText(file));
        Assert.AreEqual(root.User, AclPolicy.ReadDescriptor(file).OwnerSid);
        Assert.AreEqual(AclRisk.Safe, AclPolicy.Evaluate(AclPolicy.ReadDescriptor(file), root.User, false));
    }

    [TestMethod]
    public void Owned_probe_directory_creation_cannot_adopt_an_existing_directory()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("probe");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        Assert.ThrowsExactly<IOException>(() => AclPolicy.CreateRestrictedDirectory(directory, root.User, requireNew: true));
        Assert.IsTrue(Directory.Exists(directory));
    }

    [TestMethod]
    public void Safe_inherited_backup_file_is_allowed()
    {
        using var root = new StorageTestRoot();
        string parent = root.Child("private-backup");
        AclPolicy.CreateRestrictedDirectory(parent, root.User);
        string child = Path.Combine(parent, "ordinary-git-output");
        File.WriteAllText(child, "backup content");
        AclDescriptor descriptor = AclPolicy.ReadDescriptor(child);
        Assert.IsFalse(descriptor.AreAccessRulesProtected);
        Assert.AreEqual(root.User, descriptor.OwnerSid);
        Assert.AreEqual(AclRisk.Safe, AclPolicy.Evaluate(descriptor, root.User, true));
        Assert.AreEqual(AclRisk.Safe, AclPolicy.Evaluate(descriptor, root.User, false));
    }

    [TestMethod]
    public void Restricted_creation_verifier_still_rejects_inherited_file_acl()
    {
        using var root = new StorageTestRoot();
        string parent = root.Child("private-parent");
        AclPolicy.CreateRestrictedDirectory(parent, root.User);
        string child = Path.Combine(parent, "inherited-file");
        File.WriteAllText(child, "content");
        using var handle = NativeFileSystem.Open(child);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => AclPolicy.VerifyRestricted(handle, root.User));
    }

    [TestMethod]
    public void Application_data_still_rejects_safe_but_unprotected_inherited_acl()
    {
        using var root = new StorageTestRoot();
        AppPaths paths = AppPaths.Create(root.Path);
        AclPolicy.CreateRestrictedDirectory(paths.LocalAppDataRoot, root.User);
        Directory.CreateDirectory(paths.DiagnosticLogRoot);
        var result = AppDataPathPolicy.Validate(paths, paths.DiagnosticLogRoot, AppDataEntryKind.Directory);
        Assert.IsFalse(result.Allowed);
        Assert.AreEqual("APPDATA_PATH_INHERITED_ACL_UNSAFE", result.ErrorCode);
    }
}
