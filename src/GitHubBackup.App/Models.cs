namespace GitHubBackup.App;

internal enum BackupMode { Daily, Full }
internal enum NetworkMode { Auto }
internal enum RunStatus { Pass, Partial, Fail, Cancelled }

internal sealed record ExecutableIdentity(uint VolumeSerialNumber, ulong FileIndex, long Length, long LastWriteTimeUtcTicks);
internal sealed record ToolDetection(string AbsolutePath, ExecutableIdentity Identity, string RawVersion, bool IsSupported);
internal enum ProcessOutputMode { TextTail, EphemeralText, CapturedFile, BinaryFile }
internal sealed record ProcessRequest(string FilePath, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment, TimeSpan Timeout,
    ProcessOutputMode OutputMode = ProcessOutputMode.TextTail, string? StandardOutputFile = null,
    long MaxStandardOutputBytes = 8 * 1024 * 1024, ExecutableIdentity? ExpectedExecutableIdentity = null,
    bool EphemeralStandardError = false);
internal sealed record ProcessResult(int? ExitCode, bool TimedOut, bool Cancelled,
    IReadOnlyList<string> StandardOutput, IReadOnlyList<string> StandardError);
internal enum ProcessTerminalKind { Succeeded, ExitFailure, TimedOut, Cancelled }
internal static class ProcessOutcomeClassifier
{
    internal static ProcessTerminalKind Classify(ProcessResult result) => result.Cancelled ? ProcessTerminalKind.Cancelled :
        result.TimedOut ? ProcessTerminalKind.TimedOut : result.ExitCode == 0 ? ProcessTerminalKind.Succeeded : ProcessTerminalKind.ExitFailure;
}
internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, OperationJob job, IProgress<string>? progress, CancellationToken cancellationToken);
}

internal sealed record AppSettings(string Owner, string BackupRoot, NetworkMode NetworkMode)
{
    internal int? ApiCredentialConsentVersion { get; init; }
    internal string? ApiCredentialConsentLogin { get; init; }
    internal bool HasApiCredentialConsentFor(string login) => ApiCredentialConsentVersion == 1
        && AuthConfigLease.IsLogin(Owner) && AuthConfigLease.IsLogin(login)
        && string.Equals(Owner, login, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ApiCredentialConsentLogin, login, StringComparison.OrdinalIgnoreCase);
    internal static AppSettings Default { get; } =
        new("yangjing6213-dev", @"D:\GitHub-Backups", NetworkMode.Auto);
}

internal sealed record RepositoryDescriptor(
    long RepositoryId,
    string Name,
    string NameWithOwner,
    string Url,
    bool IsPrivate,
    bool IsArchived,
    bool IsFork,
    bool HasWikiEnabled,
    DateTimeOffset? UpdatedAt,
    long DiskUsage,
    string LocalName,
    string RemoteState);

internal sealed record AssetIdentity
{
    internal string Owner { get; }
    internal string Repository { get; }
    internal long Id { get; }
    internal string ResourceKey => $"/repos/{Owner}/{Repository}/releases/assets/{Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    internal AssetIdentity(string owner, string repository, long id)
    {
        if (string.IsNullOrEmpty(owner) || !AuthConfigLease.IsLogin(owner)
            || string.IsNullOrEmpty(repository) || repository.Length > 100 || repository is "." or ".."
            || repository.EndsWith('.') || repository.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            || id <= 0) throw new ArgumentException("HTTP_ASSET_IDENTITY_INVALID");
        Owner = owner; Repository = repository; Id = id;
    }
}

internal sealed record ReleaseAsset(long Id, string Name, long Size, string? Digest, DateTimeOffset UpdatedAt);
internal sealed record ReleaseRecord(long Id, string TagName, string Name, bool IsDraft, bool IsPrerelease,
    DateTimeOffset? PublishedAt, IReadOnlyList<ReleaseAsset> Assets);
internal sealed record ReleaseInventory(IReadOnlyList<ReleaseRecord> Releases)
{
    internal static ReleaseInventory Empty { get; } = new([]);
}
internal sealed record ReleaseAssetExpectation(long Size, string? Sha256);
internal sealed record AssetDownloadResult(string FullPath, long Length, string Sha256, bool RemoteDigestVerified);
internal sealed record ReleaseAssetGeneration(AssetIdentity Identity, ReleaseAsset Asset, string FullPath,
    string LocalName, int Generation, bool Reused);
internal sealed record ReleaseGeneration(ReleaseRecord Release, string LocalTag, int Generation,
    bool Reused, IReadOnlyList<ReleaseAssetGeneration> Assets);
internal sealed record ReleasePlan(string RepositoryDirectory, string Owner, string Repository,
    IReadOnlyList<ReleaseGeneration> Generations, long ChangedBytes);
internal sealed class ReleaseException(string code) : IOException(code)
{
    internal string Code { get; } = code;
}

internal sealed record BackupSummary(
    int SchemaVersion,
    BackupMode? Mode,
    DateTimeOffset? StartedAt,
    DateTimeOffset CompletedAt,
    RunStatus Status,
    bool WasCancelled,
    string Owner,
    string StartedRunId,
    int RepositoryCount,
    int WarningCount,
    IReadOnlyList<string> FailedRepositories,
    int SkippedWikiCount,
    string FailurePhase,
    string ErrorCode,
    string BackupRoot,
    string Manifest,
    string Log)
{
    internal static BackupSummary PreflightFailure(BackupMode mode, string owner, string runId,
        string ownerRoot, string failurePhase, string errorCode) =>
        new(2, mode, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, RunStatus.Fail, false,
            owner, runId, 0, 0, [], 0, failurePhase, errorCode, ownerRoot, "", "");
}

internal sealed record SummaryReadResult(
    BackupSummary? Latest,
    BackupSummary? LatestCoreSuccess,
    IReadOnlyList<string> Warnings)
{
    internal bool LatestIsFallback { get; init; }
}
