using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class RepositoryNameMapperTests
{
    private static RepositoryDescriptor Repo(long id, string name, string? localName = null) =>
        new(id, name, "sample-owner/" + name, "https://github.com/sample-owner/" + name,
            false, false, false, true, null, 0, localName ?? name, "active");

    [TestMethod]
    public void Rename_preserves_name_by_id_and_deleted_id_blocks_name_reuse()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(owner, root.User);
        RepositoryDescriptor historical = Repo(42, "old", "old");
        RepositoryDescriptor deleted = Repo(7, "reused", "reused") with { RemoteState = "deleted" };

        IReadOnlyList<RepositoryDescriptor> result = ManifestStore.Reconcile(owner,
            [historical, deleted], [Repo(42, "new"), Repo(8, "reused")], []);

        Assert.AreEqual("old", result.Single(r => r.RepositoryId == 42).LocalName);
        Assert.AreEqual("sample-owner/new", result.Single(r => r.RepositoryId == 42).NameWithOwner);
        Assert.AreEqual("deleted", result.Single(r => r.RepositoryId == 7).RemoteState);
        Assert.AreEqual("reused", result.Single(r => r.RepositoryId == 7).LocalName);
        StringAssert.Matches(result.Single(r => r.RepositoryId == 8).LocalName, new System.Text.RegularExpressions.Regex("^reused-[0-9a-f]{8}$"));
        Assert.IsFalse(Directory.Exists(Path.Combine(owner, "manifests")));
    }

    [TestMethod]
    public void Unknown_children_in_each_tree_reserve_case_insensitive_local_names()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        foreach ((string tree, string child) in new[]
        {
            ("mirrors", "Mirror.git"), ("wikis", "Wiki.wiki.git"),
            ("metadata", "Meta"), ("releases", "Release")
        }) AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, tree, child), root.User);

        IReadOnlyList<RepositoryDescriptor> result = ManifestStore.Reconcile(owner, [],
            [Repo(1, "mirror"), Repo(2, "wiki"), Repo(3, "meta"), Repo(4, "release")], []);

        foreach (RepositoryDescriptor mapped in result)
            Assert.AreNotEqual(mapped.Name, mapped.LocalName, ignoreCase: true);
        Assert.IsFalse(Directory.Exists(Path.Combine(owner, "manifests")));
    }

    [TestMethod]
    [DataRow(1L, "CON")]
    [DataRow(2L, "NUL")]
    [DataRow(3L, "repo.")]
    [DataRow(4L, "repo ")]
    [DataRow(5L, "a:b")]
    [DataRow(6L, "-repo")]
    [DataRow(7L, "CON.txt")]
    [DataRow(8L, "NUL.repo")]
    [DataRow(9L, "COM1.foo")]
    public void Unsafe_windows_name_gets_deterministic_id_hash(long id, string name)
    {
        string first = RepositoryNameMapper.MapNew(id, name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        string second = RepositoryNameMapper.MapNew(id, name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.AreEqual(first, second);
        Assert.IsTrue(RepositoryNameMapper.IsSafeLocalName(first));
        StringAssert.Matches(first, new System.Text.RegularExpressions.Regex("^[A-Za-z0-9._-]+-[0-9a-f]{8}$"));
    }

    [TestMethod]
    public void Long_stem_can_extend_hash_after_first_candidate_is_reserved()
    {
        string stem = new('a', 90);
        Assert.AreEqual(90, stem.Length);
        string first = RepositoryNameMapper.MapNew(42, stem, new HashSet<string>(), forceHash: true);
        string second = RepositoryNameMapper.MapNew(42, stem,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first }, forceHash: true);

        Assert.IsTrue(RepositoryNameMapper.IsSafeLocalName(second));
        Assert.AreNotEqual(first, second);
        Assert.IsLessThanOrEqualTo(second.Length, 100);
        StringAssert.Matches(second, new System.Text.RegularExpressions.Regex("-[0-9a-f]{16}$"));
        Assert.AreEqual(second, RepositoryNameMapper.MapNew(42, stem,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first }, forceHash: true));
    }

    [TestMethod]
    public void Same_case_insensitive_name_proposals_never_share_a_path()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(owner, root.User);

        IReadOnlyList<RepositoryDescriptor> result = ManifestStore.Reconcile(owner, [],
            [Repo(1, "Foo"), Repo(2, "foo")], []);

        Assert.AreNotEqual(result[0].LocalName, result[1].LocalName, ignoreCase: true);
    }

    [TestMethod]
    public void Simultaneous_case_collisions_map_the_same_by_id_regardless_of_discovery_order()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(owner, root.User);
        RepositoryDescriptor upper = Repo(1, "Foo");
        RepositoryDescriptor lower = Repo(2, "foo");

        IReadOnlyList<RepositoryDescriptor> forward = ManifestStore.Reconcile(owner, [], [upper, lower], []);
        IReadOnlyList<RepositoryDescriptor> reverse = ManifestStore.Reconcile(owner, [], [lower, upper], []);

        Assert.AreEqual(forward.Single(x => x.RepositoryId == 1).LocalName,
            reverse.Single(x => x.RepositoryId == 1).LocalName);
        Assert.AreEqual(forward.Single(x => x.RepositoryId == 2).LocalName,
            reverse.Single(x => x.RepositoryId == 2).LocalName);
    }

    [TestMethod]
    public void Reconciliation_reads_history_while_same_process_holds_owner_lock()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(owner, root.User);
        using OperationLockLease operation = OperationLocks.AcquireStorage(owner, "run-1");

        IReadOnlyList<RepositoryDescriptor> result = ManifestStore.Reconcile(owner, [], [Repo(42, "repo")], []);

        Assert.AreEqual("repo", result.Single().LocalName);
    }

    [TestMethod]
    public void Unsafe_unknown_tree_blocks_reconciliation_without_creating_manifest()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        string metadata = Path.Combine(owner, "metadata");
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        StorageTestRoot.Grant(metadata, System.Security.AccessControl.FileSystemRights.Write);

        Assert.Throws<UnauthorizedAccessException>(() => ManifestStore.Reconcile(owner, [], [Repo(1, "repo")], []));
        Assert.IsFalse(Directory.Exists(Path.Combine(owner, "manifests")));
    }
}
