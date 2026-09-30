using System.Runtime.InteropServices;
using System.Security.Cryptography;
namespace GitHubBackup.App;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCredential
{
    internal uint Flags, Type;
    internal nint TargetName, Comment;
    internal long LastWritten;
    internal uint CredentialBlobSize;
    internal nint CredentialBlob;
    internal uint Persist, AttributeCount;
    internal nint Attributes, TargetAlias, UserName;
}
internal interface ICredentialNative
{
    int Enumerate(string filter, uint flags, out uint count, out nint buffer);
    int Write(ref NativeCredential credential);
    int Read(string target, uint type, uint flags, out nint buffer);
    int Delete(string target, uint type, uint flags);
    void Free(nint buffer);
}
internal interface IGitHubCredentialReader
{
    GitHubCredentialLease ReadExact(string login);
}
internal sealed class GitHubCredentialReader(ICredentialNative native) : IGitHubCredentialReader
{
    internal GitHubCredentialReader() : this(new CredentialNative()) { }

    public GitHubCredentialLease ReadExact(string login)
    {
        if (!AuthConfigLease.IsLogin(login)) throw new AuthBoundaryException("AUTH_LOGIN_MISMATCH");
        string target = "gh:github.com:" + login;
        nint buffer = 0;
        byte[]? owned = null;
        try
        {
            try
            {
                int error = native.Read(target, 1, 0, out buffer);
                if (error == 1168) throw new AuthBoundaryException("AUTH_API_CREDENTIAL_MISSING");
                if (error != 0 || buffer == 0) throw new AuthBoundaryException("AUTH_API_CREDENTIAL_READ_FAILED");
                NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(buffer);
                if (credential.Type != 1 || credential.TargetName == 0
                    || !string.Equals(Marshal.PtrToStringUni(credential.TargetName), target, StringComparison.OrdinalIgnoreCase)
                    || credential.CredentialBlobSize is < 1 or > 2560 || credential.CredentialBlob == 0)
                    throw new AuthBoundaryException("AUTH_API_CREDENTIAL_FORMAT_UNSUPPORTED");
                owned = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, owned, 0, owned.Length);
                if (owned.Any(value => value is < 0x21 or > 0x7e))
                    throw new AuthBoundaryException("AUTH_API_CREDENTIAL_FORMAT_UNSUPPORTED");
            }
            finally { if (buffer != 0) native.Free(buffer); }
            var lease = new GitHubCredentialLease(login, owned);
            owned = null;
            return lease;
        }
        catch (Exception ex) when (ex is not AuthBoundaryException and not OutOfMemoryException)
        { throw new AuthBoundaryException("AUTH_API_CREDENTIAL_READ_FAILED"); }
        finally { if (owned is not null) CryptographicOperations.ZeroMemory(owned); }
    }
}
internal sealed class WindowsCredentialStore(ICredentialNative native, Action<byte[]>? random = null) : ICredentialStore
{
    internal WindowsCredentialStore() : this(new CredentialNative()) { }
    public HashSet<string> PerUserTargets()
    {
        const string prefix = "gh:github.com:";
        return Enumerate(prefix + "*").Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.Length > prefix.Length).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    private HashSet<string> Enumerate(string filter)
    {
        nint buffer = 0;
        try
        {
            int error = native.Enumerate(filter, 0, out uint count, out buffer);
            if (error == 1168) return new(StringComparer.OrdinalIgnoreCase);
            if (error != 0 || count > 10000 || count != 0 && buffer == 0) throw new AuthBoundaryException("AUTH_KEYRING_ENUMERATION_FAILED");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                nint credential = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                if (credential == 0) throw new AuthBoundaryException("AUTH_KEYRING_ENUMERATION_FAILED");
                // CredEnumerate's native block can contain secrets. Application code
                // projects ONLY Type and TargetName, never blob size/pointer/value.
                if (Marshal.ReadInt32(credential, 4) != 1) continue;
                nint name = Marshal.ReadIntPtr(credential, 8);
                if (name == 0) throw new AuthBoundaryException("AUTH_KEYRING_ENUMERATION_FAILED");
                names.Add(Marshal.PtrToStringUni(name)!);
            }
            return names;
        }
        finally { if (buffer != 0) native.Free(buffer); }
    }
    public void Probe()
    {
        const string prefix = "GitHubBackupTool:CredentialProbe:";
        byte[] nonce = new byte[32], blob = new byte[32];
        byte[]? returned = null;
        nint targetName = 0, userName = 0; GCHandle pinned = default;
        string target = ""; bool written = false;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Fill(nonce); target = prefix + Convert.ToHexStringLower(nonce);
                if (!Enumerate(prefix + "*").Contains(target)) break;
                target = "";
            }
            if (target.Length == 0) throw new AuthBoundaryException("AUTH_KEYRING_PROBE_COLLISION");
            Fill(blob); targetName = Marshal.StringToHGlobalUni(target); userName = Marshal.StringToHGlobalUni("GitHubBackupTool");
            pinned = GCHandle.Alloc(blob, GCHandleType.Pinned);
            var credential = new NativeCredential { Type = 1, TargetName = targetName, UserName = userName, CredentialBlobSize = (uint)blob.Length, CredentialBlob = pinned.AddrOfPinnedObject(), Persist = 2 };
            if (native.Write(ref credential) != 0) throw new AuthBoundaryException("AUTH_KEYRING_PROBE_WRITE_FAILED");
            written = true;
            nint read = 0;
            try
            {
                if (native.Read(target, 1, 0, out read) != 0 || read == 0) throw new AuthBoundaryException("AUTH_KEYRING_PROBE_READ_FAILED");
                // Full projection is permitted ONLY for this app-created random probe.
                NativeCredential value = Marshal.PtrToStructure<NativeCredential>(read);
                if (value.Type != 1 || Marshal.PtrToStringUni(value.TargetName) != target || value.CredentialBlobSize != blob.Length || value.CredentialBlob == 0)
                    throw new AuthBoundaryException("AUTH_KEYRING_PROBE_MISMATCH");
                returned = new byte[blob.Length]; Marshal.Copy(value.CredentialBlob, returned, 0, returned.Length);
                if (!CryptographicOperations.FixedTimeEquals(blob, returned)) throw new AuthBoundaryException("AUTH_KEYRING_PROBE_MISMATCH");
            }
            finally { if (read != 0) native.Free(read); }
        }
        finally
        {
            try
            {
                if (written)
                {
                    if (native.Delete(target, 1, 0) != 0) throw new AuthBoundaryException("AUTH_KEYRING_PROBE_CLEANUP_FAILED");
                    nint check = 0;
                    try { if (native.Read(target, 1, 0, out check) != 1168) throw new AuthBoundaryException("AUTH_KEYRING_PROBE_CLEANUP_FAILED"); }
                    finally { if (check != 0) native.Free(check); }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce); CryptographicOperations.ZeroMemory(blob);
                if (returned is not null) CryptographicOperations.ZeroMemory(returned);
                if (pinned.IsAllocated) pinned.Free();
                if (targetName != 0) Marshal.FreeHGlobal(targetName);
                if (userName != 0) Marshal.FreeHGlobal(userName);
            }
        }
    }
    private void Fill(byte[] bytes) { if (random is null) RandomNumberGenerator.Fill(bytes); else random(bytes); }
}

internal sealed class CredentialNative : ICredentialNative
{
    public int Enumerate(string filter, uint flags, out uint count, out nint buffer) => CredEnumerate(filter, flags, out count, out buffer) ? 0 : Marshal.GetLastWin32Error();
    public int Write(ref NativeCredential credential) => CredWrite(ref credential, 0) ? 0 : Marshal.GetLastWin32Error();
    public int Read(string target, uint type, uint flags, out nint buffer) => CredRead(target, type, flags, out buffer) ? 0 : Marshal.GetLastWin32Error();
    public int Delete(string target, uint type, uint flags) => CredDelete(target, type, flags) ? 0 : Marshal.GetLastWin32Error();
    public void Free(nint buffer) => CredFree(buffer);
    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredEnumerate(string filter, uint flags, out uint count, out nint buffer);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern void CredFree(nint buffer);
}
