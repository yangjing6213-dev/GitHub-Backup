using System.Text.Json;

namespace GitHubBackup.App;

internal sealed record LegacyIdentityEvidence(string RepositoryJsonPath);
internal sealed record LegacyBinding(bool Bound, long BoundRepositoryId, string LocalName, string ErrorCode, string OwnerRoot);

internal static class LegacyIdentityBinder
{
    internal static async Task<LegacyBinding> TryBindAsync(string approvedOwnerRoot, RepositoryDescriptor legacy,
        IReadOnlyList<RepositoryDescriptor> currentRepositories, string authenticatedOwner,
        LegacyIdentityEvidence evidence, CancellationToken cancellationToken)
    {
        string ownerRoot = NativeFileSystem.CanonicalPath(approvedOwnerRoot);
        LegacyBinding unresolved = new(false, 0, legacy.LocalName, "LEGACY_IDENTITY_UNRESOLVED", ownerRoot);
        if (legacy.RepositoryId != 0 || !AuthConfigLease.IsLogin(authenticatedOwner)
            || !RepositoryNameMapper.IsSafeLocalName(legacy.LocalName)
            || !RepositoryNameMapper.IsSafeLocalName(legacy.Name)
            || !string.Equals(legacy.NameWithOwner, authenticatedOwner + "/" + legacy.Name,
                StringComparison.OrdinalIgnoreCase))
            return unresolved;
        try
        {
            string path = NativeFileSystem.CanonicalPath(Path.Combine(ownerRoot, "metadata", legacy.LocalName, "repository.json"));
            string localDirectory = Path.GetDirectoryName(path)!;
            string metadataDirectory = Path.GetDirectoryName(localDirectory)!;
            if (!string.Equals(path, NativeFileSystem.CanonicalPath(evidence.RepositoryJsonPath), StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path) != "repository.json"
                || !string.Equals(Path.GetFileName(localDirectory), legacy.LocalName, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(metadataDirectory) != "metadata"
                || !string.Equals(Path.GetFileName(ownerRoot), authenticatedOwner, StringComparison.OrdinalIgnoreCase)
                || !new SourceIntegrityAudit().ValidateExistingTrees(metadataDirectory, [path], true).Allowed)
                return unresolved;

            using PathLease ownerLease = SummaryStore.RequirePrivateDirectory(ownerRoot);
            using PathLease metadataLease = SummaryStore.RequirePrivateDirectory(metadataDirectory);
            using PathLease localLease = SummaryStore.RequirePrivateDirectory(localDirectory);
            using JsonDocument json = await MetadataJson.ReadDocumentAsync(path, cancellationToken, MetadataJson.MaxFileBytes)
                .ConfigureAwait(false);
            JsonElement item = json.RootElement;
            long id = Required(item, "id").GetInt64();
            string? fullName = Required(item, "full_name").GetString();
            string? owner = Required(Required(item, "owner"), "login").GetString();
            if (id <= 0 || fullName != legacy.NameWithOwner
                || !string.Equals(owner, authenticatedOwner, StringComparison.OrdinalIgnoreCase)
                || currentRepositories.Count(r => r.RepositoryId == id
                    && string.Equals(r.NameWithOwner, authenticatedOwner + "/" + r.Name,
                        StringComparison.OrdinalIgnoreCase)) != 1)
                return unresolved;
            return new(true, id, legacy.LocalName, "", ownerRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { return unresolved; }
    }

    private static JsonElement Required(JsonElement item, string key)
    {
        if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count(property => property.NameEquals(key)) != 1)
            throw new JsonException("LEGACY_IDENTITY_INVALID");
        return item.GetProperty(key);
    }
}
