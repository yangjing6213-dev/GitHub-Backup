using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

// One lease for backup and restore; Windows releases the exclusive handle when its process dies.
internal sealed class OperationLockLease(PathLease directories, SafeFileHandle handle,
    string ownerRoot, string lockPath, NativeFileIdentity identity, bool isOwner, OperationLockLease? ownerLease = null) : IDisposable
{
    private bool disposed;

    internal void RequireRepository(OperationLockLease owner,string localName)
    {
        if(disposed||handle.IsClosed) throw new ObjectDisposedException(nameof(OperationLockLease));
        string root=owner.RequireOwnerRoot();
        if(isOwner||!ReferenceEquals(owner,ownerLease)||!RepositoryNameMapper.IsSafeLocalName(localName)
            ||!string.Equals(lockPath,Path.Combine(root,StorageLayout.LockDirectoryName,"repository-"+localName+".lock"),StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("REPOSITORY_LOCK_MISMATCH");
        if(NativeFileSystem.Inspect(handle,lockPath,false)!=identity) throw new PathBoundaryException("LOCK_PATH_IDENTITY_CHANGED");
    }

    internal string RequireOwnerRoot()
    {
        if (disposed || handle.IsClosed) throw new ObjectDisposedException(nameof(OperationLockLease));
        if (!isOwner) throw new ArgumentException("OWNER_LOCK_REQUIRED");
        if (NativeFileSystem.Inspect(handle, lockPath, directory: false) != identity)
            throw new PathBoundaryException("LOCK_PATH_IDENTITY_CHANGED");
        return ownerRoot;
    }

    internal SafeFileHandle? BorrowOwnerHandle(string path) =>
        string.Equals(NativeFileSystem.CanonicalPath(path), lockPath, StringComparison.OrdinalIgnoreCase)
            ? RequireOwnerHandle() : null;

    private SafeFileHandle RequireOwnerHandle()
    {
        _ = RequireOwnerRoot();
        return handle;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        handle.Dispose();
        directories.Dispose();
    }
}

internal static class OperationLocks
{
    // History must not create a backup root, lock file, or change any existing ACL.
    internal static OperationLockLease AcquireExistingStorage(string ownerRoot)
    {
        string root = NativeFileSystem.CanonicalPath(ownerRoot);
        string directory = Path.Combine(root, StorageLayout.LockDirectoryName);
        string path = Path.Combine(directory, "owner.lock");
        PathLease directories = SummaryStore.RequirePrivateDirectory(directory);
        SafeFileHandle? handle = null;
        try
        {
            handle = NativeFileSystem.Open(path, shareWrite: false, shareRead: false);
            var identity = NativeFileSystem.Inspect(handle, path, false);
            if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(handle), WindowsIdentity.GetCurrent().User!, true) != AclRisk.Safe)
                throw new UnauthorizedAccessException("LOCK_PATH_UNSAFE");
            return new(directories, handle, root, path, identity, true);
        }
        catch { handle?.Dispose(); directories.Dispose(); throw; }
    }

    internal static OperationLockLease AcquireStorage(string ownerRoot, string runId) =>
        Acquire(ownerRoot, "owner.lock", runId, ownerLease: null);

    internal static OperationLockLease AcquireRepository(OperationLockLease ownerLease, string localName, string runId)
    {
        ArgumentNullException.ThrowIfNull(ownerLease);
        if (!RepositoryNameMapper.IsSafeLocalName(localName)) throw new ArgumentException("LOCK_REPOSITORY_NAME_INVALID");
        return Acquire(ownerLease.RequireOwnerRoot(), "repository-" + localName + ".lock", runId, ownerLease);
    }

    private static OperationLockLease Acquire(string ownerRoot, string fileName, string runId,
        OperationLockLease? ownerLease)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Length > 100
            || runId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("LOCK_RUN_ID_INVALID");
        string owner = NativeFileSystem.CanonicalPath(ownerRoot);
        if (!string.Equals(Path.TrimEndingDirectorySeparator(ownerRoot), owner, StringComparison.OrdinalIgnoreCase))
            throw new PathBoundaryException("LOCK_PATH_ALIAS_REJECTED");
        string directory = NativeFileSystem.CanonicalPath(Path.Combine(owner, StorageLayout.LockDirectoryName));
        string path = NativeFileSystem.CanonicalPath(Path.Combine(directory, fileName));
        if (!directory.StartsWith(owner + "\\", StringComparison.OrdinalIgnoreCase)
            || !path.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase))
            throw new PathBoundaryException("LOCK_PATH_OUTSIDE_OWNER");

        using PathLease rootLease = SummaryStore.RequirePrivateDirectory(owner);
        if (ownerLease is null && !new SourceIntegrityAudit().ValidateExistingTrees(owner, [], true).Allowed)
            throw new PathBoundaryException("LOCK_PATH_UNSAFE");
        AclPolicy.CreateRestrictedDirectory(directory, WindowsIdentity.GetCurrent().User!);
        PathLease directories = SummaryStore.RequirePrivateDirectory(directory);
        SafeFileHandle? handle = null;
        try
        {
            handle = AclPolicy.OpenOrCreateExclusiveRestrictedFile(path, WindowsIdentity.GetCurrent().User!);
            NativeFileIdentity identity = NativeFileSystem.Inspect(handle, path, directory: false);
            var operation = new OperationLockLease(directories, handle, owner, path, identity, ownerLease is null,ownerLease);
            if (ownerLease is null && !new SourceIntegrityAudit().ValidateExistingTrees(owner, [], true, operation).Allowed)
            {
                operation.Dispose();
                throw new PathBoundaryException("LOCK_PATH_UNSAFE");
            }
            return operation;
        }
        catch { handle?.Dispose(); directories.Dispose(); throw; }
    }
}
