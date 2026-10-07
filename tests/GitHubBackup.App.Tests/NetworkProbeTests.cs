using GitHubBackup.App;
namespace GitHubBackup.App.Tests;
[TestClass]
public sealed class NetworkProbeTests
{
    [TestMethod]
    [DataRow(429, null, 10)]
    [DataRow(403, "0", 10)]
    [DataRow(403, "1", 6)]
    [DataRow(403, "invalid", 6)]
    [DataRow(401, "0", 5)]
    [DataRow(503, null, 4)]
    public void Native_status_uses_only_status_and_official_remaining_header(int status, string? remaining, int expected) =>
        Assert.AreEqual((NetworkFailureKind)expected, NetworkProbe.ClassifyHttpStatus(status, remaining));
    [TestMethod]
    public void Secondary_limit_header_marks_forbidden_as_rate_limited_even_when_primary_quota_remains() =>
        Assert.AreEqual(NetworkFailureKind.RateLimited, NetworkProbe.ClassifyHttpStatus(403, "1", "2"));
    [TestMethod]
    [DataRow("gh: Bad credentials (HTTP 401)",5)]
    [DataRow("gh: Forbidden (HTTP 403)",6)]
    [DataRow("gh: Not Found (HTTP 404)",7)]
    [DataRow("gh: Proxy Authentication Required (HTTP 407)",8)]
    [DataRow("gh: Internal Server Error (HTTP 500)",4)]
    [DataRow("gh: API rate limit exceeded (HTTP 403)",10)]
    [DataRow("Get \"https://api.github.com/user\": dial tcp 127.0.0.1:443: connect: connection refused",2)]
    [DataRow("Get \"https://api.github.com/user\": read tcp 127.0.0.1:443: read: connection reset by peer",3)]
    [DataRow("Get \"https://api.github.com/user\": tls: failed to verify certificate: x509: certificate signed by unknown authority",9)]
    [DataRow("Get \"https://api.github.com/user\": context deadline exceeded",1)]
    [DataRow("arbitrary quoted connection refused",11)]
    [DataRow("gh: Bad credentials (HTTP 401)\ngh: Internal Server Error (HTTP 500)",11)]
    [DataRow("gh: message says connection refused (HTTP 401)",5)]
    [DataRow("gh: API rate limit exceeded (HTTP 403)\nX-RateLimit-Reset: secret",11)]
    public void Only_unambiguous_pinned_diagnostics_have_typed_results(string diagnostic, int expected) =>
        Assert.AreEqual((NetworkFailureKind)expected, NetworkProbe.Classify(diagnostic));
    [TestMethod]
    public void Observer_completes_synchronously_across_chunks_and_discards_oversize_or_contradictory_input()
    {
        using var observer = new NetworkDiagnosticObserver();
        observer.Report("gh: Bad "); observer.Report("credentials (HTTP 401)\n");
        Assert.AreEqual(NetworkFailureKind.Unauthorized,observer.Complete());
        Assert.AreEqual(NetworkFailureKind.Unknown,observer.Complete());
        observer.Report(new string('x',8193)); observer.Report("gh: Internal Server Error (HTTP 500)");
        Assert.AreEqual(NetworkFailureKind.Unknown,observer.Complete());
    }
    [TestMethod]
    [DataRow("http://github.com/user/repo")][DataRow("https://attacker.invalid/user/repo")]
    [DataRow("https://user:secret@github.com/user/repo")][DataRow("https://github.com/user/repo?token=secret")]
    [DataRow("https://github.com/user/repo#fragment")][DataRow("https://github.com:444/user/repo")]
    public async Task Repository_probe_rejects_unvalidated_urls_before_start(string url)
    {
        using var h = new PreflightFixture();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => new NetworkProbe(h.Runner).CheckRepositoryAsync(h.Tools,new(url),h.Environment,h.Job,default));
        Assert.HasCount(0,h.Runner.Requests);
    }
    [TestMethod][DataRow("timeout",1)][DataRow("error",2)]
    public async Task Auth_status_json_network_failure_remains_typed_and_does_not_run_api(string state,int expected)
    {
        using var h = new PreflightFixture();
        var runner = new ScriptedProcessRunner();
        runner.Results.Enqueue(request =>
        {
            string status = PreflightFixture.Status.Replace("\"state\":\"success\"","\"state\":\"" + state + "\",\"error\":\"Get \\\"https://api.github.com/user\\\": dial tcp 127.0.0.1:443: connect: connection refused\"");
            h.Write(request.StandardOutputFile!,status); return new(0,false,false,[],[]);
        });
        var service = new AuthService(h.Paths,runner,(_,_) => Task.FromResult(h.Tools));
        var result = await service.CheckAsync(h.Tools,h.Environment,h.Job,default);
        Assert.IsFalse(result.AuthReady); Assert.AreEqual("AUTH_STATUS_NOT_READY",result.ErrorCode);
        Assert.AreEqual((NetworkFailureKind)expected,result.NetworkFailure!.FailureKind); Assert.IsNull(result.NetworkFailure.RateLimitReset);
        Assert.HasCount(1,runner.Requests);
    }
    [TestMethod]
    public async Task Native_runner_observer_is_terminal_before_ephemeral_result_returns()
    {
        using var tools = await TestToolBuilder.CreateAsync(); using var job = OperationJob.Create();
        using var observer = new NetworkDiagnosticObserver();
        var result = await new ProcessRunner().RunAsync(new(tools.Executable,["stream","err:gh: Bad ","err:credentials (HTTP 401)\n"],tools.Root,tools.Environment,
            TimeSpan.FromSeconds(10),ProcessOutputMode.EphemeralText,ExpectedExecutableIdentity: ExecutableTrust.CaptureTrustedIdentity(tools.Executable),EphemeralStandardError: true),job,observer,default);
        Assert.AreEqual(0,result.ExitCode); Assert.HasCount(0,result.StandardError); Assert.HasCount(0,result.StandardOutput);
        Assert.AreEqual(NetworkFailureKind.Unauthorized,observer.Complete());
    }
    [TestMethod]
    [DataRow("timeout",1,3)][DataRow("refused",2,3)][DataRow("reset",3,3)][DataRow("http500",4,3)]
    [DataRow("http401",5,1)][DataRow("http403",6,1)][DataRow("http404",7,1)][DataRow("http407",8,1)][DataRow("tls",9,1)][DataRow("http429",10,1)]
    public async Task Git_probe_retries_only_observed_transient_failures(string failure,int kind,int attempts)
    {
        using var h = new PreflightFixture(); await using var context = await GitRuntimeContext.CreatePublicProbeAsync(h.Environment,default);
        var runner = new FailureRunner(failure);
        var result = await new NetworkProbe(runner).CheckPublicGitAsync(h.Tools,context.Environment,h.Job,default);
        Assert.AreEqual(attempts,runner.Calls); Assert.AreEqual((NetworkFailureKind)kind,result.FailureKind); Assert.IsFalse(result.Success); Assert.IsNull(result.RateLimitReset);
    }
    private sealed class FailureRunner(string failure) : IProcessRunner
    {
        internal int Calls;
        public Task<ProcessResult> RunAsync(ProcessRequest request,OperationJob job,IProgress<string>? progress,CancellationToken token)
        {
            Calls++; Assert.AreEqual(ProcessOutputMode.EphemeralText,request.OutputMode); Assert.IsTrue(request.EphemeralStandardError);
            string suffix = failure.StartsWith("http",StringComparison.Ordinal) ? "The requested URL returned error: " + failure[4..] : failure switch
            { "refused" => "Failed to connect to github.com port 443 after 10 ms: Could not connect to server", "reset" => "Recv failure: Connection was reset", "tls" => "SSL certificate problem: unable to get local issuer certificate", _ => "" };
            progress!.Report("fatal: unable to access 'https://github.com/github/gitignore.git/': " + suffix);
            return Task.FromResult(new ProcessResult(failure == "timeout" ? null : 128,failure == "timeout",false,[],[]));
        }
    }
}
