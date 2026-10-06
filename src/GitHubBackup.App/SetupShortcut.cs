using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace GitHubBackup.App;

// Serialization and inspection only. No filesystem persistence, resolution or launch.
internal static class SetupShortcut
{
    internal const int MaximumBytes = 65536;
    private const int PathCapacity = 260; // GetPath's MAX_PATH includes the terminating NUL.
    private const uint RawPath = 4;
    private const int ChangedApartment = unchecked((int)0x80010106);

    internal static byte[] Create(string target, string workingDirectory)
    {
        RequirePath(target, nameof(target));
        RequirePath(workingDirectory, nameof(workingDirectory));
        return WithShellLink(link =>
        {
            link.SetPath(target);
            link.SetArguments("");
            link.SetWorkingDirectory(workingDirectory);
            using var memory = new MemoryStream();
            ((IPersistStream)link).Save(new MemoryComStream(memory), true);
            if (memory.Length is <= 0 or > MaximumBytes)
                throw new IOException("SETUP_SHORTCUT_SIZE_INVALID");
            return memory.ToArray();
        });
    }

    internal static void Validate(byte[] bytes, string target, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        RequirePath(target, nameof(target));
        RequirePath(workingDirectory, nameof(workingDirectory));
        if (bytes.Length is < 76 or > MaximumBytes)
            throw new IOException("SETUP_SHORTCUT_SIZE_INVALID");
        try
        {
            WithShellLink(link =>
            {
                using var memory = new MemoryStream(bytes, writable: false);
                ((IPersistStream)link).Load(new MemoryComStream(memory));
                var path = new StringBuilder(PathCapacity);
                int result = link.GetPath(path, path.Capacity, IntPtr.Zero, RawPath);
                Marshal.ThrowExceptionForHR(result);
                if (result != 0 || !string.Equals(path.ToString(), target, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("SETUP_SHORTCUT_TARGET_MISMATCH");
                var arguments = new StringBuilder(MaximumBytes + 1);
                link.GetArguments(arguments, arguments.Capacity);
                if (arguments.Length != 0)
                    throw new IOException("SETUP_SHORTCUT_ARGUMENTS_NOT_EMPTY");
                var directory = new StringBuilder(PathCapacity);
                link.GetWorkingDirectory(directory, directory.Capacity);
                if (!string.Equals(directory.ToString(), workingDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("SETUP_SHORTCUT_WORKING_DIRECTORY_MISMATCH");
                return true;
            });
        }
        catch (COMException error)
        {
            throw new IOException("SETUP_SHORTCUT_INVALID_BYTES", error);
        }
    }

    private static void RequirePath(string path, string parameter)
    {
        if (string.IsNullOrEmpty(path) || path.Length >= PathCapacity)
            throw new ArgumentException("SETUP_SHORTCUT_PATH_CAPACITY", parameter);
        string canonical = NativeFileSystem.CanonicalPath(path);
        if (!string.Equals(canonical, path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SETUP_SHORTCUT_PATH_NOT_CANONICAL", parameter);
    }

    private static T WithShellLink<T>(Func<IShellLinkW, T> action)
    {
        int initialized = CoInitializeEx(IntPtr.Zero, 2);
        if (initialized < 0 && initialized != ChangedApartment)
            Marshal.ThrowExceptionForHR(initialized);
        object? instance = null;
        try
        {
            instance = new ShellLinkCom();
            return action((IShellLinkW)instance);
        }
        finally
        {
            try
            {
                if (instance is not null) Marshal.FinalReleaseComObject(instance);
            }
            finally
            {
                // S_OK and S_FALSE both add a COM initialization reference.
                // RPC_E_CHANGED_MODE uses the caller's apartment without adding one.
                if (initialized >= 0) CoUninitialize();
            }
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkCom;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        // This unused slot preserves SetPath's native vtable position.
        void UnusedResolveSlot(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("00000109-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistStream
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.Interface)] IStream stream);
        void Save([MarshalAs(UnmanagedType.Interface)] IStream stream, [MarshalAs(UnmanagedType.Bool)] bool clearDirty);
        void GetSizeMax(out ulong size);
    }

    // The Shell Link receives only this bounded memory stream, never a path.
    private sealed class MemoryComStream(MemoryStream memory) : IStream
    {
        public void Read(byte[] buffer, int count, IntPtr read)
        {
            if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            int actual = memory.Read(buffer, 0, count);
            if (read != IntPtr.Zero) Marshal.WriteInt32(read, actual);
        }

        public void Write(byte[] buffer, int count, IntPtr written)
        {
            if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            RequireSize(checked(memory.Position + count));
            memory.Write(buffer, 0, count);
            if (written != IntPtr.Zero) Marshal.WriteInt32(written, count);
        }

        public void Seek(long offset, int origin, IntPtr newPosition)
        {
            long start = origin switch
            {
                0 => 0,
                1 => memory.Position,
                2 => memory.Length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            RequireSize(checked(start + offset));
            long position = memory.Seek(offset, (SeekOrigin)origin);
            if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, position);
        }

        public void SetSize(long size)
        {
            RequireSize(size);
            memory.SetLength(size);
        }

        public void Stat(out STATSTG stat, int flags) =>
            stat = new STATSTG { type = 2, cbSize = memory.Length, grfMode = memory.CanWrite ? 2 : 0 };

        public void Commit(int flags) { }
        public void CopyTo(IStream target, long count, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long offset, long count, int type) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long count, int type) => throw new NotSupportedException();
        public void Clone(out IStream stream) => throw new NotSupportedException();

        private static void RequireSize(long size)
        {
            if (size is < 0 or > MaximumBytes) throw new IOException("SETUP_SHORTCUT_SIZE_INVALID");
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrency);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
