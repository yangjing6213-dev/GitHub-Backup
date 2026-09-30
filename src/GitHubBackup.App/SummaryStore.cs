using System.Security.Principal;
using System.Text.Json;

namespace GitHubBackup.App;

internal sealed class SummaryStore
{
    private readonly AppPaths paths;

    internal SummaryStore(AppPaths? paths = null) => this.paths = paths ??
        AppPaths.Create(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal async Task<SummaryReadResult> ReadLatestAsync(string ownerRoot, string fallbackSummaryRoot,
        CancellationToken cancellationToken, string? selectedOwner = null)
    {
        var warnings = new List<string>();
        var summaries = new List<BackupSummary>();
        var fallbackSummaries = new List<BackupSummary>();
        await ReadOwnerAsync(ownerRoot, summaries, warnings, cancellationToken).ConfigureAwait(false);
        bool fallbackTrusted = await ReadFallbackAsync(fallbackSummaryRoot, fallbackSummaries, warnings, cancellationToken).ConfigureAwait(false);
        if (!warnings.Contains("Owner summary root ignored: unsafe or inaccessible.")
            && await RunningMarkerStore.ReadAsync(ownerRoot, cancellationToken, fallbackTrusted ? paths : null).ConfigureAwait(false) is { } markerState)
            warnings.Add(markerState);
        IOrderedEnumerable<BackupSummary> ordered = summaries.Concat(fallbackSummaries)
            .Where(x => selectedOwner is null || string.Equals(x.Owner, selectedOwner, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.StartedAt ?? x.CompletedAt)
            .ThenByDescending(x => x.CompletedAt)
            .ThenByDescending(x => x.StartedRunId, StringComparer.Ordinal);
        var latest = ordered.FirstOrDefault();
        return new(latest, ordered.FirstOrDefault(x => x.Status is RunStatus.Pass or RunStatus.Partial), warnings)
        { LatestIsFallback = latest is not null && fallbackSummaries.Any(summary => ReferenceEquals(summary, latest)) };
    }

    internal async Task<string> WriteAsync(string ownerRoot, BackupSummary summary, string fallbackSummaryRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (summary.Status == RunStatus.Cancelled) summary = summary with { WasCancelled = true };
        ValidateSummary(summary);
        string owner = NativeFileSystem.CanonicalPath(ownerRoot);
        string fallback = NativeFileSystem.CanonicalPath(fallbackSummaryRoot);
        if (!string.Equals(fallback, paths.DiagnosticFallbackRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("UNAPPROVED_FALLBACK_ROOT");
        try
        {
            using PathLease ownerLease = RequirePrivateDirectory(owner);
            string manifestRoot = Path.Combine(owner, "manifests");
            if (!Directory.Exists(manifestRoot))
                AclPolicy.CreateRestrictedDirectory(manifestRoot, WindowsIdentity.GetCurrent().User!);
            using PathLease lease = RequirePrivateDirectory(manifestRoot);
            string path = Path.Combine(manifestRoot, "summary-" + summary.StartedRunId + ".json");
            await WriteFileAsync(path, summary, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is not AtomicFileCleanupException && ex is IOException or UnauthorizedAccessException)
        {
            return await WriteFallbackAsync(summary, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<string> WriteFallbackAsync(BackupSummary summary, CancellationToken token, AtomicFileCommitHooks? hooks = null)
    {
        ValidateSummary(summary);
        using AppDataPathLease directory = AppDataPathPolicy.Acquire(paths, paths.DiagnosticFallbackRoot,
            AppDataEntryKind.Directory, createMissingDirectories: true);
        string path = Path.Combine(paths.DiagnosticFallbackRoot, "summary-" + summary.StartedRunId + ".json");
        using (AppDataPathPolicy.Acquire(paths, path, AppDataEntryKind.File, allowMissingFile: true)) { }
        await WriteFileAsync(path, summary, token, hooks).ConfigureAwait(false);
        using (AppDataPathPolicy.Acquire(paths, path, AppDataEntryKind.File)) { }
        return path;
    }

    private static void ValidateSummary(BackupSummary summary)
    {
        if (summary.SchemaVersion != 2 || string.IsNullOrWhiteSpace(summary.Owner) ||
            string.IsNullOrWhiteSpace(summary.StartedRunId) ||
            summary.StartedRunId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            summary.RepositoryCount < 0 || summary.WarningCount < 0 || summary.SkippedWikiCount < 0 ||
            summary.FailedRepositories is null ||
            (summary.Status == RunStatus.Fail && summary.RepositoryCount == 0 &&
             (string.IsNullOrWhiteSpace(summary.FailurePhase) || string.IsNullOrWhiteSpace(summary.ErrorCode))))
            throw new ArgumentException("Invalid summary contract.", nameof(summary));
    }

    private static async Task WriteFileAsync(string path, BackupSummary summary, CancellationToken token, AtomicFileCommitHooks? hooks = null)
    {
        try
        {
            using var existing = NativeFileSystem.Open(path);
            NativeFileSystem.Inspect(existing, path, directory: false);
            RequirePrivate(existing);
        }
        catch (FileNotFoundException) { }
        await AtomicFile.WriteAsync(path, (stream, cancellationToken) =>
            JsonSerializer.SerializeAsync(stream, summary, MetadataJson.Options, cancellationToken), token, hooks).ConfigureAwait(false);
    }

    private async Task ReadOwnerAsync(string root, List<BackupSummary> summaries, List<string> warnings, CancellationToken token)
    {
        try
        {
            string manifestRoot = Path.Combine(NativeFileSystem.CanonicalPath(root), "manifests");
            using PathLease ownerLease = RequirePrivateDirectory(NativeFileSystem.CanonicalPath(root));
            using PathLease lease = RequirePrivateDirectory(manifestRoot);
            await ReadDirectoryAsync(manifestRoot, false, summaries, warnings, token).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { warnings.Add("Owner summary root ignored: unsafe or inaccessible."); }
    }

    private async Task<bool> ReadFallbackAsync(string root, List<BackupSummary> summaries, List<string> warnings, CancellationToken token)
    {
        try
        {
            string canonical = NativeFileSystem.CanonicalPath(root);
            if (!string.Equals(canonical, paths.DiagnosticFallbackRoot, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("Fallback summary root ignored: unapproved path.");
                return false;
            }
            using AppDataPathLease lease = AppDataPathPolicy.Acquire(paths, canonical, AppDataEntryKind.Directory);
            await ReadDirectoryAsync(canonical, true, summaries, warnings, token).ConfigureAwait(false);
            return true;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { warnings.Add("Fallback summary root ignored: unsafe or inaccessible."); }
        return false;
    }

    private async Task ReadDirectoryAsync(string directory, bool fallback, List<BackupSummary> summaries,
        List<string> warnings, CancellationToken token)
    {
        string[] files = Directory.EnumerateFiles(directory, "summary-*.json", SearchOption.TopDirectoryOnly)
            .Where(x => IsSummaryName(Path.GetFileName(x))).ToArray();
        foreach (string path in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using AppDataPathLease? lease = fallback
                    ? AppDataPathPolicy.Acquire(paths, path, AppDataEntryKind.File) : null;
                using JsonDocument document = await MetadataJson.ReadDocumentAsync(path, token, MetadataJson.MaxFileBytes).ConfigureAwait(false);
                JsonElement json = document.RootElement;
                if (json.ValueKind != JsonValueKind.Object) throw new JsonException();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (json.EnumerateObject().Any(property => !names.Add(property.Name)))
                    throw new JsonException("Duplicate summary field.");
                int version = 1;
                if (Property(json, "schemaVersion") is { } schema &&
                    (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out version)))
                    throw new JsonException("Unsupported schema version.");
                if (version is < 1 or > 2) throw new JsonException("Unsupported schema version.");
                string[] required = ["status", "owner", "startedRunId", "completedAt", "repositoryCount",
                    "warningCount", "failedRepositories", "backupRoot", "manifest", "log"];
                if (version == 2)
                    required = [.. required, "mode", "startedAt", "wasCancelled", "skippedWikiCount", "failurePhase", "errorCode"];
                if (required.Any(name => Property(json, name) is null)) throw new JsonException("Unrecognized summary.");
                BackupSummary summary = JsonSerializer.Deserialize<BackupSummary>(json, MetadataJson.Options)!;
                if (summary is null || string.IsNullOrWhiteSpace(summary.Owner) || string.IsNullOrWhiteSpace(summary.StartedRunId)
                    || summary.CompletedAt == default || summary.RepositoryCount < 0 || summary.WarningCount < 0
                    || summary.FailedRepositories is null || (version == 2 && summary.Status == RunStatus.Cancelled && !summary.WasCancelled))
                    throw new JsonException("Unrecognized summary.");
                summaries.Add(summary with
                {
                    SchemaVersion = version,
                    FailedRepositories = summary.FailedRepositories ?? [],
                    FailurePhase = summary.FailurePhase ?? "",
                    ErrorCode = summary.ErrorCode ?? "",
                    BackupRoot = summary.BackupRoot ?? "",
                    Manifest = summary.Manifest ?? "",
                    Log = summary.Log ?? "",
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { warnings.Add("Summary ignored (invalid, unsafe, or unsupported schema): " + Path.GetFileName(path)); }
        }
    }

    private static bool IsSummaryName(string name) => name.StartsWith("summary-", StringComparison.Ordinal)
        && name.EndsWith(".json", StringComparison.Ordinal)
        && name.Length > "summary-.json".Length
        && name[8..^5].All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static JsonElement? Property(JsonElement json, string name)
    {
        foreach (JsonProperty property in json.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    internal static PathLease RequirePrivateDirectory(string path)
    {
        PathLease lease = NativeFileSystem.PinDirectories(path);
        try
        {
            using var handle = NativeFileSystem.Open(path);
            NativeFileSystem.Inspect(handle, path, directory: true);
            RequirePrivate(handle);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static void RequirePrivate(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(handle), WindowsIdentity.GetCurrent().User!, true) != AclRisk.Safe)
            throw new UnauthorizedAccessException("ACL_PRIVATE_BOUNDARY_REQUIRED");
    }
}
