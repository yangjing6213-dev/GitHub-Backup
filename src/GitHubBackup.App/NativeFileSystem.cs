using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

// Handles are opened without following reparses. Directory leases deny delete sharing,
// so a validated ancestor cannot be renamed or replaced while an operation uses it.
internal static class NativeFileSystem
{
    internal const uint ReadControl = 0x20000;
    internal const uint ReadAttributes = 0x80;
    internal const uint WriteDac = 0x40000;
    internal const uint WriteOwner = 0x80000;
    internal const uint DeleteAccess = 0x10000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;

    internal static string CanonicalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.Length < 3 || path[1] != ':' || path[2] != Path.DirectorySeparatorChar)
            throw new ArgumentException("A fully qualified local path is required.", nameof(path));
        if (path.AsSpan(2).Contains(':') || path.Contains('/') || path.Split('\\').Skip(1).Any(s => s.EndsWith(' ') || (s.EndsWith('.') && s != "." && s != "..")))
            throw new ArgumentException("Ambiguous Windows path.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static IEnumerable<string> Segments(string canonicalPath)
    {
        string root = Path.GetPathRoot(canonicalPath)!;
        yield return root;
        string current = root;
        foreach (string part in canonicalPath[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            yield return current;
        }
    }

    // FILE_READ_DATA / FILE_LIST_DIRECTORY makes Windows enforce sharing restrictions;
    // metadata-only handles do not prevent rename even without FILE_SHARE_DELETE.
    internal static SafeFileHandle Open(string path, uint access = ReadControl | ReadAttributes | 1,
        bool shareDelete = false, bool shareWrite = true, bool shareRead = true)
    {
        SafeFileHandle handle = CreateFile(path, access, (shareRead ? 1u : 0u) | (shareWrite ? 2u : 0u) | (shareDelete ? 4u : 0u),
            IntPtr.Zero, 3, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); Throw(error); }
        return handle;
    }

    internal static NativeFileIdentity Inspect(SafeFileHandle handle, string expectedPath, bool? directory = null) =>
        InspectCore(handle, expectedPath, directory, rejectHardLinks: true);

    // Executable hardlinks share the same owner/DACL and sharing restrictions.
    // Only executable trust may use this read-only role; private data stays single-link.
    internal static NativeFileIdentity InspectExecutable(SafeFileHandle handle, string expectedPath) =>
        InspectCore(handle, expectedPath, directory: false, rejectHardLinks: false);

    private static NativeFileIdentity InspectCore(SafeFileHandle handle, string expectedPath, bool? directory, bool rejectHardLinks)
    {
        if (!GetFileInformationByHandle(handle, out FileInformation info)) ThrowLastError();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new PathBoundaryException("REPARSE_POINT_REJECTED");
        if (directory is not null && ((info.Attributes & FileAttributes.Directory) != 0) != directory)
            throw new PathBoundaryException("TYPE_MISMATCH");
        if (rejectHardLinks && (info.Attributes & FileAttributes.Directory) == 0 && info.NumberOfLinks != 1)
            throw new PathBoundaryException("HARDLINK_REJECTED");
        var finalPath = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Capacity, 0);
        if (count == 0 || count >= finalPath.Capacity) ThrowLastError();
        string actual = finalPath.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(CanonicalPath(actual), CanonicalPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new PathBoundaryException("IDENTITY_CHANGED");
        return new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    internal static RawSecurityDescriptor ReadSecurity(SafeFileHandle handle)
    {
        GetKernelObjectSecurity(handle, 5, null, 0, out uint size);
        if (size == 0) ThrowLastError();
        var bytes = new byte[size];
        if (!GetKernelObjectSecurity(handle, 5, bytes, size, out _)) ThrowLastError();
        return new RawSecurityDescriptor(bytes, 0);
    }

    internal static PathLease PinDirectories(string path)
    {
        var lease = new PathLease();
        try
        {
            foreach (string segment in Segments(CanonicalPath(path)))
            {
                SafeFileHandle handle = Open(segment);
                lease.Add(handle);
                Inspect(handle, segment, directory: true);
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static void CreateDirectory(string path, byte[] descriptor)
    {
        WithSecurity(descriptor, security =>
        {
            if (!CreateDirectoryNative(path, ref security)) ThrowLastError();
            return true;
        });
    }

    internal static SafeFileHandle CreateRestrictedFile(string path, byte[] descriptor) =>
        WithSecurity(descriptor, security =>
        {
            SafeFileHandle handle = CreateFileWithSecurity(path, 0xC0000000 | ReadControl | ReadAttributes,
                1, ref security, 1, OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); Throw(error); }
            return handle;
        });

    internal static SafeFileHandle OpenOrCreateExclusiveRestrictedFile(string path, byte[] descriptor) =>
        WithSecurity(descriptor, security =>
        {
            SafeFileHandle handle = CreateFileWithSecurity(path, 0xC0000000 | ReadControl | ReadAttributes,
                0, ref security, 4, OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); Throw(error); }
            return handle;
        });

    internal static void SetSecurity(SafeFileHandle handle, byte[] descriptor)
    {
        // Unlike SetNamedSecurityInfo, this handle API does not propagate changes to children.
        if (!SetKernelObjectSecurity(handle, 0x80000005, descriptor)) ThrowLastError();
    }

    internal static void DeleteByHandle(SafeFileHandle handle)
    {
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) ThrowLastError();
    }

    internal static void RenameDirectory(string source,string destination)
    {
        MirrorSafeCopy.RequireChild(Path.GetDirectoryName(source)!,destination);
        using var parents=PinDirectories(Path.GetDirectoryName(source)!);
        using var handle=MirrorSafeCopy.OpenPrivate(source,access:ReadControl|ReadAttributes|DeleteAccess|1);
        Inspect(handle,source,true);
        byte[] name=Encoding.Unicode.GetBytes(CanonicalPath(destination));
        int nameOffset=IntPtr.Size==8?20:12;
        IntPtr buffer=Marshal.AllocHGlobal(nameOffset+name.Length+2);
        try
        {
            for(int i=0;i<nameOffset;i++)Marshal.WriteByte(buffer,i,0);
            Marshal.WriteInt32(buffer,nameOffset-4,name.Length);Marshal.Copy(name,0,buffer+nameOffset,name.Length);
            Marshal.WriteInt16(buffer,nameOffset+name.Length,0);
            if(!SetFileInformationBuffer(handle,3,buffer,(uint)(nameOffset+name.Length+2)))ThrowLastError();
            Inspect(handle,destination,true);
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }

    private static T WithSecurity<T>(byte[] descriptor, Func<SecurityAttributes, T> action)
    {
        GCHandle pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try { return action(new() { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pin.AddrOfPinnedObject() }); }
        finally { pin.Free(); }
    }

    internal static void ThrowLastError() => Throw(Marshal.GetLastWin32Error());
    private static void Throw(int error)
    {
        if (error is 2 or 3) throw new FileNotFoundException("The expected filesystem entry is missing.");
        if (error == 5) throw new UnauthorizedAccessException("The filesystem entry is inaccessible.");
        throw new IOException("Windows filesystem operation failed.", new Win32Exception(error));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int InheritHandle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal FileAttributes Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileWithSecurity(string path, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryNative(string path, ref SecurityAttributes security);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[]? descriptor, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[] descriptor);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte information, uint size);
    [DllImport("kernel32.dll",EntryPoint="SetFileInformationByHandle",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationBuffer(SafeFileHandle handle,int informationClass,IntPtr information,uint size);
}

internal readonly record struct NativeFileIdentity(uint VolumeSerialNumber, ulong FileIndex);
internal sealed class PathBoundaryException(string code) : IOException(code)
{
    internal string Code { get; } = code;
}
internal sealed class PathLease : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    internal void Add(SafeFileHandle handle) => handles.Add(handle);
    public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }
}
