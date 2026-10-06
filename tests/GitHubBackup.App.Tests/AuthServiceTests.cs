using GitHubBackup.App;
using System.Security.Principal;
using System.Text;
using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class AuthServiceTests
{
    [TestMethod]
    [DataRow(false, false)][DataRow(false, true)][DataRow(true, false)][DataRow(true, true)]
    public async Task Cleanup_failure_preserves_keyring_postcondition_and_safe_failure_for_completed_and_fallback_login(bool fallback, bool enumerationFails)
    {
        using var h = new AuthHarness();
        var context = await GitRuntimeContext.CreatePublicProbeAsync(h.Environment, default);
        using var pinned = NativeFileSystem.Open(context.Environment["GIT_CONFIG_GLOBAL"]!);
        var carrier = new GitRuntimeCleanupException(context); h.Detect = (_, _) => throw carrier;
        h.Runner.Results.Enqueue(_ =>
        {
            h.Store.Missing = !enumerationFails; h.Store.ThrowEnumeration = enumerationFails;
            File.WriteAllText(h.Hosts, AuthHarness.Metadata);
            if (fallback) throw new IOException("PRIVATE_LOGIN_FAILURE");
            return new(0, false, false, [], []);
        });
        try
        {
            var failure = await Assert.ThrowsAsync<AuthCleanupException>(h.Login);
            Assert.AreSame(carrier, failure.Cleanup); Assert.AreSame(context, failure.Cleanup.Context);
            Assert.IsFalse(failure.Outcome.AuthReady); Assert.AreEqual("", failure.Outcome.Login);
            Assert.AreEqual(3, h.Store.Calls, "Probe, before snapshot, and post-login target enumeration must all execute.");
            Assert.AreEqual(enumerationFails ? "AUTH_KEYRING_ENUMERATION_FAILED" : "AUTH_KEYRING_TARGET_MISSING", failure.Message);
            Assert.DoesNotContain("PRIVATE", failure.ToString()); Assert.AreEqual(1, h.Redetections);
        }
        finally { pinned.Dispose(); await context.DisposeAsync(); }
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task Login_redetection_cleanup_carrier_is_not_converted_to_auth_failure(bool fallback)
    {
        using var h = new AuthHarness();
        var context = await GitRuntimeContext.CreatePublicProbeAsync(h.Environment, default);
        using var pinned = NativeFileSystem.Open(context.Environment["GIT_CONFIG_GLOBAL"]!);
        var carrier = new GitRuntimeCleanupException(context);
        h.Detect = (_, _) => throw carrier;
        h.Runner.Results.Enqueue(_ => { if (fallback) throw new IOException("synthetic login failure"); return new(1, false, false, [], []); });
        try
        {
            var failure = await Assert.ThrowsAsync<AuthCleanupException>(h.Login);
            Assert.AreSame(carrier, failure.Cleanup); Assert.AreEqual(1, h.Redetections);
        }
        finally { pinned.Dispose(); await context.DisposeAsync(); }
    }

    [TestMethod]
    public async Task Api_credential_acquisition_requires_protected_saved_consent()
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, AuthHarness.Metadata);
        using var config = h.Service.AcquireConfig();
        var reader = new FakeApiReader();
        var forged = new AppSettings("fixture-user", "X:\\backup", NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        Assert.AreEqual("AUTH_API_CONSENT_REQUIRED",
            (await Assert.ThrowsExactlyAsync<AuthBoundaryException>(() => h.Service.AcquireApiCredentialAsync(forged.Owner, config, reader, default))).Code);
        await new SettingsStore(h.Paths).SaveAsync(forged, default);
        Assert.AreEqual("AUTH_API_CONSENT_REQUIRED",
            (await Assert.ThrowsExactlyAsync<AuthBoundaryException>(() => h.Service.AcquireApiCredentialAsync("other-user", config, reader, default))).Code);
        Assert.AreEqual(0, reader.ReadCount);
        using var acquired = await h.Service.AcquireApiCredentialAsync("fixture-user", config, reader, default);
        Assert.AreEqual("fixture-user", acquired.Login);
        Assert.AreEqual(1, reader.ReadCount);
    }

    [TestMethod]
    public async Task Saved_consent_withdrawal_or_version_or_owner_change_blocks_read()
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, AuthHarness.Metadata);
        using var config = h.Service.AcquireConfig();
        var store = new SettingsStore(h.Paths);
        var reader = new FakeApiReader();
        var granted = new AppSettings("fixture-user", "X:\\backup", NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        await store.SaveAsync(granted, default);
        AppSettings oldSnapshot = (await store.LoadAsync(default)).Settings;
        foreach (AppSettings changed in new[]
        {
            oldSnapshot with { ApiCredentialConsentVersion = null, ApiCredentialConsentLogin = null },
            oldSnapshot with { ApiCredentialConsentVersion = 2 },
            oldSnapshot with { Owner = "other-user" }
        })
        {
            await store.SaveAsync(changed, default);
            Assert.AreEqual("AUTH_API_CONSENT_REQUIRED",
                (await Assert.ThrowsExactlyAsync<AuthBoundaryException>(() => h.Service.AcquireApiCredentialAsync(oldSnapshot.Owner, config, reader, default))).Code);
            Assert.AreEqual(0, reader.ReadCount);
        }
    }

    [TestMethod]
    public async Task Unsafe_saved_settings_source_blocks_read()
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, AuthHarness.Metadata);
        using var config = h.Service.AcquireConfig();
        var reader = new FakeApiReader();
        await new SettingsStore(h.Paths).SaveAsync(new AppSettings("fixture-user", "X:\\backup", NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" }, default);
        var info = new FileInfo(h.Paths.SettingsFile);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(security);
        AuthBoundaryException error = await Assert.ThrowsExactlyAsync<AuthBoundaryException>(() =>
            h.Service.AcquireApiCredentialAsync("fixture-user", config, reader, default));
        Assert.AreEqual("AUTH_API_SETTINGS_UNTRUSTED", error.Code);
        Assert.IsFalse(error.ToString().Contains(h.Paths.SettingsFile, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(0, reader.ReadCount);
    }

    [TestMethod]
    public async Task Cancelled_acquisition_does_not_read_credential()
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, AuthHarness.Metadata);
        using var config = h.Service.AcquireConfig();
        var reader = new FakeApiReader();
        await new SettingsStore(h.Paths).SaveAsync(new AppSettings("fixture-user", "X:\\backup", NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" }, default);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            h.Service.AcquireApiCredentialAsync("fixture-user", config, reader, cancel.Token));
        Assert.AreEqual(0, reader.ReadCount);
    }

    [TestMethod]
    public async Task Changed_auth_config_blocks_api_credential_read()
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, AuthHarness.Metadata);
        using var config = h.Service.AcquireConfig();
        var reader = new FakeApiReader();
        await new SettingsStore(h.Paths).SaveAsync(new AppSettings("fixture-user", "X:\\backup", NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" }, default);
        File.WriteAllText(h.Config, "version: \"1\"\napi_host: attacker.invalid\n");
        Assert.AreEqual("AUTH_CONFIG_CONTENT_REJECTED",
            (await Assert.ThrowsExactlyAsync<AuthBoundaryException>(() => h.Service.AcquireApiCredentialAsync("fixture-user", config, reader, default))).Code);
        Assert.AreEqual(0, reader.ReadCount);
    }

    [TestMethod]
    public async Task Reader_cannot_switch_selected_login()
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, AuthHarness.Metadata);
        using var config = h.Service.AcquireConfig();
        var reader = new FakeApiReader { OverrideLogin = "other-user" };
        await new SettingsStore(h.Paths).SaveAsync(new AppSettings("fixture-user", "X:\\backup", NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" }, default);
        Assert.AreEqual("AUTH_LOGIN_MISMATCH",
            (await Assert.ThrowsExactlyAsync<AuthBoundaryException>(() => h.Service.AcquireApiCredentialAsync("fixture-user", config, reader, default))).Code);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        Assert.ThrowsExactly<ObjectDisposedException>(() => reader.LastLease!.AttachAuthorization(request));
    }

    private sealed class FakeApiReader : IGitHubCredentialReader
    {
        internal int ReadCount;
        internal string? OverrideLogin;
        internal GitHubCredentialLease? LastLease;
        public GitHubCredentialLease ReadExact(string login)
        {
            ReadCount++;
            return LastLease = new GitHubCredentialLease(OverrideLogin ?? login, "TEST_CANARY"u8.ToArray());
        }
    }
    [TestMethod]
    public async Task Cancelled_login_deadline_retains_operation_until_detection_finally_finishes()
    {
        using var h = new AuthHarness(); var detection = new DelayedDetectionCompletion();
        h.Detect = detection.RunAsync;
        h.Runner.Results.Enqueue(_ => { h.Job.BeginCancellation(); return new(null,false,true,[],[]); });
        Task<AuthResult> login = h.Login();
        await detection.AssertStillOwnedThenReleaseAsync(login);
        Assert.IsFalse((await login).AuthReady);
        Assert.IsTrue(detection.Running!.IsCompleted); Assert.AreEqual(1, detection.CompletionWrites);
        Assert.IsTrue(detection.Job!.IsCancellationRequested); Assert.HasCount(1, h.Runner.Requests);
        Assert.IsFalse(File.Exists(h.Hosts));
    }

    [TestMethod]
    public async Task Native_redetection_failure_after_rejected_login_is_a_typed_auth_result()
    {
        using var h = new AuthHarness();
        h.Detect = (_, _) => throw new System.ComponentModel.Win32Exception(193, "UNSAFE_NATIVE_ERROR_BODY");
        h.Runner.Results.Enqueue(_ => { File.WriteAllText(h.Hosts, "oauth_token: TESTONLY\n"); return new(0,false,false,[],[]); });
        AuthResult result = await h.Login();
        Assert.IsFalse(result.AuthReady); Assert.AreEqual("AUTH_TOOL_REDETECTION_FAILED", result.ErrorCode);
        Assert.IsFalse(File.Exists(h.Hosts)); Assert.HasCount(1, h.Runner.Requests);
    }

    [TestMethod]
    [DataRow("empty")] [DataRow("config")] [DataRow("pair")]
    public async Task Empty_or_inert_app_config_requires_login_without_any_command(string state)
    {
        using var h = new AuthHarness();
        if (state != "empty") h.Write("config.yml", "version: \"1\"\n");
        if (state == "pair") h.Write("hosts.yml", "");
        Assert.AreEqual("AUTH_APP_LOGIN_REQUIRED", (await h.Check()).ErrorCode);
        Assert.HasCount(0, h.Runner.Requests); Assert.AreEqual(0, h.Store.Calls);
    }

    [TestMethod]
    public async Task Unconfirmed_login_does_not_seed_files_or_touch_store()
    {
        using var h = new AuthHarness();
        var result = await h.Service.LoginAsync(h.Tools, h.Environment, h.Job, false, null, default);
        Assert.AreEqual("AUTH_SHARED_CREDENTIAL_CONFIRMATION_REQUIRED", result.ErrorCode);
        Assert.HasCount(0, h.Runner.Requests); Assert.AreEqual(0, h.Store.Calls);
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(h.Paths.AppGhConfigDirectory).Any());
    }

    [TestMethod]
    public async Task Confirmed_login_seeds_protected_inert_pair_and_revalidates_before_status_and_api()
    {
        using var h = new AuthHarness();
        h.Runner.Results.Enqueue(request =>
        {
            CollectionAssert.AreEqual(new[] { "auth", "login", "--hostname", "github.com", "--git-protocol", "https", "--web" }, request.Arguments.ToArray());
            Assert.AreEqual(ProcessOutputMode.EphemeralText, request.OutputMode);
            Assert.AreEqual("version: \"1\"\n", File.ReadAllText(h.Config)); Assert.AreEqual(0L, new FileInfo(h.Hosts).Length);
            using var handle = NativeFileSystem.Open(h.Hosts);
            AclPolicy.VerifyRestricted(handle, WindowsIdentity.GetCurrent().User!);
            Assert.ThrowsExactly<IOException>(() => File.Move(h.Hosts, h.Hosts + ".moved"));
            File.WriteAllText(h.Hosts, AuthHarness.Metadata);
            return new(0, false, false, [], []);
        });
        h.QueueStatus(); h.QueueApi();
        AuthResult result = await h.Login();
        Assert.IsTrue(result.AuthReady); Assert.AreEqual("fixture-user", result.Login);
        Assert.AreEqual("AUTH_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED", result.ErrorCode);
        Assert.AreEqual(1, h.Redetections);
        Assert.HasCount(3, h.Runner.Requests);
        Assert.IsTrue(h.Runner.Requests.All(r => r.Environment["GH_CONFIG_DIR"] == h.Paths.AppGhConfigDirectory && r.ExpectedExecutableIdentity == h.Tools.GitHubCli!.Identity));
        Assert.IsFalse(h.Runner.Requests.Any(r => r.Arguments.Contains("logout") || r.Arguments.Contains("token") || r.Arguments.Contains("--show-token")));
    }

    [TestMethod]
    public async Task Device_code_prompt_opens_only_fixed_page_once_and_keeps_code_only_in_ephemeral_ui_output()
    {
        const string code = "ABCD-EFGH";
        var opened = new List<string>();
        var displayed = new List<string>();
        var runner = new ProgressOutputRunner(
            ["! First copy your one-time code: ABCD-", "EFGH\n", "! First copy your one-time code: IJKL-MNOP\n"],
            new(1, false, false, [], []));
        using var h = new AuthHarness(opened.Add, runner);

        AuthResult result = await h.Login(new InlineProgress(displayed.Add));

        Assert.AreEqual("AUTH_LOGIN_FAILED_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED", result.ErrorCode);
        CollectionAssert.AreEqual(new[] { AuthService.DeviceLoginUrl }, opened);
        CollectionAssert.AreEqual(new[] { "! First copy your one-time code: ABCD-", "EFGH\n", "! First copy your one-time code: IJKL-MNOP\n" }, displayed);
        Assert.AreEqual(ProcessOutputMode.EphemeralText, runner.Requests.Single().OutputMode);
        Assert.IsFalse(runner.Requests.Single().Arguments.Any(argument => argument.Contains(code, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Browser_open_failure_is_safe_and_does_not_interrupt_forwarding_device_code()
    {
        var displayed = new List<string>();
        var runner = new ProgressOutputRunner(
            ["! First copy your one-time code: ABCD-EFGH\n"],
            new(1, false, false, [], []));
        using var h = new AuthHarness(_ => throw new InvalidOperationException("PRIVATE_BROWSER_EXCEPTION_CANARY"), runner);

        AuthResult result = await h.Login(new InlineProgress(displayed.Add));

        Assert.AreEqual("AUTH_LOGIN_FAILED_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED", result.ErrorCode);
        Assert.IsFalse(result.AuthReady);
        Assert.IsTrue(result.BrowserOpenFailed);
        Assert.AreEqual("! First copy your one-time code: ABCD-EFGH\n", string.Concat(displayed));
        Assert.IsFalse(result.ToString().Contains("PRIVATE_BROWSER_EXCEPTION_CANARY", StringComparison.Ordinal));
        Assert.HasCount(1, runner.Requests);
    }

    [TestMethod]
    public async Task Successful_login_keeps_shared_credential_warning_when_browser_open_failed()
    {
        const string prompt = "! First copy your one-time code: ABCD-EFGH\n";
        var loginRunner = new PromptThenScriptedRunner(prompt);
        using var h = new AuthHarness(_ => throw new InvalidOperationException(), loginRunner);
        loginRunner.Next = h.Runner;
        loginRunner.CompleteLogin = () => File.WriteAllText(h.Hosts, AuthHarness.Metadata);
        h.QueueStatus(); h.QueueApi();

        AuthResult result = await h.Login();

        Assert.IsTrue(result.AuthReady);
        Assert.AreEqual("fixture-user", result.Login);
        Assert.AreEqual("AUTH_SHARED_ACTIVE_SLOT_MAY_HAVE_CHANGED", result.ErrorCode);
        Assert.IsTrue(result.BrowserOpenFailed);
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class ProgressOutputRunner(string[] output, ProcessResult result) : IProcessRunner
    {
        internal List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, OperationJob job, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request);
            foreach (string chunk in output) progress?.Report(chunk);
            return Task.FromResult(result);
        }
    }

    private sealed class PromptThenScriptedRunner(string prompt) : IProcessRunner
    {
        internal IProcessRunner? Next { get; set; }
        internal Action? CompleteLogin { get; set; }
        public Task<ProcessResult> RunAsync(ProcessRequest request, OperationJob job, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            if (request.Arguments.SequenceEqual(["auth", "login", "--hostname", "github.com", "--git-protocol", "https", "--web"]))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(prompt);
                CompleteLogin?.Invoke();
                return Task.FromResult(new ProcessResult(0, false, false, [], []));
            }
            return (Next ?? throw new InvalidOperationException("Test runner was not initialized."))
                .RunAsync(request, job, progress, cancellationToken);
        }
    }

    [TestMethod]
    [DataRow("api_host: attacker.invalid\n")]
    [DataRow("version: \"1\"\nversion: \"1\"\n")]
    [DataRow("version: &x \"1\"\n")]
    public async Task Unsafe_general_config_is_blocked_without_command_or_deletion(string config)
    {
        using var h = new AuthHarness(); h.Write("config.yml", config); h.Write("hosts.yml", AuthHarness.Metadata);
        Assert.AreEqual("AUTH_CONFIG_CONTENT_REJECTED", (await h.Check()).ErrorCode);
        Assert.HasCount(0, h.Runner.Requests); Assert.AreEqual(config, File.ReadAllText(h.Config)); Assert.IsTrue(File.Exists(h.Hosts));
    }

    [TestMethod]
    public async Task Unknown_sibling_prevents_plaintext_cleanup()
    {
        using var h = new AuthHarness(); h.Seed(); h.Write("unexpected.yml", "fixture"); File.WriteAllText(h.Hosts, "oauth_token: TESTONLY\n");
        Assert.AreEqual("AUTH_CONFIG_UNEXPECTED_ENTRY", (await h.Check()).ErrorCode);
        Assert.IsTrue(File.Exists(h.Hosts)); Assert.IsTrue(File.Exists(Path.Combine(h.Paths.AppGhConfigDirectory, "unexpected.yml"))); Assert.HasCount(0, h.Runner.Requests);
    }

    [TestMethod]
    public async Task Startup_plaintext_residue_is_removed_by_identity_before_any_command()
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, "github.com:\n    oauth_token: TESTONLY\n");
        Assert.AreEqual("AUTH_PLAINTEXT_RESIDUE_REMOVED", (await h.Check()).ErrorCode);
        Assert.IsFalse(File.Exists(h.Hosts)); Assert.AreEqual("version: \"1\"\n", File.ReadAllText(h.Config)); Assert.HasCount(0, h.Runner.Requests);
    }

    [TestMethod]
    [DataRow("login")] [DataRow("status")] [DataRow("api")]
    public async Task Post_command_plaintext_prevents_output_consumption(string phase)
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, AuthHarness.Metadata);
        if (phase != "login") h.QueueStatus(phase == "status" ? () => File.WriteAllText(h.Hosts, "oauth_token: TESTONLY\n") : null);
        if (phase == "api") h.QueueApi(() => File.WriteAllText(h.Hosts, "oauth_token: TESTONLY\n"));
        if (phase == "login") h.Runner.Results.Enqueue(_ => { File.WriteAllText(h.Hosts, "oauth_token: TESTONLY\n"); return new(0,false,false,[],[]); });
        var result = phase == "login" ? await h.Login() : await h.Check();
        Assert.IsFalse(result.AuthReady); Assert.AreEqual("AUTH_PLAINTEXT_STORAGE_REJECTED", result.ErrorCode);
        Assert.IsFalse(File.Exists(h.Hosts)); Assert.HasCount(phase == "api" ? 2 : 1, h.Runner.Requests);
    }

    [TestMethod]
    [DataRow("state", "AUTH_STATUS_NOT_READY")]
    [DataRow("source", "AUTH_PLAINTEXT_STORAGE_REJECTED")]
    [DataRow("malformed", "AUTH_STATUS_INVALID")]
    [DataRow("api", "AUTH_API_FAILED")]
    [DataRow("mismatch", "AUTH_LOGIN_MISMATCH")]
    public async Task Structured_status_and_api_failures_remain_distinguishable(string fault, string expected)
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, AuthHarness.Metadata);
        h.QueueStatus(text: fault switch { "state" => AuthHarness.Status.Replace("success", "error"), "source" => AuthHarness.Status.Replace("keyring", "oauth_token"), "malformed" => "{}", _ => null });
        if (fault is "api" or "mismatch") h.QueueApi(text: fault == "mismatch" ? "other-user" : null, exit: fault == "api" ? 1 : 0);
        Assert.AreEqual(expected, (await h.Check()).ErrorCode);
    }

    [TestMethod]
    public async Task Reusable_lease_blocks_renames_and_detects_content_changes()
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, AuthHarness.Metadata);
        using var lease = h.Service.AcquireConfig();
        Assert.AreEqual("fixture-user", lease.Login); Assert.AreEqual(h.Paths.AppGhConfigDirectory, lease.CreateEnvironment(h.Environment)["GH_CONFIG_DIR"]);
        Assert.ThrowsExactly<IOException>(() => Directory.Move(h.Paths.AppGhConfigDirectory, h.Paths.AppGhConfigDirectory + ".moved"));
        Assert.ThrowsExactly<IOException>(() => File.Move(h.Hosts, h.Hosts + ".moved"));
        File.WriteAllText(h.Config, "version: \"1\"\napi_host: attacker.invalid\n");
        Assert.ThrowsExactly<AuthBoundaryException>(() => lease.Revalidate());
    }

    [TestMethod]
    public async Task Api_that_leaves_empty_hosts_cannot_report_ready()
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, AuthHarness.Metadata);
        h.QueueStatus(); h.QueueApi(() => File.WriteAllText(h.Hosts, ""));
        Assert.AreEqual("AUTH_APP_LOGIN_REQUIRED", (await h.Check()).ErrorCode);
    }

    [TestMethod]
    [DataRow("\"123\"", "123")]
    [DataRow("\"true\"", "true")]
    [DataRow("'null'", "null")]
    [DataRow("fixture-user", "fixture-user")]
    public void Bounded_literal_username_quotes_and_official_null_user_nodes_are_accepted(string yaml, string login)
    {
        using var h = new AuthHarness(); h.Seed();
        File.WriteAllText(h.Hosts, "github.com:\n    git_protocol: https\n    users:\n        " + yaml + ":\n    user: " + yaml + "\n");
        using var lease = h.Service.AcquireConfig(); Assert.AreEqual(login, lease.Login);
    }

    [TestMethod]
    [DataRow("    \"oaut\\x68_token\": TESTONLY-SECRET\n")]
    [DataRow("    unknown: TESTONLY-SECRET\n")]
    [DataRow("    user: &alias TESTONLY-SECRET\n")]
    [DataRow("    user: \"escaped\\x2duser\"\n")]
    [DataRow("    users: {fixture-user: {unknown: TESTONLY-SECRET}}\n")]
    public async Task Unknown_or_escaped_yaml_is_rejected_before_child_or_cleanup(string yaml)
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, "github.com:\n" + yaml);
        Assert.AreEqual("AUTH_CONFIG_CONTENT_REJECTED", (await h.Check()).ErrorCode);
        Assert.HasCount(0, h.Runner.Requests); Assert.IsTrue(File.Exists(h.Hosts));
    }

    [TestMethod]
    public async Task Login_uses_fresh_detection_identity_for_following_status()
    {
        using var h = new AuthHarness();
        h.FreshTools = h.Tools with { GitHubCli = h.Tools.GitHubCli! with { Identity = new(5,6,7,8) } };
        h.Runner.Results.Enqueue(_ => { File.WriteAllText(h.Hosts, AuthHarness.Metadata); return new(0,false,false,[],[]); });
        h.QueueStatus(); h.QueueApi(); Assert.IsTrue((await h.Login()).AuthReady);
        Assert.IsTrue(h.Runner.Requests.Skip(1).All(r => r.ExpectedExecutableIdentity == h.FreshTools.GitHubCli!.Identity));
    }

    [TestMethod]
    public async Task Cancelled_login_cleans_exact_hosts_and_redetects_under_new_job()
    {
        using var h = new AuthHarness();
        h.Runner.Results.Enqueue(_ => { h.Job.BeginCancellation(); return new(null,false,true,[],[]); });
        Assert.IsFalse((await h.Login()).AuthReady); Assert.IsFalse(File.Exists(h.Hosts)); Assert.IsTrue(File.Exists(h.Config));
        Assert.AreEqual(1, h.Redetections); Assert.AreNotSame(h.Job, h.LastDetectionJob); Assert.IsTrue(h.LastDetectionJob!.IsCancellationRequested);
        Assert.HasCount(1, h.Runner.Requests);
    }

    [TestMethod]
    public async Task Cancellation_during_post_login_status_removes_only_hosts_and_skips_api()
    {
        using var h = new AuthHarness();
        h.Runner.Results.Enqueue(_ => { File.WriteAllText(h.Hosts, AuthHarness.Metadata); return new(0,false,false,[],[]); });
        h.QueueStatus(() => h.Job.BeginCancellation());
        AuthResult result = await h.Login();
        Assert.IsFalse(result.AuthReady); Assert.IsFalse(File.Exists(h.Hosts)); Assert.IsTrue(File.Exists(h.Config));
        Assert.HasCount(2, h.Runner.Requests);
    }

    [TestMethod]
    public async Task Precancelled_login_never_touches_store_or_seeds_files()
    {
        using var h = new AuthHarness(); h.Job.BeginCancellation();
        Assert.IsFalse((await h.Login()).AuthReady); Assert.AreEqual(0, h.Store.Calls); Assert.HasCount(0, h.Runner.Requests);
        Assert.IsFalse(File.Exists(h.Config));
    }

    [TestMethod]
    [DataRow("read", "AUTH_CONFIG_READ_ACL_UNSAFE")]
    [DataRow("write", "AUTH_CONFIG_WRITE_ACL_UNSAFE")]
    [DataRow("null", "AUTH_CONFIG_NULL_DACL_UNSAFE")]
    [DataRow("inherited", "AUTH_CONFIG_INHERITED_ACL_UNSAFE")]
    [DataRow("hardlink", "AUTH_CONFIG_HARDLINK_REJECTED")]
    [DataRow("junction", "AUTH_CONFIG_REPARSE_POINT_REJECTED")]
    public async Task Unsafe_filesystem_boundary_never_runs_a_child_or_removes_canary(string fault, string expected)
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, AuthHarness.Metadata);
        string sentinel = Path.Combine(h.Paths.LocalAppDataAnchor, "sentinel"); File.WriteAllText(sentinel, "KEEP");
        if (fault is "read" or "write") StorageTestRoot.Grant(h.Paths.AppGhConfigDirectory, fault == "read" ? FileSystemRights.ReadData : FileSystemRights.WriteData);
        if (fault == "null") h.SetNull(h.Hosts);
        if (fault == "inherited") { var info = new FileInfo(h.Hosts); var acl = info.GetAccessControl(); acl.SetAccessRuleProtection(false, true); info.SetAccessControl(acl); }
        if (fault == "hardlink") { File.Delete(h.Hosts); StorageTestRoot.CreateHardLink(h.Hosts, sentinel); }
        if (fault == "junction") { File.Delete(h.Hosts); StorageTestRoot.CreateJunction(h.Hosts, h.Paths.LocalAppDataAnchor); }
        try
        {
            Assert.AreEqual(expected, (await h.Check()).ErrorCode); Assert.HasCount(0, h.Runner.Requests);
            Assert.AreEqual("KEEP", File.ReadAllText(sentinel)); Assert.IsTrue(File.Exists(h.Config));
        }
        finally { if (fault == "junction") Directory.Delete(h.Hosts); }
    }

    [TestMethod]
    public async Task Plaintext_cleanup_sharing_failure_is_blocking_and_retains_file()
    {
        using var h = new AuthHarness(); h.Seed(); File.WriteAllText(h.Hosts, "oauth_token: TESTONLY\n");
        using var blocker = NativeFileSystem.Open(h.Hosts);
        Assert.AreEqual("AUTH_CONFIG_CLEANUP_FAILED", (await h.Check()).ErrorCode);
        Assert.IsTrue(File.Exists(h.Hosts)); Assert.HasCount(0, h.Runner.Requests);
    }

    [TestMethod]
    [DataRow("cancelled")] [DataRow("fallback")] [DataRow("status")]
    public async Task Preexisting_per_user_target_loss_is_reported_for_every_login_outcome(string fault)
    {
        using var h = new AuthHarness();
        h.Runner.Results.Enqueue(_ =>
        {
            h.Store.Missing = true;
            File.WriteAllText(h.Hosts, fault == "fallback" ? "oauth_token: TESTONLY\n" : fault == "cancelled" ? "" : AuthHarness.Metadata);
            return new(0, false, fault == "cancelled", [], []);
        });
        if (fault == "status") h.QueueStatus(text: "{}");
        Assert.AreEqual("AUTH_KEYRING_TARGET_MISSING", (await h.Login()).ErrorCode);
    }

    private sealed class AuthHarness : IDisposable
    {
        internal const string Metadata = "github.com:\n    git_protocol: https\n    users:\n        fixture-user: {}\n    user: fixture-user\n";
        internal const string Status = "{\"hosts\":{\"github.com\":[{\"state\":\"success\",\"active\":true,\"host\":\"github.com\",\"login\":\"fixture-user\",\"tokenSource\":\"keyring\"}]}}";
        private readonly StorageTestRoot root = new();
        internal AppPaths Paths { get; }
        internal string Config => Path.Combine(Paths.AppGhConfigDirectory, "config.yml");
        internal string Hosts => Path.Combine(Paths.AppGhConfigDirectory, "hosts.yml");
        internal readonly ScriptedProcessRunner Runner = new();
        internal readonly FakeCredentialStore Store = new();
        internal readonly OperationJob Job = OperationJob.Create();
        internal readonly ToolInventory Tools = new(null, new(@"C:\fixture\gh.exe", new(1,2,3,4), "2.100.0", true), null, null);
        internal IReadOnlyDictionary<string,string?> Environment { get; }
        internal AuthService Service { get; }
        internal int Redetections;
        internal ToolInventory? FreshTools;
        internal OperationJob? LastDetectionJob;
        internal Func<OperationJob,CancellationToken,Task<ToolInventory>>? Detect;
        internal void SetNull(string path) => root.SetNullDacl(path);
        internal AuthHarness(Action<string>? openBrowser = null, IProcessRunner? processRunner = null)
        {
            Paths = AppPaths.Create(root.Path);
            using var lease = AppDataPathPolicy.Acquire(Paths, Paths.AppGhConfigDirectory, AppDataEntryKind.Directory, true);
            Environment = ChildEnvironmentBuilder.CreateBase(new Dictionary<string,string?> { ["SystemRoot"] = System.Environment.GetEnvironmentVariable("SystemRoot"), ["TEMP"] = root.Path }, []);
            Service = new(Paths, processRunner ?? Runner, Store, (job, token) => { Redetections++; LastDetectionJob = job; return Detect is null ? Task.FromResult(FreshTools ?? Tools) : Detect(job, token); }, openBrowser);
        }
        internal void Write(string name, string contents) { using var stream = AclPolicy.CreateRestrictedFile(Path.Combine(Paths.AppGhConfigDirectory, name), root.User); stream.Write(Encoding.UTF8.GetBytes(contents)); }
        internal void Seed() { Write("config.yml", "version: \"1\"\n"); Write("hosts.yml", ""); }
        internal Task<AuthResult> Check() => Service.CheckAsync(Tools, Environment, Job, default);
        internal Task<AuthResult> Login() => Service.LoginAsync(Tools, Environment, Job, true, null, default);
        internal Task<AuthResult> Login(IProgress<string>? progress) => Service.LoginAsync(Tools, Environment, Job, true, progress, default);
        internal void QueueStatus(Action? mutate = null, string? text = null) => Runner.Results.Enqueue(request =>
        {
            CollectionAssert.AreEqual(new[] { "auth", "status", "--active", "--hostname", "github.com", "--json", "hosts" }, request.Arguments.ToArray());
            Assert.AreEqual(ProcessOutputMode.CapturedFile, request.OutputMode);
            Assert.IsTrue(request.EphemeralStandardError);
            WriteOutput(request, text ?? Status); mutate?.Invoke(); return new(0,false,false,[],[]);
        });
        internal void QueueApi(Action? mutate = null, string? text = null, int exit = 0) => Runner.Results.Enqueue(request =>
        {
            CollectionAssert.AreEqual(new[] { "api", "user", "--jq", ".login" }, request.Arguments.ToArray());
            WriteOutput(request, text ?? "fixture-user\n"); mutate?.Invoke(); return new(exit,false,false,[],[]);
        });
        private static void WriteOutput(ProcessRequest request, string text) { using var stream = AclPolicy.CreateRestrictedFile(request.StandardOutputFile!, WindowsIdentity.GetCurrent().User!); stream.Write(Encoding.UTF8.GetBytes(text)); }
        public void Dispose() { Job.Dispose(); root.Dispose(); }
    }
    private sealed class FakeCredentialStore : ICredentialStore
    {
        internal int Calls;
        internal bool Missing;
        internal bool ThrowEnumeration;
        public void Probe() { Calls++; }
        public HashSet<string> PerUserTargets() { Calls++; if (ThrowEnumeration) throw new IOException("PRIVATE_ENUMERATION_FAILURE"); return new(Missing ? ["gh:github.com:user-a"] : ["gh:github.com:user-a", "gh:github.com:user-b"], StringComparer.OrdinalIgnoreCase); }
    }
}

[TestClass]
public sealed class WindowsCredentialStoreTests
{
    [TestMethod]
    public void Native_enumeration_projects_only_generic_per_user_targets_with_poison_blob_pointers()
    {
        using var native = new FakeNative();
        native.Targets = ["gh:github.com:", "gh:github.com:user-a", "gh:github.com:user-B", "gh:github.com", "gh:github.com.evil:x"];
        var names = new WindowsCredentialStore(native).PerUserTargets();
        Assert.IsTrue(names.SetEquals(["gh:github.com:USER-A", "gh:github.com:user-b"]));
        CollectionAssert.AreEqual(new[] { "enumerate:gh:github.com:*:0", "free" }, native.Calls.ToArray());
    }

    [TestMethod]
    public void Native_enumeration_not_found_is_empty_and_other_error_is_typed()
    {
        using var native = new FakeNative { Error = 1168 };
        Assert.HasCount(0, new WindowsCredentialStore(native).PerUserTargets());
        native.Error = 5;
        var error = Assert.ThrowsExactly<AuthBoundaryException>(() => new WindowsCredentialStore(native).PerUserTargets());
        Assert.AreEqual("AUTH_KEYRING_ENUMERATION_FAILED", error.Code);
    }

    [TestMethod]
    public void Probe_regenerates_colliding_app_target_without_overwriting_it()
    {
        string collision = "GitHubBackupTool:CredentialProbe:" + string.Concat(Enumerable.Repeat("01", 32));
        using var native = new FakeNative { Targets = [collision] };
        byte next = 0;
        new WindowsCredentialStore(native, bytes => bytes.AsSpan().Fill(++next)).Probe();
        Assert.IsTrue(native.Calls.All(c => c != "write:" + collision));
        Assert.AreEqual(2, native.Calls.Count(c => c.StartsWith("enumerate:")));
        Assert.AreEqual(1, native.Calls.Count(c => c.StartsWith("write:")));
    }

    [TestMethod]
    public void Non_generic_native_entries_are_excluded_without_reading_poison_blob()
    {
        using var native = new FakeNative { Targets = ["gh:github.com:user-a"], CredentialType = 2 };
        Assert.HasCount(0, new WindowsCredentialStore(native).PerUserTargets());
    }

    [TestMethod]
    [DataRow("")] [DataRow("write")] [DataRow("read")] [DataRow("mismatch")] [DataRow("delete")] [DataRow("verify")]
    public void Probe_uses_only_app_generated_target_and_verifies_cleanup(string fault)
    {
        using var native = new FakeNative { Fault = fault };
        var buffers = new List<byte[]>();
        var store = new WindowsCredentialStore(native, bytes => { buffers.Add(bytes); bytes.AsSpan().Fill((byte)buffers.Count); });
        if (fault.Length == 0) store.Probe();
        else Assert.ThrowsExactly<AuthBoundaryException>(() => store.Probe());
        Assert.IsTrue(native.Calls.Where(c => c.StartsWith("read:") || c.StartsWith("delete:") || c.StartsWith("write:")).All(c => c.Contains("GitHubBackupTool:CredentialProbe:", StringComparison.Ordinal)));
        Assert.AreEqual(fault == "write" ? 0 : 1, native.Calls.Count(c => c.StartsWith("delete:")));
        Assert.IsTrue(buffers.All(b => b.All(v => v == 0)));
        Assert.AreEqual(1u, native.Written.Type); Assert.AreEqual(2u, native.Written.Persist); Assert.AreEqual(0u, native.Written.Flags);
    }

    private sealed class FakeNative : ICredentialNative, IDisposable
    {
        internal string[] Targets = [];
        internal List<string> Calls = [];
        internal int Error;
        internal uint CredentialType = 1;
        internal string Fault = "";
        internal NativeCredential Written;
        private readonly Dictionary<nint,List<nint>> allocations = [];
        private byte[] blob = [];
        private bool deleted;
        public int Enumerate(string filter, uint flags, out uint count, out nint buffer)
        {
            Calls.Add($"enumerate:{filter}:{flags}"); count = (uint)Targets.Length; buffer = 0;
            if (Error != 0) return Error;
            buffer = Marshal.AllocHGlobal(Math.Max(1, Targets.Length) * IntPtr.Size); allocations[buffer] = [];
            for (int i = 0; i < Targets.Length; i++)
            {
                nint target = Marshal.StringToHGlobalUni(Targets[i]); nint entry = Marshal.AllocHGlobal(Marshal.SizeOf<NativeCredential>());
                Marshal.StructureToPtr(new NativeCredential { Type = CredentialType, TargetName = target, CredentialBlobSize = uint.MaxValue, CredentialBlob = -1 }, entry, false);
                Marshal.WriteIntPtr(buffer, i * IntPtr.Size, entry); allocations[buffer].AddRange([target, entry]);
            }
            return 0;
        }
        public int Write(ref NativeCredential credential)
        {
            Written = credential; string target = Marshal.PtrToStringUni(credential.TargetName)!; Calls.Add("write:" + target);
            blob = new byte[credential.CredentialBlobSize]; Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return Fault == "write" ? 5 : 0;
        }
        public int Read(string target, uint type, uint flags, out nint buffer)
        {
            Assert.AreEqual(1u, type); Assert.AreEqual(0u, flags); Calls.Add("read:" + target); buffer = 0;
            if (deleted && Fault != "verify") return 1168;
            if (Fault == "read") return 5;
            buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeCredential>()); nint name = Marshal.StringToHGlobalUni(target); nint data = Marshal.AllocHGlobal(blob.Length);
            byte[] copy = (byte[])blob.Clone(); if (Fault == "mismatch") copy[0] ^= 1; Marshal.Copy(copy, 0, data, copy.Length);
            Marshal.StructureToPtr(new NativeCredential { Type = 1, TargetName = name, CredentialBlob = data, CredentialBlobSize = (uint)copy.Length }, buffer, false);
            allocations[buffer] = [name, data]; return 0;
        }
        public int Delete(string target, uint type, uint flags) { Assert.AreEqual(1u, type); Assert.AreEqual(0u, flags); Calls.Add("delete:" + target); deleted = true; return Fault == "delete" ? 5 : 0; }
        public void Free(nint buffer) { Calls.Add("free"); foreach (nint item in allocations[buffer]) Marshal.FreeHGlobal(item); allocations.Remove(buffer); Marshal.FreeHGlobal(buffer); }
        public void Dispose() { Assert.HasCount(0, allocations); Array.Clear(blob); }
    }
}
