using System.Security.Principal;
using System.Text.Json;

namespace GitHubBackup.App;

internal sealed record BackupCheckpoint(
    int SchemaVersion,
    string Owner,
    string RunId,
    DateTimeOffset UpdatedAt,
    bool PausedByRateLimit,
    int TotalRepositories,
    IReadOnlyList<string> CompletedRepositories,
    string? CurrentRepository,
    string Phase,
    DateTimeOffset? RateLimitReset);

/// <summary>
/// Stores the small amount of state needed to explain and continue a paused run.
/// It never contains repository content or credentials.
/// </summary>
internal sealed class BackupCheckpointStore
{
    private const int CurrentSchemaVersion = 1;
    private const string FileName = "backup-progress.json";

    internal async Task WriteAsync(string ownerRoot, BackupCheckpoint checkpoint, CancellationToken token)
    {
        if (!AuthConfigLease.IsLogin(checkpoint.Owner)) throw new ArgumentException("CHECKPOINT_OWNER_INVALID");
        string root = NativeFileSystem.CanonicalPath(ownerRoot);
        if (!string.Equals(Path.GetFileName(root), checkpoint.Owner, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("CHECKPOINT_OWNER_ROOT_INVALID");
        string progress = Path.Combine(root, "progress");
        using PathLease ownerLease = SummaryStore.RequirePrivateDirectory(root);
        if (!Directory.Exists(progress)) AclPolicy.CreateRestrictedDirectory(progress, WindowsIdentity.GetCurrent().User!, requireNew: true);
        using PathLease progressLease = SummaryStore.RequirePrivateDirectory(progress);
        string path = Path.Combine(progress, FileName);
        await AtomicFile.WriteAsync(path, (stream, cancellationToken) =>
            JsonSerializer.SerializeAsync(stream, checkpoint with { SchemaVersion = CurrentSchemaVersion }, MetadataJson.Options, cancellationToken), token)
            .ConfigureAwait(false);
    }

    internal async Task<BackupCheckpoint?> ReadAsync(string ownerRoot, string owner, CancellationToken token)
    {
        if (!AuthConfigLease.IsLogin(owner)) throw new ArgumentException("CHECKPOINT_OWNER_INVALID");
        string root = NativeFileSystem.CanonicalPath(ownerRoot);
        if (!string.Equals(Path.GetFileName(root), owner, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("CHECKPOINT_OWNER_ROOT_INVALID");
        string progress = Path.Combine(root, "progress");
        if (!Directory.Exists(progress)) return null;
        using PathLease ownerLease = SummaryStore.RequirePrivateDirectory(root);
        using PathLease progressLease = SummaryStore.RequirePrivateDirectory(progress);
        string path = Path.Combine(progress, FileName);
        if (!File.Exists(path)) return null;
        using JsonDocument document = await MetadataJson.ReadDocumentAsync(path, token).ConfigureAwait(false);
        BackupCheckpoint? checkpoint = document.RootElement.Deserialize<BackupCheckpoint>(MetadataJson.Options);
        if (checkpoint is null || checkpoint.SchemaVersion != CurrentSchemaVersion
            || !AuthConfigLease.IsLogin(checkpoint.Owner)
            || !string.Equals(checkpoint.Owner, owner, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(checkpoint.RunId)
            || checkpoint.RunId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            || checkpoint.TotalRepositories < 0
            || checkpoint.CompletedRepositories.Any(name => !IsSafeLocalName(name)))
            throw new JsonException("Invalid backup checkpoint.");
        return checkpoint;
    }

    private static bool IsSafeLocalName(string name) => name.Length is > 0 and <= 100
        && name is not "." and not ".."
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
