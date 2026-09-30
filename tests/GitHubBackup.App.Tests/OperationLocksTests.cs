using System.Diagnostics;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class OperationLocksTests
{
    [TestMethod]
    public void Owner_and_repository_handles_exclude_another_operation_and_keep_unknown_files()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, ".locks"), root.User);
        string unknown = Path.Combine(owner, ".locks", "unknown.lock");
        File.WriteAllText(unknown, "leave me");

        using (OperationLockLease first = OperationLocks.AcquireStorage(owner, "run-1"))
        {
            Assert.Throws<IOException>(() => OperationLocks.AcquireStorage(owner, "run-2"));
            using OperationLockLease repository = OperationLocks.AcquireRepository(first, "repo", "run-1");
            Assert.Throws<IOException>(() => OperationLocks.AcquireRepository(first, "repo", "run-2"));
        }
        using OperationLockLease next = OperationLocks.AcquireStorage(owner, "run-3");
        Assert.AreEqual("leave me", File.ReadAllText(unknown));
    }

    [TestMethod]
    public async Task Process_death_releases_exclusive_owner_handle_without_deleting_lock_file()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(owner, root.User);
        using (OperationLocks.AcquireStorage(owner, "initial")) { }
        string lockPath = Path.Combine(owner, ".locks", "owner.lock");
        string signal = root.Child("holder-ready");
        string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        string script = "$f=[IO.File]::Open(" + Quote(lockPath)
            + ",[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);"
            + "[IO.File]::WriteAllText(" + Quote(signal) + ",'ready');Start-Sleep -Seconds 60";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        using Process child = Process.Start(start)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(signal)) await Task.Delay(25, deadline.Token);
            Assert.Throws<IOException>(() => OperationLocks.AcquireStorage(owner, "contended"));
            child.Kill();
            Assert.IsTrue(child.WaitForExit(10000));
            using OperationLockLease recovered = OperationLocks.AcquireStorage(owner, "recovered");
            Assert.IsTrue(File.Exists(lockPath));
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            child.WaitForExit(10000);
        }
    }

    [TestMethod]
    public async Task Second_process_cannot_acquire_repository_lock_while_file_is_owned()
    {
        using var root = new StorageTestRoot();
        string ownerRoot = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(ownerRoot, root.User);
        using OperationLockLease owner = OperationLocks.AcquireStorage(ownerRoot, "run");
        using (OperationLocks.AcquireRepository(owner, "repo", "run")) { }
        string lockPath = Path.Combine(ownerRoot, ".locks", "repository-repo.lock");
        string signal = root.Child("repository-holder-ready");
        string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        string script = "$f=[IO.File]::Open(" + Quote(lockPath)
            + ",[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);"
            + "[IO.File]::WriteAllText(" + Quote(signal) + ",'ready');Start-Sleep -Seconds 60";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        using Process child = Process.Start(start)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(signal)) await Task.Delay(25, deadline.Token);
            Assert.Throws<IOException>(() => OperationLocks.AcquireRepository(owner, "repo", "contended"));
            child.Kill();
            Assert.IsTrue(child.WaitForExit(10000));
            using OperationLockLease recovered = OperationLocks.AcquireRepository(owner, "repo", "recovered");
            Assert.IsTrue(File.Exists(lockPath));
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            child.WaitForExit(10000);
        }
    }

    [TestMethod]
    public void Junction_lock_directory_is_rejected_without_touching_external_target()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        string external = root.Child("external");
        AclPolicy.CreateRestrictedDirectory(owner, root.User);
        AclPolicy.CreateRestrictedDirectory(external, root.User);
        string sentinel = Path.Combine(external, "sentinel");
        File.WriteAllText(sentinel, "untouched");
        string junction = Path.Combine(owner, ".locks");
        StorageTestRoot.CreateJunction(junction, external);
        try
        {
            Assert.Throws<IOException>(() => OperationLocks.AcquireStorage(owner, "run"));
            Assert.AreEqual("untouched", File.ReadAllText(sentinel));
            Assert.HasCount(1, Directory.EnumerateFileSystemEntries(external).ToArray());
        }
        finally
        {
            using var handle = NativeFileSystem.Open(junction, NativeFileSystem.DeleteAccess, shareDelete: true);
            NativeFileSystem.DeleteByHandle(handle);
        }
    }

    [TestMethod]
    public void Broad_write_acl_lock_directory_is_not_repaired()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        string locks = Path.Combine(owner, ".locks");
        AclPolicy.CreateRestrictedDirectory(locks, root.User);
        StorageTestRoot.Grant(locks, System.Security.AccessControl.FileSystemRights.Write);

        Assert.Throws<PathBoundaryException>(() => OperationLocks.AcquireStorage(owner, "run"));
        Assert.IsFalse(File.Exists(Path.Combine(locks, "owner.lock")));
    }

    [TestMethod]
    public void Path_alias_and_hardlinked_lock_file_are_rejected_without_external_write()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, ".locks"), root.User);
        Assert.Throws<PathBoundaryException>(() => OperationLocks.AcquireStorage(Path.Combine(owner, "."), "run"));
        string external = root.Child("external-file");
        File.WriteAllText(external, "untouched");
        string lockPath = Path.Combine(owner, ".locks", "owner.lock");
        StorageTestRoot.CreateHardLink(lockPath, external);
        try
        {
            Assert.Throws<PathBoundaryException>(() => OperationLocks.AcquireStorage(owner, "run"));
            Assert.AreEqual("untouched", File.ReadAllText(external));
        }
        finally { File.Delete(lockPath); }
    }

    [TestMethod]
    public void Full_source_audit_succeeds_with_held_owner_lock_and_still_checks_unknown_files()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(owner, root.User);
        using OperationLockLease operation = OperationLocks.AcquireStorage(owner, "run");

        SourceIntegrityResult result = new SourceIntegrityAudit().ValidateExistingTrees(owner, [], true, operation);

        Assert.IsTrue(result.Allowed);
        string unknown = Path.Combine(owner, ".locks", "unknown.lock");
        File.WriteAllText(unknown, "unknown");
        StorageTestRoot.Grant(Path.Combine(owner, ".locks"), System.Security.AccessControl.FileSystemRights.Read);
        Assert.IsFalse(new SourceIntegrityAudit().ValidateExistingTrees(owner, [], true, operation).Allowed);
    }

    [TestMethod]
    public void Repository_lock_is_bound_to_live_owner_lease()
    {
        using var root = new StorageTestRoot();
        string firstOwner = root.Child("first-owner");
        string otherOwner = root.Child("other-owner");
        AclPolicy.CreateRestrictedDirectory(firstOwner, root.User);
        AclPolicy.CreateRestrictedDirectory(otherOwner, root.User);
        Assert.ThrowsExactly<ArgumentNullException>(() => OperationLocks.AcquireRepository(null!, "repo", "run"));
        OperationLockLease owner = OperationLocks.AcquireStorage(firstOwner, "run");
        using (owner)
        using (OperationLockLease repository = OperationLocks.AcquireRepository(owner, "repo", "run"))
        {
            Assert.IsTrue(File.Exists(Path.Combine(firstOwner, ".locks", "repository-repo.lock")));
            Assert.IsFalse(Directory.Exists(Path.Combine(otherOwner, ".locks")));
            Assert.ThrowsExactly<ArgumentException>(() =>
                new SourceIntegrityAudit().ValidateExistingTrees(otherOwner, [], true, owner));
        }
        Assert.ThrowsExactly<ObjectDisposedException>(() => OperationLocks.AcquireRepository(owner, "other", "run"));
    }
}
