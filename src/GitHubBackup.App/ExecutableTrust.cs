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
        if (expected is { AppPackageFullName: not null } packageExpected)
        {
            if (packageExpected.AppPackagePath is null)
                throw new IOException("PROCESS_EXECUTABLE_PACKAGE_IDENTITY_INVALID");
            return AcquireRegisteredPackageExecutable(absolutePath, packageExpected.AppPackageFullName!, packageExpected.AppPackagePath, packageExpected);
        }
        if (expected?.AppPackagePath is not null) throw new IOException("PROCESS_EXECUTABLE_PACKAGE_IDENTITY_INVALID");
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

    internal static ExecutableIdentity CaptureRegisteredPackageIdentity(string absolutePath, RegisteredWingetPackage package)
    {
        using var lease = AcquireRegisteredPackageExecutable(absolutePath, package.FullName, package.Path, null);
        return lease.Identity;
    }

    private static ExecutableLease AcquireRegisteredPackageExecutable(string absolutePath, string packageFullName,
        string packagePath, ExecutableIdentity? expected)
    {
        string canonical = NativeFileSystem.CanonicalPath(absolutePath);
        string canonicalPackagePath = NativeFileSystem.CanonicalPath(packagePath);
        if (!WingetPackageResolver.IsCurrentRegisteredPackage(packageFullName, canonicalPackagePath)
            || !string.Equals(canonical, Path.Combine(canonicalPackagePath, "winget.exe"), StringComparison.OrdinalIgnoreCase)
            || new DriveInfo(Path.GetPathRoot(canonical)!).DriveType != DriveType.Fixed)
            throw new IOException("PROCESS_EXECUTABLE_PACKAGE_REJECTED");

        // Windows protects registered package files as read-only and prevents launch
        // if they are altered. Normal users can execute/read the image but cannot
        // obtain READ_CONTROL on every WindowsApps ancestor, so use a read-only
        // image handle and bind its identity to the OS package registration.
        SafeFileHandle handle = File.OpenHandle(canonical, FileMode.Open, FileAccess.Read, FileShare.Read);
        var lease = new ExecutableLease();
        try
        {
            NativeFileIdentity native = NativeFileSystem.InspectExecutable(handle, canonical);
            var identity = new ExecutableIdentity(native.VolumeSerialNumber, native.FileIndex,
                RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle).Ticks, packageFullName, canonicalPackagePath);
            if (expected is not null && expected != identity) throw new IOException("PROCESS_EXECUTABLE_IDENTITY_CHANGED");
            lease.Handles.Add(handle);
            lease.Identity = identity;
            return lease;
        }
        catch { handle.Dispose(); lease.Dispose(); throw; }
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
