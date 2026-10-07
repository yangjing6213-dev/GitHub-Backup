using System.Text;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class ActionsArchiveServiceTests
{
    [TestMethod]
    public async Task Actions_metadata_logs_and_artifacts_are_saved_with_expiry_and_count_limits()
    {
        using var root = new StorageTestRoot();
        string ownerRoot = root.Child("fixture-user");
        AclPolicy.CreateRestrictedDirectory(ownerRoot, root.User);
        var transport = new FixtureTransport();

        ActionsBackupReport report = await new ActionsArchiveService().SaveAsync(ownerRoot, "fixture-user", "fixture-user",
            "repo", "repo", ActionsArchiveService.MinimumMaxBytes, transport, default);

        Assert.AreEqual(1, report.WorkflowRunCount);
        Assert.AreEqual(2, report.ArtifactCount);
        Assert.AreEqual(1, report.DownloadedRunLogs);
        Assert.AreEqual(1, report.DownloadedArtifacts);
        Assert.AreEqual(12, report.BytesCopied);
        Assert.AreEqual(1, report.SkippedCount);
        CollectionAssert.Contains(report.Warnings.ToArray(), "ACTIONS_ARTIFACT_EXPIRED");
        Assert.IsTrue(File.Exists(Path.Combine(ownerRoot, "actions", "repo", "run-logs", "101.zip")));
        Assert.IsTrue(File.Exists(Path.Combine(ownerRoot, "actions", "repo", "artifacts", "201.zip")));
        Assert.IsFalse(File.Exists(Path.Combine(ownerRoot, "actions", "repo", "artifacts", "202.zip")));
        CollectionAssert.AreEqual("log123"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(ownerRoot, "actions", "repo", "run-logs", "101.zip")));
        CollectionAssert.AreEqual("zip456"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(ownerRoot, "actions", "repo", "artifacts", "201.zip")));
    }

    [TestMethod]
    public async Task Actions_download_does_not_write_when_declared_size_exceeds_limit()
    {
        using var root = new StorageTestRoot();
        string ownerRoot = root.Child("fixture-user");
        AclPolicy.CreateRestrictedDirectory(ownerRoot, root.User);
        var transport = new FixtureTransport { ArtifactSize = ActionsArchiveService.MinimumMaxBytes + 1 };

        ActionsBackupReport report = await new ActionsArchiveService().SaveAsync(ownerRoot, "fixture-user", "fixture-user",
            "repo", "repo", ActionsArchiveService.MinimumMaxBytes, transport, default);

        Assert.AreEqual(0, report.DownloadedArtifacts);
        Assert.IsTrue(report.Warnings.Contains("ACTIONS_SIZE_LIMIT_REACHED"));
        Assert.IsFalse(File.Exists(Path.Combine(ownerRoot, "actions", "repo", "artifacts", "201.zip")));
    }

    [TestMethod]
    public async Task A_single_actions_download_failure_isolated_from_other_items()
    {
        using var root = new StorageTestRoot();
        string ownerRoot = root.Child("fixture-user");
        AclPolicy.CreateRestrictedDirectory(ownerRoot, root.User);
        var transport = new FixtureTransport { FailArtifactDownload = true };

        ActionsBackupReport report = await new ActionsArchiveService().SaveAsync(ownerRoot, "fixture-user", "fixture-user",
            "repo", "repo", ActionsArchiveService.MinimumMaxBytes, transport, default);

        Assert.AreEqual(1, report.DownloadedRunLogs);
        Assert.AreEqual(0, report.DownloadedArtifacts);
        Assert.IsTrue(report.Warnings.Contains("ACTIONS_DOWNLOAD_FAILED"));
        Assert.IsTrue(File.Exists(Path.Combine(ownerRoot, "actions", "repo", "run-logs", "101.zip")));
    }

    [TestMethod]
    public async Task Actions_rate_limit_is_not_hidden_as_an_item_warning()
    {
        using var root = new StorageTestRoot();
        string ownerRoot = root.Child("fixture-user");
        AclPolicy.CreateRestrictedDirectory(ownerRoot, root.User);
        var transport = new FixtureTransport { RateLimitArtifactDownload = true };

        var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() => new ActionsArchiveService().SaveAsync(ownerRoot,
            "fixture-user", "fixture-user", "repo", "repo", ActionsArchiveService.MinimumMaxBytes, transport, default));
        Assert.AreEqual(NetworkFailureKind.RateLimited, error.FailureKind);
    }

    private sealed class FixtureTransport : IGitHubHttpTransport
    {
        internal long ArtifactSize { get; init; } = 6;
        internal bool FailArtifactDownload { get; init; }
        internal bool RateLimitArtifactDownload { get; init; }
        public string BoundLogin => "fixture-user";
        public long BoundAccountId => 7;
        public Task<GitHubResponse> SendAsync(GitHubRequest request, CancellationToken token)
        {
            string body = request.Path.EndsWith("/actions/runs", StringComparison.Ordinal)
                ? "{\"total_count\":1,\"workflow_runs\":[{\"id\":101}]}"
                : "{\"total_count\":2,\"artifacts\":[{\"id\":201,\"name\":\"build\",\"size_in_bytes\":6,\"expired\":false},{\"id\":202,\"name\":\"old\",\"size_in_bytes\":6,\"expired\":true}]}";
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            return Task.FromResult(new GitHubResponse(200,
                new Dictionary<string, string> { ["Content-Length"] = bytes.Length.ToString() }, new MemoryStream(bytes)));
        }
        public Task<GitHubResponse> DownloadAssetAsync(AssetIdentity asset, CancellationToken token) => throw new AssertFailedException();
        public Task<GitHubResponse> DownloadActionsBinaryAsync(string authenticatedOwner, string resourcePath, CancellationToken token)
        {
            if (FailArtifactDownload && resourcePath.Contains("/artifacts/201/", StringComparison.Ordinal))
                throw new HttpTransferException("HTTP_STATUS_500", NetworkFailureKind.Http5xx);
            if (RateLimitArtifactDownload && resourcePath.Contains("/artifacts/201/", StringComparison.Ordinal))
                throw new HttpTransferException("HTTP_RATE_LIMITED", NetworkFailureKind.RateLimited, DateTimeOffset.UtcNow.AddMinutes(1));
            byte[] bytes = resourcePath.Contains("/runs/", StringComparison.Ordinal) ? "log123"u8.ToArray() : "zip456"u8.ToArray();
            var headers = new Dictionary<string, string> { ["Content-Length"] = resourcePath.Contains("/artifacts/201/", StringComparison.Ordinal)
                ? ArtifactSize.ToString() : bytes.Length.ToString() };
            return Task.FromResult(new GitHubResponse(200, headers, new MemoryStream(bytes)));
        }
        public void Dispose() { }
    }
}
