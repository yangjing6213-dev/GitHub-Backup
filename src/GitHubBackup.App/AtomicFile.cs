using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

// Scheduling seam around the commit boundary; the filesystem operations remain real.
internal sealed record AtomicFileCommitHooks(Func<Task>? AfterFlush = null, Func<Task>? CommitStarted = null);

// Retain exact ownership evidence when rollback cannot remove this operation's temporary file.
internal sealed class AtomicFileCleanupException(string path, NativeFileIdentity identity) : IOException("ATOMIC_CLEANUP_PENDING")
{
    internal string OwnedPath { get; } = path;
    internal NativeFileIdentity Identity { get; } = identity;
    internal void RetryCleanup() => AtomicFile.DeleteOwnedFile(OwnedPath, Identity);
}

internal static class AtomicFile
{
    internal static async Task WriteAsync(string path, Func<FileStream, CancellationToken, Task> writer,
        CancellationToken cancellationToken, AtomicFileCommitHooks? hooks = null, bool requireNew = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string canonical = NativeFileSystem.CanonicalPath(path);
        string parent = Path.GetDirectoryName(canonical)!;
        using PathLease parents = NativeFileSystem.PinDirectories(parent);
        SafeFileHandle? prior = null;
        try { prior = NativeFileSystem.Open(canonical); NativeFileSystem.Inspect(prior, canonical, directory: false); }
        catch (FileNotFoundException) { }
        catch { prior?.Dispose(); throw; }
        if (requireNew && prior is not null) { prior.Dispose(); throw new IOException("IMMUTABLE_GENERATION_EXISTS"); }
        string temporary = Path.Combine(parent, ".atomic-" + Guid.NewGuid().ToString("N") + ".tmp");
        NativeFileIdentity? temporaryIdentity = null;
        bool committed = false;
        try
        {
            await using (FileStream stream = AclPolicy.CreateRestrictedFile(temporary, WindowsIdentity.GetCurrent().User!))
            {
                temporaryIdentity = NativeFileSystem.Inspect(stream.SafeFileHandle, temporary, directory: false);
                await writer(stream, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (hooks?.AfterFlush is not null) await hooks.AfterFlush().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // Cancellation stops here. Nothing below this point observes the token.
                if (hooks?.CommitStarted is not null) await hooks.CommitStarted().ConfigureAwait(false);
            }
            // Existing target was pinned against rename/delete through all asynchronous work.
            prior?.Dispose();
            if (prior is not null) File.Replace(temporary, canonical, null);
            else File.Move(temporary, canonical);
            committed = true;
        }
        finally
        {
            prior?.Dispose();
            if (!committed && temporaryIdentity is not null)
            {
                try { DeleteOwnedFile(temporary, temporaryIdentity.Value); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { throw new AtomicFileCleanupException(temporary, temporaryIdentity.Value); }
            }
        }
    }

    internal static void DeleteOwnedFile(string path, NativeFileIdentity expectedIdentity)
    {
        using SafeFileHandle handle = NativeFileSystem.Open(path, NativeFileSystem.ReadAttributes | NativeFileSystem.DeleteAccess, shareDelete: true);
        if (NativeFileSystem.Inspect(handle, path, directory: false) != expectedIdentity)
            throw new PathBoundaryException("IDENTITY_CHANGED");
        NativeFileSystem.DeleteByHandle(handle);
    }
}
