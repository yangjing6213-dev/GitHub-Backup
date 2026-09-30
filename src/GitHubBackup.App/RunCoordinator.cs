namespace GitHubBackup.App;

internal sealed record BackupRunResult(BackupSummary Summary, bool HasPendingCleanup)
{
    internal NetworkCheckResult? NetworkFailure { get; init; }
}

// All work, including explicit cleanup retries, occupies the same UI operation slot.
internal sealed class RunCoordinator
{
    private readonly object gate = new();
    private Task<BackupRunResult>? active;
    private CancellationTokenSource? cancellation;
    private OperationJob? job;
    private Func<Task>? pendingCleanup;
    private bool retrying;
    internal bool IsBusy { get { lock (gate) return active is not null || pendingCleanup is not null; } }
    internal bool HasPendingCleanup { get { lock (gate) return pendingCleanup is not null; } }

    internal Task<BackupRunResult> RunAsync(OperationJob operation, Func<CancellationToken, Task<BackupRunResult>> work,
        Func<Task> cleanup, CancellationToken token)
    {
        lock (gate)
        {
            if (IsBusy) throw new InvalidOperationException("OPERATION_ALREADY_ACTIVE");
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            job = operation;
            active = ExecuteAsync(work, cleanup, cancellation.Token);
            return active;
        }
    }

    private async Task<BackupRunResult> ExecuteAsync(Func<CancellationToken, Task<BackupRunResult>> work,
        Func<Task> cleanup, CancellationToken token)
    {
        await Task.Yield(); // Publish the operation slot before any synchronous work can finish.
        using var registration = token.Register(job!.BeginCancellation);
        BackupRunResult result;
        try { result = await work(token).ConfigureAwait(false); }
        finally
        {
            try { await cleanup().ConfigureAwait(false); }
            catch (Exception)
            {
                lock (gate) pendingCleanup = cleanup;
            }
            lock (gate)
            {
                active = null; cancellation!.Dispose(); cancellation = null; job = null;
            }
        }
        return HasPendingCleanup ? result with
        {
            HasPendingCleanup = true,
            Summary = result.Summary with { Status = RunStatus.Fail, FailurePhase = "cleanup", ErrorCode = "BACKUP_CLEANUP_PENDING" }
        } : result;
    }

    internal async Task CancelAsync()
    {
        Task<BackupRunResult>? running;
        lock (gate)
        {
            running = active;
            job?.BeginCancellation();
            cancellation?.Cancel();
        }
        if (running is not null) await running.ConfigureAwait(false);
    }

    internal async Task RetryCleanupAsync()
    {
        Func<Task>? cleanup;
        lock (gate)
        {
            if (active is not null || retrying) throw new InvalidOperationException("OPERATION_ALREADY_ACTIVE");
            cleanup = pendingCleanup;
            if (cleanup is null) return;
            retrying = true;
        }
        try
        {
            await cleanup().ConfigureAwait(false);
            lock (gate) pendingCleanup = null;
        }
        catch (Exception) { throw new IOException("BACKUP_CLEANUP_PENDING"); }
        finally { lock (gate) retrying = false; }
    }
}
