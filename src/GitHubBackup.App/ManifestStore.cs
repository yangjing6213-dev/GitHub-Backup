using System.Globalization;
using System.Text.Json;

namespace GitHubBackup.App;

internal sealed class ManifestStore
{
    private readonly TimeZoneInfo legacyTimeZone;

    internal ManifestStore(TimeZoneInfo? legacyTimeZone = null) =>
        this.legacyTimeZone = legacyTimeZone ?? TimeZoneInfo.Local;

    internal static IReadOnlyList<RepositoryDescriptor> Reconcile(string ownerRoot,
        IReadOnlyList<RepositoryDescriptor> previous, IReadOnlyList<RepositoryDescriptor> current,
        IReadOnlyList<LegacyBinding> bindings) =>
        RepositoryNameMapper.Reconcile(ownerRoot, previous, current, bindings);

    internal async Task<IReadOnlyList<RepositoryDescriptor>> ReadLatestAsync(string ownerRoot, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(NativeFileSystem.CanonicalPath(ownerRoot), "manifests");
        using PathLease ownerLease = SummaryStore.RequirePrivateDirectory(NativeFileSystem.CanonicalPath(ownerRoot));
        try { _ = File.GetAttributes(directory); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return []; }
        using PathLease lease = SummaryStore.RequirePrivateDirectory(directory);
        string? latest = Directory.EnumerateFiles(directory, "repositories-*.json", SearchOption.TopDirectoryOnly)
            .Where(x => IsManifestName(Path.GetFileName(x)))
            .Select(path => (Path: path, Timestamp: RunTimestamp(Path.GetFileNameWithoutExtension(path)[13..])))
            .OrderByDescending(x => x.Timestamp.HasValue)
            .ThenByDescending(x => x.Timestamp)
            .ThenByDescending(x => Path.GetFileName(x.Path), StringComparer.Ordinal)
            .Select(x => x.Path)
            .FirstOrDefault();
        if (latest is null) return [];
        using JsonDocument json = await MetadataJson.ReadDocumentAsync(latest, cancellationToken).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Manifest must be an array.");
        var result = new List<RepositoryDescriptor>();
        foreach (JsonElement element in json.RootElement.EnumerateArray())
        {
            RepositoryDescriptor item = JsonSerializer.Deserialize<RepositoryDescriptor>(element, MetadataJson.Options)!;
            if (item is null || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.NameWithOwner)
                || string.IsNullOrWhiteSpace(item.Url)) throw new JsonException("Invalid repository manifest entry.");
            result.Add(item with { LocalName = item.LocalName ?? item.Name, RemoteState = item.RemoteState ?? "" });
        }
        return result;
    }

    internal async Task<string> WriteAsync(string ownerRoot, string runId,
        IReadOnlyList<RepositoryDescriptor> repositories, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid run ID.", nameof(runId));
        string directory = Path.Combine(NativeFileSystem.CanonicalPath(ownerRoot), "manifests");
        using PathLease ownerLease = SummaryStore.RequirePrivateDirectory(NativeFileSystem.CanonicalPath(ownerRoot));
        using PathLease lease = SummaryStore.RequirePrivateDirectory(directory);
        string path = Path.Combine(directory, "repositories-" + runId + ".json");
        try
        {
            using var existing = NativeFileSystem.Open(path);
            NativeFileSystem.Inspect(existing, path, directory: false);
            if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(existing), System.Security.Principal.WindowsIdentity.GetCurrent().User!, true) != AclRisk.Safe)
                throw new UnauthorizedAccessException("ACL_PRIVATE_BOUNDARY_REQUIRED");
        }
        catch (FileNotFoundException) { }
        await AtomicFile.WriteAsync(path, (stream, token) =>
            JsonSerializer.SerializeAsync(stream, repositories, MetadataJson.Options, token), cancellationToken).ConfigureAwait(false);
        return path;
    }

    internal static async Task<JsonElement> ReadPagesAsync(string path, CancellationToken cancellationToken)
    {
        string canonical = NativeFileSystem.CanonicalPath(path);
        using PathLease lease = SummaryStore.RequirePrivateDirectory(Path.GetDirectoryName(canonical)!);
        using JsonDocument document = await MetadataJson.ReadDocumentAsync(canonical, cancellationToken).ConfigureAwait(false);
        JsonElement outer = document.RootElement;
        JsonValueKind pageKind = Path.GetFileName(canonical) is "workflows.pages.json" or "actions-runs.pages.json" or "actions-artifacts.pages.json"
            ? JsonValueKind.Object : JsonValueKind.Array;
        if (outer.ValueKind != JsonValueKind.Array || outer.EnumerateArray().Any(page => page.ValueKind != pageKind))
            throw new JsonException("Pages have an invalid outer shape.");
        return outer.Clone();
    }

    private static bool IsManifestName(string name) => name.StartsWith("repositories-", StringComparison.Ordinal)
        && name.EndsWith(".json", StringComparison.Ordinal)
        && name.Length > "repositories-.json".Length
        && name[13..^5].All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private DateTimeOffset? RunTimestamp(string runId)
    {
        if (runId.Length == 15 && DateTime.TryParseExact(runId, "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime legacy))
        {
            try { return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(legacy, legacyTimeZone)); }
            catch (ArgumentException) { return null; } // Invalid local time during a daylight-saving transition.
        }
        if (runId.Length > 20 && runId[19] == '-' &&
            runId[20..].All(Uri.IsHexDigit) &&
            DateTime.TryParseExact(runId[..19], "yyyyMMdd'T'HHmmssfff'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime utc))
            return new DateTimeOffset(utc);
        return null; // Unrecognized IDs remain readable and sort after dated IDs by ordinal filename.
    }
}
