using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

internal enum AppDataEntryKind { Directory, File }
internal sealed record AppDataPathValidation(bool Allowed, string CanonicalPath, string ErrorCode)
{
    internal NativeFileIdentity? Identity { get; init; }
}

internal static class AppDataPathPolicy
{
    internal static AppDataPathValidation Validate(AppPaths paths, string expectedPath, AppDataEntryKind kind)
    {
        string canonical = "";
        try
        {
            canonical = NativeFileSystem.CanonicalPath(expectedPath);
            using AppDataPathLease lease = Acquire(paths, canonical, kind);
            return new(true, canonical, "") { Identity = lease.Identity };
        }
        catch (PathBoundaryException ex) { return new(false, canonical, "APPDATA_PATH_" + ex.Code); }
        catch (FileNotFoundException) { return new(false, canonical, "APPDATA_PATH_MISSING"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return new(false, canonical, "APPDATA_PATH_INACCESSIBLE"); }
    }

    // Consumers retain this lease through I/O; the value-only Validate result is not an I/O capability.
    internal static AppDataPathLease Acquire(AppPaths paths, string expectedPath, AppDataEntryKind kind,
        bool createMissingDirectories = false, bool allowMissingFile = false)
    {
        string canonical = NativeFileSystem.CanonicalPath(expectedPath);
        string anchor = NativeFileSystem.CanonicalPath(paths.LocalAppDataAnchor);
        string appRoot = Path.Combine(anchor, "GitHubBackupTool");
        if (!string.Equals(paths.LocalAppDataRoot, appRoot, StringComparison.OrdinalIgnoreCase)
            || !(string.Equals(canonical, appRoot, StringComparison.OrdinalIgnoreCase)
                || canonical.StartsWith(appRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new PathBoundaryException("OUTSIDE_APPLICATION_ROOT");
        if (new DriveInfo(Path.GetPathRoot(anchor)!).DriveType != DriveType.Fixed)
            throw new PathBoundaryException("VOLUME_REJECTED");
        var lease = new AppDataPathLease();
        var user = WindowsIdentity.GetCurrent().User!;
        try
        {
            foreach (string segment in NativeFileSystem.Segments(canonical))
            {
                bool isFinal = string.Equals(segment, canonical, StringComparison.OrdinalIgnoreCase);
                bool directory = !isFinal || kind == AppDataEntryKind.Directory;
                bool appOwned = string.Equals(segment, appRoot, StringComparison.OrdinalIgnoreCase)
                    || segment.StartsWith(appRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                SafeFileHandle handle;
                try { handle = NativeFileSystem.Open(segment); }
                catch (FileNotFoundException) when (appOwned && directory && createMissingDirectories)
                {
                    AclPolicy.CreateRestrictedDirectory(segment, user);
                    handle = NativeFileSystem.Open(segment);
                }
                catch (FileNotFoundException) when (appOwned && isFinal && !directory && allowMissingFile)
                { return lease; }
                lease.Add(handle);
                NativeFileIdentity identity = NativeFileSystem.Inspect(handle, segment, directory);
                if (appOwned) CheckPrivateDescriptor(AclPolicy.ReadDescriptor(handle), user);
                // A second independently opened no-follow handle binds pathname and identity.
                using SafeFileHandle comparison = NativeFileSystem.Open(segment);
                if (NativeFileSystem.Inspect(comparison, segment, directory) != identity)
                    throw new PathBoundaryException("IDENTITY_CHANGED");
                if (isFinal) lease.Identity = identity;
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static void CheckPrivateDescriptor(AclDescriptor descriptor, SecurityIdentifier user)
    {
        if (!descriptor.DaclPresent || descriptor.IsNullDacl) throw new PathBoundaryException("NULL_DACL_UNSAFE");
        if (!AclPolicy.IsApproved(descriptor.OwnerSid, user)) throw new PathBoundaryException("OWNER_UNSAFE");
        AclEntry[] foreign = descriptor.Entries.Where(e => e.AccessControlType == AccessControlType.Allow && !AclPolicy.IsApproved(e.Sid, user)).ToArray();
        const FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        if (foreign.Any(e => (e.Rights & write) != 0 || ((uint)e.Rights & 0x50000000) != 0)) throw new PathBoundaryException("WRITE_ACL_UNSAFE");
        if (foreign.Any(e => e.Rights != 0)) throw new PathBoundaryException("READ_ACL_UNSAFE");
        if (!descriptor.AreAccessRulesProtected) throw new PathBoundaryException("INHERITED_ACL_UNSAFE");
    }
}

internal sealed class AppDataPathLease : IDisposable
{
    private readonly PathLease handles = new();
    internal NativeFileIdentity? Identity { get; set; }
    internal void Add(SafeFileHandle handle) => handles.Add(handle);
    public void Dispose() => handles.Dispose();
}
