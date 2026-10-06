using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

internal sealed record SetupIdentity([property: System.Text.Json.Serialization.JsonRequired] ulong Volume,
    [property: System.Text.Json.Serialization.JsonRequired] string FileId);

// Setup authority is the held handle plus the full FILE_ID_INFO, not a pathname
// or content hash alone. No operation replaces an existing destination.
internal static class SetupNative
{
    internal static SetupIdentity Inspect(SafeFileHandle handle, string path, bool directory, SecurityIdentifier user)
    {
        AclPolicy.VerifyRestricted(handle, user);
        return InspectIdentity(handle, path, directory);
    }

    internal static SetupIdentity InspectIdentity(SafeFileHandle handle, string path, bool directory)
    {
        NativeFileSystem.Inspect(handle, path, directory);
        byte[] info = new byte[24];
        if (!GetFileInformationByHandleEx(handle, 18, info, 24)) NativeFileSystem.ThrowLastError();
        return new(BinaryPrimitives.ReadUInt64LittleEndian(info), Convert.ToHexString(info.AsSpan(8, 16)));
    }

    internal static SafeFileHandle CreateSharedPrograms(string path, SecurityIdentifier user)
    {
        var handle = CreateDirectory2Default(path, 0x120081, 1, 1, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("SETUP_PROGRAMS_CREATE_FAILED", new System.ComponentModel.Win32Exception(error)); }
        try
        {
            var created = InspectIdentity(handle, path, true);
            VerifySharedParent(handle, user);
            handle.Dispose();
            handle = NativeFileSystem.Open(path);
            if (InspectIdentity(handle, path, true) != created) throw new IOException("SETUP_PROGRAMS_SUBSTITUTED");
            VerifySharedParent(handle, user);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static SafeFileHandle Open(string path, bool directory, SecurityIdentifier user, bool writable = false)
    {
        var handle = NativeFileSystem.Open(path, NativeFileSystem.ReadControl | NativeFileSystem.ReadAttributes
            | NativeFileSystem.DeleteAccess | 0x80000000 | (writable ? 0x40000000u : 0), shareWrite: directory);
        try { Inspect(handle, path, directory, user); return handle; }
        catch { handle.Dispose(); throw; }
    }

    internal static SafeFileHandle Create(string path, bool directory, SecurityIdentifier user)
    {
        string inheritance = directory ? "OICI" : "";
        var raw = new RawSecurityDescriptor($"O:{user.Value}D:P(A;{inheritance};FA;;;{user.Value})(A;{inheritance};FA;;;SY)(A;{inheritance};FA;;;BA)");
        byte[] descriptor = new byte[raw.BinaryLength];
        raw.GetBinaryForm(descriptor, 0);
        var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pin.AddrOfPinnedObject() };
            SafeFileHandle handle = directory
                ? CreateDirectory2(path, 0x130081, 1, 1, ref security)
                : CreateFile(path, 0xC0030080, 1, ref security, 1, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("SETUP_CREATE_NEW_FAILED", new System.ComponentModel.Win32Exception(error)); }
            try
            {
                SetupIdentity created = Inspect(handle, path, directory, user);
                if (!directory) return handle;
                // CreateDirectory2W's returned read-shared directory lease cannot
                // accommodate Win32 rename's write open. Reacquire a write-shared,
                // no-delete-shared lease, accepting only the original full ID.
                // Interruption in this transition leaves an unowned conflict;
                // later runs must refuse it, never infer ownership from its name.
                handle.Dispose();
                handle = Open(path, true, user);
                if (Inspect(handle, path, true, user) != created) throw new IOException("SETUP_CREATED_DIRECTORY_SUBSTITUTED");
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        finally { pin.Free(); }
    }

    internal static void VerifySharedParent(SafeFileHandle handle, SecurityIdentifier user)
    {
        var raw = NativeFileSystem.ReadSecurity(handle);
        if (!AclPolicy.IsApproved(raw.Owner, user) || raw.DiscretionaryAcl is null
            || (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0)
            throw new IOException("SETUP_SHARED_PARENT_ACL_INVALID");
        const uint readOnly = 0xA01200A9; // generic read/execute, read/control/synchronize, list/readEA/readAttrs/traverse
        foreach (GenericAce ace in raw.DiscretionaryAcl)
        {
            if (ace is not CommonAce common || common.IsCallback
                || common.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
            {
                if ((ace.AceFlags & AceFlags.InheritOnly) != 0
                    && (ace.AceFlags & (AceFlags.ObjectInherit | AceFlags.ContainerInherit)) == 0) continue;
                throw new IOException("SETUP_SHARED_PARENT_UNKNOWN_ACE");
            }
            if (common.AceQualifier == AceQualifier.AccessAllowed && !AclPolicy.IsApproved(common.SecurityIdentifier, user)
                && ((uint)common.AccessMask & ~readOnly) != 0)
                throw new IOException("SETUP_SHARED_PARENT_FOREIGN_WRITE");
        }
    }

    internal static string Hash(SafeFileHandle handle)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536]; long offset = 0;
        int count;
        while ((count = RandomAccess.Read(handle, buffer, offset)) != 0) { hash.AppendData(buffer, 0, count); offset += count; }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static byte[] Read(SafeFileHandle handle, int maximum)
    {
        long size = RandomAccess.GetLength(handle);
        if (size < 0 || size > maximum) throw new IOException("SETUP_RECORD_TOO_LARGE");
        byte[] bytes = new byte[(int)size]; int offset = 0;
        while (offset < bytes.Length)
        {
            int count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (count == 0) throw new IOException("SETUP_RECORD_TRUNCATED");
            offset += count;
        }
        return bytes;
    }

    internal static void Write(SafeFileHandle handle, ReadOnlySpan<byte> bytes, long offset = 0)
    {
        RandomAccess.Write(handle, bytes, offset);
        if (!FlushFileBuffers(handle)) NativeFileSystem.ThrowLastError();
    }

    internal static void Copy(SafeFileHandle source, SafeFileHandle destination)
    {
        byte[] bytes = new byte[65536]; long offset = 0; int count;
        while ((count = RandomAccess.Read(source, bytes, offset)) != 0)
        { RandomAccess.Write(destination, bytes.AsSpan(0, count), offset); offset += count; }
        if (!FlushFileBuffers(destination)) NativeFileSystem.ThrowLastError();
    }

    internal static void Rename(SafeFileHandle file, string destination)
    {
        byte[] name = Encoding.Unicode.GetBytes(NativeFileSystem.CanonicalPath(destination));
        int nameOffset = IntPtr.Size == 8 ? 20 : 12;
        IntPtr buffer = Marshal.AllocHGlobal(nameOffset + name.Length + 2);
        try
        {
            for (int i = 0; i < nameOffset + name.Length + 2; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, nameOffset - 4, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            if (!SetFileInformationByHandle(file, 3, buffer, (uint)(nameOffset + name.Length + 2))) NativeFileSystem.ThrowLastError();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int InheritHandle; }
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectory2W", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateDirectory2(string path, uint access, uint share, uint flags, ref SecurityAttributes security);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectory2W", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateDirectory2Default(string path, uint access, uint share, uint flags, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, byte[] information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, IntPtr information, uint size);
}
