using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitHubBackup.App;

internal sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Warnings);

internal sealed class SettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    internal async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using AppDataPathLease parent = RequireDirectory();
        using AppDataPathLease file = RequireFile(allowMissing: true);
        if (file.Identity is null) return new(AppSettings.Default, []);
        try
        {
            using var stream = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = json.RootElement;
            string owner = root.GetProperty("owner").GetString()!;
            string backupRoot = root.GetProperty("backupRoot").GetString()!;
            string network = root.GetProperty("networkMode").GetString()!;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(backupRoot) || network != "Auto")
                throw new JsonException();
            int? consentVersion = root.TryGetProperty("apiCredentialConsentVersion", out JsonElement version)
                && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int value) ? value : null;
            string? consentLogin = root.TryGetProperty("apiCredentialConsentLogin", out JsonElement login) && login.ValueKind == JsonValueKind.String
                ? login.GetString() : null;
            string scope = root.TryGetProperty("repositoryScope", out JsonElement scopeElement) && scopeElement.ValueKind == JsonValueKind.String
                ? scopeElement.GetString() ?? "" : "";
            bool collaborators = root.TryGetProperty("includeCollaboratorRepositories", out JsonElement collaboratorElement)
                && collaboratorElement.ValueKind == JsonValueKind.True;
            bool includeActions = root.TryGetProperty("includeActionsArtifacts", out JsonElement actionsElement)
                && actionsElement.ValueKind == JsonValueKind.True;
            long actionsMaxBytes = ActionsArchiveService.DefaultMaxBytes;
            if (root.TryGetProperty("actionsMaxBytes", out JsonElement actionsLimitElement))
            {
                if (actionsLimitElement.ValueKind != JsonValueKind.Number || !actionsLimitElement.TryGetInt64(out actionsMaxBytes))
                    throw new JsonException();
            }
            if (includeActions && actionsMaxBytes is < ActionsArchiveService.MinimumMaxBytes or > ActionsArchiveService.MaximumMaxBytes)
                throw new JsonException();
            if (scope.Length > 0 && !AuthConfigLease.IsLogin(scope)) throw new JsonException();
            var settings = new AppSettings(owner, backupRoot, NetworkMode.Auto)
            { ApiCredentialConsentVersion = consentVersion, ApiCredentialConsentLogin = consentLogin,
                RepositoryScope = scope, IncludeCollaboratorRepositories = collaborators,
                IncludeActionsArtifacts = includeActions, ActionsMaxBytes = actionsMaxBytes };
            return new(settings.HasApiCredentialConsentFor(owner)
                ? settings with { ApiCredentialConsentLogin = owner.ToLowerInvariant() }
                : settings with { ApiCredentialConsentVersion = null, ApiCredentialConsentLogin = null }, []);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { return new(AppSettings.Default, ["SETTINGS_INVALID_DEFAULTS_USED"]); }
    }

    internal async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(settings.Owner) || string.IsNullOrWhiteSpace(settings.BackupRoot) || settings.NetworkMode != NetworkMode.Auto)
            throw new ArgumentException("Invalid settings.", nameof(settings));
        if (settings.IncludeActionsArtifacts && settings.ActionsMaxBytes is < ActionsArchiveService.MinimumMaxBytes or > ActionsArchiveService.MaximumMaxBytes)
            throw new ArgumentException("ACTIONS_SIZE_LIMIT_INVALID", nameof(settings));
        using AppDataPathLease parent = RequireDirectory();
        using (RequireFile(allowMissing: true)) { }
        await AtomicFile.WriteAsync(paths.SettingsFile, async (stream, token) =>
        {
            // Explicit allowlist: adding a model property cannot persist a credential accidentally.
            if (settings.HasApiCredentialConsentFor(settings.Owner))
                await JsonSerializer.SerializeAsync(stream, new { owner = settings.Owner, backupRoot = settings.BackupRoot, networkMode = "Auto",
                    apiCredentialConsentVersion = 1, apiCredentialConsentLogin = settings.Owner.ToLowerInvariant(),
                    repositoryScope = settings.RepositoryScope.Length == 0 ? null : settings.RepositoryScope.ToLowerInvariant(),
                    includeCollaboratorRepositories = settings.IncludeCollaboratorRepositories ? true : (bool?)null,
                    includeActionsArtifacts = settings.IncludeActionsArtifacts ? true : (bool?)null,
                    actionsMaxBytes = settings.IncludeActionsArtifacts ? settings.ActionsMaxBytes : (long?)null }, WriteOptions, token).ConfigureAwait(false);
            else
                await JsonSerializer.SerializeAsync(stream, new { owner = settings.Owner, backupRoot = settings.BackupRoot, networkMode = "Auto",
                    repositoryScope = settings.RepositoryScope.Length == 0 ? null : settings.RepositoryScope.ToLowerInvariant(),
                    includeCollaboratorRepositories = settings.IncludeCollaboratorRepositories ? true : (bool?)null,
                    includeActionsArtifacts = settings.IncludeActionsArtifacts ? true : (bool?)null,
                    actionsMaxBytes = settings.IncludeActionsArtifacts ? settings.ActionsMaxBytes : (long?)null }, WriteOptions, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        using (RequireFile(allowMissing: false)) { }
    }

    private AppDataPathLease RequireDirectory()
    {
        try { return AppDataPathPolicy.Acquire(paths, paths.LocalAppDataRoot, AppDataEntryKind.Directory, createMissingDirectories: true); }
        catch (PathBoundaryException ex) { throw new UnauthorizedAccessException("APPDATA_PATH_" + ex.Code); }
    }

    private AppDataPathLease RequireFile(bool allowMissing)
    {
        try { return AppDataPathPolicy.Acquire(paths, paths.SettingsFile, AppDataEntryKind.File, allowMissingFile: allowMissing); }
        catch (PathBoundaryException ex) { throw new UnauthorizedAccessException("APPDATA_PATH_" + ex.Code); }
    }
}
