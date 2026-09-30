namespace GitHubBackup.App;
internal static class RetryPolicy
{
    internal static bool ShouldRetry(NetworkFailureKind kind, int attempt, int maxAttempts = 3) =>
        attempt >= 1 && attempt < Math.Min(3, maxAttempts) &&
        kind is NetworkFailureKind.Timeout or NetworkFailureKind.ConnectionRefused or NetworkFailureKind.ConnectionReset or NetworkFailureKind.Http5xx;
    internal static async Task<NetworkCheckResult> ExecuteAsync(Func<CancellationToken,Task<NetworkCheckResult>> check, CancellationToken token, Func<TimeSpan,CancellationToken,Task>? delay = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            NetworkCheckResult result = await check(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.Success || !ShouldRetry(result.FailureKind, attempt)) return result;
            await (delay ?? Task.Delay)(TimeSpan.FromMilliseconds(250 * attempt), token).ConfigureAwait(false);
        }
    }
}
