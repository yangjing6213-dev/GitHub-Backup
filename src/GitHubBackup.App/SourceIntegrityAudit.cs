using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;
namespace GitHubBackup.App;
internal sealed record SensitivePathAssessment(string FullPath, AclRisk Risk, string ErrorCode);
internal sealed record SourceIntegrityResult(bool Allowed, IReadOnlyList<SensitivePathAssessment> UnsafePaths);
internal sealed class SourceIntegrityAudit(Func<string,SafeFileHandle,AclDescriptor>? readDescriptor = null)
{
    internal SourceIntegrityResult ValidateExistingTrees(string ownerRoot, IReadOnlyList<string> candidateRoots,
        bool requireRestrictedRead, OperationLockLease? heldOwnerLock = null)
    {
        string owner = NativeFileSystem.CanonicalPath(ownerRoot);
        if (heldOwnerLock is not null
            && !string.Equals(heldOwnerLock.RequireOwnerRoot(), owner, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SOURCE_OWNER_LOCK_MISMATCH");
        // Historical on-disk names are always covered, even when discovery yields
        // zero current repositories. Candidate names never authorize an escape.
        foreach (string candidate in candidateRoots)
        {
            string path = NativeFileSystem.CanonicalPath(candidate);
            if (!path.Equals(owner, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(owner + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("SOURCE_PATH_OUTSIDE_OWNER");
        }
        var results = new Dictionary<string,SensitivePathAssessment>(StringComparer.OrdinalIgnoreCase);
        WalkRoot(owner, true, requireRestrictedRead, results, heldOwnerLock);
        return Result(results);
    }
    internal SourceIntegrityResult ValidateBackupRoot(string backupRoot)
    {
        string path = NativeFileSystem.CanonicalPath(backupRoot);
        var results = new Dictionary<string,SensitivePathAssessment>(StringComparer.OrdinalIgnoreCase);
        WalkRoot(path, false, true, results, null);
        return Result(results);
    }
    private void WalkRoot(string root, bool recursive, bool requireRead,
        Dictionary<string,SensitivePathAssessment> results, OperationLockLease? heldOwnerLock)
    {
        using var boundary = new PathLease();
        foreach (string segment in NativeFileSystem.Segments(root))
        {
            try
            {
                var handle = NativeFileSystem.Open(segment); boundary.Add(handle);
                NativeFileSystem.Inspect(handle,segment,true);
            }
            catch (FileNotFoundException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                string defect = ex is PathBoundaryException invalid ? invalid.Code : "INSPECTION_FAILED";
                results[segment] = new(segment,AclRisk.Block,Category(root,segment) + "_" + defect); return;
            }
        }
        Walk(root,root,recursive,false,requireRead,results,heldOwnerLock);
    }
    private static SourceIntegrityResult Result(Dictionary<string,SensitivePathAssessment> results) => new(results.Count == 0,
        Array.AsReadOnly(results.Values.OrderBy(x => x.FullPath,StringComparer.OrdinalIgnoreCase).ToArray()));
    private void Walk(string path, string owner, bool recursive, bool allowMissing, bool requireRead,
        Dictionary<string,SensitivePathAssessment> results, OperationLockLease? heldOwnerLock)
    {
        string prefix = Category(owner,path);
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception ex) when (allowMissing && ex is FileNotFoundException or DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) { Add("REPARSE_POINT_REJECTED"); return; }
            using var parents = NativeFileSystem.PinDirectories(Path.GetDirectoryName(path)!);
            SafeFileHandle? borrowed = heldOwnerLock?.BorrowOwnerHandle(path);
            using SafeFileHandle? owned = borrowed is null ? NativeFileSystem.Open(path) : null;
            SafeFileHandle handle = borrowed ?? owned!;
            bool directory = (attributes & FileAttributes.Directory) != 0;
            NativeFileIdentity identity = NativeFileSystem.Inspect(handle,path,directory);
            Assess();
            if (recursive && directory)
                foreach (string child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.OrdinalIgnoreCase))
                    Walk(NativeFileSystem.CanonicalPath(child),owner,true,false,requireRead,results,heldOwnerLock);
            if (NativeFileSystem.Inspect(handle,path,directory) != identity) Add("IDENTITY_CHANGED");
            else Assess();
            void Assess()
            {
                AclDescriptor descriptor = readDescriptor is null ? AclPolicy.ReadDescriptor(handle) : readDescriptor(path,handle);
                var user = WindowsIdentity.GetCurrent().User!;
                if (!AclPolicy.IsApproved(descriptor.OwnerSid,user)) { Add("OWNER_UNSAFE"); return; }
                if (!descriptor.DaclPresent || descriptor.IsNullDacl) { Add("NULL_DACL_UNSAFE"); return; }
                var unapproved = descriptor.Entries.Where(e => e.AccessControlType == AccessControlType.Allow && !AclPolicy.IsApproved(e.Sid,user)).ToArray();
                const FileSystemRights read = FileSystemRights.ReadData | FileSystemRights.ReadExtendedAttributes | FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions | FileSystemRights.ExecuteFile;
                if (requireRead && unapproved.Any(e => (e.Rights & read) != 0)) { Add("READ_ACL_UNSAFE"); return; }
                if (unapproved.Any(e => (e.Rights & ~read) != 0 || (!requireRead && (e.Rights & (FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0))) Add("WRITE_ACL_UNSAFE");
            }
        }
        catch (PathBoundaryException ex) { Add(ex.Code); }
        catch (FileNotFoundException) { Add("IDENTITY_CHANGED"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception) { Add("INSPECTION_FAILED"); }
        void Add(string defect)
        {
            var assessment = new SensitivePathAssessment(path,AclRisk.Block,prefix + "_" + defect);
            if (!results.TryGetValue(path,out var existing) || Rank(assessment.ErrorCode) < Rank(existing.ErrorCode)) results[path] = assessment;
        }
    }
    private static int Rank(string code) => code.EndsWith("OWNER_UNSAFE",StringComparison.Ordinal) ? 1 : code.EndsWith("NULL_DACL_UNSAFE",StringComparison.Ordinal) ? 2 : code.EndsWith("READ_ACL_UNSAFE",StringComparison.Ordinal) ? 3 : code.EndsWith("WRITE_ACL_UNSAFE",StringComparison.Ordinal) ? 4 : 0;
    private static string Category(string owner, string path)
    {
        string relative = Path.GetRelativePath(owner,path);
        string first = relative.Split('\\')[0].ToLowerInvariant();
        return first switch { ".locks" => "LOCK_PATH", "manifests" => "MANIFEST", "mirrors" or "wikis" => "MIRROR_SOURCE", "metadata" => "METADATA_SOURCE", "releases" => "RELEASE_SOURCE", _ => "PREFLIGHT_PRIVATE" };
    }
}
