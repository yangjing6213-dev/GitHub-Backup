using System.Text;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SecretRedactorTests
{
    [TestMethod]
    [DataRow("github_pat_TESTONLY_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [DataRow("ghp_TESTONLYABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [DataRow("prefixghp_TESTONLYABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [DataRow("prefix_github_pat_TESTONLY_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [DataRow("prefixGHp_TESTONLYABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [DataRow("Authorization: Bearer TESTONLY-SECRET")]
    [DataRow("Cookie: session=TESTONLY-SECRET")]
    [DataRow("https://user:TESTONLY-SECRET@proxy.example:8443/")]
    [DataRow("GIT_ASKPASS=TESTONLY-SECRET")]
    [DataRow("https://objects.githubusercontent.com/path/asset.bin?X-Amz-Signature=TESTONLY-SECRET&X-Amz-Expires=300#fragment")]
    [DataRow("https://storage.example/blob?sig=TESTONLY-SECRET&se=2099-01-01")]
    [DataRow("https://example.test/path?sig='TESTONLY-SECRET'")]
    [DataRow("https://user:TESTONLY'SECRET@proxy.example/")]
    public void Every_split_boundary_hides_canary(string canary)
    {
        for (int split = 1; split < canary.Length; split++)
        {
            var redactor = new SecretRedactor();
            string actual = redactor.Push(canary[..split]) + redactor.Push(canary[split..]) + redactor.Complete();
            Assert.AreEqual(-1, actual.IndexOf("TESTONLY", StringComparison.Ordinal), $"split {split}: {actual}");
            Assert.AreEqual(-1, actual.IndexOf("ghp_", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(-1, actual.IndexOf("github_pat_", StringComparison.OrdinalIgnoreCase));
        }
        var stream = new SafeTextStream();
        string characters = string.Concat(canary.Select(ch => stream.Push(ch.ToString()))) + stream.Complete();
        Assert.DoesNotContain("TESTONLY", characters);
    }

    [TestMethod]
    public void Interleaved_streams_keep_independent_state()
    {
        var stdout = new SafeTextStream();
        var stderr = new SafeTextStream();
        string actual = stdout.Push("gh") + stderr.Push("notice\n")
            + stdout.Push("p_TESTONLY-SECRET-ABCDEFGHIJKLMNOPQRSTUVWXYZ") + stdout.Complete() + stderr.Complete();
        Assert.AreEqual(-1, actual.IndexOf("TESTONLY", StringComparison.Ordinal));
        Assert.AreEqual(-1, actual.IndexOf("ghp_", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(actual, "notice");
    }

    [TestMethod]
    [DataRow("gh\u001b[31mp_TESTONLYABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [DataRow("Authorization:\u001b[0m Bearer TESTONLY-SECRET")]
    [DataRow("https://objects.githubusercontent.com/a?X-Amz-\u001b[32mSignature=TESTONLY-SECRET")]
    public void Controls_are_removed_before_redaction(string hostile)
    {
        var stream = new SafeTextStream();
        string actual = string.Concat(hostile.Select(ch => stream.Push(ch.ToString()))) + stream.Complete();
        Assert.AreEqual(-1, actual.IndexOf("TESTONLY", StringComparison.Ordinal));
        Assert.AreEqual(-1, actual.IndexOf("ghp_", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(-1, actual.IndexOf("X-Amz-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain('\u001b', actual);
    }

    [TestMethod]
    public void Utf8_decoder_retains_partial_multibyte_character()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("é Authorization: Bearer TESTONLY-SECRET\n");
        var stream = new SafeTextStream();
        string actual = string.Concat(bytes.Select(b => stream.Push(new byte[] { b }))) + stream.Complete();
        StringAssert.Contains(actual, "é");
        Assert.AreEqual(-1, actual.IndexOf("TESTONLY", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Truncated_uri_does_not_release_unfinished_userinfo()
    {
        var stream = new SafeTextStream();
        string actual = stream.Push("https://TESTONLY-SECRET" + new string('x', 9000) + "@proxy.example/\n") + stream.Complete();
        Assert.DoesNotContain("TESTONLY", actual);
        StringAssert.Contains(actual, "[TRUNCATED]");
        Assert.IsLessThanOrEqualTo(8192, actual.TrimEnd('\n').Length);
    }

    [TestMethod]
    [DataRow("gh\rp_TESTONLYABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [DataRow("ALL_PROXY=socks5://user:TESTONLY-SECRET@proxy.example:1080")]
    [DataRow("GIT_ASKPASS=\"path with TESTONLY-SECRET\"")]
    [DataRow("gh\u001b(Bp_TESTONLYABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [DataRow("gh\u001bPignored\u001b\\p_TESTONLYABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    public void Controls_and_proxy_assignments_do_not_expose_secrets(string hostile)
    {
        var stream = new SafeTextStream();
        string actual = string.Concat(hostile.Select(ch => stream.Push(ch.ToString()))) + stream.Complete();
        Assert.DoesNotContain("TESTONLY", actual);
    }

    [TestMethod]
    public void Both_interleaved_streams_and_osc_sequences_are_independent()
    {
        var stdout = new SafeTextStream();
        var stderr = new SafeTextStream();
        string actual = stdout.Push("gh") + stderr.Push("Authorization:")
            + stdout.Push("\u001b]title\a") + stderr.Push(" Bearer TESTONLY-SECRET\n")
            + stdout.Push("p_TESTONLY-SECRET\n") + stdout.Complete() + stderr.Complete();
        Assert.DoesNotContain("TESTONLY", actual);
        Assert.DoesNotContain('\u001b', actual);
        StringAssert.Contains(actual, "[REDACTED]");
    }

    [TestMethod]
    public void Redaction_expansion_still_respects_physical_line_limit()
    {
        var stream = new SafeTextStream();
        string actual = stream.Push(string.Concat(Enumerable.Repeat("ghp_x ", 1300))) + stream.Complete();
        Assert.IsLessThanOrEqualTo(8192, actual.Length);
        StringAssert.Contains(actual, "[TRUNCATED]");
    }
}
