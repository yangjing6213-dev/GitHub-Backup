using System.Security.Principal;

namespace GitHubBackup.App;

internal sealed record ValidatedLogDocument(string DisplayName, IReadOnlyList<string> Lines);
internal sealed record SafeOpenResult(bool Opened, string ErrorCode);

internal sealed class SafeOpenService(IProcessRunner runner, Func<ToolDetection>? resolveExplorer = null)
{
    internal async Task<ValidatedLogDocument> ReadLatestLogAsync(string ownerRoot, BackupSummary summary, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string owner = NativeFileSystem.CanonicalPath(ownerRoot);
        string logs = Path.Combine(owner, "logs");
        string path = ApprovedLogPath(logs, summary.Log);
        using var ownerLease = SummaryStore.RequirePrivateDirectory(owner);
        using var logLease = SummaryStore.RequirePrivateDirectory(logs);
        using var handle = NativeFileSystem.Open(path, shareWrite: false);
        var identity = NativeFileSystem.Inspect(handle, path, false);
        if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(handle), WindowsIdentity.GetCurrent().User!, true) != AclRisk.Safe)
            throw new UnauthorizedAccessException("ACL_PRIVATE_BOUNDARY_REQUIRED");
        // Read the inspected no-follow handle, never reopen the path after validation.
        await using var input = new FileStream(handle, FileAccess.Read);
        var safe = new SafeTextStream(); var tail = new Queue<string>(); byte[] buffer = new byte[4096];
        void Append(string text)
        {
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                tail.Enqueue(line); if (tail.Count > 2000) tail.Dequeue();
            }
        }
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            Append(safe.Push(buffer.AsSpan(0, count)));
        Append(safe.Complete()); token.ThrowIfCancellationRequested();
        if (NativeFileSystem.Inspect(handle, path, false) != identity) throw new PathBoundaryException("IDENTITY_CHANGED");
        // A fixed title avoids reflecting even a token-shaped filename from untrusted history.
        return new("最近一次运行日志", Array.AsReadOnly(tail.ToArray()));
    }

    private static string ApprovedLogPath(string logs, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Split('\\').Any(part => part is "." or ".."))
            throw new UnauthorizedAccessException("UNAPPROVED_RUN_LOG_PATH");
        string path = NativeFileSystem.CanonicalPath(candidate), name = Path.GetFileName(path);
        if (!string.Equals(Path.GetDirectoryName(path), logs, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith("backup-", StringComparison.Ordinal) || !name.EndsWith(".log", StringComparison.Ordinal)
            || name.Length <= "backup-.log".Length || !name[7..^4].All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new UnauthorizedAccessException("UNAPPROVED_RUN_LOG_PATH");
        return path;
    }

    internal async Task<SafeOpenResult> OpenBackupFolderAsync(string backupRoot, OperationJob job, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            string canonical = NativeFileSystem.CanonicalPath(backupRoot);
            RequireFixedFolder(canonical);
            using var directory = SummaryStore.RequirePrivateDirectory(canonical);
            using var handle = NativeFileSystem.Open(canonical);
            var identity = NativeFileSystem.Inspect(handle, canonical, true);
            var explorer = (resolveExplorer ?? (() => new ToolPathPolicy().ResolveSystem32Executable("explorer.exe")))();
            using var executable = ExecutableTrust.Acquire(explorer.AbsolutePath, explorer.Identity);
            // Revalidate read-only immediately before handing one path argument to the runner.
            RequireFixedFolder(canonical);
            using var repeat = SummaryStore.RequirePrivateDirectory(canonical);
            using var repeatedHandle = NativeFileSystem.Open(canonical);
            if (NativeFileSystem.Inspect(repeatedHandle, canonical, true) != identity)
                throw new PathBoundaryException("IDENTITY_CHANGED");
            token.ThrowIfCancellationRequested();
            var result = await runner.RunAsync(new(explorer.AbsolutePath, [canonical], Path.GetDirectoryName(explorer.AbsolutePath)!,
                ChildEnvironmentBuilder.CreateCurrentBase([]), TimeSpan.FromSeconds(30), ExpectedExecutableIdentity: explorer.Identity),
                job, null, token).ConfigureAwait(false);
            return ProcessOutcomeClassifier.Classify(result) == ProcessTerminalKind.Succeeded ? new(true, "") : new(false, "SAFE_FOLDER_OPEN_FAILED");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return new(false, "SAFE_FOLDER_OPEN_REJECTED"); }
    }

    private static void RequireFixedFolder(string canonical)
    {
        string volume = Path.GetPathRoot(canonical)!;
        if (Path.TrimEndingDirectorySeparator(volume).Equals(canonical, StringComparison.OrdinalIgnoreCase)
            || new DriveInfo(volume).DriveType != DriveType.Fixed) throw new IOException("STORAGE_FIXED_VOLUME_REQUIRED");
    }
}
