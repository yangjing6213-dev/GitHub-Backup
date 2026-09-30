using System.Text.Json;

namespace GitHubBackup.App;

internal sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Warnings);

internal sealed class SettingsStore(AppPaths paths)
{
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
            var settings = new AppSettings(owner, backupRoot, NetworkMode.Auto)
            { ApiCredentialConsentVersion = consentVersion, ApiCredentialConsentLogin = consentLogin };
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
        using AppDataPathLease parent = RequireDirectory();
        using (RequireFile(allowMissing: true)) { }
        await AtomicFile.WriteAsync(paths.SettingsFile, async (stream, token) =>
        {
            // Explicit allowlist: adding a model property cannot persist a credential accidentally.
            if (settings.HasApiCredentialConsentFor(settings.Owner))
                await JsonSerializer.SerializeAsync(stream, new { owner = settings.Owner, backupRoot = settings.BackupRoot, networkMode = "Auto",
                    apiCredentialConsentVersion = 1, apiCredentialConsentLogin = settings.Owner.ToLowerInvariant() }, cancellationToken: token).ConfigureAwait(false);
            else
                await JsonSerializer.SerializeAsync(stream, new { owner = settings.Owner, backupRoot = settings.BackupRoot, networkMode = "Auto" }, cancellationToken: token).ConfigureAwait(false);
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
