using System.Text.Json;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class MirrorDiagnosticTests
{
    [TestMethod]
    [DataRow("identity", "MIRROR_IDENTITY_INVALID")]
    [DataRow("core-fsck", "MIRROR_FSCK_FAILED")]
    [DataRow("lfs-fsck", "MIRROR_LOCAL_LFS_INTEGRITY_FAILED")]
    [DataRow("clone", "MIRROR_GIT_CLONE_UNCLASSIFIED")]
    [DataRow("fetch", "MIRROR_GIT_FETCH_UNCLASSIFIED")]
    public async Task Failure_keeps_the_most_specific_safe_code(string fault, string code)
    {
        await using var f = await MirrorFixture.CreateAsync();
        if (fault != "clone") f.Seed();
        f.Runner.Fault = fault;
        var result = await f.Service.BackupAsync(f.Repository, f.Context, f.RepositoryLock, default);
        Assert.IsTrue(result.CoreFailed);
        Assert.AreEqual(code, result.ErrorCodes.Last());
    }

    [TestMethod]
    [DataRow(true, "MIRROR_GIT_CLONE_FORBIDDEN")]
    [DataRow(false, "MIRROR_GIT_CLONE_UNCLASSIFIED")]
    public async Task Exact_clone_preamble_is_classified_without_searching_arbitrary_server_text(bool exact, string code)
    {
        await using var f = await MirrorFixture.CreateAsync();
        var result = await new RepositoryBackupService(new FailedClone(exact))
            .BackupAsync(f.Repository, f.Context, f.RepositoryLock, default);
        Assert.IsTrue(result.CoreFailed);
        Assert.AreEqual(code, result.ErrorCodes.Last());
        Assert.IsFalse(result.ErrorCodes.Any(value => value.Contains("PRIVATE")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Failure_summary_and_log_keep_categories_and_counts_without_exception_text(bool mixed)
    {
        await using var f = await OrchestrationFixture.CreateAsync(repositoryNames: ["repo", "healthy"]);
        f.Git.BeforeRun = request =>
        {
            if (mixed && request.Arguments.Any(value => value.EndsWith("/healthy.git", StringComparison.Ordinal)))
                throw new UnauthorizedAccessException("PRIVATE_PASSWORD_AND_PATH");
            f.Git.Fault = "clone";
        };
        var result = await f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), default);
        Assert.AreEqual(RunStatus.Fail, result.Summary.Status);
        Assert.HasCount(2, result.Summary.FailedRepositories);
        Assert.AreEqual(mixed ? "MIRROR_BACKUP_FAILED" : "MIRROR_GIT_CLONE_UNCLASSIFIED", result.Summary.ErrorCode);
        string log = File.ReadAllText(result.Summary.Log);
        Assert.Contains($"MIRROR_FAILURE_CATEGORY MIRROR_GIT_CLONE_UNCLASSIFIED COUNT {(mixed ? 1 : 2)}", log);
        if (mixed) Assert.Contains("MIRROR_FAILURE_CATEGORY MIRROR_LOCAL_ACCESS_DENIED COUNT 1", log);
        Assert.DoesNotContain("PRIVATE_PASSWORD_AND_PATH", log);
        foreach (string line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.GetProperty("level").GetString() == "ERROR")
                Assert.AreEqual(JsonValueKind.Null, json.RootElement.GetProperty("repository").ValueKind);
        }
    }

    [TestMethod]
    public async Task Locked_lfs_hook_never_promotes_and_retains_cleanup_until_retry()
    {
        await using var f = await MirrorFixture.CreateAsync();
        f.Seed();
        FileStream? blocker = null;
        string? hooks = null;
        f.Runner.BeforeRun = request =>
        {
            if (!request.FilePath.EndsWith("git-lfs.exe", StringComparison.OrdinalIgnoreCase)) return;
            hooks = request.Environment["GIT_CONFIG_VALUE_0"]!;
            MirrorSafeCopyTests.Write(hooks, "pre-push", "synthetic owned hook");
            blocker = new FileStream(Path.Combine(hooks, "pre-push"), FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        try
        {
            var result = await f.Service.BackupAsync(f.Repository, f.Context, f.RepositoryLock, default);
            Assert.IsTrue(result.CoreFailed);
            Assert.IsTrue(result.Critical);
            Assert.IsTrue(f.Context.ConsistencyPending);
            Assert.HasCount(1, f.Context.OwnedStaging);
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(f.Final, "marker")));
            Assert.HasCount(0, Directory.GetFileSystemEntries(f.Session.Snapshot.EmptyHooksDirectory!));
        }
        finally
        {
            blocker?.Dispose();
            f.Runner.BeforeRun = null;
            f.RepositoryLock.Dispose();
            var recovered = await new MirrorPromotion(f.Runner).RecoverAllAsync(f.Context, default);
            Assert.HasCount(0, recovered.ErrorCodes);
        }
        Assert.IsFalse(Directory.Exists(hooks));
        Assert.IsFalse(f.Context.ConsistencyPending);
        Assert.HasCount(0, f.Context.OwnedStaging);
    }

    [TestMethod]
    public async Task Failure_is_logged_before_the_next_repository_starts()
    {
        // Discovery orders repositories by name; observe the later one.
        await using var f = await OrchestrationFixture.CreateAsync(repositoryNames: ["aaa", "zzz"]);
        bool nextStarted = false, failureVisible = false;
        var progress = new BackupObserver();
        f.Git.BeforeRun = request =>
        {
            if (request.Arguments.Any(value => value.EndsWith("/zzz.git", StringComparison.Ordinal)))
            {
                nextStarted = true;
                failureVisible = progress.LogTail.Any(line => line.Contains("MIRROR_FAILURE MIRROR_GIT_CLONE_UNCLASSIFIED", StringComparison.Ordinal));
            }
            f.Git.Fault = "clone";
        };
        await f.Orchestrator.RunAsync(f.Request(BackupMode.Daily), default, progress);
        Assert.IsTrue(nextStarted);
        Assert.IsTrue(failureVisible);
    }

    [TestMethod]
    public async Task Authentication_boundary_failure_is_not_reported_as_disk_failure()
    {
        await using var f = await MirrorFixture.CreateAsync();
        f.Runner.BeforeRun = _ => throw new AuthBoundaryException("PRIVATE_AUTH_DETAIL");
        var result = await f.Service.BackupAsync(f.Repository, f.Context, f.RepositoryLock, default);
        Assert.AreEqual("MIRROR_AUTH_INVALID", result.ErrorCodes.Last());
        Assert.DoesNotContain("PRIVATE_AUTH_DETAIL", string.Join(' ', result.ErrorCodes));
        Assert.Contains("登录", MainForm.ErrorText(result.ErrorCodes.Last()));
    }

    [TestMethod]
    [DataRow("MIRROR_GIT_CLONE_CONNECTION_RESET")]
    [DataRow("MIRROR_GIT_FETCH_UNAUTHORIZED")]
    [DataRow("MIRROR_LOCAL_ACCESS_DENIED")]
    public void Known_mirror_failures_have_actionable_bilingual_guidance(string code)
    {
        Assert.DoesNotContain("目前无法判断具体原因", MainForm.ErrorText(code));
        Assert.DoesNotContain("specific cause could not be identified", MainForm.ErrorText(code, english: true));
        Assert.Contains(code, MainForm.ErrorText(code));
    }

    private sealed class BackupObserver : IProgress<BackupProgress>
    {
        internal IReadOnlyList<string> LogTail { get; private set; } = [];
        public void Report(BackupProgress value) { if (value.LogTail is not null) LogTail = value.LogTail; }
    }

    private sealed class FailedClone(bool exact) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, OperationJob job, IProgress<string>? progress, CancellationToken token)
        {
            string preamble = exact ? $"Cloning into bare repository '{request.Arguments[^1]}'...\r\n" : "remote: PRIVATE_SERVER_TEXT\n";
            progress?.Report(preamble + "fatal: unable to access 'https://github.com/fixture-user/repo.git/': The requested URL returned error: 403\n");
            return Task.FromResult(new ProcessResult(128, false, false, [], []));
        }
    }
}
