using System.Globalization;
using System.Security.Cryptography;

namespace GitHubBackup.App;

internal sealed record AppPaths(string LocalAppDataAnchor, string LocalAppDataRoot,
    string SettingsFile, string DiagnosticLogRoot, string DiagnosticFallbackRoot, string AppGhConfigDirectory)
{
    internal string ScheduleFile => Path.Combine(LocalAppDataRoot, "schedule.json");

    internal static AppPaths Create(string localAppData)
    {
        string anchor = NativeFileSystem.CanonicalPath(localAppData);
        string root = Path.Combine(anchor, "GitHubBackupTool");
        string diagnostics = Path.Combine(root, "diagnostics");
        return new(anchor, root, Path.Combine(root, "settings.json"), diagnostics,
            Path.Combine(diagnostics, "fallback-summaries"), Path.Combine(root, "gh"));
    }

    internal static string SelectInitialBackupRoot(string userProfile, Func<string, bool> directoryExists) =>
        directoryExists(@"D:\") ? @"D:\GitHub-Backups" : Path.Combine(userProfile, "GitHub-Backups");
}

internal static class StorageLayout
{
    internal const string LockDirectoryName = ".locks";
}

internal static class RunIdFactory
{
    internal static string Create(TimeProvider timeProvider) =>
        timeProvider.GetUtcNow().ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)
        + "-" + RandomNumberGenerator.GetHexString(16);
}
