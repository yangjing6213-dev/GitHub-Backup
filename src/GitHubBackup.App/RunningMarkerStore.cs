using System.Text.Json;

namespace GitHubBackup.App;

internal sealed record RunningMarker(string RunId, BackupMode Mode, string Owner, DateTimeOffset StartedAt);

internal static class RunningMarkerStore
{
    private sealed class PendingCleanupException : IOException
    { internal PendingCleanupException() : base("BACKUP_CLEANUP_PENDING") { } }

    internal static async Task<string?> ReadAsync(string ownerRoot, CancellationToken token, AppPaths? fallbackPaths = null)
    {
        AppDataPathLease? fallbackLease = null;
        try
        {
            string root = NativeFileSystem.CanonicalPath(ownerRoot);
            using var directory = SummaryStore.RequirePrivateDirectory(root);
            if (!File.Exists(Path.Combine(root, "RUNNING.json"))) return null;
            OperationLockLease owner;
            try { owner = OperationLocks.AcquireExistingStorage(root); }
            catch (IOException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 })
            { return "RUNNING_MARKER_ACTIVE"; }
            using var ownerLease = owner;
            var (marker, _) = await ReadMarkerAsync(root, token).ConfigureAwait(false);
            string? summaryPath = null;
            try
            {
                await ValidateCompletedAsync(root, marker, null, false, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            {
                if (fallbackPaths is null) return "RUNNING_MARKER_INCOMPLETE";
                summaryPath = Path.Combine(fallbackPaths.DiagnosticFallbackRoot, "summary-" + marker.RunId + ".json");
                try
                {
                    // Keep the protected fallback path pinned through validation.
                    fallbackLease = AppDataPathPolicy.Acquire(fallbackPaths, summaryPath, AppDataEntryKind.File);
                    await ValidateCompletedAsync(root, marker, summaryPath, false, token).ConfigureAwait(false);
                }
                catch (Exception fallbackError) when (fallbackError is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
                { return "RUNNING_MARKER_INCOMPLETE"; }
            }
            return "RUNNING_MARKER_COMPLETED_RESIDUE";
        }
        catch (FileNotFoundException) { return Directory.Exists(ownerRoot) && File.Exists(Path.Combine(ownerRoot, "RUNNING.json")) ? "RUNNING_MARKER_INCOMPLETE" : null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { return "RUNNING_MARKER_UNRESOLVED"; }
        finally { fallbackLease?.Dispose(); }
    }

    internal static Task WriteAsync(OperationLockLease owner, RunningMarker marker, CancellationToken token) =>
        AtomicFile.WriteAsync(Path.Combine(owner.RequireOwnerRoot(), "RUNNING.json"),
            (stream, ct) => JsonSerializer.SerializeAsync(stream, marker, MetadataJson.Options, ct), token, requireNew: true);

    internal static async Task RemoveCompletedAsync(OperationLockLease owner, string? expectedRunId, string? summaryPath,
        CancellationToken token, AppPaths? fallbackPaths = null)
    {
        string root = owner.RequireOwnerRoot(), path = Path.Combine(root, "RUNNING.json");
        if (!File.Exists(path)) return;
        var (marker, identity) = await ReadMarkerAsync(root, token).ConfigureAwait(false);
        if (expectedRunId is not null && marker.RunId != expectedRunId) throw new IOException("RUNNING_MARKER_UNRESOLVED");
        try { await ValidateCompletedAsync(root, marker, summaryPath, expectedRunId is not null, token).ConfigureAwait(false); }
        catch (Exception ex) when (fallbackPaths is not null && expectedRunId is null && summaryPath is null
            && ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException
            && ex is not PendingCleanupException)
        {
            string fallback = Path.Combine(fallbackPaths.DiagnosticFallbackRoot, "summary-" + marker.RunId + ".json");
            using var fallbackLease = AppDataPathPolicy.Acquire(fallbackPaths, fallback, AppDataEntryKind.File);
            await ValidateCompletedAsync(root, marker, fallback, false, token).ConfigureAwait(false);
            AtomicFile.DeleteOwnedFile(path, identity);
            return;
        }
        AtomicFile.DeleteOwnedFile(path, identity);
    }

    private static async Task<(RunningMarker Marker, NativeFileIdentity Identity)> ReadMarkerAsync(string root, CancellationToken token)
    {
        string path = Path.Combine(root, "RUNNING.json");
        NativeFileIdentity identity;
        using (var file = MirrorSafeCopy.OpenPrivate(path, shareWrite: false)) identity = NativeFileSystem.Inspect(file, path, false);
        using var json = await MetadataJson.ReadDocumentAsync(path, token, 8192).ConfigureAwait(false);
        string[] properties = json.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        if (properties.Length != 4 || new[] { "runId", "mode", "owner", "startedAt" }.Any(n => properties.Count(p => p == n) != 1))
            throw new IOException("RUNNING_MARKER_UNRESOLVED");
        var marker = json.RootElement.Deserialize<RunningMarker>(MetadataJson.Options) ?? throw new IOException("RUNNING_MARKER_UNRESOLVED");
        BackupRunContext.RequireRunId(marker.RunId);
        if (marker.Mode is not (BackupMode.Daily or BackupMode.Full)
            || marker.StartedAt == default || marker.StartedAt.Offset != TimeSpan.Zero
            || !string.Equals(marker.Owner, Path.GetFileName(root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("RUNNING_MARKER_UNRESOLVED");
        return (marker, identity);
    }

    private static async Task ValidateCompletedAsync(string root, RunningMarker marker, string? summaryPath, bool currentRun, CancellationToken token)
    {
        string summary = summaryPath ?? Path.Combine(root, "manifests", "summary-" + marker.RunId + ".json");
        if (!File.Exists(summary)) throw new IOException("RUNNING_MARKER_UNRESOLVED");
        using var completed = await MetadataJson.ReadDocumentAsync(summary, token).ConfigureAwait(false);
        var fields = completed.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        if (fields.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length)
            throw new IOException("RUNNING_MARKER_UNRESOLVED");
        var record = completed.RootElement.Deserialize<BackupSummary>(MetadataJson.Options);
        if (!currentRun && record?.ErrorCode == "BACKUP_CLEANUP_PENDING")
            throw new PendingCleanupException();
        if (record is null || record.SchemaVersion != 2 || record.StartedRunId != marker.RunId || record.Mode != marker.Mode
            || record.StartedAt != marker.StartedAt || record.CompletedAt < marker.StartedAt
            || !string.Equals(record.Owner, marker.Owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(record.BackupRoot, root, StringComparison.OrdinalIgnoreCase)
            || record.Status is not (RunStatus.Pass or RunStatus.Partial or RunStatus.Fail or RunStatus.Cancelled))
            throw new IOException("RUNNING_MARKER_UNRESOLVED");
    }
}
