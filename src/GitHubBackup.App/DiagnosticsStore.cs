using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

internal sealed record DiagnosticExportPreview(string PreviewId, IReadOnlyList<string> SourcePaths,
    string SourceFingerprint, string PreviewFilePath, string PreviewSha256, IReadOnlyList<string> DisplayLines);
internal enum DiagnosticSaveStatus { Saved, Cancelled, Stale }

internal sealed class DiagnosticsStore
{
    private const long MaxPreviewBytes = 100L * 1024 * 1024;
    private readonly AppPaths paths;
    private readonly string? ownerRoot;
    private readonly Dictionary<string, DiagnosticExportPreview> issued = new(StringComparer.Ordinal);

    internal DiagnosticsStore(AppPaths paths, string? approvedOwnerRoot = null)
    {
        this.paths = paths;
        ownerRoot = approvedOwnerRoot is null ? null : NativeFileSystem.CanonicalPath(approvedOwnerRoot);
    }

    internal async Task<DiagnosticExportPreview> PreviewAsync(IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        if (sourcePaths.Count is < 1 or > 20) throw new ArgumentException("Choose 1–20 diagnostic sources.", nameof(sourcePaths));
        string[] sources = sourcePaths.Select(ApprovedSource).ToArray();
        if (sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new ArgumentException("Duplicate diagnostic source.", nameof(sourcePaths));
        string before = await FingerprintAsync(sources, cancellationToken).ConfigureAwait(false);
        using AppDataPathLease directory = AppDataPathPolicy.Acquire(paths, paths.DiagnosticLogRoot,
            AppDataEntryKind.Directory, createMissingDirectories: true);
        string id = Guid.NewGuid().ToString("N");
        string previewPath = Path.Combine(paths.DiagnosticLogRoot, "preview-" + id + ".txt");
        var display = new Queue<string>();
        await AtomicFile.WriteAsync(previewPath, async (output, token) =>
        {
            foreach (string source in sources)
            {
                await ReadSourceAsync(source, async (input, sourceToken) =>
                {
                    byte[] buffer = new byte[4096];
                    var safe = new SafeTextStream();
                    int count;
                    while ((count = await input.ReadAsync(buffer, sourceToken).ConfigureAwait(false)) != 0)
                        await AppendAsync(safe.Push(buffer.AsSpan(0, count)), output, display, sourceToken).ConfigureAwait(false);
                    await AppendAsync(safe.Complete() + "\n", output, display, sourceToken).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
        using (AppDataPathPolicy.Acquire(paths, previewPath, AppDataEntryKind.File)) { }
        string after = await FingerprintAsync(sources, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            DeletePreview(previewPath);
            throw new IOException("DIAGNOSTIC_SOURCE_CHANGED");
        }
        string hash = await HashApprovedPreviewAsync(previewPath, cancellationToken).ConfigureAwait(false);
        var preview = new DiagnosticExportPreview(id, Array.AsReadOnly(sources), after, previewPath, hash,
            Array.AsReadOnly(display.ToArray()));
        lock (issued) issued.Add(id, preview);
        Prune();
        return preview;
    }

    internal async Task<DiagnosticSaveStatus> SaveAsync(DiagnosticExportPreview preview, string targetPath,
        bool userConfirmed, CancellationToken cancellationToken)
    {
        if (!userConfirmed) return DiagnosticSaveStatus.Cancelled;
        DiagnosticExportPreview? original;
        lock (issued) issued.TryGetValue(preview.PreviewId, out original);
        if (original is null || original.PreviewFilePath != preview.PreviewFilePath
            || original.PreviewSha256 != preview.PreviewSha256 || original.SourceFingerprint != preview.SourceFingerprint
            || !original.SourcePaths.SequenceEqual(preview.SourcePaths, StringComparer.OrdinalIgnoreCase))
            return DiagnosticSaveStatus.Stale;
        string target = NativeFileSystem.CanonicalPath(targetPath);
        if (Within(target, paths.LocalAppDataRoot) || (ownerRoot is not null && Within(target, ownerRoot)))
            throw new UnauthorizedAccessException("DIAGNOSTIC_EXPORT_TARGET_PROTECTED");
        try
        {
            string fingerprint = await FingerprintAsync(original.SourcePaths, cancellationToken).ConfigureAwait(false);
            if (fingerprint != original.SourceFingerprint) return DiagnosticSaveStatus.Stale;
            using AppDataPathLease previewLease = AppDataPathPolicy.Acquire(paths, original.PreviewFilePath, AppDataEntryKind.File);
            // Hash and copy the same read-only, no-write/no-delete shared handle.
            await using var input = new FileStream(original.PreviewFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)) != original.PreviewSha256)
                return DiagnosticSaveStatus.Stale;
            input.Position = 0;
            await AtomicFile.WriteAsync(target, async (output, token) =>
            {
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            lock (issued) issued.Remove(preview.PreviewId);
            return DiagnosticSaveStatus.Saved;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or PathBoundaryException)
        { return DiagnosticSaveStatus.Stale; }
    }

    internal void Prune()
    {
        using AppDataPathLease directory = AppDataPathPolicy.Acquire(paths, paths.DiagnosticLogRoot,
            AppDataEntryKind.Directory, createMissingDirectories: true);
        string appLogs = Path.Combine(paths.LocalAppDataRoot, "logs");
        using AppDataPathLease logDirectory = AppDataPathPolicy.Acquire(paths, appLogs,
            AppDataEntryKind.Directory, createMissingDirectories: true);
        var files = Directory.EnumerateFiles(paths.DiagnosticLogRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => IsDiagnosticName(Path.GetFileName(path)) || IsPreviewName(Path.GetFileName(path)))
            .Concat(Directory.EnumerateFiles(appLogs, "*", SearchOption.TopDirectoryOnly)
                .Where(path => IsDiagnosticName(Path.GetFileName(path))))
            .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal).ToArray();
        long total = 0;
        for (int i = 0; i < files.Length; i++)
        {
            FileInfo file = files[i];
            using AppDataPathLease lease = AppDataPathPolicy.Acquire(paths, file.FullName, AppDataEntryKind.File);
            long length = file.Length;
            if (i < 20 && total + length <= MaxPreviewBytes) { total += length; continue; }
            NativeFileIdentity identity = lease.Identity!.Value;
            // Release the no-delete file lease before deleting the exact identity by handle.
            lease.Dispose();
            AtomicFile.DeleteOwnedFile(file.FullName, identity);
            lock (issued)
            {
                string[] removed = issued.Where(pair => string.Equals(pair.Value.PreviewFilePath, file.FullName,
                    StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray();
                foreach (string id in removed) issued.Remove(id);
            }
        }
    }

    private string ApprovedSource(string source)
    {
        string path = NativeFileSystem.CanonicalPath(source);
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileName(path);
        bool appDiagnostic = string.Equals(directory, paths.DiagnosticLogRoot, StringComparison.OrdinalIgnoreCase)
            && IsDiagnosticName(name);
        bool appLog = string.Equals(directory, Path.Combine(paths.LocalAppDataRoot, "logs"), StringComparison.OrdinalIgnoreCase)
            && IsDiagnosticName(name);
        bool fallback = string.Equals(directory, paths.DiagnosticFallbackRoot, StringComparison.OrdinalIgnoreCase)
            && IsSummaryName(name);
        bool owner = ownerRoot is not null &&
            ((string.Equals(directory, Path.Combine(ownerRoot, "logs"), StringComparison.OrdinalIgnoreCase) && IsBackupName(name))
            || (string.Equals(directory, Path.Combine(ownerRoot, "manifests"), StringComparison.OrdinalIgnoreCase) && IsSummaryName(name)));
        if (!(appDiagnostic || appLog || fallback || owner))
            throw new UnauthorizedAccessException("UNAPPROVED_DIAGNOSTIC_SOURCE");
        return path;
    }

    private static bool IsDiagnosticName(string name) => SafeName(name, "diagnostic-", ".log");
    private static bool IsPreviewName(string name) => SafeName(name, "preview-", ".txt");
    private static bool IsBackupName(string name) => SafeName(name, "backup-", ".log");
    private static bool IsSummaryName(string name) => SafeName(name, "summary-", ".json");
    private static bool SafeName(string name, string prefix, string suffix) =>
        name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal)
        && name.Length > prefix.Length + suffix.Length
        && name[prefix.Length..^suffix.Length].All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    private static bool Within(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private async Task<string> FingerprintAsync(IReadOnlyList<string> sources, CancellationToken token)
    {
        var builder = new StringBuilder();
        foreach (string path in sources)
        {
            await ReadSourceAsync(path, async (input, sourceToken) =>
            {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, sourceToken).ConfigureAwait(false));
                builder.Append(path).Append('|').Append(input.Length).Append('|')
                    .Append(File.GetLastWriteTimeUtc(input.SafeFileHandle).Ticks).Append('|')
                    .Append(hash).Append('\n');
            }, token).ConfigureAwait(false);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private async Task ReadSourceAsync(string path, Func<FileStream, CancellationToken, Task> read, CancellationToken token)
    {
        string directory = Path.GetDirectoryName(path)!;
        bool appData = Within(path, paths.LocalAppDataRoot);
        using IDisposable directoryLease = appData
            ? AppDataPathPolicy.Acquire(paths, directory, AppDataEntryKind.Directory)
            : SummaryStore.RequirePrivateDirectory(ownerRoot!);
        using IDisposable fileLease = appData
            ? AppDataPathPolicy.Acquire(paths, path, AppDataEntryKind.File)
            : OpenPrivateOwnerFile(path, directory);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await read(input, token).ConfigureAwait(false);
    }

    private static IDisposable OpenPrivateOwnerFile(string path, string directory)
    {
        PathLease lease = SummaryStore.RequirePrivateDirectory(directory);
        try
        {
            SafeFileHandle file = NativeFileSystem.Open(path);
            lease.Add(file);
            NativeFileSystem.Inspect(file, path, directory: false);
            if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(file), WindowsIdentity.GetCurrent().User!, true) != AclRisk.Safe)
                throw new UnauthorizedAccessException("ACL_PRIVATE_BOUNDARY_REQUIRED");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static async Task AppendAsync(string text, Stream output, Queue<string> display, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (output.Length + bytes.Length > MaxPreviewBytes) throw new IOException("DIAGNOSTIC_PREVIEW_TOO_LARGE");
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
        foreach (string line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            display.Enqueue(line);
            if (display.Count > 2000) display.Dequeue();
        }
    }

    private async Task<string> HashApprovedPreviewAsync(string path, CancellationToken token)
    {
        using AppDataPathLease lease = AppDataPathPolicy.Acquire(paths, path, AppDataEntryKind.File);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
    }

    private void DeletePreview(string path)
    {
        using AppDataPathLease lease = AppDataPathPolicy.Acquire(paths, path, AppDataEntryKind.File);
        NativeFileIdentity identity = lease.Identity!.Value;
        lease.Dispose();
        AtomicFile.DeleteOwnedFile(path, identity);
    }
}
