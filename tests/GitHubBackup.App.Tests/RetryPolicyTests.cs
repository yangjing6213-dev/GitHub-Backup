using GitHubBackup.App;
namespace GitHubBackup.App.Tests;
[TestClass]
public sealed class RetryPolicyTests
{
    [TestMethod]
    [DataRow(1,1,true)][DataRow(2,1,true)][DataRow(3,2,true)][DataRow(4,2,true)][DataRow(4,3,false)]
    [DataRow(5,1,false)][DataRow(6,1,false)][DataRow(7,1,false)][DataRow(8,1,false)][DataRow(9,1,false)][DataRow(10,1,false)][DataRow(11,1,false)]
    public void Only_transient_failures_retry_within_three_total_attempts(int kind, int attempt, bool expected) =>
        Assert.AreEqual(expected, RetryPolicy.ShouldRetry((NetworkFailureKind)kind, attempt, 3));
    [TestMethod]
    public async Task Execute_caps_attempts_and_preserves_observed_reset()
    {
        int calls = 0;
        var result = await RetryPolicy.ExecuteAsync(_ => { calls++; return Task.FromResult(new NetworkCheckResult(false, NetworkFailureKind.Timeout, null, "TIMEOUT")); }, default, (_,_) => Task.CompletedTask);
        Assert.AreEqual(3, calls); Assert.AreEqual(NetworkFailureKind.Timeout, result.FailureKind);
        var reset = DateTimeOffset.FromUnixTimeSeconds(1700000000); calls = 0;
        result = await RetryPolicy.ExecuteAsync(_ => { calls++; return Task.FromResult(new NetworkCheckResult(false, NetworkFailureKind.RateLimited, reset, "RATE")); }, default);
        Assert.AreEqual(1, calls); Assert.AreEqual(reset, result.RateLimitReset);
    }
    [TestMethod]
    public async Task Cancellation_interrupts_backoff_before_next_attempt()
    {
        using var cancel = new CancellationTokenSource(); int calls = 0;
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => RetryPolicy.ExecuteAsync(_ => { calls++; return Task.FromResult(new NetworkCheckResult(false, NetworkFailureKind.Timeout, null, "TIMEOUT")); }, cancel.Token,
            async (_,token) => { cancel.Cancel(); await Task.Delay(TimeSpan.FromMinutes(1), token); }));
        Assert.AreEqual(1, calls);
    }
}
