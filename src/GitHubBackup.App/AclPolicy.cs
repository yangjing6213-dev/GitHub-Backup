using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

internal enum AclRisk { Safe, Warning, Block }
internal sealed record AclEntry(SecurityIdentifier Sid, FileSystemRights Rights, AccessControlType AccessControlType);
internal sealed record AclDescriptor(SecurityIdentifier? OwnerSid, bool DaclPresent, bool IsNullDacl,
    bool AreAccessRulesProtected, IReadOnlyList<AclEntry> Entries);

internal static class AclPolicy
{
    internal static bool IsApproved(SecurityIdentifier? sid, SecurityIdentifier currentUser) =>
        sid is not null && (sid.Equals(currentUser) || sid.Value is "S-1-5-18" or "S-1-5-32-544");

    internal static AclRisk Evaluate(AclDescriptor descriptor, SecurityIdentifier currentUser, bool hasPrivateRepositories) =>
        descriptor.DaclPresent && !descriptor.IsNullDacl
        && IsApproved(descriptor.OwnerSid, currentUser)
        && !descriptor.Entries.Any(e => e.AccessControlType == AccessControlType.Allow && e.Rights != 0 && !IsApproved(e.Sid, currentUser))
            ? AclRisk.Safe : AclRisk.Block;

    internal static AclDescriptor ReadDescriptor(string path)
    {
        string canonical = NativeFileSystem.CanonicalPath(path);
        using PathLease parents = NativeFileSystem.PinDirectories(Path.GetDirectoryName(canonical)!);
        using SafeFileHandle handle = NativeFileSystem.Open(canonical);
        NativeFileSystem.Inspect(handle, canonical);
        return ReadDescriptor(handle);
    }

    internal static AclDescriptor ReadDescriptor(SafeFileHandle handle)
    {
        RawSecurityDescriptor raw = NativeFileSystem.ReadSecurity(handle);
        var entries = new List<AclEntry>();
        if (raw.DiscretionaryAcl is not null)
        {
            foreach (GenericAce ace in raw.DiscretionaryAcl)
            {
                if ((ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
                if (ace is CommonAce common && !common.IsCallback && common.AceQualifier is AceQualifier.AccessAllowed or AceQualifier.AccessDenied)
                    entries.Add(new(common.SecurityIdentifier, (FileSystemRights)common.AccessMask,
                        common.AceQualifier == AceQualifier.AccessAllowed ? AccessControlType.Allow : AccessControlType.Deny));
                else
                    // Conditional/object/unknown ACEs cannot establish this strict private boundary.
                    entries.Add(new(new("S-1-1-0"), FileSystemRights.FullControl, AccessControlType.Allow));
            }
        }
        return new(raw.Owner, (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0,
            raw.DiscretionaryAcl is null, (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, entries);
    }

    internal static void CreateRestrictedDirectory(string path, SecurityIdentifier currentUser, bool requireNew = false)
    {
        string canonical = NativeFileSystem.CanonicalPath(path);
        using var lease = new PathLease();
        foreach (string segment in NativeFileSystem.Segments(canonical))
        {
            SafeFileHandle handle;
            bool created = false;
            try { handle = NativeFileSystem.Open(segment); }
            catch (FileNotFoundException)
            {
                NativeFileSystem.CreateDirectory(segment, RestrictedDescriptor(currentUser, directory: true));
                created = true;
                handle = NativeFileSystem.Open(segment);
            }
            lease.Add(handle);
            NativeFileSystem.Inspect(handle, segment, directory: true);
            if (requireNew && !created && string.Equals(segment, canonical, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The owned directory must be newly created.");
            if (created || string.Equals(segment, canonical, StringComparison.OrdinalIgnoreCase)) VerifyRestricted(handle, currentUser);
        }
    }

    internal static FileStream CreateRestrictedFile(string path, SecurityIdentifier currentUser)
    {
        string canonical = NativeFileSystem.CanonicalPath(path);
        using PathLease parents = NativeFileSystem.PinDirectories(Path.GetDirectoryName(canonical)!);
        SafeFileHandle handle = NativeFileSystem.CreateRestrictedFile(canonical, RestrictedDescriptor(currentUser, directory: false));
        try
        {
            NativeFileSystem.Inspect(handle, canonical, directory: false);
            VerifyRestricted(handle, currentUser);
            return new FileStream(handle, FileAccess.ReadWrite);
        }
        catch { handle.Dispose(); throw; }
    }

    internal static SafeFileHandle OpenOrCreateExclusiveRestrictedFile(string path, SecurityIdentifier currentUser)
    {
        string canonical = NativeFileSystem.CanonicalPath(path);
        using PathLease parents = NativeFileSystem.PinDirectories(Path.GetDirectoryName(canonical)!);
        SafeFileHandle handle = NativeFileSystem.OpenOrCreateExclusiveRestrictedFile(canonical,
            RestrictedDescriptor(currentUser, directory: false));
        try
        {
            NativeFileSystem.Inspect(handle, canonical, directory: false);
            VerifyRestricted(handle, currentUser);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void HardenExisting(string path, SecurityIdentifier currentUser, IReadOnlyCollection<string> confirmedPaths)
    {
        string canonical = NativeFileSystem.CanonicalPath(path);
        if (!confirmedPaths.Any(p => string.Equals(NativeFileSystem.CanonicalPath(p), canonical, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("ACL_REPAIR_CONFIRMATION_REQUIRED");
        using PathLease parents = NativeFileSystem.PinDirectories(Path.GetDirectoryName(canonical)!);
        using SafeFileHandle handle = NativeFileSystem.Open(canonical,
            NativeFileSystem.ReadControl | NativeFileSystem.ReadAttributes | NativeFileSystem.WriteDac | NativeFileSystem.WriteOwner);
        NativeFileSystem.Inspect(handle, canonical);
        bool directory = (File.GetAttributes(canonical) & FileAttributes.Directory) != 0;
        NativeFileSystem.SetSecurity(handle, RestrictedDescriptor(currentUser, directory));
        NativeFileSystem.Inspect(handle, canonical, directory);
        VerifyRestricted(handle, currentUser);
    }

    internal static void VerifyRestricted(SafeFileHandle handle, SecurityIdentifier user)
    {
        AclDescriptor descriptor = ReadDescriptor(handle);
        if (!descriptor.AreAccessRulesProtected || Evaluate(descriptor, user, true) != AclRisk.Safe)
            throw new UnauthorizedAccessException("ACL_PRIVATE_BOUNDARY_REQUIRED");
    }

    private static byte[] RestrictedDescriptor(SecurityIdentifier user, bool directory)
    {
        // Protected ACL and explicit owner are passed to the native CREATE operation.
        string inheritance = directory ? "OICI" : "";
        var descriptor = new RawSecurityDescriptor($"O:{user.Value}D:P(A;{inheritance};FA;;;{user.Value})(A;{inheritance};FA;;;SY)(A;{inheritance};FA;;;BA)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }
}
