using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class RealMirrorFailureTests
{
    [TestMethod]
    public async Task Real_git_and_lfs_backup_normal_committed_repository_without_lfs_objects()
    {
        await using var fixture = await MirrorFixture.CreateAsync(realGit: true);
        string origin = await CreateOriginAsync(fixture);
        var runner = new RealLfsRunner(origin);
        var result = await new RepositoryBackupService(runner).BackupAsync(
            fixture.Repository, fixture.Context, fixture.RepositoryLock, default);

        Assert.IsFalse(result.CoreFailed, string.Join(',', result.ErrorCodes) + "\n" + runner.Diagnostic);
        Assert.AreEqual(0, result.WarningCount, string.Join(',', result.ErrorCodes));
        Assert.AreEqual(2, runner.LfsRequests);
        Assert.IsTrue(Directory.Exists(fixture.Final));
        Assert.HasCount(0, Directory.GetFileSystemEntries(fixture.Session.Snapshot.EmptyHooksDirectory!));

        File.WriteAllText(Path.Combine(origin, "readme.txt"), "An updated normal repository.\n");
        await LocalGitRunner.LocalAsync(origin, ["add", "readme.txt"], fixture.Preflight.Environment);
        await LocalGitRunner.LocalAsync(origin,
            ["-c", "user.name=Fixture", "-c", "user.email=fixture@localhost", "commit", "--no-gpg-sign", "-m", "update"],
            fixture.Preflight.Environment);
        result = await new RepositoryBackupService(runner).BackupAsync(
            fixture.Repository, fixture.Context, fixture.RepositoryLock, default);
        Assert.IsFalse(result.CoreFailed, string.Join(',', result.ErrorCodes) + "\n" + runner.Diagnostic);
        Assert.AreEqual(0, result.WarningCount, string.Join(',', result.ErrorCodes));
        Assert.AreEqual(4, runner.LfsRequests);
        Assert.HasCount(0, Directory.GetFileSystemEntries(fixture.Session.Snapshot.EmptyHooksDirectory!));
        Assert.AreEqual(MirrorSafeCopy.SafeConfig("https://github.com/fixture-user/repo.git"),
            File.ReadAllText(Path.Combine(fixture.Final, "config")));
        Assert.IsGreaterThan(0, runner.GeneratedHookFiles, "The real LFS fsck must exercise hook creation.");
        Assert.IsTrue(runner.HooksDirectories.All(path => !Directory.Exists(path)));
        Assert.HasCount(0, Directory.GetDirectories(fixture.Final, ".lfs-hooks-*"));
    }

    [TestMethod]
    public async Task Real_empty_repository_skips_lfs_fsck_and_completes_mirror()
    {
        await using var fixture = await MirrorFixture.CreateAsync(realGit: true);
        string origin = fixture.Preflight.Root.Child("empty-origin");
        AclPolicy.CreateRestrictedDirectory(origin, fixture.Preflight.User);
        await LocalGitRunner.LocalAsync(origin, ["init", "--bare"], fixture.Preflight.Environment);
        var runner = new RealLfsRunner(origin);

        var result = await new RepositoryBackupService(runner).BackupAsync(
            fixture.Repository, fixture.Context, fixture.RepositoryLock, default);

        Assert.IsFalse(result.CoreFailed, string.Join(',', result.ErrorCodes) + "\n" + runner.Diagnostic);
        Assert.AreEqual(0, result.WarningCount, string.Join(',', result.ErrorCodes));
        Assert.AreEqual(1, runner.LfsFetchRequests);
        Assert.AreEqual(0, runner.LfsFsckRequests);
        Assert.IsTrue(Directory.Exists(fixture.Final));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task Real_lfs_generated_hooks_are_cleaned_when_the_command_fails_or_is_cancelled(bool cancelled, bool throwIOException)
    {
        await using var fixture = await MirrorFixture.CreateAsync(realGit: true);
        string origin = await CreateOriginAsync(fixture);
        var runner = new RealLfsRunner(origin)
        {
            OverrideFsckResult = cancelled
                ? new ProcessResult(null, false, true, [], []) : new ProcessResult(1, false, false, [], []),
            ThrowAfterFsck = throwIOException
        };

        var result = await new RepositoryBackupService(runner).BackupAsync(
            fixture.Repository, fixture.Context, fixture.RepositoryLock, default);

        Assert.AreEqual(cancelled, result.Cancelled);
        Assert.AreEqual(!cancelled, result.CoreFailed);
        Assert.IsFalse(result.Critical, string.Join(',', result.ErrorCodes));
        Assert.IsGreaterThan(0, runner.GeneratedHookFiles);
        Assert.IsTrue(runner.HooksDirectories.All(path => !Directory.Exists(path)));
        Assert.HasCount(0, Directory.GetFileSystemEntries(fixture.Session.Snapshot.EmptyHooksDirectory!));
        Assert.IsFalse(Directory.Exists(fixture.Staging));
        Assert.IsFalse(Directory.Exists(fixture.Final));
        Assert.HasCount(0, fixture.Context.OwnedStaging);
    }

    private static async Task<string> CreateOriginAsync(MirrorFixture fixture)
    {
        string origin = fixture.Preflight.Root.Child("origin");
        AclPolicy.CreateRestrictedDirectory(origin, fixture.Preflight.User);
        await LocalGitRunner.LocalAsync(origin, ["init", "."], fixture.Preflight.Environment);
        File.WriteAllText(Path.Combine(origin, "readme.txt"), "A normal committed repository.\n");
        await LocalGitRunner.LocalAsync(origin, ["add", "readme.txt"], fixture.Preflight.Environment);
        await LocalGitRunner.LocalAsync(origin,
            ["-c", "user.name=Fixture", "-c", "user.email=fixture@localhost", "commit", "--no-gpg-sign", "-m", "fixture"],
            fixture.Preflight.Environment);
        return origin;
    }

    private sealed class RealLfsRunner(string origin) : IProcessRunner
    {
        private readonly LocalGitRunner git = new(origin);
        private readonly ProcessRunner native = new();
        private readonly string lfs = @"C:\Program Files\Git\mingw64\bin\git-lfs.exe";
        internal int LfsRequests { get; private set; }
        internal int LfsFetchRequests { get; private set; }
        internal int LfsFsckRequests { get; private set; }
        internal string Diagnostic { get; private set; } = "";
        internal int GeneratedHookFiles { get; private set; }
        internal List<string> HooksDirectories { get; } = [];
        internal ProcessResult? OverrideFsckResult { get; init; }
        internal bool ThrowAfterFsck { get; init; }

        public async Task<ProcessResult> RunAsync(ProcessRequest request, OperationJob job,
            IProgress<string>? progress, CancellationToken token)
        {
            if (!request.FilePath.EndsWith("git-lfs.exe", StringComparison.OrdinalIgnoreCase))
                return await git.RunAsync(request, job, progress, token);
            LfsRequests++;
            if (request.Arguments[0] == "fetch") LfsFetchRequests++;
            if (request.Arguments[0] == "fsck") LfsFsckRequests++;
            var observer = new CollectProgress(value => { Diagnostic += value; progress?.Report(value); });
            var result = await native.RunAsync(request with
            {
                FilePath = lfs,
                ExpectedExecutableIdentity = ExecutableTrust.CaptureTrustedIdentity(lfs)
            }, job, observer, token);
            Diagnostic += "\nLFS exit: " + result.ExitCode + "\n";
            string hooks = request.Environment["GIT_CONFIG_VALUE_0"]!;
            HooksDirectories.Add(hooks);
            GeneratedHookFiles += Directory.GetFiles(hooks).Length;
            if (request.Arguments[0] == "fsck" && ThrowAfterFsck)
                throw new IOException("SYNTHETIC_LFS_PROCESS_FAILURE");
            return request.Arguments[0] == "fsck" && OverrideFsckResult is not null ? OverrideFsckResult : result;
        }
    }

    private sealed class CollectProgress(Action<string> receive) : IProgress<string>
    {
        public void Report(string value) => receive(value);
    }
}
