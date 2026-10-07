using System.Globalization;
using System.Security.Principal;
using System.Text.Json;

namespace GitHubBackup.App;

internal sealed record ActionsBackupReport(
    int WorkflowRunCount,
    int ArtifactCount,
    int DownloadedRunLogs,
    int DownloadedArtifacts,
    long BytesCopied,
    int SkippedCount,
    IReadOnlyList<string> Warnings)
{
    internal bool Success => Warnings.Count == 0;
}

internal sealed class ActionsArchiveService
{
    internal const long DefaultMaxBytes = 512L * 1024 * 1024;
    internal const long MinimumMaxBytes = 16L * 1024 * 1024;
    internal const long MaximumMaxBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumRuns = 500;
    private const int MaximumArtifacts = 500;

    internal async Task<ActionsBackupReport> SaveAsync(
        string ownerRoot,
        string authenticatedOwner,
        string repositoryOwner,
        string repository,
        string localName,
        long maxBytes,
        IGitHubHttpTransport transport,
        CancellationToken token)
    {
        if (!AuthConfigLease.IsLogin(authenticatedOwner) || !AuthConfigLease.IsLogin(repositoryOwner)
            || transport.BoundAccountId <= 0
            || !string.Equals(transport.BoundLogin, authenticatedOwner, StringComparison.OrdinalIgnoreCase))
            throw new HttpTransferException("HTTP_REPOSITORY_IDENTITY_REJECTED");
        _ = new AssetIdentity(repositoryOwner, repository, 1);
        if (!RepositoryNameMapper.IsSafeLocalName(localName)) throw new ArgumentException("ACTIONS_LOCAL_NAME_INVALID");
        if (maxBytes is < MinimumMaxBytes or > MaximumMaxBytes) throw new ArgumentException("ACTIONS_SIZE_LIMIT_INVALID");

        string root = NativeFileSystem.CanonicalPath(ownerRoot);
        using PathLease ownerLease = SummaryStore.RequirePrivateDirectory(root);
        string actionsParent = Path.Combine(root, "actions");
        AclPolicy.CreateRestrictedDirectory(actionsParent, WindowsIdentity.GetCurrent().User!);
        using PathLease actionsLease = SummaryStore.RequirePrivateDirectory(actionsParent);
        string directory = Path.Combine(actionsParent, localName);
        AclPolicy.CreateRestrictedDirectory(directory, WindowsIdentity.GetCurrent().User!, requireNew: false);
        using PathLease directoryLease = SummaryStore.RequirePrivateDirectory(directory);

        var warnings = new List<string>();
        var pages = new RawPageStore(directory);
        await pages.SaveMetadataAsync(authenticatedOwner, repositoryOwner, repository, "actions-runs.pages.json", transport, token).ConfigureAwait(false);
        await pages.SaveMetadataAsync(authenticatedOwner, repositoryOwner, repository, "actions-artifacts.pages.json", transport, token).ConfigureAwait(false);
        JsonElement runsPage = await ManifestStore.ReadPagesAsync(Path.Combine(directory, "actions-runs.pages.json"), token).ConfigureAwait(false);
        JsonElement artifactsPage = await ManifestStore.ReadPagesAsync(Path.Combine(directory, "actions-artifacts.pages.json"), token).ConfigureAwait(false);

        long[] runs = ReadRunIds(runsPage);
        ActionArtifact[] artifacts = ReadArtifacts(artifactsPage);
        int skipped = 0;
        long copied = 0;
        int downloadedLogs = 0;
        int downloadedArtifacts = 0;
        int runLimit = Math.Min(runs.Length, MaximumRuns);
        int artifactLimit = Math.Min(artifacts.Length, MaximumArtifacts);
        if (runs.Length > runLimit) { skipped += runs.Length - runLimit; warnings.Add("ACTIONS_RUN_LIMIT_REACHED"); }
        if (artifacts.Length > artifactLimit) { skipped += artifacts.Length - artifactLimit; warnings.Add("ACTIONS_ARTIFACT_LIMIT_REACHED"); }

        string logsDirectory = Path.Combine(directory, "run-logs");
        string artifactsDirectory = Path.Combine(directory, "artifacts");
        AclPolicy.CreateRestrictedDirectory(logsDirectory, WindowsIdentity.GetCurrent().User!);
        AclPolicy.CreateRestrictedDirectory(artifactsDirectory, WindowsIdentity.GetCurrent().User!);

        foreach (long runId in runs.Take(runLimit))
        {
            token.ThrowIfCancellationRequested();
            if (copied >= maxBytes) { skipped++; warnings.Add("ACTIONS_SIZE_LIMIT_REACHED"); continue; }
            string path = Path.Combine(logsDirectory, runId.ToString(CultureInfo.InvariantCulture) + ".zip");
            try
            {
                DownloadResult result = await SaveBinaryAsync($"/repos/{repositoryOwner}/{repository}/actions/runs/{runId}/logs",
                    path, authenticatedOwner, maxBytes - copied, transport, token).ConfigureAwait(false);
                if (!result.Saved) { skipped++; warnings.Add(result.WarningCode!); continue; }
                copied = checked(copied + result.Bytes);
                downloadedLogs++;
            }
            catch (ActionsLimitException)
            {
                skipped++; warnings.Add("ACTIONS_SIZE_LIMIT_REACHED");
            }
            catch (Exception ex) when (IsDownloadItemFailure(ex))
            {
                skipped++; warnings.Add("ACTIONS_DOWNLOAD_FAILED");
            }
        }

        foreach (ActionArtifact artifact in artifacts.Take(artifactLimit))
        {
            token.ThrowIfCancellationRequested();
            if (artifact.Expired || artifact.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            {
                skipped++; warnings.Add("ACTIONS_ARTIFACT_EXPIRED"); continue;
            }
            if (artifact.Size < 0) { skipped++; warnings.Add("ACTIONS_ARTIFACT_SIZE_INVALID"); continue; }
            if (copied >= maxBytes || artifact.Size > maxBytes - copied)
            {
                skipped++; warnings.Add("ACTIONS_SIZE_LIMIT_REACHED"); continue;
            }
            string path = Path.Combine(artifactsDirectory, artifact.Id.ToString(CultureInfo.InvariantCulture) + ".zip");
            try
            {
                DownloadResult result = await SaveBinaryAsync($"/repos/{repositoryOwner}/{repository}/actions/artifacts/{artifact.Id}/zip",
                    path, authenticatedOwner, maxBytes - copied, transport, token, artifact.Size).ConfigureAwait(false);
                if (!result.Saved) { skipped++; warnings.Add(result.WarningCode!); continue; }
                copied = checked(copied + result.Bytes);
                downloadedArtifacts++;
            }
            catch (ActionsLimitException)
            {
                skipped++; warnings.Add("ACTIONS_SIZE_LIMIT_REACHED");
            }
            catch (Exception ex) when (IsDownloadItemFailure(ex))
            {
                skipped++; warnings.Add("ACTIONS_DOWNLOAD_FAILED");
            }
        }

        var index = new
        {
            schemaVersion = 1,
            authenticatedOwner,
            repositoryOwner,
            repository,
            maxBytes,
            generatedAtUtc = DateTimeOffset.UtcNow,
            workflowRunCount = runs.Length,
            artifactCount = artifacts.Length,
            downloadedRunLogs = downloadedLogs,
            downloadedArtifacts,
            bytesCopied = copied,
            skippedCount = skipped,
            warnings = warnings.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        };
        await AtomicFile.WriteAsync(Path.Combine(directory, "index.json"),
            (stream, cancellation) => JsonSerializer.SerializeAsync(stream, index, MetadataJson.Options, cancellation), token).ConfigureAwait(false);
        using (SummaryStore.RequirePrivateDirectory(directory)) { }
        return new(runs.Length, artifacts.Length, downloadedLogs, downloadedArtifacts, copied, skipped,
            warnings.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static long[] ReadRunIds(JsonElement pages)
    {
        var result = new HashSet<long>();
        foreach (JsonElement page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("workflow_runs", out JsonElement runs) || runs.ValueKind != JsonValueKind.Array)
                throw new HttpTransferException("HTTP_ACTIONS_METADATA_INVALID");
            foreach (JsonElement run in runs.EnumerateArray())
            {
                if (!run.TryGetProperty("id", out JsonElement id) || !id.TryGetInt64(out long value) || value <= 0)
                    throw new HttpTransferException("HTTP_ACTIONS_METADATA_INVALID");
                result.Add(value);
            }
        }
        return result.Order().ToArray();
    }

    private static ActionArtifact[] ReadArtifacts(JsonElement pages)
    {
        var result = new Dictionary<long, ActionArtifact>();
        foreach (JsonElement page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("artifacts", out JsonElement artifacts) || artifacts.ValueKind != JsonValueKind.Array)
                throw new HttpTransferException("HTTP_ACTIONS_METADATA_INVALID");
            foreach (JsonElement artifact in artifacts.EnumerateArray())
            {
                if (!artifact.TryGetProperty("id", out JsonElement id) || !id.TryGetInt64(out long value) || value <= 0
                    || !artifact.TryGetProperty("size_in_bytes", out JsonElement size) || !size.TryGetInt64(out long bytes))
                    throw new HttpTransferException("HTTP_ACTIONS_METADATA_INVALID");
                bool expired = artifact.TryGetProperty("expired", out JsonElement expiredElement)
                    && expiredElement.ValueKind == JsonValueKind.True;
                DateTimeOffset? expiresAt = null;
                if (artifact.TryGetProperty("expires_at", out JsonElement expiresElement)
                    && expiresElement.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(expiresElement.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
                    expiresAt = parsed;
                result[value] = new(value, bytes, expired, expiresAt);
            }
        }
        return result.Values.OrderBy(item => item.Id).ToArray();
    }

    private static async Task<DownloadResult> SaveBinaryAsync(string resourcePath, string destination,
        string authenticatedOwner, long remaining, IGitHubHttpTransport transport, CancellationToken token, long? expectedSize = null)
    {
        if (expectedSize is > 0 and var known && known > remaining) return new(false, 0, "ACTIONS_SIZE_LIMIT_REACHED");
        using GitHubResponse response = await transport.DownloadActionsBinaryAsync(authenticatedOwner, resourcePath, token).ConfigureAwait(false);
        if (response.StatusCode != 200) throw new HttpTransferException("HTTP_ACTIONS_DOWNLOAD_INVALID");
        if (response.Headers.TryGetValue("Content-Length", out string? contentLength)
            && (!long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out long declared) || declared < 0))
            throw new HttpTransferException("HTTP_ACTIONS_DOWNLOAD_INVALID");
        if (response.Headers.TryGetValue("Content-Length", out contentLength)
            && long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out long length) && length > remaining)
            return new(false, 0, "ACTIONS_SIZE_LIMIT_REACHED");

        long copied = 0;
        try
        {
            await AtomicFile.WriteAsync(destination, async (output, cancellation) =>
            {
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = await response.Body.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
                {
                    if (copied > remaining - read) throw new ActionsLimitException();
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    copied += read;
                }
            }, token).ConfigureAwait(false);
        }
        catch (ActionsLimitException) { throw; }
        return new(true, copied, null);
    }

    private static bool IsDownloadItemFailure(Exception error)
    {
        if (error is AtomicFileCleanupException) return false;
        if (error is HttpTransferException transfer) return transfer.FailureKind != NetworkFailureKind.RateLimited;
        return error is UnauthorizedAccessException or InvalidOperationException or IOException;
    }

    private sealed record ActionArtifact(long Id, long Size, bool Expired, DateTimeOffset? ExpiresAt);
    private sealed record DownloadResult(bool Saved, long Bytes, string? WarningCode);
    private sealed class ActionsLimitException : IOException;
}
