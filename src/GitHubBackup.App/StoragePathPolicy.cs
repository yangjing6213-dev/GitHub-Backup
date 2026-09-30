using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

internal sealed record StoragePathValidation(bool Allowed, string ErrorCode, string? SuggestedPath);
internal sealed record StoragePathProbes(Func<string, DriveType> DriveType,
    Func<string, FileAttributes?> Attributes, Func<string, CancellationToken, Task> AtomicProbe);

internal static class StoragePathPolicy
{
    internal static Task<StoragePathValidation> ValidateAsync(string path, CancellationToken cancellationToken) =>
        ValidateAsync(path, new(root => new DriveInfo(root).DriveType, Attributes, ProbeAsync), cancellationToken);

    internal static async Task<StoragePathValidation> ValidateAsync(string path, StoragePathProbes probes, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string canonical = NativeFileSystem.CanonicalPath(path);
            string volume = Path.GetPathRoot(canonical)!;
            if (string.Equals(Path.TrimEndingDirectorySeparator(canonical), Path.TrimEndingDirectorySeparator(volume), StringComparison.OrdinalIgnoreCase))
                return new(false, "STORAGE_VOLUME_ROOT_REJECTED", null);
            if (probes.DriveType(volume) != DriveType.Fixed) return new(false, "STORAGE_FIXED_VOLUME_REQUIRED", null);
            string? ancestor = null;
            bool missing = false;
            foreach (string segment in NativeFileSystem.Segments(canonical))
            {
                FileAttributes? attributes = probes.Attributes(segment);
                if (attributes is null) { missing = true; continue; }
                if (missing) return new(false, "STORAGE_PATH_CHANGED", null);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return new(false, "STORAGE_REPARSE_POINT_REJECTED", null);
                if ((attributes & FileAttributes.Directory) == 0) return new(false, "STORAGE_DIRECTORY_REQUIRED", null);
                ancestor = segment;
            }
            if (ancestor is null) return new(false, "STORAGE_VOLUME_UNAVAILABLE", null);
            await probes.AtomicProbe(ancestor, cancellationToken).ConfigureAwait(false);
            return new(true, "", null);
        }
        catch (ArgumentException) { return new(false, "STORAGE_PATH_INVALID", null); }
        catch (PathBoundaryException ex) { return new(false, "STORAGE_" + ex.Code, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(false, "STORAGE_ATOMIC_PROBE_FAILED", null); }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static async Task ProbeAsync(string ancestor, CancellationToken cancellationToken)
    {
        using PathLease parent = NativeFileSystem.PinDirectories(ancestor);
        var user = WindowsIdentity.GetCurrent().User!;
        string directory = Path.Combine(ancestor, ".githubbackup-probe-" + Guid.NewGuid().ToString("N"));
        AclPolicy.CreateRestrictedDirectory(directory, user, requireNew: true);
        using SafeFileHandle directoryHandle = NativeFileSystem.Open(directory);
        NativeFileIdentity directoryIdentity = NativeFileSystem.Inspect(directoryHandle, directory, directory: true);
        string first = Path.Combine(directory, "first");
        string second = Path.Combine(directory, "second");
        NativeFileIdentity? firstIdentity = null, secondIdentity = null;
        try
        {
            await using (FileStream stream = AclPolicy.CreateRestrictedFile(first, user))
            {
                firstIdentity = NativeFileSystem.Inspect(stream.SafeFileHandle, first, directory: false);
                await stream.WriteAsync("old"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            await using (FileStream stream = AclPolicy.CreateRestrictedFile(second, user))
            {
                secondIdentity = NativeFileSystem.Inspect(stream.SafeFileHandle, second, directory: false);
                await stream.WriteAsync("new"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(second, first, null);
            firstIdentity = secondIdentity;
            secondIdentity = null;
            using SafeFileHandle verify = NativeFileSystem.Open(first);
            if (NativeFileSystem.Inspect(verify, first, directory: false) != firstIdentity
                || !File.ReadAllBytes(first).AsSpan().SequenceEqual("new"u8)) throw new IOException("STORAGE_PROBE_CONTENT_MISMATCH");
        }
        finally
        {
            if (secondIdentity is not null) AtomicFile.DeleteOwnedFile(second, secondIdentity.Value);
            if (firstIdentity is not null) AtomicFile.DeleteOwnedFile(first, firstIdentity.Value);
            directoryHandle.Dispose();
            using SafeFileHandle cleanup = NativeFileSystem.Open(directory, NativeFileSystem.ReadAttributes | NativeFileSystem.DeleteAccess, shareDelete: true);
            if (NativeFileSystem.Inspect(cleanup, directory, directory: true) != directoryIdentity) throw new PathBoundaryException("IDENTITY_CHANGED");
            NativeFileSystem.DeleteByHandle(cleanup);
        }
    }
}
