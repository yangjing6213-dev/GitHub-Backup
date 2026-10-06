using GitHubBackup.App;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class GitRuntimeContextTests
{
    [TestMethod]
    public async Task Released_candidate_rejects_stale_requests_and_allows_new_runtime_on_the_same_job()
    {
        using var h = new PreflightFixture();
        var first = await GitRuntimeContext.CreateAsync(h.Tools.Git!,h.Tools.GitHubCli!,h.Runner,h.Job,h.Environment,default);
        GitRuntimeContext? second = null;
        try
        {
            string directory = Path.GetDirectoryName(first.Environment["GIT_CONFIG_GLOBAL"])!;
            await first.DisposeCandidateAsync(h.Job);
            Assert.IsFalse(Directory.Exists(directory));
            Assert.IsFalse(h.Job.IsCancellationRequested);
            Assert.ThrowsExactly<ObjectDisposedException>(() => ((RuntimeEnvironment)first.Environment).Owner!.AcquireRequest(h.Job));
            second = await GitRuntimeContext.CreateAsync(h.Tools.Git!,h.Tools.GitHubCli!,h.Runner,h.Job,h.Environment,default);
            using (second.AcquireRequest(h.Job)) { }
            Assert.AreNotEqual(directory,Path.GetDirectoryName(second.Environment["GIT_CONFIG_GLOBAL"]));
            Assert.IsFalse(h.Job.IsCancellationRequested);
        }
        finally
        {
            await h.Job.CancelAllAsync(TimeSpan.FromSeconds(5));
            await first.DisposeAsync();
            if (second is not null) await second.DisposeAsync();
        }
    }

    [TestMethod]
    [DataRow("session")]
    [DataRow("request")]
    [DataRow("job-lease")]
    [DataRow("different-job")]
    public async Task Candidate_release_preserves_live_or_mismatched_resources(string boundary)
    {
        using var h = new PreflightFixture();
        using var other = OperationJob.Create();
        var runtime = await GitRuntimeContext.CreateAsync(h.Tools.Git!,h.Tools.GitHubCli!,h.Runner,h.Job,h.Environment,default);
        IDisposable? active = null;
        try
        {
            if (boundary == "session") runtime.FreezeSessionInventory(h.Tools);
            if (boundary == "request") active = runtime.AcquireRequest(h.Job);
            if (boundary == "job-lease") active = h.Job.CreateRequestJob();
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await runtime.DisposeCandidateAsync(boundary == "different-job" ? other : h.Job));
            Assert.AreEqual(boundary == "different-job" ? "GIT_RUNTIME_OPERATION_MISMATCH" : "GIT_RUNTIME_OPERATION_STILL_ACTIVE",error.Message);
            Assert.IsTrue(Directory.Exists(Path.GetDirectoryName(runtime.Environment["GIT_CONFIG_GLOBAL"])));
            Assert.IsFalse(h.Job.IsCancellationRequested);
            Assert.IsFalse(other.IsCancellationRequested);
        }
        finally
        {
            active?.Dispose();
            await h.Job.CancelAllAsync(TimeSpan.FromSeconds(5));
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Public_probe_is_not_an_owned_route_candidate()
    {
        using var h = new PreflightFixture();
        await using var runtime = await GitRuntimeContext.CreatePublicProbeAsync(h.Environment,default);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await runtime.DisposeCandidateAsync(h.Job));
        Assert.AreEqual("GIT_RUNTIME_OPERATION_MISMATCH",error.Message);
        Assert.IsTrue(Directory.Exists(Path.GetDirectoryName(runtime.Environment["GIT_CONFIG_GLOBAL"])));
        Assert.IsFalse(h.Job.IsCancellationRequested);
    }

    [TestMethod]
    public async Task Installed_git_and_gh_create_helper_with_isolated_empty_config_and_system_only_parent_path()
    {
        using var tools = await TestToolBuilder.CreateAsync();
        string realGit = @"C:\Program Files\Git\cmd\git.exe";
        string realGh = @"C:\Program Files\GitHub CLI\gh.exe";
        var git = new ToolDetection(realGit, ExecutableTrust.CaptureTrustedIdentity(realGit), "installed", true);
        var gh = new ToolDetection(realGh, ExecutableTrust.CaptureTrustedIdentity(realGh), "installed", true);
        string ghConfig = Path.Combine(tools.Root, "empty-gh-config");
        AclPolicy.CreateRestrictedDirectory(ghConfig, WindowsIdentity.GetCurrent().User!);
        var environment = ChildEnvironmentBuilder.Build(ChildEnvironmentBuilder.CreateBase(new Dictionary<string,string?>
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["TEMP"] = tools.Root, ["TMP"] = tools.Root, ["USERPROFILE"] = tools.Root,
            ["APPDATA"] = tools.Root, ["LOCALAPPDATA"] = tools.Root
        }, []), new Dictionary<string,string?> { ["GH_CONFIG_DIR"] = ghConfig });
        var runner = new EmptyConfigSetupRunner(realGh);
        using var job = OperationJob.Create();
        GitRuntimeContext? context = null;
        try
        {
            context = await GitRuntimeContext.CreateAsync(git, gh, runner, job, environment, default);
            Assert.IsTrue(runner.SetupSucceeded, "The real gh process must invoke installed Git successfully.");
            CollectionAssert.AreEqual(new[] { Path.GetDirectoryName(realGit)!, Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32") }, context.Environment["PATH"]!.Split(Path.PathSeparator));
            Assert.AreEqual(ghConfig, context.Environment["GH_CONFIG_DIR"]);
            Assert.AreEqual(tools.Root, context.Environment["USERPROFILE"]);
            Assert.IsFalse(context.Environment.ContainsKey("GH_TOKEN"));
            Assert.Contains("auth git-credential", File.ReadAllText(context.Environment["GIT_CONFIG_GLOBAL"]!));
            Assert.HasCount(0, Directory.GetFileSystemEntries(ghConfig));
        }
        finally
        {
            await job.CancelAllAsync(TimeSpan.FromSeconds(5));
            if (context is not null) await context.DisposeAsync();
        }
    }

    // --force only lets this test initialize a helper with no configured account.
    // Production receives the unchanged, non-force request and never reads test credentials.
    private sealed class EmptyConfigSetupRunner(string gh) : IProcessRunner
    {
        private readonly ProcessRunner native = new();
        internal bool SetupSucceeded { get; private set; }
        public async Task<ProcessResult> RunAsync(ProcessRequest request, OperationJob job, IProgress<string>? progress, CancellationToken token)
        {
            if (request.FilePath != gh) return await native.RunAsync(request, job, progress, token);
            CollectionAssert.AreEqual(new[] { "auth", "setup-git", "--hostname", "github.com" }, request.Arguments.ToArray());
            var result = await native.RunAsync(request with { Arguments = request.Arguments.Append("--force").ToArray() }, job, progress, token);
            SetupSucceeded = ProcessOutcomeClassifier.Classify(result) == ProcessTerminalKind.Succeeded;
            return result;
        }
    }

    [TestMethod]
    public async Task Changed_git_identity_is_rejected_before_gh_can_launch_nested_git()
    {
        using var tools = await TestToolBuilder.CreateAsync();
        string gitPath = Path.Combine(tools.Root, "git.exe");
        var identity = ExecutableTrust.CaptureTrustedIdentity(gitPath);
        var git = new ToolDetection(gitPath, identity with { FileIndex = identity.FileIndex + 1 }, "fixture", true);
        var gh = new ToolDetection(tools.Executable, ExecutableTrust.CaptureTrustedIdentity(tools.Executable), "fixture", true);
        var runner = new ScriptedProcessRunner(); using var job = OperationJob.Create();
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => GitRuntimeContext.CreateAsync(git, gh, runner, job, tools.Environment, default));
        Assert.AreEqual("PROCESS_EXECUTABLE_IDENTITY_CHANGED", error.Message);
        Assert.HasCount(0, runner.Requests);
        Assert.HasCount(0, Directory.GetDirectories(tools.Root, "GitHubBackup-git-*"));
        AssertParentLeaseReleased(tools.Root);
    }

    [TestMethod]
    public async Task Validated_git_cannot_be_replaced_while_gh_resolves_it_through_path()
    {
        using var tools = await TestToolBuilder.CreateAsync();
        string gitPath = Path.Combine(tools.Root, "git.exe");
        var git = new ToolDetection(gitPath, ExecutableTrust.CaptureTrustedIdentity(gitPath), "fixture", true);
        var gh = new ToolDetection(tools.Executable, ExecutableTrust.CaptureTrustedIdentity(tools.Executable), "fixture", true);
        var runner = new GitPinProbeRunner(gitPath); using var job = OperationJob.Create();
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => GitRuntimeContext.CreateAsync(git, gh, runner, job, tools.Environment, default));
        Assert.AreEqual("GIT_RUNTIME_COMMAND_FAILED", error.Message);
        Assert.IsTrue(runner.AttemptedReplacement);
        using var after = new FileStream(gitPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
    }

    private sealed class GitPinProbeRunner(string git) : IProcessRunner
    {
        internal bool AttemptedReplacement { get; private set; }
        public Task<ProcessResult> RunAsync(ProcessRequest request, OperationJob job, IProgress<string>? progress, CancellationToken token)
        {
            Assert.AreEqual(Path.GetDirectoryName(git), request.Environment["PATH"]!.Split(Path.PathSeparator)[0]);
            AttemptedReplacement = true;
            Assert.ThrowsExactly<IOException>(() => { using var ignored = new FileStream(git, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); });
            Assert.ThrowsExactly<IOException>(() => File.Move(git, git + ".replaced"));
            return Task.FromResult(new ProcessResult(1, false, false, [], []));
        }
    }

    [TestMethod]
    [DataRow("wrong-name")]
    [DataRow("path-separator")]
    public async Task Ambiguous_git_lookup_path_is_rejected_before_starting_a_process(string mutation)
    {
        using var tools = await TestToolBuilder.CreateAsync();
        string gitPath = tools.Executable;
        if (mutation == "path-separator")
        {
            string directory = Path.Combine(tools.Root, "git;injected");
            AclPolicy.CreateRestrictedDirectory(directory, WindowsIdentity.GetCurrent().User!);
            gitPath = Path.Combine(directory, "git.exe");
            File.Copy(tools.Executable, gitPath);
        }
        var git = new ToolDetection(gitPath, ExecutableTrust.CaptureTrustedIdentity(gitPath), "fixture", true);
        var runner = new ScriptedProcessRunner(); using var job = OperationJob.Create();
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => GitRuntimeContext.CreateAsync(git, git, runner, job, tools.Environment, default));
        Assert.AreEqual("GIT_RUNTIME_GIT_PATH_INVALID", error.Message);
        Assert.HasCount(0, runner.Requests);
        Assert.HasCount(0, Directory.GetDirectories(tools.Root, "GitHubBackup-git-*"));
    }

    [TestMethod]
    public async Task Adding_git_directory_preserves_live_authentication_binding()
    {
        using var fixture = new PreflightFixture();
        using var tools = await TestToolBuilder.CreateAsync();
        using var authentication = fixture.Auth.AcquireConfig();
        var inherited = authentication.CreateEnvironment(tools.Environment);
        string realGit = @"C:\Program Files\Git\cmd\git.exe";
        var git = new ToolDetection(realGit, ExecutableTrust.CaptureTrustedIdentity(realGit), "installed", true);
        var gh = new ToolDetection(tools.Executable, ExecutableTrust.CaptureTrustedIdentity(tools.Executable), "fixture", true);
        using var job = OperationJob.Create();
        var context = await GitRuntimeContext.CreateAsync(git, gh, new SetupGitRunner(gh.AbsolutePath), job, inherited, default);
        try
        {
            var environment = (RuntimeEnvironment)context.Environment;
            Assert.AreSame(context, environment.Owner);
            Assert.AreSame(authentication, environment.Authentication);
            Assert.AreEqual("fixture-user", environment.AuthenticatedLogin);
            Assert.AreEqual(fixture.Paths.AppGhConfigDirectory, environment["GH_CONFIG_DIR"]);
            Assert.AreEqual(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32"), inherited["PATH"]);
            File.WriteAllText(fixture.Hosts, PreflightFixture.Metadata.Replace("fixture-user", "changed-user", StringComparison.Ordinal));
            var error = Assert.ThrowsExactly<AuthBoundaryException>(() => environment.RevalidateAuthentication());
            Assert.AreEqual("AUTH_LOGIN_MISMATCH", error.Code);
        }
        finally
        {
            await job.CancelAllAsync(TimeSpan.FromSeconds(5));
            await context.DisposeAsync();
        }
    }

    [TestMethod]
    [DataRow(false, "telemetry")] [DataRow(true, "telemetry")]
    [DataRow(false, "notifier")] [DataRow(true, "notifier")]
    [DataRow(false, "unknown")] [DataRow(true, "unknown")]
    public async Task Invalid_environment_leaves_no_runtime_directory_or_parent_lease(bool authenticated, string mutation)
    {
        using var tools = await TestToolBuilder.CreateAsync();
        var environment = new Dictionary<string,string?>(tools.Environment, StringComparer.OrdinalIgnoreCase);
        if (mutation == "unknown") environment["UNAPPROVED_KEY"] = "fixture";
        else environment.Remove(mutation == "telemetry" ? "GH_TELEMETRY" : "GH_NO_UPDATE_NOTIFIER");
        using var job = OperationJob.Create(); var runner = new ScriptedProcessRunner();
        string git = Path.Combine(tools.Root, "git.exe");
        var tool = new ToolDetection(git, ExecutableTrust.CaptureTrustedIdentity(git), "fixture", true);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => authenticated
            ? GitRuntimeContext.CreateAsync(tool, tool, runner, job, environment, default)
            : GitRuntimeContext.CreatePublicProbeAsync(environment, default));
        Assert.HasCount(0, runner.Requests);
        Assert.HasCount(0, Directory.GetDirectories(tools.Root, "GitHubBackup-git-*"));
        AssertParentLeaseReleased(tools.Root);
    }

    private static void AssertParentLeaseReleased(string path)
    {
        // Rename immediately, without GC/finalizers: any retained READ_DATA lease
        // on this parent would deny delete sharing and make this operation fail.
        string moved = path + ".lease-check";
        Directory.Move(path, moved); Directory.Move(moved, path);
    }

    [TestMethod]
    [DataRow(false, "directory")] [DataRow(true, "directory")]
    [DataRow(false, "global.gitconfig")] [DataRow(true, "global.gitconfig")]
    [DataRow(false, "hooks")] [DataRow(true, "hooks")]
    public async Task Failure_after_resource_acquisition_rolls_back_owned_entries_and_lease(bool authenticated, string failureStage)
    {
        using var tools = await TestToolBuilder.CreateAsync();
        using var job = OperationJob.Create(); var runner = new ScriptedProcessRunner();
        string git = Path.Combine(tools.Root, "git.exe");
        var tool = new ToolDetection(git, ExecutableTrust.CaptureTrustedIdentity(git), "fixture", true);
        string? createdDirectory = null; bool reachedFailure = false;
        void AfterCreated(string path)
        {
            if (Path.GetFileName(path).StartsWith("GitHubBackup-git-", StringComparison.Ordinal)) createdDirectory = path;
            if ((failureStage == "directory" && path == createdDirectory) || Path.GetFileName(path) == failureStage)
            {
                Assert.IsTrue(File.Exists(path) || Directory.Exists(path));
                Assert.ThrowsExactly<IOException>(() => Directory.Move(createdDirectory!, createdDirectory! + ".unexpected"));
                reachedFailure = true;
                throw new IOException("INJECTED_INITIALIZATION_FAILURE");
            }
        }
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => authenticated
            ? GitRuntimeContext.CreateAsync(tool, tool, runner, job, tools.Environment, default, AfterCreated)
            : GitRuntimeContext.CreatePublicProbeAsync(tools.Environment, default, AfterCreated));
        Assert.AreEqual("INJECTED_INITIALIZATION_FAILURE", error.Message);
        Assert.IsTrue(reachedFailure); Assert.IsNotNull(createdDirectory); Assert.IsFalse(Directory.Exists(createdDirectory));
        Assert.HasCount(0, runner.Requests); Assert.HasCount(0, Directory.GetDirectories(tools.Root, "GitHubBackup-git-*"));
        AssertParentLeaseReleased(tools.Root);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task File_creation_io_failure_rolls_back_runtime_directory_and_lease(bool authenticated)
    {
        using var tools = await TestToolBuilder.CreateAsync();
        using var job = OperationJob.Create(); var runner = new ScriptedProcessRunner();
        string git = Path.Combine(tools.Root, "git.exe");
        var tool = new ToolDetection(git, ExecutableTrust.CaptureTrustedIdentity(git), "fixture", true);
        string? createdDirectory = null;
        void DenyConfigCreation(string path)
        {
            if (!Path.GetFileName(path).StartsWith("GitHubBackup-git-", StringComparison.Ordinal)) return;
            createdDirectory = path;
            // Change only this newly created test directory; subsequent native
            // CreateRestrictedFile must receive a real Windows access-denied error.
            var info = new DirectoryInfo(path); var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles, AccessControlType.Deny));
            info.SetAccessControl(acl);
        }
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => authenticated
            ? GitRuntimeContext.CreateAsync(tool, tool, runner, job, tools.Environment, default, DenyConfigCreation)
            : GitRuntimeContext.CreatePublicProbeAsync(tools.Environment, default, DenyConfigCreation));
        Assert.IsNotNull(createdDirectory); Assert.IsFalse(Directory.Exists(createdDirectory));
        Assert.HasCount(0, runner.Requests); AssertParentLeaseReleased(tools.Root);
    }

    [TestMethod] public void Official_helper_decodes_quote_escapes_and_rejects_extra_tokens()
    {
        const string path = @"C:\Program Files\O'Brien\gh.exe";
        string official = "!'C:\\Program Files\\O'\\''Brien\\gh.exe' auth git-credential";
        string pair = "credential.https://github.com.helper\n\0credential.https://github.com.helper\n";
        GitRuntimeContext.ValidateHelpers(pair + official + "\0", path);
        foreach (string value in new[] { official + " get", official + " ; calc", "!gh auth git-credential", "!\"" + path + "\" auth git-credential", official + "\n", official.Replace("auth", "AUTH", StringComparison.Ordinal) })
            Assert.ThrowsExactly<InvalidDataException>(() => GitRuntimeContext.ValidateHelpers(pair + value + "\0", path));
        foreach (string extra in new[] { "include.path\nx\0", "credential.https://evil.com.helper\n\0", "credential.https://github.com.helper\nevil\0" })
            Assert.ThrowsExactly<InvalidDataException>(() => GitRuntimeContext.ValidateHelpers(pair + official + "\0" + extra, path));
    }

    [TestMethod] public void Executable_trust_allows_read_and_ancestor_add_directory_but_rejects_installation_injection()
    {
        string user = WindowsIdentity.GetCurrent().User!.Value;
        ExecutableTrust.CheckDescriptor(new RawSecurityDescriptor($"O:{user}D:(A;;FR;;;WD)(A;;0x4;;;BU)(A;;FA;;;{user})"), false);
        foreach (string rights in new[] { "0x2", "0x4", "0x10", "0x40", "0x100", "SD", "WD", "WO", "GW", "GA" })
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => ExecutableTrust.CheckDescriptor(new RawSecurityDescriptor($"O:{user}D:(A;;{rights};;;WD)(A;;FA;;;{user})"), true));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => ExecutableTrust.CheckDescriptor(new RawSecurityDescriptor($"O:{user}D:(A;;0x40;;;BU)(A;;FA;;;{user})"), false));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => ExecutableTrust.CheckDescriptor(new RawSecurityDescriptor("O:WDD:(A;;FR;;;WD)"), true));
    }

    [TestMethod] public async Task Public_context_has_empty_isolation_and_removes_it_after_use()
    {
        using var tools = await TestToolBuilder.CreateAsync();
        var context = await GitRuntimeContext.CreatePublicProbeAsync(tools.Environment, default);
        string config = context.Environment["GIT_CONFIG_GLOBAL"]!;
        Assert.AreEqual("", File.ReadAllText(config)); Assert.AreEqual("1", context.Environment["GIT_CONFIG_NOSYSTEM"]); Assert.IsTrue(Directory.Exists(context.EmptyHooksDirectory));
        await context.DisposeAsync(); Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(config)));
    }

    [TestMethod] public async Task Production_temp_context_retains_private_capability_on_this_machine()
    {
        var environment = ChildEnvironmentBuilder.CreateBase(new Dictionary<string,string?> { ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"), ["TEMP"] = Path.GetTempPath() }, []);
        var context = await GitRuntimeContext.CreatePublicProbeAsync(environment, default);
        string directory = Path.GetDirectoryName(context.Environment["GIT_CONFIG_GLOBAL"])!;
        using (var lease = SummaryStore.RequirePrivateDirectory(directory))
            Assert.ThrowsExactly<IOException>(() => Directory.Move(directory, directory + ".moved"));
        await context.DisposeAsync(); Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod] public async Task Official_pairs_reduce_to_github_and_real_git_invokes_only_fake_helper()
    {
        using var tools = await TestToolBuilder.CreateAsync();
        string installation = Path.Combine(tools.Root, "Program Files", "O'Brien GitHub CLI");
        AclPolicy.CreateRestrictedDirectory(installation, WindowsIdentity.GetCurrent().User!);
        foreach (string source in Directory.GetFiles(Path.GetDirectoryName(tools.Executable)!)) File.Copy(source, Path.Combine(installation, Path.GetFileName(source)));
        string gh = Path.Combine(installation, "gh.exe");
        string realGit = @"C:\Program Files\Git\bin\git.exe";
        var git = new ToolDetection(realGit, ExecutableTrust.CaptureTrustedIdentity(realGit), "fixture", true);
        var ghDetection = new ToolDetection(gh, ExecutableTrust.CaptureTrustedIdentity(gh), "fixture", true);
        var runner = new SetupGitRunner(gh);
        using var job = OperationJob.Create();
        var context = await GitRuntimeContext.CreateAsync(git, ghDetection, runner, job, tools.Environment, default);
        string config = context.Environment["GIT_CONFIG_GLOBAL"]!;
        Assert.DoesNotContain("gist", File.ReadAllText(config));
        Assert.Contains("helper =\n", File.ReadAllText(config));
        Assert.IsTrue(runner.Requests.All(x => x.ExpectedExecutableIdentity == (x.FilePath == gh ? ghDetection.Identity : git.Identity)));
        CollectionAssert.AreEqual(new[] { "auth", "setup-git", "--hostname", "github.com" }, runner.Requests[0].Arguments.ToArray());
        // Real Git needs stdin for credential fill; this test-only start is restricted
        // to the disposable config and the exact fake helper, with no real keyring.
        var start = new ProcessStartInfo(realGit) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(config)! };
        start.ArgumentList.Add("credential"); start.ArgumentList.Add("fill"); start.Environment.Clear();
        foreach (var pair in context.Environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync("protocol=https\nhost=github.com\n\n"); process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync(); Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, process.ExitCode, await error);
        CollectionAssert.AreEqual(new[] { "auth", "git-credential", "get" }, JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(config)!, "helper-args.json")))!);
        File.Delete(Path.Combine(Path.GetDirectoryName(config)!, "helper-args.json"));
        Assert.Contains("username=TESTONLY", await output);
        Assert.ThrowsExactly<InvalidOperationException>(() => context.DisposeAsync());
        await job.CancelAllAsync(TimeSpan.FromSeconds(5)); await context.DisposeAsync();
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(config)));
    }

    [TestMethod]
    [DataRow("extra-helper")] [DataRow("different-path")] [DataRow("host")] [DataRow("include")] [DataRow("extra-token")]
    public async Task Unexpected_setup_configuration_is_rejected_before_any_helper_invocation(string mutation)
    {
        using var tools = await TestToolBuilder.CreateAsync();
        string realGit = @"C:\Program Files\Git\cmd\git.exe";
        var git = new ToolDetection(realGit, ExecutableTrust.CaptureTrustedIdentity(realGit), "fixture", true);
        var gh = new ToolDetection(tools.Executable, ExecutableTrust.CaptureTrustedIdentity(tools.Executable), "fixture", true);
        var runner = new SetupGitRunner(gh.AbsolutePath, mutation); using var job = OperationJob.Create();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => GitRuntimeContext.CreateAsync(git, gh, runner, job, tools.Environment, default));
        Assert.HasCount(2, runner.Requests);
        Assert.IsFalse(Directory.EnumerateFiles(tools.Root, "helper-args.json", SearchOption.AllDirectories).Any());
        Assert.IsFalse(Directory.EnumerateDirectories(tools.Root, "GitHubBackup-git-*").Any());
    }

    private sealed class SetupGitRunner(string gh, string? mutation = null) : IProcessRunner
    {
        private readonly ProcessRunner native = new();
        internal List<ProcessRequest> Requests { get; } = [];
        public async Task<ProcessResult> RunAsync(ProcessRequest request, OperationJob job, IProgress<string>? progress, CancellationToken token)
        {
            Requests.Add(request);
            if (request.FilePath != gh) return await native.RunAsync(request, job, progress, token);
            string config = request.Environment["GIT_CONFIG_GLOBAL"]!;
            string helper = GitRuntimeContext.Helper(gh).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
            string text = string.Join("", new[] { "github.com", "gist.github.com" }.Select(host => $"[credential \"https://{host}\"]\n helper =\n helper = \"{helper}\"\n"));
            text = mutation switch
            {
                "extra-helper" => text + " helper = evil\n",
                "different-path" => text.Replace("FakeTool.exe", "other.exe", StringComparison.Ordinal),
                "host" => text.Replace("github.com", "evil.example", StringComparison.Ordinal),
                "include" => text + "[include]\n path = missing-file\n",
                "extra-token" => text.Replace("auth git-credential", "auth git-credential get", StringComparison.Ordinal),
                _ => text
            };
            await AtomicFile.WriteAsync(config, async (stream, ct) => await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct), token);
            return new(0, false, false, [], []);
        }
    }
    [TestMethod] public void Child_environment_removes_unapproved_variables_and_does_not_mutate_parent()
    {
        var parent = new Dictionary<string,string?> { ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"), ["TEMP"] = Path.GetTempPath(), ["GH_TOKEN"] = "TESTONLY", ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "evil", ["GIT_ASKPASS"] = "evil", ["https_proxy"] = "evil", ["OPENAI_API_KEY"] = "evil", ["AWS_SECRET_ACCESS_KEY"] = "evil", ["SSH_AUTH_SOCK"] = "evil", ["SSL_CERT_FILE"] = "evil", ["CANARY"] = "evil" };
        var snapshot = parent.ToArray();
        var child = ChildEnvironmentBuilder.CreateBase(parent, []);
        Assert.HasCount(5, child); Assert.IsTrue(child.ContainsKey("PATH")); CollectionAssert.AreEqual(snapshot, parent.ToArray());
        foreach (string key in new[] { "PATH", "TEMP", "GH_TOKEN", "GIT_SSH_COMMAND", "UNRELATED" })
            Assert.ThrowsExactly<ArgumentException>(() => ChildEnvironmentBuilder.Build(child, new Dictionary<string,string?> { [key] = "x" }));
        Assert.ThrowsExactly<ArgumentException>(() => ChildEnvironmentBuilder.Build(child, new Dictionary<string,string?> { ["NO_PROXY"] = "x\ny" }));
    }

    [TestMethod] public void Gh_telemetry_and_update_checks_are_disabled_and_cannot_be_overridden()
    {
        var parent = new Dictionary<string,string?> { ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"), ["GH_TELEMETRY"] = "true", ["GH_NO_UPDATE_NOTIFIER"] = "0" };
        var child = ChildEnvironmentBuilder.CreateBase(parent, []);
        Assert.AreEqual("false", child["GH_TELEMETRY"]); Assert.AreEqual("1", child["GH_NO_UPDATE_NOTIFIER"]);
        Assert.AreEqual("true", parent["GH_TELEMETRY"]);
        foreach (string key in new[] { "GH_TELEMETRY", "GH_NO_UPDATE_NOTIFIER" })
            Assert.ThrowsExactly<ArgumentException>(() => ChildEnvironmentBuilder.Build(child, new Dictionary<string,string?> { [key] = "true" }));
    }
}
