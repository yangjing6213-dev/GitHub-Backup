using GitHubBackup.App;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Net;
using System.Net.Sockets;
namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class PreflightServiceTests
{
    [TestMethod]
    public async Task Native_preflight_requires_saved_consent_before_credential_read_or_discovery()
    {
        using var h = new PreflightFixture();
        var reader = new NativeReader();
        var handler = new NativeHandler();

        PreflightCheckResult result = await NativeService(h).CheckNativeAsync(BackupMode.Full,
            new("fixture-user", h.BackupRoot, NetworkMode.Auto), h.Job, reader, default, _ => handler);

        Assert.IsNull(result.LiveSession);
        Assert.IsTrue(result.Snapshot.Report.Issues.Any(issue => issue.ErrorCode == "AUTH_API_CONSENT_REQUIRED"));
        Assert.AreEqual(0, reader.Reads);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task Native_preflight_transfers_bound_transport_to_session_and_disposes_on_cleanup()
    {
        using var h = new PreflightFixture();
        var settings = new AppSettings("fixture-user", h.BackupRoot, NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        await new SettingsStore(h.Paths).SaveAsync(settings, default);
        var reader = new NativeReader();
        var handler = new NativeHandler();

        PreflightCheckResult result = await NativeService(h).CheckNativeAsync(BackupMode.Full,
            settings, h.Job, reader, default, _ => handler);

        Assert.IsNotNull(result.LiveSession);
        Assert.AreEqual(NativeFileSystem.CanonicalPath(h.BackupRoot), result.Snapshot.ApprovedBackupRoot);
        Assert.HasCount(0, result.Snapshot.Repositories);
        Assert.AreEqual(1, reader.Reads);
        CollectionAssert.AreEqual(new[] { "/user", "/user/repos" }, handler.Paths);
        Assert.AreEqual(7L, result.LiveSession.HttpTransport!.BoundAccountId);
        Assert.IsTrue(reader.Secret!.Any(value => value != 0));
        await result.LiveSession.DisposeAsync();
        Assert.IsTrue(reader.Secret!.All(value => value == 0));
        Assert.IsTrue(handler.Disposed);
        Assert.IsTrue(h.Job.IsCancellationRequested);
    }

    [TestMethod]
    public async Task Native_discovery_failure_disposes_bound_transport_before_return()
    {
        using var h = new PreflightFixture();
        var settings = new AppSettings("fixture-user", h.BackupRoot, NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        await new SettingsStore(h.Paths).SaveAsync(settings, default);
        var reader = new NativeReader();
        var handler = new NativeHandler { BadDiscovery = true };

        PreflightCheckResult result = await NativeService(h).CheckNativeAsync(BackupMode.Full,
            settings, h.Job, reader, default, _ => handler);

        Assert.IsNull(result.LiveSession);
        Assert.IsTrue(reader.Secret!.All(value => value == 0));
        Assert.IsTrue(handler.Disposed);
        Assert.IsTrue(h.Job.IsCancellationRequested);
    }

    [TestMethod]
    [DataRow("/user")]
    [DataRow("/user/repos")]
    public async Task Native_cancellation_during_binding_or_discovery_releases_transport_and_lease(string cancelPath)
    {
        using var h = new PreflightFixture();
        var settings = new AppSettings("fixture-user", h.BackupRoot, NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        await new SettingsStore(h.Paths).SaveAsync(settings, default);
        using var cancellation = new CancellationTokenSource();
        var reader = new NativeReader();
        var handler = new NativeHandler { CancelPath = cancelPath, Cancellation = cancellation };

        PreflightCheckResult result = await NativeService(h).CheckNativeAsync(BackupMode.Full,
            settings, h.Job, reader, cancellation.Token, _ => handler);

        Assert.IsNull(result.LiveSession);
        Assert.IsTrue(result.Snapshot.Report.Issues.Any(issue => issue.ErrorCode == "PREFLIGHT_CANCELLED"));
        Assert.IsTrue(reader.Secret!.All(value => value == 0));
        Assert.IsTrue(handler.Disposed);
        Assert.IsTrue(h.Job.IsCancellationRequested);
    }

    [TestMethod]
    public async Task Session_keeps_runtime_ownership_if_cleanup_fails_after_transport_release()
    {
        using var h = new PreflightFixture { HoldRuntimeUse = true };
        var settings = new AppSettings("fixture-user", h.BackupRoot, NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        await new SettingsStore(h.Paths).SaveAsync(settings, default);
        var reader = new NativeReader();
        var handler = new NativeHandler();
        PreflightCheckResult result = await NativeService(h).CheckNativeAsync(BackupMode.Full,
            settings, h.Job, reader, default, _ => handler);
        var session = result.LiveSession!;
        Assert.IsNotNull(session);
        string runtime = Path.GetDirectoryName(result.Snapshot.ChildEnvironment["GIT_CONFIG_GLOBAL"])!;
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await session.DisposeAsync());
            Assert.IsTrue(handler.Disposed);
            Assert.IsTrue(reader.Secret!.All(value => value == 0));
            Assert.IsTrue(Directory.Exists(runtime));
        }
        finally
        {
            h.HeldRuntimeUse?.Dispose();
            await session.DisposeAsync();
        }
        Assert.IsFalse(Directory.Exists(runtime));
    }

    [TestMethod]
    public async Task Session_retries_transport_release_until_handler_cleanup_succeeds()
    {
        using var h = new PreflightFixture();
        var settings = new AppSettings("fixture-user", h.BackupRoot, NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        await new SettingsStore(h.Paths).SaveAsync(settings, default);
        var reader = new NativeReader();
        var handler = new NativeHandler { DisposeFailuresRemaining = 2 };
        PreflightCheckResult result = await NativeService(h).CheckNativeAsync(BackupMode.Full,
            settings, h.Job, reader, default, _ => handler);
        var session = result.LiveSession!;
        Assert.IsNotNull(session);
        string runtime = Path.GetDirectoryName(result.Snapshot.ChildEnvironment["GIT_CONFIG_GLOBAL"])!;

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            Assert.AreEqual("HTTP_CLEANUP_FAILED", (await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await session.DisposeAsync())).Message);
            Assert.AreEqual(attempt, handler.DisposeAttempts);
            Assert.IsTrue(reader.Secret!.All(value => value == 0));
            Assert.IsFalse(h.Job.IsCancellationRequested);
            Assert.IsTrue(Directory.Exists(runtime));
        }
        await session.DisposeAsync();
        Assert.AreEqual(3, handler.DisposeAttempts);
        Assert.IsTrue(handler.Disposed);
        Assert.IsTrue(h.Job.IsCancellationRequested);
        Assert.IsFalse(Directory.Exists(runtime));
    }

    [TestMethod]
    public async Task Failed_native_preflight_retains_pending_cleanup_until_handler_is_released()
    {
        using var h = new PreflightFixture();
        var settings = new AppSettings("fixture-user", h.BackupRoot, NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        await new SettingsStore(h.Paths).SaveAsync(settings, default);
        var reader = new NativeReader();
        var handler = new NativeHandler { BadDiscovery = true, DisposeFailuresRemaining = 2 };
        var service = NativeService(h);

        PreflightCheckResult result = await service.CheckNativeAsync(BackupMode.Full,
            settings, h.Job, reader, default, _ => handler);

        Assert.IsNull(result.LiveSession);
        Assert.IsTrue(result.Snapshot.Report.Issues.Any(issue => issue.ErrorCode == "PREFLIGHT_CLEANUP_FAILED"));
        Assert.IsTrue(service.HasPendingCleanup);
        Assert.AreEqual(1, handler.DisposeAttempts);
        Assert.IsTrue(reader.Secret!.All(value => value == 0));
        Assert.AreEqual("PREFLIGHT_CLEANUP_FAILED", (await Assert.ThrowsExactlyAsync<IOException>(() =>
            service.RetryCleanupAsync())).Message);
        Assert.IsTrue(service.HasPendingCleanup);
        Assert.AreEqual(2, handler.DisposeAttempts);
        await service.RetryCleanupAsync();
        Assert.IsFalse(service.HasPendingCleanup);
        Assert.AreEqual(3, handler.DisposeAttempts);
        Assert.IsTrue(h.Job.IsCancellationRequested);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Failed_binding_retains_unbound_transport_until_cleanup_succeeds(bool cancelBinding)
    {
        using var h = new PreflightFixture();
        var settings = new AppSettings("fixture-user", h.BackupRoot, NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        await new SettingsStore(h.Paths).SaveAsync(settings, default);
        using var cancellation = new CancellationTokenSource();
        var reader = new NativeReader();
        var handler = new NativeHandler { BadUser = !cancelBinding, CancelPath = cancelBinding ? "/user" : null,
            Cancellation = cancellation, DisposeFailuresRemaining = 2 };
        var service = NativeService(h);

        PreflightCheckResult result = await service.CheckNativeAsync(BackupMode.Full,
            settings, h.Job, reader, cancellation.Token, _ => handler);

        Assert.IsNull(result.LiveSession);
        Assert.IsTrue(result.Snapshot.Report.Issues.Any(issue => issue.ErrorCode == "PREFLIGHT_CLEANUP_FAILED"));
        if (cancelBinding) Assert.IsTrue(result.Snapshot.Report.Issues.Any(issue => issue.ErrorCode == "PREFLIGHT_CANCELLED"));
        Assert.IsTrue(service.HasPendingCleanup);
        CollectionAssert.AreEqual(new[] { "/user" }, handler.Paths);
        Assert.IsTrue(reader.Secret!.All(value => value == 0));
        Assert.AreEqual(2, handler.DisposeAttempts);
        await service.RetryCleanupAsync();
        Assert.IsFalse(service.HasPendingCleanup);
        Assert.AreEqual(3, handler.DisposeAttempts);
        Assert.IsTrue(handler.Disposed);
        Assert.IsTrue(h.Job.IsCancellationRequested);
    }

    [TestMethod]
    public async Task Failed_binding_never_clears_pending_cleanup_while_handler_release_fails()
    {
        using var h = new PreflightFixture();
        var settings = new AppSettings("fixture-user", h.BackupRoot, NetworkMode.Auto)
        { ApiCredentialConsentVersion = 1, ApiCredentialConsentLogin = "fixture-user" };
        await new SettingsStore(h.Paths).SaveAsync(settings, default);
        var reader = new NativeReader();
        var handler = new NativeHandler { BadUser = true, DisposeFailuresRemaining = int.MaxValue };
        var service = NativeService(h);

        PreflightCheckResult result = await service.CheckNativeAsync(BackupMode.Full,
            settings, h.Job, reader, default, _ => handler);

        Assert.IsNull(result.LiveSession);
        Assert.IsTrue(service.HasPendingCleanup);
        Assert.IsTrue(reader.Secret!.All(value => value == 0));
        CollectionAssert.AreEqual(new[] { "/user" }, handler.Paths);
        try
        {
            for (int retry = 0; retry < 2; retry++)
            {
                Assert.AreEqual("PREFLIGHT_CLEANUP_FAILED", (await Assert.ThrowsExactlyAsync<IOException>(() =>
                    service.RetryCleanupAsync())).Message);
                Assert.IsTrue(service.HasPendingCleanup);
                Assert.IsFalse(handler.Disposed);
            }
            Assert.AreEqual(4, handler.DisposeAttempts);
        }
        finally
        {
            handler.DisposeFailuresRemaining = 0;
            await service.RetryCleanupAsync();
        }
        Assert.IsFalse(service.HasPendingCleanup);
    }

    internal static PreflightService NativeService(PreflightFixture h)
    {
        var probe = new NetworkProbe(h.Runner);
        return new PreflightService(h.Auth, h.Runner, (_,_) => Task.FromResult(h.Tools), h.Environment,
            new ProxyScope(probe, new Dictionary<string,string?>(), origin => origin), h.Audit,
            new(_ => DriveType.Fixed, path => File.Exists(path) || Directory.Exists(path) ? File.GetAttributes(path) : null,
                (_,_) => Task.CompletedTask), _ => 1073741824);
    }

    private sealed class NativeReader : IGitHubCredentialReader
    {
        internal int Reads;
        internal byte[]? Secret;
        public GitHubCredentialLease ReadExact(string login)
        {
            Reads++;
            Secret = "SYNTHETIC_PREFLIGHT_SECRET"u8.ToArray();
            return new GitHubCredentialLease(login, Secret);
        }
    }

    private sealed class NativeHandler : HttpMessageHandler
    {
        internal readonly List<string> Paths = [];
        internal int Calls;
        internal bool Disposed;
        internal int DisposeFailuresRemaining;
        internal int DisposeAttempts;
        internal bool BadDiscovery;
        internal bool BadUser;
        internal string? CancelPath;
        internal CancellationTokenSource? Cancellation;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == CancelPath)
            {
                Cancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            byte[] body = request.RequestUri.AbsolutePath == "/user"
                ? BadUser ? "invalid"u8.ToArray() : "{\"login\":\"fixture-user\",\"id\":7}"u8.ToArray()
                : BadDiscovery ? "invalid"u8.ToArray() : "[]"u8.ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
        protected override void Dispose(bool disposing)
        {
            DisposeAttempts++;
            if (DisposeFailuresRemaining-- > 0) throw new IOException("SYNTHETIC_DISPOSE_FAILURE");
            Disposed = true;
            base.Dispose(disposing);
        }
    }
    [TestMethod]
    public void Every_required_gate_and_blocking_issue_controls_start()
    {
        for (int i = -1; i < 8; i++)
        {
            var flags = Enumerable.Range(0,8).Select(n => n != i).ToArray();
            var report = new PreflightReport(flags[0],flags[1],flags[2],flags[3],flags[4],flags[5],flags[6],flags[7],[]);
            Assert.AreEqual(i == -1, report.CanStartBackup, $"gate {i}");
            Assert.IsFalse((report with { Issues = [new("BLOCK", "", true)] }).CanStartBackup);
        }
    }
    [TestMethod][DataRow(false)][DataRow(true)]
    public async Task Success_retains_session_and_freezes_discovery_profile_job_and_runtime(bool hasRepository)
    {
        using var h = new PreflightFixture();
        if (hasRepository) h.Repositories = [PreflightFixture.Repository];
        var result = await h.Check();
        Assert.IsTrue(result.Snapshot.Report.CanStartBackup, string.Join(",", result.Snapshot.Report.Issues.Select(i => i.ErrorCode)));
        var session = result.LiveSession!; Assert.IsNotNull(session);
        Assert.AreEqual(1, h.Discoveries); Assert.AreEqual(1, h.Detections);
        Assert.IsInstanceOfType<RuntimeEnvironment>(session.CreateEnvironment());
        string runtime = Path.GetDirectoryName(result.Snapshot.ChildEnvironment["GIT_CONFIG_GLOBAL"])!;
        Assert.IsTrue(Directory.Exists(runtime)); Assert.IsNotNull(result.Snapshot.EmptyHooksDirectory);
        Assert.IsFalse(h.Job.IsCancellationRequested);
        Assert.AreEqual(hasRepository ? 2 : 1, h.Runner.Requests.Count(r => r.Arguments[0] == "ls-remote"));
        Assert.AreEqual(!hasRepository, result.Snapshot.Report.Issues.Any(i => i.ErrorCode == "GIT_OWNER_PROBE_NOT_APPLICABLE" && !i.BlocksBackup));
        Assert.ThrowsExactly<IOException>(() => File.Move(h.Hosts, h.Hosts + ".moved"));
        await session.DisposeAsync();
        Assert.IsTrue(h.Job.IsCancellationRequested); Assert.IsFalse(Directory.Exists(runtime));
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.CreateEnvironment());
        File.Move(h.Hosts, h.Hosts + ".moved"); File.Move(h.Hosts + ".moved", h.Hosts);
    }
    [TestMethod]
    public async Task Public_git_success_does_not_hide_api_failure_or_trigger_nonconnection_fallback()
    {
        using var h = new PreflightFixture { ApiFailure = "gh: Forbidden (HTTP 403)" };
        var result = await h.Check();
        Assert.IsFalse(result.Snapshot.Report.NetworkReady); Assert.IsFalse(result.Snapshot.Report.AuthReady);
        Assert.IsNull(result.LiveSession); Assert.AreEqual(0,h.Resolutions); Assert.AreEqual(0,h.Discoveries);
        Assert.IsTrue(h.Runner.Requests.Any(r => r.Arguments.SequenceEqual(new[] { "api", "user", "--jq", ".login" })));
        h.AssertFailedCleanup(result);
    }
    [TestMethod]
    public async Task Api_connection_failure_retries_then_selects_one_system_profile_for_both_endpoints()
    {
        using var h = new PreflightFixture { ApiFailure = "Get \"https://api.github.com/user\": dial tcp 127.0.0.1:443: connect: connection refused", FailOnlyDirect = true, SystemProxy = new("http://127.0.0.1:8080") };
        var result = await h.Check(); await using var session = result.LiveSession!;
        Assert.IsNotNull(session); Assert.IsTrue(result.Snapshot.Report.NetworkReady);
        Assert.AreEqual(2,h.Resolutions); Assert.AreEqual(1,h.Discoveries);
        Assert.AreEqual(4,h.Runner.Requests.Count(r => r.Arguments[0] == "api"));
        var selected = h.Runner.Requests.Where(r => r.Environment.ContainsKey("HTTPS_PROXY")).ToArray();
        Assert.IsTrue(selected.Any(r => r.Arguments[0] == "ls-remote")); Assert.IsTrue(selected.Any(r => r.Arguments[0] == "api"));
        Assert.IsTrue(selected.All(r => r.Environment["HTTPS_PROXY"] == "http://127.0.0.1:8080/" && !r.Environment.ContainsKey("NO_PROXY")));
    }
    [TestMethod]
    [DataRow("setup")][DataRow("query")][DataRow("discovery")][DataRow("repository")]
    public async Task Changed_auth_metadata_is_checked_before_consuming_each_operation_output(string stage)
    {
        using var h = new PreflightFixture { MutateAt = stage, Repositories = [PreflightFixture.Repository] };
        var result = await h.Check(); Assert.IsNull(result.LiveSession); Assert.IsFalse(result.Snapshot.Report.CanStartBackup);
        Assert.IsTrue(result.Snapshot.Report.Issues.Any(i => i.ErrorCode.StartsWith("AUTH_", StringComparison.Ordinal)));
        h.AssertFailedCleanup(result);
    }
    [TestMethod]
    public async Task Invalid_helper_closes_job_before_runtime_cleanup()
    {
        using var h = new PreflightFixture { InvalidHelper = true };
        var result = await h.Check(); Assert.IsNull(result.LiveSession); h.AssertFailedCleanup(result);
    }
    [TestMethod]
    public async Task Account_switch_at_authenticated_base_boundary_is_rejected_before_setup_git()
    {
        using var h = new PreflightFixture { MutateAt = "authenticated-base" };
        var result = await h.Check(); Assert.IsNull(result.LiveSession);
        Assert.IsFalse(h.Runner.Requests.Any(r => r.Arguments.Contains("setup-git")));
        Assert.IsTrue(result.Snapshot.Report.Issues.Any(i => i.ErrorCode == "AUTH_LOGIN_MISMATCH"));
        h.AssertFailedCleanup(result);
    }
    [TestMethod]
    public async Task Failed_runtime_cleanup_retains_capability_until_explicit_retry()
    {
        using var h = new PreflightFixture { InvalidHelper = true, HoldRuntimeUse = true };
        try
        {
            var result = await h.Check(); Assert.IsNull(result.LiveSession);
            Assert.IsTrue(result.Snapshot.Report.Issues.Any(i => i.ErrorCode == "PREFLIGHT_CLEANUP_FAILED"));
            Assert.IsTrue(h.Service!.HasPendingCleanup); Assert.IsTrue(h.Job.IsCancellationRequested);
            Assert.HasCount(0,result.Snapshot.ChildEnvironment); Assert.IsNull(result.Snapshot.EmptyHooksDirectory);
            Assert.IsNotEmpty(Directory.GetDirectories(h.Root.Path,"GitHubBackup-git-*"));
            Assert.ThrowsExactly<IOException>(() => File.Move(h.Hosts,h.Hosts + ".moved"));
            var failedRetry = await Assert.ThrowsExactlyAsync<IOException>(() => h.Service.RetryCleanupAsync());
            Assert.AreEqual("PREFLIGHT_CLEANUP_FAILED",failedRetry.Message); Assert.IsTrue(h.Service.HasPendingCleanup);
            int calls = h.Runner.Requests.Count;
            using var nextJob = OperationJob.Create();
            var refused = await h.Service.CheckAsync(BackupMode.Daily,new("fixture-user",h.BackupRoot,NetworkMode.Auto),nextJob,
                (_,_,_,_,_) => throw new AssertFailedException("No discovery while cleanup pending"),default);
            Assert.IsNull(refused.LiveSession); Assert.IsTrue(refused.Snapshot.Report.Issues.Any(i => i.ErrorCode == "PREFLIGHT_CLEANUP_PENDING"));
            Assert.HasCount(calls,h.Runner.Requests); Assert.IsTrue(h.Service.HasPendingCleanup);
        }
        finally
        {
            h.HeldRuntimeUse?.Dispose();
            await h.Service!.RetryCleanupAsync();
            if (h.CapturedRuntime is not null) await h.CapturedRuntime.DisposeAsync();
        }
        Assert.IsFalse(h.Service.HasPendingCleanup); Assert.HasCount(0,Directory.GetDirectories(h.Root.Path,"GitHubBackup-git-*"));
    }
    [TestMethod]
    [DataRow("storage")][DataRow("space")][DataRow("tools")][DataRow("auth")][DataRow("owner")][DataRow("discover")][DataRow("cancel")]
    public async Task Local_and_discovery_failures_return_readable_blocked_diagnostics(string gate)
    {
        using var h = new PreflightFixture { Gate = gate };
        if (gate == "auth") File.WriteAllText(h.Hosts, "");
        using var cancelled = new CancellationTokenSource(); if (gate == "cancel") cancelled.Cancel();
        var result = await h.Check(cancelled.Token);
        Assert.IsNull(result.LiveSession); Assert.IsFalse(result.Snapshot.Report.CanStartBackup);
        Assert.IsTrue(result.Snapshot.Report.Issues.Any(i => i.BlocksBackup)); h.AssertFailedCleanup(result);
    }
    [TestMethod]
    [DataRow("logs\\backup-old.log", "PREFLIGHT_PRIVATE_READ_ACL_UNSAFE")]
    [DataRow("mirrors\\repo.git\\objects\\aa\\object", "MIRROR_SOURCE_READ_ACL_UNSAFE")]
    [DataRow("wikis\\old.wiki.git\\lfs\\objects\\object", "MIRROR_SOURCE_READ_ACL_UNSAFE")]
    [DataRow("metadata\\orphan-on-disk\\issues.json", "METADATA_SOURCE_READ_ACL_UNSAFE")]
    [DataRow("releases\\old\\payload", "RELEASE_SOURCE_READ_ACL_UNSAFE")]
    [DataRow("manifests\\old.json", "MANIFEST_READ_ACL_UNSAFE")]
    [DataRow(".locks\\owner.lock", "LOCK_PATH_READ_ACL_UNSAFE")]
    public async Task Recursive_private_audit_includes_public_history_orphans_and_sensitive_files(string relative, string code)
    {
        using var h = new PreflightFixture(); string file = h.MakeFile(relative);
        h.DescriptorOverrides[file] = PreflightFixture.Descriptor(h.User, FileSystemRights.Read);
        var result = await h.Check(); Assert.IsFalse(result.Snapshot.Report.PrivacyReady); Assert.IsNull(result.LiveSession);
        var assessment = result.Snapshot.UnsafeSensitivePaths.Single(p => p.FullPath.Equals(file, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(code, assessment.ErrorCode);
        Assert.AreEqual(code.StartsWith("PREFLIGHT_PRIVATE",StringComparison.Ordinal), result.Snapshot.Report.SourceIntegrityReady);
    }
    [TestMethod]
    [DataRow("mirrors\\repo.git", "MIRROR_SOURCE_WRITE_ACL_UNSAFE")]
    [DataRow(".locks", "LOCK_PATH_WRITE_ACL_UNSAFE")]
    [DataRow("manifests", "MANIFEST_WRITE_ACL_UNSAFE")]
    [DataRow("metadata\\orphan", "METADATA_SOURCE_WRITE_ACL_UNSAFE")]
    [DataRow("releases\\repo", "RELEASE_SOURCE_WRITE_ACL_UNSAFE")]
    public void Typed_write_only_defects_never_emit_duplicate_generic_assessments(string relative, string code)
    {
        using var h = new PreflightFixture(); string path = h.MakeDirectory(relative);
        h.DescriptorOverrides[path] = PreflightFixture.Descriptor(h.User, FileSystemRights.Write | FileSystemRights.Delete);
        var result = h.Audit.ValidateExistingTrees(h.OwnerRoot, [path,path], true);
        Assert.IsFalse(result.Allowed); Assert.AreEqual(code, result.UnsafePaths.Single().ErrorCode);
    }
    [TestMethod][DataRow("owner")][DataRow("null")][DataRow("absent")][DataRow("read")][DataRow("empty")]
    public void Descriptor_defect_precedence_and_present_empty_dacl_are_distinct(string fault)
    {
        using var h = new PreflightFixture(); string path = h.MakeFile("mirrors\\orphan.git\\object");
        var descriptor = PreflightFixture.Descriptor(h.User, FileSystemRights.FullControl);
        h.DescriptorOverrides[path] = fault switch
        {
            "owner" => descriptor with { OwnerSid = new("S-1-5-21-9-8-7-1002"), IsNullDacl = true },
            "null" => descriptor with { IsNullDacl = true }, "absent" => descriptor with { DaclPresent = false },
            "empty" => descriptor with { Entries = [] }, _ => descriptor
        };
        var result = h.Audit.ValidateExistingTrees(h.OwnerRoot, [path], true);
        if (fault == "empty") Assert.IsTrue(result.Allowed);
        else Assert.AreEqual("MIRROR_SOURCE_" + (fault == "owner" ? "OWNER" : fault is "null" or "absent" ? "NULL_DACL" : "READ_ACL") + "_UNSAFE", result.UnsafePaths.Single().ErrorCode);
    }
    [TestMethod]
    public void Junction_is_rejected_before_descent_and_unsafe_target_is_never_read()
    {
        using var h = new PreflightFixture(); string target = h.Root.Child("outside"); Directory.CreateDirectory(target);
        string junction = Path.Combine(h.MakeDirectory("mirrors"), "orphan.git");
        StorageTestRoot.CreateJunction(junction,target);
        try
        {
            var result = h.Audit.ValidateExistingTrees(h.OwnerRoot,[junction],true);
            Assert.AreEqual("MIRROR_SOURCE_REPARSE_POINT_REJECTED", result.UnsafePaths.Single().ErrorCode);
            Assert.IsFalse(h.DescriptorsRead.Any(p => p.StartsWith(target,StringComparison.OrdinalIgnoreCase)));
        }
        finally { Directory.Delete(junction); }
    }
    [TestMethod]
    public void Reparse_ancestor_reports_exact_repair_path_without_reading_any_descendant()
    {
        using var h = new PreflightFixture(); string target = h.Root.Child("outside"); Directory.CreateDirectory(Path.Combine(target,"nested"));
        string junction = h.Root.Child("boundary"); StorageTestRoot.CreateJunction(junction,target);
        try
        {
            var result = h.Audit.ValidateExistingTrees(Path.Combine(junction,"nested"),[],true);
            Assert.AreEqual(junction,result.UnsafePaths.Single().FullPath); Assert.HasCount(0,h.DescriptorsRead);
        }
        finally { Directory.Delete(junction); }
    }
    [TestMethod]
    public void Real_child_acl_and_hardlink_are_audited_without_payload_reads_or_repairs()
    {
        using var h = new PreflightFixture(); string file = h.MakeFile("mirrors\\repo.git\\objects\\object");
        var info = new FileInfo(file); var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"),FileSystemRights.Read,AccessControlType.Allow)); info.SetAccessControl(acl);
        var result = h.Audit.ValidateExistingTrees(h.OwnerRoot,[],true);
        Assert.AreEqual("MIRROR_SOURCE_READ_ACL_UNSAFE",result.UnsafePaths.Single().ErrorCode);
        string linked = Path.Combine(Path.GetDirectoryName(file)!,"linked"); StorageTestRoot.CreateHardLink(linked,file);
        result = h.Audit.ValidateExistingTrees(h.OwnerRoot,[],true);
        Assert.HasCount(2,result.UnsafePaths); Assert.IsTrue(result.UnsafePaths.All(p => p.ErrorCode == "MIRROR_SOURCE_HARDLINK_REJECTED"));
    }
    [TestMethod]
    public async Task Missing_drive_recommends_profile_root_and_safe_recheck_succeeds()
    {
        using var h = new PreflightFixture();
        var proxy = new ProxyScope(new NetworkProbe(h.Runner),new Dictionary<string,string?>(),uri => uri);
        var service = new PreflightService(h.Auth,h.Runner,(_,_) => Task.FromResult(h.Tools),h.Environment,proxy,h.Audit,
            new(_ => DriveType.NoRootDirectory,_ => null,(_,_) => throw new AssertFailedException("No atomic probe on missing drive")));
        var failed = await service.CheckAsync(BackupMode.Daily,new("fixture-user",@"D:\GitHub-Backups",NetworkMode.Auto),h.Job,(_,_,_,_,_) => throw new AssertFailedException(),default);
        Assert.IsFalse(failed.Snapshot.Report.StorageReady);
        Assert.IsTrue(failed.Snapshot.Report.Issues.Single().UserMessage.Contains(Path.Combine(h.Root.Path,"GitHub-Backups"),StringComparison.Ordinal));
        using var replacement = new PreflightFixture(); var passed = await replacement.Check(); await using var session = passed.LiveSession!;
        Assert.IsTrue(passed.Snapshot.Report.CanStartBackup);
    }
    [TestMethod]
    public async Task Discovery_exception_returns_diagnostic_data_and_releases_all_leases()
    {
        using var h = new PreflightFixture { Gate = "json-discovery" };
        var result = await h.Check(); Assert.IsNull(result.LiveSession); h.AssertFailedCleanup(result);
        Assert.IsFalse(result.Snapshot.Report.RepositoryVisibilityKnown);
    }
    [TestMethod][DataRow("empty")][DataRow("login")][DataRow("config")]
    public async Task Real_runner_revalidates_auth_after_output_and_before_return(string mutation)
    {
        using var h = new PreflightFixture(); using var tools = await TestToolBuilder.CreateAsync();
        using var config = h.Auth.AcquireConfig();
        await using var context = await GitRuntimeContext.CreatePublicProbeAsync(config.CreateEnvironment(tools.Environment),default);
        var environment = ProxyScope.Merge(ChildEnvironmentBuilder.Build(context.Environment,new Dictionary<string,string?> { ["LC_ALL"] = "C" }),ProxyProfile.Direct);
        var request = new ProcessRequest(tools.Executable,["stream","err:ready\n"],tools.Root,environment,TimeSpan.FromSeconds(10),ProcessOutputMode.EphemeralText,ExpectedExecutableIdentity: ExecutableTrust.CaptureTrustedIdentity(tools.Executable),EphemeralStandardError:true);
        var error = await Assert.ThrowsExactlyAsync<AuthBoundaryException>(() => new ProcessRunner().RunAsync(request,h.Job,new InlineProgress(_ =>
        {
            if (mutation == "config") File.WriteAllText(Path.Combine(h.Paths.AppGhConfigDirectory,"config.yml"),"version: \"2\"\n");
            else File.WriteAllText(h.Hosts,mutation == "empty" ? "" : PreflightFixture.Metadata.Replace("fixture-user","changed-user"));
        }),default));
        Assert.AreEqual(mutation == "config" ? "AUTH_CONFIG_CONTENT_REJECTED" : "AUTH_LOGIN_MISMATCH",error.Code);
    }
    [TestMethod][DataRow("dispose")][DataRow("login")]
    public async Task Real_runner_rejects_stale_auth_before_second_process_can_start(string mutation)
    {
        using var h = new PreflightFixture(); using var tools = await TestToolBuilder.CreateAsync();
        using var config = h.Auth.AcquireConfig();
        await using var context = await GitRuntimeContext.CreatePublicProbeAsync(config.CreateEnvironment(tools.Environment),default);
        var environment = ChildEnvironmentBuilder.Build(context.Environment,new Dictionary<string,string?> { ["LC_ALL"] = "C" });
        var request = new ProcessRequest(tools.Executable,["exit-code","0"],tools.Root,environment,TimeSpan.FromSeconds(10),ExpectedExecutableIdentity:ExecutableTrust.CaptureTrustedIdentity(tools.Executable));
        Assert.AreEqual(0,(await new ProcessRunner().RunAsync(request,h.Job,null,default)).ExitCode);
        if (mutation == "dispose") config.Dispose(); else File.WriteAllText(h.Hosts,PreflightFixture.Metadata.Replace("fixture-user","changed-user"));
        string sentinel = Path.Combine(tools.Root,"must-not-start"); request = request with { Arguments = ["write-sentinel-then-wait",sentinel], Timeout = TimeSpan.FromMilliseconds(100) };
        if (mutation == "dispose") await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => new ProcessRunner().RunAsync(request,h.Job,null,default));
        else await Assert.ThrowsExactlyAsync<AuthBoundaryException>(() => new ProcessRunner().RunAsync(request,h.Job,null,default));
        Assert.IsFalse(File.Exists(sentinel));
    }
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}

internal sealed class PreflightFixture : IDisposable
{
    internal const string Metadata = "github.com:\n    git_protocol: https\n    users:\n        fixture-user: {}\n    user: fixture-user\n";
    internal const string Status = "{\"hosts\":{\"github.com\":[{\"state\":\"success\",\"active\":true,\"host\":\"github.com\",\"login\":\"fixture-user\",\"tokenSource\":\"keyring\"}]}}";
    internal static RepositoryDescriptor Repository { get; } = new(1,"repo","fixture-user/repo","https://github.com/fixture-user/repo",false,false,false,true,null,0,"repo","");
    internal StorageTestRoot Root { get; }
    internal SecurityIdentifier User => Root.User;
    internal string BackupRoot { get; }
    internal string OwnerRoot => Path.Combine(BackupRoot,"fixture-user");
    internal AppPaths Paths { get; }
    internal string Hosts => Path.Combine(Paths.AppGhConfigDirectory,"hosts.yml");
    internal IReadOnlyDictionary<string,string?> Environment { get; }
    internal OperationJob Job { get; } = OperationJob.Create();
    internal ToolInventory Tools { get; set; } = new(new(@"C:\fixture\git.exe",new(1,2,3,4),"2.55.0.windows.3",true),new(@"C:\fixture\gh.exe",new(1,3,3,4),"2.100.0",true),new(@"C:\fixture\git-lfs.exe",new(1,4,3,4),"3.7.1",true),null);
    internal PreflightRunner Runner { get; }
    internal AuthService Auth { get; }
    internal SourceIntegrityAudit Audit { get; }
    internal Dictionary<string,AclDescriptor> DescriptorOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<string> DescriptorsRead { get; } = [];
    internal IReadOnlyList<RepositoryDescriptor> Repositories = [];
    internal string Gate = "", ApiFailure = "", MutateAt = "";
    internal bool FailOnlyDirect, InvalidHelper, HoldRuntimeUse;
    internal IDisposable? HeldRuntimeUse;
    internal GitRuntimeContext? CapturedRuntime;
    internal PreflightService? Service;
    internal Uri? SystemProxy;
    internal int Discoveries, Detections, Resolutions;
    internal PreflightFixture(string? backupRoot=null,string? fixtureRoot=null)
    {
        Root=new(fixtureRoot);
        BackupRoot = backupRoot??Root.Child("backup"); AclPolicy.CreateRestrictedDirectory(BackupRoot,User); AclPolicy.CreateRestrictedDirectory(OwnerRoot,User);
        Paths = AppPaths.Create(Root.Path);
        using (AppDataPathPolicy.Acquire(Paths,Paths.AppGhConfigDirectory,AppDataEntryKind.Directory,true)) { }
        Write(Path.Combine(Paths.AppGhConfigDirectory,"config.yml"),"version: \"1\"\n"); Write(Hosts,Metadata);
        Environment = ChildEnvironmentBuilder.CreateBase(new Dictionary<string,string?> { ["SystemRoot"] = System.Environment.GetEnvironmentVariable("SystemRoot"), ["TEMP"] = Root.Path, ["USERPROFILE"] = Root.Path },[]);
        Runner = new(this); Auth = new(Paths,Runner,new NoCredentialStore(),(_,_) => Task.FromResult(Tools));
        Audit = new((path,handle) => { DescriptorsRead.Add(path); return DescriptorOverrides.TryGetValue(path,out var value) ? value : AclPolicy.ReadDescriptor(handle); });
    }
    internal async Task<PreflightCheckResult> Check(CancellationToken token = default)
    {
        var probe = new NetworkProbe(Runner);
        var proxies = new ProxyScope(probe,new Dictionary<string,string?>(), origin => { Resolutions++; return SystemProxy ?? origin; });
        int enumerations = 0;
        IReadOnlyDictionary<string,string?> selectedBase = MutateAt == "authenticated-base" ? new EnumerationObservedEnvironment(Environment,() =>
        {
            if (++enumerations == 2) File.WriteAllText(Hosts,Metadata.Replace("fixture-user","changed-user"));
        }) : Environment;
        var service = Service = new PreflightService(Auth,Runner,(_,_) => { Detections++; return Task.FromResult(Gate == "tools" ? ToolInventory.Empty : Tools); },selectedBase,proxies,Audit,
            new(_ => DriveType.Fixed, path => File.Exists(path) || Directory.Exists(path) ? File.GetAttributes(path) : null, (_,_) => Gate == "storage" ? Task.FromException(new IOException()) : Task.CompletedTask),
            _ => Gate == "space" ? 1073741823 : 1073741824);
        var result = await service.CheckAsync(BackupMode.Full,new(Gate == "owner" ? "wrong-user" : "fixture-user",BackupRoot,NetworkMode.Auto),Job,
            (owner,tools,environment,job,cancellation) =>
            {
                Discoveries++; Assert.AreSame(Job,job); Assert.AreSame(Tools,tools); Assert.IsInstanceOfType<RuntimeEnvironment>(environment);
                if (Gate == "discover") throw new IOException("PRIVATE_RAW_EXCEPTION");
                if (Gate == "json-discovery") throw new System.Text.Json.JsonException("PRIVATE_RAW_EXCEPTION");
                if (MutateAt == "discovery") File.WriteAllText(Hosts,"");
                return Task.FromResult(Repositories);
            },token);
        return result;
    }
    internal string MakeDirectory(string relative) { string path = Path.Combine(OwnerRoot,relative); AclPolicy.CreateRestrictedDirectory(path,User); return path; }
    internal string MakeFile(string relative) { string path = Path.Combine(OwnerRoot,relative); AclPolicy.CreateRestrictedDirectory(Path.GetDirectoryName(path)!,User); Write(path,"opaque fixture"); return path; }
    internal void Write(string path,string text) { using var stream = AclPolicy.CreateRestrictedFile(path,User); stream.Write(Encoding.UTF8.GetBytes(text)); }
    internal static AclDescriptor Descriptor(SecurityIdentifier user, FileSystemRights rights) => new(user,true,false,true,[new(new("S-1-1-0"),rights,AccessControlType.Allow)]);
    internal void AssertFailedCleanup(PreflightCheckResult result)
    {
        Assert.IsTrue(Job.IsCancellationRequested); Assert.IsNull(result.Snapshot.EmptyHooksDirectory); Assert.HasCount(0,result.Snapshot.ChildEnvironment);
        Assert.HasCount(0,Directory.GetDirectories(Root.Path,"GitHubBackup-git-*"));
    }
    public void Dispose() { Job.Dispose(); Root.Dispose(); }
    private sealed class NoCredentialStore : ICredentialStore
    { public void Probe() => Assert.Fail("No credential-store access in preflight"); public HashSet<string> PerUserTargets() => throw new AssertFailedException("No keyring enumeration"); }
    private sealed class EnumerationObservedEnvironment(IReadOnlyDictionary<string,string?> inner,Action observe) : IReadOnlyDictionary<string,string?>
    {
        public string? this[string key] => inner[key];
        public IEnumerable<string> Keys => inner.Keys;
        public IEnumerable<string?> Values => inner.Values;
        public int Count => inner.Count;
        public bool ContainsKey(string key) => inner.ContainsKey(key);
        public bool TryGetValue(string key,out string? value) => inner.TryGetValue(key,out value);
        public IEnumerator<KeyValuePair<string,string?>> GetEnumerator() { observe(); return inner.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    internal sealed class PreflightRunner(PreflightFixture fixture) : IProcessRunner
    {
        internal List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, OperationJob job, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            var environment = request.Environment as RuntimeEnvironment;
            environment?.RevalidateAuthentication();
            try { return RunCore(request,job,progress,cancellationToken); }
            finally { environment?.RevalidateAuthentication(); }
        }
        private Task<ProcessResult> RunCore(ProcessRequest request, OperationJob job, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); Assert.AreSame(fixture.Job,job);
            Assert.IsInstanceOfType<RuntimeEnvironment>(request.Environment);
            bool api = request.Arguments[0] == "api";
            string stage = request.Arguments.Contains("setup-git") ? "setup" : request.Arguments[0] == "config" ? "query" : request.Arguments.Contains(Repository.Url) ? "repository" : "";
            if (stage == "setup" && fixture.HoldRuntimeUse)
            {
                fixture.CapturedRuntime = ((RuntimeEnvironment)request.Environment).Owner;
                fixture.HeldRuntimeUse = fixture.CapturedRuntime!.AcquireRequest(job);
            }
            if (stage.Length != 0 && stage == fixture.MutateAt) File.WriteAllText(fixture.Hosts,"");
            if (api && fixture.ApiFailure.Length != 0 && (!fixture.FailOnlyDirect || !request.Environment.ContainsKey("HTTPS_PROXY")))
            { progress?.Report(fixture.ApiFailure); return Task.FromResult(new ProcessResult(1,false,false,[],[])); }
            if (request.StandardOutputFile is not null)
            {
                string text = request.Arguments.Contains("status") ? Status : api ? "fixture-user\n" : fixture.InvalidHelper ? "bad" : "credential.https://github.com.helper\n\0credential.https://github.com.helper\n!'C:\\fixture\\gh.exe' auth git-credential\0";
                fixture.Write(request.StandardOutputFile,text);
            }
            return Task.FromResult(new ProcessResult(0,false,false,[],[]));
        }
    }
}
