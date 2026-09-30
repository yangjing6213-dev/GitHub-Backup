using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using GitHubBackup.App;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App.Tests;

internal sealed class StorageTestRoot : IDisposable
{
    internal string Path { get; }
    internal SecurityIdentifier User { get; } = WindowsIdentity.GetCurrent().User!;
    internal StorageTestRoot(string? path=null)
    {
        Path=path??System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GitHubBackup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    internal string Child(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() => Directory.Delete(Path, recursive: true);
    internal static void Grant(string path, FileSystemRights rights)
    {
        var directory = new DirectoryInfo(path);
        DirectorySecurity security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), rights, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    internal void SetNullDacl(string path)
    {
        var descriptor = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            User, null, null, null);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        using SafeFileHandle handle = NativeFileSystem.Open(path, NativeFileSystem.ReadControl | NativeFileSystem.WriteDac | NativeFileSystem.WriteOwner);
        NativeFileSystem.SetSecurity(handle, bytes);
    }

    internal static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        string substitute = @"\??\" + target;
        byte[] substituteBytes = Encoding.Unicode.GetBytes(substitute);
        byte[] printBytes = Encoding.Unicode.GetBytes(target);
        byte[] buffer = new byte[16 + substituteBytes.Length + printBytes.Length + 4];
        using (var writer = new BinaryWriter(new MemoryStream(buffer)))
        {
            writer.Write(0xA0000003u);
            writer.Write((ushort)(buffer.Length - 8));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)substituteBytes.Length);
            writer.Write((ushort)(substituteBytes.Length + 2));
            writer.Write((ushort)printBytes.Length);
            writer.Write(substituteBytes);
            writer.Write((ushort)0);
            writer.Write(printBytes);
            writer.Write((ushort)0);
        }
        using SafeFileHandle handle = NativeFileSystem.Open(path, 0x40000000);
        if (!DeviceIoControl(handle, 0x900a4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Could not create owned test junction.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }

    internal static void CreateHardLink(string path, string target)
    {
        if (!CreateHardLinkNative(path, target, IntPtr.Zero))
            throw new IOException("Could not create owned test hardlink.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkNative(string path, string target, IntPtr security);
}
