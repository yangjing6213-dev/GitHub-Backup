using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

internal static class ExecutableTrust
{
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    internal static ExecutableIdentity CaptureTrustedIdentity(string absolutePath)
    {
        using var lease = Acquire(absolutePath, null);
        return lease.Identity;
    }
    internal static void ValidateForLaunch(string absolutePath, ExecutableIdentity expectedIdentity)
    { using var lease = Acquire(absolutePath, expectedIdentity); }

    // Keep all no-follow handles, including a no-write/no-delete executable handle,
    // alive across CreateProcess. A value-only identity check cannot pin an image.
    internal static ExecutableLease Acquire(string absolutePath, ExecutableIdentity? expected)
    {
        string canonical = NativeFileSystem.CanonicalPath(absolutePath);
        if (!string.Equals(canonical, absolutePath, StringComparison.OrdinalIgnoreCase)
            || new DriveInfo(Path.GetPathRoot(canonical)!).DriveType != DriveType.Fixed)
            throw new IOException("PROCESS_EXECUTABLE_PATH_REJECTED");
        var lease = new ExecutableLease();
        try
        {
            string installation = Path.GetDirectoryName(canonical)!;
            foreach (string segment in NativeFileSystem.Segments(canonical))
            {
                bool file = string.Equals(segment, canonical, StringComparison.OrdinalIgnoreCase);
                SafeFileHandle handle = file ? OpenImage(segment) : NativeFileSystem.Open(segment);
                lease.Handles.Add(handle);
                NativeFileIdentity id = file ? NativeFileSystem.InspectExecutable(handle, segment) : NativeFileSystem.Inspect(handle, segment, true);
                CheckDescriptor(NativeFileSystem.ReadSecurity(handle), file || string.Equals(segment, installation, StringComparison.OrdinalIgnoreCase));
                if (file)
                    lease.Identity = new(id.VolumeSerialNumber, id.FileIndex, RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle).Ticks);
            }
            if (expected is not null && expected != lease.Identity) throw new IOException("PROCESS_EXECUTABLE_IDENTITY_CHANGED");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static void CheckDescriptor(RawSecurityDescriptor descriptor, bool installationOrFile)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        bool Approved(SecurityIdentifier? sid) => AclPolicy.IsApproved(sid, user) || sid?.Value == TrustedInstaller;
        if (!Approved(descriptor.Owner) || descriptor.DiscretionaryAcl is null
            || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0)
            throw new UnauthorizedAccessException("PROCESS_EXECUTABLE_ACL_REJECTED");
        const uint mutations = 0x000D0152; // DELETE, WRITE_DAC, WRITE_OWNER, write data/EA/attributes, DELETE_CHILD
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if ((ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (ace is not CommonAce common || common.IsCallback || common.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                throw new UnauthorizedAccessException("PROCESS_EXECUTABLE_ACL_REJECTED");
            if (common.AceQualifier != AceQualifier.AccessAllowed || Approved(common.SecurityIdentifier)) continue;
            uint mask = (uint)common.AccessMask;
            // Ordinary ancestors can grant this-folder-only AddSubdirectory; inherited
            // mutation is checked again on the selected next segment. Read is normal.
            uint forbidden = mutations | (installationOrFile ? 4u : 0u);
            if ((mask & (forbidden | 0x50000000u)) != 0)
                throw new UnauthorizedAccessException("PROCESS_EXECUTABLE_ACL_REJECTED");
        }
    }

    private static SafeFileHandle OpenImage(string path)
    {
        var handle = CreateFile(path, 0x20081, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); NativeFileSystem.ThrowLastError(); }
        return handle;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}

internal sealed class ExecutableLease : IDisposable
{
    internal PathLease Handles { get; } = new();
    internal ExecutableIdentity Identity { get; set; } = null!;
    public void Dispose() => Handles.Dispose();
}
