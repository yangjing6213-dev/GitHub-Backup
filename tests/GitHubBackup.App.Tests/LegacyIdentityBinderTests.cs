using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class LegacyIdentityBinderTests
{
    private static RepositoryDescriptor Repo(long id, string name, string localName) =>
        new(id, name, "sample-owner/" + name, "https://github.com/sample-owner/" + name,
            false, false, false, true, null, 0, localName, id == 0 ? "legacy-unresolved" : "active");

    [TestMethod]
    public async Task Protected_immutable_id_binds_remote_rename_without_rewriting_evidence()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        string metadata = Path.Combine(owner, "metadata", "OldName");
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        string path = Path.Combine(metadata, "repository.json");
        const string body = "{\"id\":42,\"full_name\":\"sample-owner/OldName\",\"owner\":{\"login\":\"sample-owner\"}}";
        await File.WriteAllTextAsync(path, body);
        RepositoryDescriptor legacy = Repo(0, "OldName", "OldName");
        RepositoryDescriptor current = Repo(42, "NewName", "NewName");

        LegacyBinding binding = await LegacyIdentityBinder.TryBindAsync(owner, legacy, [current], "sample-owner",
            new LegacyIdentityEvidence(path), CancellationToken.None);
        IReadOnlyList<RepositoryDescriptor> mapped = ManifestStore.Reconcile(owner, [legacy], [current], [binding]);

        Assert.IsTrue(binding.Bound);
        Assert.AreEqual(42L, binding.BoundRepositoryId);
        Assert.AreEqual("OldName", mapped.Single().LocalName);
        Assert.AreEqual("sample-owner/NewName", mapped.Single().NameWithOwner);
        Assert.AreEqual(body, await File.ReadAllTextAsync(path));
        Assert.IsFalse(Directory.Exists(Path.Combine(owner, "manifests")));
    }

    [TestMethod]
    public async Task Binding_from_another_root_with_same_owner_cannot_reconcile_here()
    {
        using var source = new StorageTestRoot();
        using var target = new StorageTestRoot();
        string sourceOwner = source.Child("sample-owner");
        string targetOwner = target.Child("sample-owner");
        string metadata = Path.Combine(sourceOwner, "metadata", "OldName");
        AclPolicy.CreateRestrictedDirectory(metadata, source.User);
        AclPolicy.CreateRestrictedDirectory(targetOwner, target.User);
        string evidence = Path.Combine(metadata, "repository.json");
        await File.WriteAllTextAsync(evidence,
            "{\"id\":42,\"full_name\":\"sample-owner/OldName\",\"owner\":{\"login\":\"sample-owner\"}}");
        RepositoryDescriptor legacy = Repo(0, "OldName", "OldName");
        RepositoryDescriptor current = Repo(42, "NewName", "NewName");

        LegacyBinding foreign = await LegacyIdentityBinder.TryBindAsync(sourceOwner, legacy, [current], "sample-owner",
            new LegacyIdentityEvidence(evidence), default);

        Assert.IsTrue(foreign.Bound);
        Assert.IsFalse((await LegacyIdentityBinder.TryBindAsync(targetOwner, legacy, [current], "sample-owner",
            new LegacyIdentityEvidence(evidence), default)).Bound);
        Assert.Throws<InvalidDataException>(() => ManifestStore.Reconcile(targetOwner, [legacy], [current], [foreign]));
    }

    [TestMethod]
    public async Task Missing_or_mismatched_evidence_never_binds_by_name()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        string metadata = Path.Combine(owner, "metadata", "OldName");
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        string path = Path.Combine(metadata, "repository.json");
        RepositoryDescriptor legacy = Repo(0, "OldName", "OldName");
        RepositoryDescriptor current = Repo(42, "OldName", "OldName");

        LegacyBinding missing = await LegacyIdentityBinder.TryBindAsync(owner, legacy, [current], "sample-owner", new(path), default);
        await File.WriteAllTextAsync(path, "{\"id\":99,\"full_name\":\"sample-owner/OldName\",\"owner\":{\"login\":\"sample-owner\"}}");
        LegacyBinding mismatch = await LegacyIdentityBinder.TryBindAsync(owner, legacy, [current], "sample-owner", new(path), default);
        IReadOnlyList<RepositoryDescriptor> mapped = ManifestStore.Reconcile(owner, [legacy], [current], [mismatch]);

        Assert.IsFalse(missing.Bound);
        Assert.IsFalse(mismatch.Bound);
        Assert.AreEqual("legacy-unresolved", mapped.Single(x => x.RepositoryId == 0).RemoteState);
        Assert.AreNotEqual("OldName", mapped.Single(x => x.RepositoryId == 42).LocalName, ignoreCase: true);
    }

    [TestMethod]
    public async Task Duplicate_id_and_broad_acl_evidence_remain_unresolved()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        string metadata = Path.Combine(owner, "metadata", "OldName");
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        string path = Path.Combine(metadata, "repository.json");
        await File.WriteAllTextAsync(path, "{\"id\":42,\"id\":42,\"full_name\":\"sample-owner/OldName\",\"owner\":{\"login\":\"sample-owner\"}}");
        RepositoryDescriptor legacy = Repo(0, "OldName", "OldName");
        RepositoryDescriptor current = Repo(42, "OldName", "OldName");

        LegacyBinding duplicate = await LegacyIdentityBinder.TryBindAsync(owner, legacy, [current], "sample-owner", new(path), default);
        await File.WriteAllTextAsync(path, "{\"id\":42,\"full_name\":\"sample-owner/OldName\",\"owner\":{\"login\":\"sample-owner\"}}");
        StorageTestRoot.Grant(metadata, System.Security.AccessControl.FileSystemRights.Read);
        LegacyBinding broad = await LegacyIdentityBinder.TryBindAsync(owner, legacy, [current], "sample-owner", new(path), default);

        Assert.IsFalse(duplicate.Bound);
        Assert.IsFalse(broad.Bound);
    }

    [TestMethod]
    public async Task Binding_reads_evidence_under_live_owner_lock()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        string metadata = Path.Combine(owner, "metadata", "OldName");
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        string path = Path.Combine(metadata, "repository.json");
        await File.WriteAllTextAsync(path, "{\"id\":42,\"full_name\":\"sample-owner/OldName\",\"owner\":{\"login\":\"sample-owner\"}}");
        using OperationLockLease operation = OperationLocks.AcquireStorage(owner, "run-1");

        LegacyBinding binding = await LegacyIdentityBinder.TryBindAsync(owner, Repo(0, "OldName", "OldName"),
            [Repo(42, "NewName", "NewName")], "sample-owner", new(path), default);

        Assert.IsTrue(binding.Bound);
    }

    [TestMethod]
    public async Task Owner_mismatch_or_ambiguous_current_id_does_not_bind()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        string metadata = Path.Combine(owner, "metadata", "OldName");
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        string path = Path.Combine(metadata, "repository.json");
        RepositoryDescriptor legacy = Repo(0, "OldName", "OldName");
        RepositoryDescriptor current = Repo(42, "NewName", "NewName");
        await File.WriteAllTextAsync(path, "{\"id\":42,\"full_name\":\"sample-owner/OldName\",\"owner\":{\"login\":\"other\"}}");

        Assert.IsFalse((await LegacyIdentityBinder.TryBindAsync(owner, legacy, [current], "sample-owner", new(path), default)).Bound);
        await File.WriteAllTextAsync(path, "{\"id\":42,\"full_name\":\"sample-owner/OldName\",\"owner\":{\"login\":\"sample-owner\"}}");
        Assert.IsFalse((await LegacyIdentityBinder.TryBindAsync(owner, legacy, [current, current], "sample-owner", new(path), default)).Bound);
    }

    [TestMethod]
    public async Task Legacy_full_name_must_identify_exact_prior_owner_and_name()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        string metadata = Path.Combine(owner, "metadata", "OldName");
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        string path = Path.Combine(metadata, "repository.json");
        await File.WriteAllTextAsync(path, "{\"id\":42,\"full_name\":\"sample-owner/OldName/extra\",\"owner\":{\"login\":\"sample-owner\"}}");
        RepositoryDescriptor malformed = Repo(0, "OldName", "OldName") with { NameWithOwner = "sample-owner/OldName/extra" };

        LegacyBinding binding = await LegacyIdentityBinder.TryBindAsync(owner, malformed,
            [Repo(42, "NewName", "NewName")], "sample-owner", new(path), default);

        Assert.IsFalse(binding.Bound);
    }

    [TestMethod]
    public async Task Reparse_metadata_directory_does_not_read_external_evidence()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        string metadata = Path.Combine(owner, "metadata");
        string external = root.Child("external");
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        AclPolicy.CreateRestrictedDirectory(external, root.User);
        string externalFile = Path.Combine(external, "repository.json");
        await File.WriteAllTextAsync(externalFile, "{\"id\":42,\"full_name\":\"sample-owner/OldName\",\"owner\":{\"login\":\"sample-owner\"}}");
        string junction = Path.Combine(metadata, "OldName");
        StorageTestRoot.CreateJunction(junction, external);
        try
        {
            LegacyBinding binding = await LegacyIdentityBinder.TryBindAsync(owner, Repo(0, "OldName", "OldName"),
                [Repo(42, "NewName", "NewName")], "sample-owner", new(Path.Combine(junction, "repository.json")), default);
            Assert.IsFalse(binding.Bound);
            Assert.HasCount(1, Directory.EnumerateFileSystemEntries(external).ToArray());
        }
        finally
        {
            using var handle = NativeFileSystem.Open(junction, NativeFileSystem.DeleteAccess, shareDelete: true);
            NativeFileSystem.DeleteByHandle(handle);
        }
    }
}
