using GitHubBackup.App;
namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class ProxyScopeTests
{
    [TestMethod]
    [DataRow("http://user:pass@proxy.example:8080")]
    [DataRow("http://proxy.example:8080/\r\nInjected: yes")]
    [DataRow("http://proxy.example:8080/?token=secret")]
    [DataRow("http://proxy.example:8080/path")]
    [DataRow("file:///C:/proxy")][DataRow("ftp://proxy.example:21")][DataRow("not a uri")]
    [DataRow("http://proxy:0")][DataRow("http://proxy:65536")][DataRow("http://proxy/#a")]
    [DataRow("http://proxy/.")][DataRow("http://proxy/../")][DataRow("http://proxy/path/..")]
    public void Unsafe_proxy_value_is_rejected_without_echo(string value)
    {
        var result = ProxyScope.Parse(value);
        Assert.IsFalse(result.IsValid); Assert.IsNull(result.Uri);
        Assert.IsFalse(result.UserMessage.Contains(value, StringComparison.Ordinal));
    }
    [TestMethod][DataRow("http://proxy.example:8080")][DataRow("https://proxy.example")][DataRow("http://[::1]:8080/")]
    public void Http_proxy_hosts_and_ipv6_are_accepted(string value) => Assert.IsTrue(ProxyScope.Parse(value).IsValid);
    [TestMethod]
    public void Parent_profile_normalizes_case_and_preserves_bypass_without_mutation()
    {
        var parent = new Dictionary<string,string?> { ["https_proxy"] = "http://127.0.0.1:8080", ["HTTP_PROXY"] = "http://proxy:80", ["all_proxy"] = "https://proxy:443", ["no_proxy"] = "localhost,127.0.0.1" };
        var profile = ProxyScope.CreateValidatedParentProfile(parent);
        CollectionAssert.AreEquivalent(new[] { "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "NO_PROXY" }, profile.Environment.Keys.ToArray());
        Assert.AreEqual("localhost,127.0.0.1", profile.Environment["NO_PROXY"]);
        Assert.IsTrue(parent.ContainsKey("https_proxy"));
    }
    [TestMethod]
    [DataRow(null)] [DataRow("")] [DataRow("*")]
    [DataRow("github.com,api.github.com,127.0.0.1")]
    public void Common_no_proxy_accepts_only_the_frozen_minimal_grammar(string? value) =>
        ProxyScope.ValidateCommonNoProxy(value);

    [TestMethod]
    [DataRow(".github.com")] [DataRow("*.github.com")] [DataRow("github.com:443")]
    [DataRow("github.com,*")] [DataRow("github.com,")] [DataRow("github.com, api.github.com")]
    [DataRow("[::1]")] [DataRow("::1")] [DataRow("127.0.0.0/8")]
    [DataRow("127.000.0.1")] [DataRow("localhost_")] [DataRow("github.com\nsecret")]
    public void Unsupported_no_proxy_fails_closed_without_echo(string value)
    {
        var error = Assert.ThrowsExactly<ArgumentException>(() => ProxyScope.ValidateCommonNoProxy(value));
        Assert.AreEqual("PROXY_PROFILE_UNSUPPORTED", error.Message);
        Assert.IsFalse(error.ToString().Contains(value, StringComparison.Ordinal));
        Assert.AreEqual("PROXY_PROFILE_UNSUPPORTED", Assert.ThrowsExactly<ArgumentException>(() =>
            ProxyScope.CreateValidatedParentProfile(new Dictionary<string,string?>
            { ["HTTPS_PROXY"] = "http://proxy.example:8080", ["NO_PROXY"] = value })).Message);
    }

    [TestMethod]
    public async Task Unsupported_parent_bypass_never_tests_a_direct_or_proxy_candidate()
    {
        var scope = new ProxyScope(new NetworkProbe(new ScriptedProcessRunner()),
            new Dictionary<string,string?> { ["HTTPS_PROXY"] = "http://proxy.example:8080", ["NO_PROXY"] = ".github.com" },
            _ => throw new AssertFailedException("No resolver expected"));
        int checks = 0;
        var selected = await scope.SelectAsync((_,_) => { checks++; throw new AssertFailedException("No route may be attempted"); }, default);
        Assert.AreEqual(0, checks);
        Assert.IsFalse(selected.Check.Success);
        Assert.AreEqual("PROXY_PROFILE_UNSUPPORTED", selected.Check.ErrorCode);
    }
    [TestMethod]
    [DataRow("github.com", "github.com", true)]
    [DataRow("github.com", "API.GITHUB.COM", true)]
    [DataRow("github.com", "evilgithub.com", false)]
    [DataRow("github.com", "github.com.evil.test", false)]
    [DataRow("127.0.0.1", "127.0.0.1", true)]
    [DataRow("127.0.0.1", "127.0.0.2", false)]
    [DataRow("1example.com", "foo.1example.com", true)]
    [DataRow("*", "api.github.com", true)]
    public void Common_bypass_has_exact_or_true_dot_subdomain_semantics(string rule, string host, bool bypass) =>
        Assert.AreEqual(bypass, ProxyScope.CommonNoProxyMatches(rule, host));

    [TestMethod]
    public void Child_environment_receives_the_same_validated_bypass_string()
    {
        var profile = ProxyScope.CreateValidatedParentProfile(new Dictionary<string,string?>
        { ["HTTPS_PROXY"] = "http://proxy.example:8080", ["NO_PROXY"] = "github.com,127.0.0.1" });
        using var h = new PreflightFixture();
        var child = ProxyScope.Merge(h.Environment, profile);
        Assert.AreEqual("github.com,127.0.0.1", child["NO_PROXY"]);
        Assert.AreEqual(profile.Environment["HTTPS_PROXY"], child["HTTPS_PROXY"]);
        Assert.AreEqual("http://proxy.example:8080/", profile.Environment["HTTPS_PROXY"]);
    }
    [TestMethod]
    public void Conflicting_case_variants_are_rejected_as_one_profile()
    {
        var parent = new Dictionary<string,string?> { ["HTTPS_PROXY"] = "http://one", ["https_proxy"] = "http://two" };
        Assert.ThrowsExactly<ArgumentException>(() => ProxyScope.CreateValidatedParentProfile(parent));
    }
    [TestMethod]
    public void System_profile_removes_all_bypass_and_direct_merge_keeps_runtime_ownership()
    {
        var parent = new Dictionary<string,string?> { ["NO_PROXY"] = "github.com" };
        var profile = ProxyScope.CreateSystemProxy(new Uri("http://127.0.0.1:8080"), parent);
        Assert.IsFalse(profile.Environment.Keys.Any(k => k.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual("github.com", parent["NO_PROXY"]);
        CollectionAssert.AreEquivalent(new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }, profile.Environment.Keys.ToArray());
    }
    [TestMethod]
    public async Task First_login_bootstrap_uses_only_public_git_and_keeps_runtime_ownership()
    {
        using var h = new PreflightFixture();
        await using var context = await GitRuntimeContext.CreatePublicProbeAsync(h.Environment,default);
        var scope = new ProxyScope(new NetworkProbe(h.Runner),new Dictionary<string,string?>(),_ => throw new AssertFailedException("Unexpected resolver"));
        Assert.AreEqual(ProxyProfile.Direct,await scope.SelectForLoginAsync(h.Tools,context,h.Job,default));
        CollectionAssert.AreEqual(new[] { "ls-remote","https://github.com/github/gitignore.git","HEAD" },h.Runner.Requests.Single().Arguments.ToArray());
        var withProxy = ProxyScope.Merge(context.Environment,ProxyScope.CreateValidatedParentProfile(new Dictionary<string,string?> { ["https_proxy"] = "http://proxy:8080", ["no_proxy"] = "github.com" }));
        var direct = ProxyScope.Merge(withProxy,ProxyProfile.Direct);
        Assert.IsFalse(direct.ContainsKey("HTTPS_PROXY")); Assert.IsFalse(direct.ContainsKey("NO_PROXY"));
        Assert.AreSame(context,((RuntimeEnvironment)direct).Owner);
        Assert.AreSame(context,((RuntimeEnvironment)withProxy).Owner);
    }
    [TestMethod]
    [DataRow("direct",false)][DataRow("single",true)][DataRow("per-origin",false)][DataRow("pac",true)][DataRow("unsafe",false)]
    public async Task System_resolver_checks_both_origins_and_accepts_only_one_safe_route(string variant,bool expected)
    {
        var resolved = new List<Uri>(); int checks = 0;
        var scope = new ProxyScope(new NetworkProbe(new ScriptedProcessRunner()),new Dictionary<string,string?>(),origin =>
        {
            resolved.Add(origin);
            return variant switch { "direct" => origin, "per-origin" => new("http://" + origin.Host + ":8080"), "unsafe" => new("http://user:password@proxy:8080"), _ => new("http://proxy:8080") };
        });
        var selected = await scope.SelectAsync((profile,_) => { checks++; return Task.FromResult(checks == 1 ? new NetworkCheckResult(false,NetworkFailureKind.ConnectionRefused,null,"NETWORK_CONNECTIONREFUSED") : new(true,NetworkFailureKind.None,null,"")); },default);
        Assert.AreEqual(expected,selected.Check.Success); Assert.AreEqual(expected ? 2 : 1,checks);
        CollectionAssert.AreEqual(new[] { "https://github.com/","https://api.github.com/" },resolved.Select(u => u.AbsoluteUri).ToArray());
    }
    [TestMethod][DataRow(4)][DataRow(5)][DataRow(6)][DataRow(7)][DataRow(8)][DataRow(9)][DataRow(10)][DataRow(11)]
    public async Task Nonconnection_failure_never_resolves_or_switches_routes(int kind)
    {
        var scope = new ProxyScope(new NetworkProbe(new ScriptedProcessRunner()),new Dictionary<string,string?> { ["HTTPS_PROXY"] = "http://existing:8080" },_ => throw new AssertFailedException("Must not resolve"));
        int checks = 0;
        var selected = await scope.SelectAsync((profile,_) => { checks++; Assert.AreEqual("Existing environment",profile.DisplayName); return Task.FromResult(new NetworkCheckResult(false,(NetworkFailureKind)kind,null,"FIXTURE")); },default);
        Assert.AreEqual(1,checks); Assert.IsFalse(selected.Check.Success);
    }
    [TestMethod]
    public async Task Unsafe_parent_profile_is_not_partially_inherited()
    {
        var scope = new ProxyScope(new NetworkProbe(new ScriptedProcessRunner()),new Dictionary<string,string?> { ["HTTPS_PROXY"] = "http://safe", ["HTTP_PROXY"] = "http://user:secret@proxy", ["NO_PROXY"] = "github.com" },_ => throw new AssertFailedException());
        var selected = await scope.SelectAsync((profile,_) => { Assert.AreEqual(ProxyProfile.Direct,profile); return Task.FromResult(new NetworkCheckResult(true,NetworkFailureKind.None,null,"")); },default);
        Assert.IsTrue(selected.Check.Success);
    }
    [TestMethod]
    [DataRow("success")][DataRow("startup")][DataRow("timeout")][DataRow("retry")][DataRow("cancellation")]
    public async Task Network_operations_preserve_named_environment_registry_and_global_git_fixtures(string outcome)
    {
        using var h = new PreflightFixture();
        // Named synthetic state only: never read/write real user/machine values,
        // WinINET keys, ordinary gh config, or the user's global Git file.
        var scopes = new Dictionary<string,Dictionary<string,string?>>
        {
            ["process"] = new() { ["https_proxy"] = "http://parent:8080", ["NO_PROXY"] = "github.com" },
            ["user"] = new() { ["HTTPS_PROXY"] = "http://user:8080" },
            ["machine"] = new() { ["HTTP_PROXY"] = "http://machine:8080" }
        };
        var winInet = new Dictionary<string,string> { ["ProxyEnable"] = "1", ["ProxyServer"] = "http://system:8080", ["AutoConfigURL"] = "http://fixture.invalid/config.pac" };
        string globalGit = h.Root.Child("global-git-config.fixture"); h.Write(globalGit,"[http]\nproxy=http://global:8080\n");
        string Fingerprint() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(scopes) + System.Text.Json.JsonSerializer.Serialize(winInet) + File.ReadAllText(globalGit))));
        string before = Fingerprint(); int calls = 0;
        using var cancellation = new CancellationTokenSource();
        var runner = new StateRunner((request,progress) =>
        {
            calls++; Assert.AreNotEqual(globalGit,request.Environment["GIT_CONFIG_GLOBAL"]);
            Assert.AreEqual("github.com",request.Environment["NO_PROXY"]);
            if (outcome == "startup") throw new System.ComponentModel.Win32Exception(193);
            if (outcome == "cancellation") { cancellation.Cancel(); return new(null,false,true,[],[]); }
            if (outcome == "timeout" || (outcome == "retry" && calls < 3)) return new(null,true,false,[],[]);
            return new(0,false,false,[],[]);
        });
        await using var context = await GitRuntimeContext.CreatePublicProbeAsync(h.Environment,default);
        var scope = new ProxyScope(new NetworkProbe(runner),scopes["process"],origin => outcome == "timeout" ? origin : new(winInet["ProxyServer"]));
        try
        {
            if (outcome == "startup") await Assert.ThrowsExactlyAsync<System.ComponentModel.Win32Exception>(() => scope.SelectForLoginAsync(h.Tools,context,h.Job,cancellation.Token));
            else if (outcome == "cancellation") await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => scope.SelectForLoginAsync(h.Tools,context,h.Job,cancellation.Token));
            else if (outcome == "timeout") await Assert.ThrowsExactlyAsync<IOException>(() => scope.SelectForLoginAsync(h.Tools,context,h.Job,cancellation.Token));
            else Assert.AreEqual("Existing environment",(await scope.SelectForLoginAsync(h.Tools,context,h.Job,cancellation.Token)).DisplayName);
            Assert.AreEqual(outcome is "timeout" or "retry" ? 3 : 1,calls);
        }
        finally { Assert.AreEqual(before,Fingerprint()); }
    }
    private sealed class StateRunner(Func<ProcessRequest,IProgress<string>?,ProcessResult> run) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request,OperationJob job,IProgress<string>? progress,CancellationToken token) => Task.FromResult(run(request,progress));
    }
}
