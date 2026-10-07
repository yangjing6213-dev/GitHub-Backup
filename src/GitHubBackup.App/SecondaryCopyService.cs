using System.Security.Principal;

namespace GitHubBackup.App;

internal sealed record SecondaryCopyReport(
    string Owner,
    string DestinationRoot,
    int FileCount,
    long BytesCopied,
    IReadOnlyList<string> Warnings)
{
    internal bool Success => FileCount > 0 && Warnings.Count == 0;
}

/// <summary>
/// Creates a verified, private second local copy without deleting or merging
/// into an existing destination.
/// </summary>
internal sealed class SecondaryCopyService
{
    private static readonly string[] AllowedRoots = ["manifests", "logs", "metadata", "mirrors", "wikis", "releases", "actions", "progress"];

    internal async Task<SecondaryCopyReport> CopyLatestAsync(string ownerRoot, string owner, string destinationParent,
        OperationJob job, CancellationToken token)
    {
        if (!AuthConfigLease.IsLogin(owner)) throw new ArgumentException("OWNER_INVALID");
        string source = NativeFileSystem.CanonicalPath(ownerRoot);
        if (!string.Equals(Path.GetFileName(source), owner, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SECONDARY_OWNER_ROOT_INVALID");
        using PathLease sourceLease = SummaryStore.RequirePrivateDirectory(source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("SECONDARY_SOURCE_MISSING");
        string parent = NativeFileSystem.CanonicalPath(destinationParent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("SECONDARY_DESTINATION_PARENT_MISSING");
        if (IsSameOrChild(parent, source)) throw new UnauthorizedAccessException("SECONDARY_DESTINATION_INSIDE_BACKUP");
        using PathLease parentLease = SummaryStore.RequirePrivateDirectory(parent);
        SourceIntegrityResult integrity = new SourceIntegrityAudit().ValidateExistingTrees(source, [], true);
        if (!integrity.Allowed) throw new UnauthorizedAccessException(integrity.UnsafePaths[0].ErrorCode);
        _ = await new ManifestStore().ReadLatestAsync(source, token).ConfigureAwait(false);

        string destination = Path.Combine(parent, "GitHubBackup-Secondary-" + owner + "-" + RunIdFactory.Create(TimeProvider.System));
        AclPolicy.CreateRestrictedDirectory(destination, WindowsIdentity.GetCurrent().User!, requireNew: true);
        int count = 0; long bytes = 0;
        foreach (string rootName in AllowedRoots)
        {
            token.ThrowIfCancellationRequested();
            string sourceRoot = Path.Combine(source, rootName);
            if (!Directory.Exists(sourceRoot)) continue;
            foreach (string file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                string canonical = NativeFileSystem.CanonicalPath(file);
                if (!IsSafeFile(canonical, sourceRoot)) throw new IOException("SECONDARY_SOURCE_PATH_INVALID");
                FileAttributes attributes = File.GetAttributes(canonical);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("SECONDARY_REPARSE_REJECTED");
                string relative = Path.GetRelativePath(source, canonical);
                string target = Path.Combine(destination, relative);
                string? targetParent = Path.GetDirectoryName(target);
                if (targetParent is null) throw new IOException("SECONDARY_TARGET_PATH_INVALID");
                EnsurePrivateDirectories(destination, targetParent);
                using var inputHandle = MirrorSafeCopy.OpenPrivate(canonical, shareWrite: false);
                await using FileStream input = new(inputHandle, FileAccess.Read);
                await using FileStream output = AclPolicy.CreateRestrictedFile(target, WindowsIdentity.GetCurrent().User!);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                output.Flush(true);
                count++; bytes = checked(bytes + new FileInfo(canonical).Length);
            }
        }
        return new(owner, destination, count, bytes, count == 0 ? ["SECONDARY_SOURCE_EMPTY"] : []);
    }

    private static bool IsSafeFile(string file, string root)
    {
        string normalized = Path.TrimEndingDirectorySeparator(NativeFileSystem.CanonicalPath(root));
        return string.Equals(file, normalized, StringComparison.OrdinalIgnoreCase)
            || file.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsurePrivateDirectories(string root, string target)
    {
        string relative = Path.GetRelativePath(root, target);
        string current = root;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current)) AclPolicy.CreateRestrictedDirectory(current, WindowsIdentity.GetCurrent().User!, requireNew: true);
            else using (SummaryStore.RequirePrivateDirectory(current)) { }
        }
    }

    private static bool IsSameOrChild(string path, string parent)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(path);
        string normalizedParent = Path.TrimEndingDirectorySeparator(parent);
        return string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
