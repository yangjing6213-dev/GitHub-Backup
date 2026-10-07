using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class RestoreServiceTests
{
    [TestMethod]
    public async Task Restore_creates_a_new_destination_and_reports_each_repository()
    {
        using var root = new StorageTestRoot();
        string backup = root.Child("backup"), owner = Path.Combine(backup, "fixture-user"), restores = root.Child("restores");
        AclPolicy.CreateRestrictedDirectory(backup, root.User);
        AclPolicy.CreateRestrictedDirectory(owner, root.User);
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, "manifests"), root.User);
        AclPolicy.CreateRestrictedDirectory(restores, root.User);
        var repository = PreflightFixture.Repository with { RemoteState = "active" };
        await new ManifestStore().WriteAsync(owner, "20261007T120000000Z-ABC", [repository], default);
        string mirror = Path.Combine(owner, "mirrors", repository.LocalName + ".git");
        AclPolicy.CreateRestrictedDirectory(mirror, root.User);
        MirrorSafeCopyTests.Write(mirror, "HEAD", "ref: refs/heads/main\n");
        MirrorSafeCopyTests.Write(mirror, "config", MirrorSafeCopy.SafeConfig(repository.Url + ".git"));
        MirrorSafeCopyTests.Write(mirror, "objects/placeholder", "object");
        foreach (string file in new[] { "repository.json", "issues.pages.json", "pull-requests.pages.json", "issue-comments.pages.json",
            "review-comments.pages.json", "releases.pages.json", "labels.pages.json", "milestones.pages.json", "workflows.pages.json" })
            MirrorSafeCopyTests.Write(Path.Combine(owner, "metadata", repository.LocalName), file, file == "repository.json" ? "{}" : "[]");
        MirrorSafeCopyTests.Write(Path.Combine(owner, "actions", repository.LocalName), "index.json", "{}");

        ToolDetection git = LocalGitRunner.Git;
        IReadOnlyDictionary<string, string?> environment = ChildEnvironmentBuilder.CreateCurrentBase([Path.GetDirectoryName(git.AbsolutePath)!]);
        using var job = OperationJob.Create();
        RestoreReport report = await new RestoreService(new FakeRestoreRunner()).RestoreLatestAsync(owner, "fixture-user", restores,
            git, environment, job, default);

        Assert.IsTrue(report.Success, string.Join(',', report.Warnings));
        Assert.AreEqual(1, report.RepositoryCount);
        Assert.AreEqual(1, report.VerifiedCount);
        Assert.AreEqual(1, report.RestoredCount);
        Assert.IsTrue(Directory.Exists(Path.Combine(report.DestinationRoot, repository.LocalName)));
        Assert.IsTrue(File.Exists(Path.Combine(report.DestinationRoot, "metadata", repository.LocalName, "repository.json")));
        Assert.IsTrue(File.Exists(Path.Combine(report.DestinationRoot, "actions", repository.LocalName, "index.json")));
        Assert.IsFalse(string.Equals(report.DestinationRoot, restores, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Restore_rejects_a_destination_inside_the_backup_tree()
    {
        using var root = new StorageTestRoot();
        string backup = root.Child("backup"), owner = Path.Combine(backup, "fixture-user");
        AclPolicy.CreateRestrictedDirectory(backup, root.User);
        AclPolicy.CreateRestrictedDirectory(owner, root.User);
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, "manifests"), root.User);
        await new ManifestStore().WriteAsync(owner, "20261007T120000000Z-ABC", [], default);
        ToolDetection git = LocalGitRunner.Git;
        IReadOnlyDictionary<string, string?> environment = ChildEnvironmentBuilder.CreateCurrentBase([Path.GetDirectoryName(git.AbsolutePath)!]);
        using var job = OperationJob.Create();

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => new RestoreService(new FakeRestoreRunner()).RestoreLatestAsync(
            owner, "fixture-user", owner, git, environment, job, default));
    }

    private sealed class FakeRestoreRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, OperationJob job, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            if (request.Arguments.Contains("clone", StringComparer.Ordinal)) Directory.CreateDirectory(request.Arguments[^1]);
            return Task.FromResult(new ProcessResult(0, false, false, [], []));
        }
    }
}
