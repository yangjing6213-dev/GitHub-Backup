using System.Security.Principal;
using System.Text.Json;

namespace GitHubBackup.App;

internal sealed record BackupRequest(BackupMode Mode, AppSettings Settings, PreflightSession Session);
internal sealed record BackupProgress(string Phase, string? Repository, int Current, int Total, IReadOnlyList<string>? LogTail = null);

internal sealed class BackupOrchestrator(IProcessRunner runner, AppPaths paths,
    Func<string, long>? availableBytes = null, SourceIntegrityAudit? integrity = null, RunCoordinator? coordinator = null,
    Action<PromotionBoundary>? promotionBoundary = null, AtomicFileCommitHooks? releaseCommitHooks = null,
    AtomicFileCommitHooks? summaryCommitHooks = null)
{
    private readonly RunCoordinator runs = coordinator ?? new();
    internal bool HasPendingCleanup => runs.HasPendingCleanup;
    internal bool IsBusy => runs.IsBusy;
    internal Task CancelAsync() => runs.CancelAsync();
    internal Task RetryCleanupAsync() => runs.RetryCleanupAsync();

    internal Task<BackupRunResult> RunAsync(BackupRequest request, CancellationToken token, IProgress<BackupProgress>? progress = null)
    {
        // Accepting a request transfers the live preflight session to this run.
        var state = new RunState(request, runner, paths, availableBytes, integrity ?? new(), promotionBoundary, releaseCommitHooks, progress);
        return runs.RunAsync(request.Session.Job, state.ExecuteAsync, state.CleanupAsync, token);
    }

    // Preflight and backup share one slot; only the accepted RunState owns session cleanup.
    internal Task<BackupRunResult> RunWithPreflightAsync(BackupMode mode, AppSettings settings,
        Func<OperationJob, CancellationToken, Task<PreflightCheckResult>> check, Func<Task> cleanupPreflight,
        IProgress<BackupProgress>? progress, CancellationToken token)
    {
        if (runs.IsBusy) throw new InvalidOperationException("OPERATION_ALREADY_ACTIVE");
        var job = OperationJob.Create(); RunState? state = null; PreflightSession? session = null;
        string runId = RunIdFactory.Create(TimeProvider.System);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        BackupSummary? rejectedSummary = null;
        AtomicFileCleanupException? receiptCleanup = null;
        return runs.RunAsync(job, async cancellation =>
        {
            try
            {
                progress?.Report(new("preflight", null, 0, 0));
                var checkedResult = await check(job, cancellation).ConfigureAwait(false);
                session = checkedResult.LiveSession;
                cancellation.ThrowIfCancellationRequested();
                if (!checkedResult.Snapshot.Report.CanStartBackup || session is null)
                {
                    var issue = checkedResult.Snapshot.Report.Issues.FirstOrDefault(x => x.BlocksBackup);
                    return Rejected(issue?.ErrorCode ?? "PREFLIGHT_CHECK_FAILED", false) with { NetworkFailure = issue?.NetworkFailure };
                }
                state = new(new(mode, settings, session), runner, paths, availableBytes, integrity ?? new(), promotionBoundary, releaseCommitHooks, progress);
                return await state.ExecuteAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return Rejected("BACKUP_CANCELLED", true); }
            catch (Exception) { return Rejected("PREFLIGHT_CHECK_FAILED", cancellation.IsCancellationRequested); }
        }, async () =>
        {
            progress?.Report(new("cleanup", null, 0, 0));
            if (receiptCleanup is not null) { receiptCleanup.RetryCleanup(); receiptCleanup = null; }
            if (state is not null)
            {
                await state.CleanupAsync().ConfigureAwait(false);
                await cleanupPreflight().ConfigureAwait(false);
                job.Dispose(); return;
            }
            bool cleanupFailed = false;
            try
            {
                if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
                await cleanupPreflight().ConfigureAwait(false);
            }
            catch (Exception)
            {
                cleanupFailed = true;
                if (rejectedSummary is not null) rejectedSummary = rejectedSummary with
                    { Status = RunStatus.Fail, FailurePhase = "cleanup", ErrorCode = "BACKUP_CLEANUP_PENDING" };
            }
            if (rejectedSummary is not null)
            {
                // Before the trusted run owns a storage root, only the protected fallback is writable.
                try { await new SummaryStore(paths).WriteFallbackAsync(rejectedSummary, CancellationToken.None, summaryCommitHooks).ConfigureAwait(false); }
                catch (AtomicFileCleanupException ex) { receiptCleanup = ex; throw; }
            }
            if (cleanupFailed) throw new IOException("BACKUP_CLEANUP_PENDING");
            job.Dispose();
        }, token);

        BackupRunResult Rejected(string code, bool cancelled)
        {
            rejectedSummary = BackupSummary.PreflightFailure(mode, settings.Owner, runId, "", "preflight", code) with
            { StartedAt = started, Status = cancelled ? RunStatus.Cancelled : RunStatus.Fail, WasCancelled = cancelled };
            return new(rejectedSummary, false);
        }
    }

    private sealed class RunState(BackupRequest request, IProcessRunner runner, AppPaths paths,
        Func<string, long>? availableBytes, SourceIntegrityAudit integrity, Action<PromotionBoundary>? promotionBoundary,
        AtomicFileCommitHooks? releaseCommitHooks, IProgress<BackupProgress>? progress)
    {
        private readonly string runId = RunIdFactory.Create(TimeProvider.System);
        private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        private readonly ManifestStore manifests = new();
        private readonly BackupCheckpointStore checkpoints = new();
        private readonly HashSet<string> failed = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> completedRepositories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> resumedRepositories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> failureDiagnosticCounts = new(StringComparer.Ordinal);
        private readonly List<AtomicFileCleanupException> ownedFiles = [];
        private OperationLockLease? owner;
        private BackupRunContext? context;
        private RunLogger? logger;
        private string ownerRoot = "", manifest = "", log = "", phase = "preflight", error = "";
        private int repositoryCount, warnings, skipped;
        private bool cancelled, writesStarted, finalized, ownsMarker;
        private bool rateLimitedStop;
        private DateTimeOffset? rateLimitReset;
        private BackupSummary? summary;
        private NetworkCheckResult? networkFailure;

        internal async Task<BackupRunResult> ExecuteAsync(CancellationToken token)
        {
            try
            {
                var session = request.Session;
                session.Revalidate(); token.ThrowIfCancellationRequested();
                if (!AuthConfigLease.IsLogin(request.Settings.Owner) || request.Mode is not (BackupMode.Daily or BackupMode.Full)
                    || !session.Snapshot.Report.CanStartBackup
                    || !string.Equals(NativeFileSystem.CanonicalPath(request.Settings.BackupRoot), session.Snapshot.ApprovedBackupRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("BACKUP_SESSION_INVALID");
                var transport = session.HttpTransport ?? throw new InvalidOperationException("BACKUP_NATIVE_SESSION_REQUIRED");
                if (!string.Equals(transport.BoundLogin, request.Settings.Owner, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("BACKUP_OWNER_SESSION_MISMATCH");
                ownerRoot = Path.Combine(session.Snapshot.ApprovedBackupRoot!, request.Settings.Owner);
                phase = "lock";
                // Creating a missing owner boundary is the only pre-gate storage initialization.
                using (SummaryStore.RequirePrivateDirectory(session.Snapshot.ApprovedBackupRoot!))
                    if (!Directory.Exists(ownerRoot)) AclPolicy.CreateRestrictedDirectory(ownerRoot, WindowsIdentity.GetCurrent().User!, requireNew: true);
                owner = OperationLocks.AcquireStorage(ownerRoot, runId);
                phase = "audit";
                if (!integrity.ValidateBackupRoot(session.Snapshot.ApprovedBackupRoot!).Allowed
                    || !integrity.ValidateExistingTrees(ownerRoot, [], true, owner).Allowed)
                    throw new IOException("BACKUP_SOURCE_UNSAFE");
                phase = "reconcile";
                var previous = await manifests.ReadLatestAsync(ownerRoot, token).ConfigureAwait(false);
                var bindings = new List<LegacyBinding>();
                foreach (var old in previous.Where(r => r.RepositoryId == 0))
                    bindings.Add(await LegacyIdentityBinder.TryBindAsync(ownerRoot, old, session.Snapshot.Repositories, request.Settings.Owner,
                        new(Path.Combine(ownerRoot, "metadata", old.LocalName, "repository.json")), token).ConfigureAwait(false));
                var mappings = ManifestStore.Reconcile(ownerRoot, previous, session.Snapshot.Repositories, bindings);
                context = BackupRunContext.Create(session, owner, mappings, runId,
                    request.Settings.RepositoryScope, request.Settings.IncludeCollaboratorRepositories);
                phase = "recovery";
                await RecoverAsync().ConfigureAwait(false);
                await RunningMarkerStore.RemoveCompletedAsync(owner, null, null, CancellationToken.None, paths).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); session.Revalidate();

                var current = mappings.Where(r => r.RemoteState == "active").ToArray();
                repositoryCount = current.Length;
                try
                {
                    BackupCheckpoint? previousCheckpoint = await checkpoints.ReadAsync(ownerRoot, request.Settings.Owner, token).ConfigureAwait(false);
                    if (previousCheckpoint is { PausedByRateLimit: true })
                    {
                        foreach (string name in previousCheckpoint.CompletedRepositories)
                            if (current.Any(repository => string.Equals(repository.LocalName, name, StringComparison.OrdinalIgnoreCase)))
                            {
                                resumedRepositories.Add(name);
                                completedRepositories.Add(name);
                            }
                    }
                }
                catch (JsonException)
                {
                    // A damaged progress hint must never block a fresh backup.
                }
                var sizes = new Dictionary<long, long>();
                var plans = new Dictionary<long, ReleasePlan>();
                var releases = new ReleaseAssetService(transport);
                var actions = new ActionsArchiveService();
                long changed = 0;
                phase = "planning";
                progress?.Report(new(phase, null, 0, repositoryCount));
                foreach (var repository in current)
                {
                    token.ThrowIfCancellationRequested(); session.Revalidate();
                    // Missing parents are zero bytes; never create them to estimate space.
                    long size = 0;
                    foreach (string path in new[] { context.Paths(repository, false).Final, context.Paths(repository, true).Final })
                        if (Directory.Exists(Path.GetDirectoryName(path)!)) size = checked(size + DiskSpacePolicy.MeasureExistingMirror(path));
                    sizes.Add(repository.RepositoryId, size);
                    if (request.Mode == BackupMode.Full)
                    {
                        string repositoryOwner = RemoteOwner(repository);
                        var inventory = await releases.ReadInventoryAsync(request.Settings.Owner, repositoryOwner, repository.Name, token).ConfigureAwait(false);
                        var plan = await releases.PlanAsync(Path.Combine(ownerRoot, "releases", repository.LocalName), request.Settings.Owner,
                            repositoryOwner, repository.Name, inventory, token).ConfigureAwait(false);
                        plans.Add(repository.RepositoryId, plan); changed = checked(changed + plan.ChangedBytes);
                    }
                }
                var requirement = DiskSpacePolicy.Calculate(request.Mode, current, sizes, changed);
                phase = "space";
                RequireSpace(requirement.RequiredFreeBytes);
                token.ThrowIfCancellationRequested(); session.Revalidate();
                phase = "initialize";
                writesStarted = true;
                foreach (string name in new[] { "manifests", "logs", "metadata", "mirrors", "wikis", "progress" }) CreateDirectory(Path.Combine(ownerRoot, name));
                if (request.Mode == BackupMode.Full) CreateDirectory(Path.Combine(ownerRoot, "releases"));
                if (request.Settings.IncludeActionsArtifacts) CreateDirectory(Path.Combine(ownerRoot, "actions"));
                manifest = await manifests.WriteAsync(ownerRoot, runId, mappings, token).ConfigureAwait(false);
                await RunningMarkerStore.WriteAsync(owner, new(runId, request.Mode, request.Settings.Owner, started), token).ConfigureAwait(false);
                ownsMarker = true;
                log = Path.Combine(ownerRoot, "logs", "backup-" + runId + ".log");
                logger = await RunLogger.CreateAsync(log, token).ConfigureAwait(false);
                await logger.WriteAsync(LogLevel.Info, "start", null, "BACKUP_STARTED", token).ConfigureAwait(false);
                progress?.Report(new(phase, null, 0, repositoryCount, logger.GetTail()));
                await SaveCheckpointAsync(current.Length, null, "planning", false, null, token).ConfigureAwait(false);
                var backup = new RepositoryBackupService(runner, promotionBoundary);
                long remaining = changed;
                int currentIndex = 0;
                foreach (var repository in current)
                {
                    currentIndex++;
                    token.ThrowIfCancellationRequested(); session.Revalidate();
                    if (resumedRepositories.Contains(repository.LocalName))
                    {
                        progress?.Report(new("mirror", repository.Name, currentIndex, repositoryCount, logger.GetTail()));
                        continue;
                    }
                    phase = "space"; RequireSpace(DiskSpacePolicy.RequiredForRepository(requirement, repository.RepositoryId, remaining));
                    using var repositoryLock = OperationLocks.AcquireRepository(owner, repository.LocalName, runId);
                    phase = "mirror";
                    progress?.Report(new(phase, repository.Name, currentIndex, repositoryCount, logger.GetTail()));
                    var core = await backup.BackupAsync(repository, context, repositoryLock, token).ConfigureAwait(false);
                    await RecordAsync(repository, core).ConfigureAwait(false);
                    if (core.CoreFailed)
                    {
                        skipped++;
                        if (request.Mode == BackupMode.Full)
                        {
                            remaining = checked(remaining - plans[repository.RepositoryId].ChangedBytes);
                            if (remaining < 0) throw new InvalidOperationException("BACKUP_RELEASE_BUDGET_INVALID");
                        }
                        continue;
                    }
                    phase = "wiki";
                    progress?.Report(new(phase, repository.Name, currentIndex, repositoryCount, logger.GetTail()));
                    await RecordAsync(repository, await backup.BackupWikiAsync(repository, context, repositoryLock, token).ConfigureAwait(false)).ConfigureAwait(false);
                    phase = "metadata";
                    progress?.Report(new(phase, repository.Name, currentIndex, repositoryCount, logger.GetTail()));
                    string metadata = Path.Combine(ownerRoot, "metadata", repository.LocalName); CreateDirectory(metadata);
                    var pages = new RawPageStore(metadata);
                    await pages.SaveRepositorySummaryAsync(repository, token).ConfigureAwait(false);
                    foreach (string file in new[] { "repository.json", "issues.pages.json", "pull-requests.pages.json", "issue-comments.pages.json",
                        "review-comments.pages.json", "releases.pages.json", "labels.pages.json", "milestones.pages.json", "workflows.pages.json" })
                    {
                        try { await pages.SaveMetadataAsync(request.Settings.Owner, RemoteOwner(repository), repository.Name, file, transport, token).ConfigureAwait(false); }
                        catch (Exception ex) when (IsItemFailure(ex))
                        {
                            warnings++; RecordNetworkFailure(ex);
                            bool limited = ex is HttpTransferException { FailureKind: NetworkFailureKind.RateLimited };
                            await LogAsync(limited ? "HTTP_RATE_LIMITED" : "METADATA_BACKUP_FAILED", repository.LocalName).ConfigureAwait(false);
                            if (limited) { rateLimitedStop = true; break; }
                        }
                    }
                    if (rateLimitedStop)
                    {
                        await SaveCheckpointAsync(current.Length, repository.LocalName, phase, true, rateLimitReset, CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    if (request.Settings.IncludeActionsArtifacts)
                    {
                        phase = "actions";
                        progress?.Report(new(phase, repository.Name, currentIndex, repositoryCount, logger.GetTail()));
                        try
                        {
                            ActionsBackupReport actionReport = await actions.SaveAsync(ownerRoot, request.Settings.Owner,
                                RemoteOwner(repository), repository.Name, repository.LocalName, request.Settings.ActionsMaxBytes,
                                transport, token).ConfigureAwait(false);
                            warnings += actionReport.Warnings.Count;
                            foreach (string warning in actionReport.Warnings.Distinct(StringComparer.Ordinal))
                                await LogAsync(warning, repository.LocalName).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (IsItemFailure(ex))
                        {
                            warnings++; RecordNetworkFailure(ex);
                            bool limited = ex is HttpTransferException { FailureKind: NetworkFailureKind.RateLimited };
                            await LogAsync(limited ? "HTTP_RATE_LIMITED" : "ACTIONS_BACKUP_FAILED", repository.LocalName).ConfigureAwait(false);
                            if (limited) rateLimitedStop = true;
                        }
                    }
                    if (rateLimitedStop)
                    {
                        await SaveCheckpointAsync(current.Length, repository.LocalName, phase, true, rateLimitReset, CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    if (request.Mode == BackupMode.Full)
                    {
                        phase = "release"; var plan = plans[repository.RepositoryId];
                        progress?.Report(new(phase, repository.Name, currentIndex, repositoryCount, logger.GetTail()));
                        try
                        {
                            await releases.MaterializeAsync(plan, FreeBytes, token).ConfigureAwait(false);
                            if (plan.Generations.Any(g => g.Assets.Any(a => a.Asset.Digest is null)))
                                await logger!.WriteAsync(LogLevel.Info, "release", repository.LocalName, "RELEASE_SIZE_ONLY_INTEGRITY", CancellationToken.None).ConfigureAwait(false);
                            await releases.CommitIndexAsync(plan, token, releaseCommitHooks).ConfigureAwait(false);
                        }
                        catch (ReleaseException ex) when (ex.Code == "BACKUP_INSUFFICIENT_FREE_SPACE") { throw; }
                        catch (Exception ex) when (IsItemFailure(ex))
                        {
                            warnings++; RecordNetworkFailure(ex);
                            bool limited = ex is HttpTransferException { FailureKind: NetworkFailureKind.RateLimited };
                            await LogAsync(limited ? "HTTP_RATE_LIMITED" : "RELEASE_BACKUP_FAILED", repository.LocalName).ConfigureAwait(false);
                            if (limited) rateLimitedStop = true;
                        }
                        remaining -= plan.ChangedBytes;
                    }
                    if (rateLimitedStop)
                    {
                        await SaveCheckpointAsync(current.Length, repository.LocalName, phase, true, rateLimitReset, CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    completedRepositories.Add(repository.LocalName);
                    await SaveCheckpointAsync(current.Length, repository.LocalName, "repository-complete", false, null, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { cancelled = true; error = "BACKUP_CANCELLED"; }
            catch (Exception ex)
            {
                if (ex is AtomicFileCleanupException cleanup) ownedFiles.Add(cleanup);
                cancelled = token.IsCancellationRequested;
                RecordNetworkFailure(ex);
                error = ex is AuthBoundaryException ? "BACKUP_AUTH_INVALID"
                    : ex is HttpTransferException { FailureKind: NetworkFailureKind.RateLimited } ? "HTTP_RATE_LIMITED"
                    : ex is ReleaseException { Code: "BACKUP_INSUFFICIENT_FREE_SPACE" } ? "BACKUP_INSUFFICIENT_FREE_SPACE"
                    : phase == "lock" ? "BACKUP_OWNER_LOCK_UNAVAILABLE" : phase == "recovery" ? "PROMOTION_RECOVERY_REQUIRED" : "BACKUP_FAILED";
            }
            summary = Summary();
            return new(summary, false) { NetworkFailure = networkFailure };
        }

        private void RecordNetworkFailure(Exception error)
        {
            if (error is HttpTransferException { FailureKind: NetworkFailureKind.RateLimited } limited)
            {
                rateLimitReset = limited.RateLimitReset;
                networkFailure = new(false, NetworkFailureKind.RateLimited, limited.RateLimitReset, "HTTP_RATE_LIMITED");
            }
        }

        private async Task RecordAsync(RepositoryDescriptor repository, RepositoryStepResult step)
        {
            warnings += step.WarningCount; skipped += step.SkippedWikiCount;
            if (step.CoreFailed)
            {
                failed.Add(repository.LocalName);
                string code = step.ErrorCodes.LastOrDefault() ?? "MIRROR_BACKUP_FAILED";
                if (!IsSafeDiagnosticCode(code)) code = "MIRROR_BACKUP_FAILED";
                failureDiagnosticCounts[code] = failureDiagnosticCounts.GetValueOrDefault(code) + 1;
                if (logger is not null)
                {
                    await logger.WriteAsync(LogLevel.Error, "mirror", null,
                        "MIRROR_FAILURE " + code, CancellationToken.None).ConfigureAwait(false);
                    progress?.Report(new("mirror", null, 0, 0, logger.GetTail()));
                }
            }
            if (step.Critical) throw new IOException("PROMOTION_RECOVERY_REQUIRED");
            if (step.Cancelled) throw new OperationCanceledException();
        }
        private static bool IsSafeDiagnosticCode(string code) => code.Length is > 0 and <= 80
            && code.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
        private static string RemoteOwner(RepositoryDescriptor repository)
        {
            string[] parts = repository.NameWithOwner.Split('/');
            if (parts.Length != 2 || !AuthConfigLease.IsLogin(parts[0])) throw new InvalidDataException("REPOSITORY_SUMMARY_INVALID");
            return parts[0];
        }
        private string MirrorFailureCode() => failureDiagnosticCounts.Count == 1
            ? failureDiagnosticCounts.Keys.Single() : "MIRROR_BACKUP_FAILED";
        private long FreeBytes() => availableBytes?.Invoke(request.Settings.BackupRoot)
            ?? new DriveInfo(Path.GetPathRoot(request.Settings.BackupRoot)!).AvailableFreeSpace;
        private void RequireSpace(long required)
        { if (FreeBytes() < required) throw new ReleaseException("BACKUP_INSUFFICIENT_FREE_SPACE"); }
        private static void CreateDirectory(string path) => AclPolicy.CreateRestrictedDirectory(path, WindowsIdentity.GetCurrent().User!);
        private async Task LogAsync(string code, string? repository = null)
        {
            if (logger is null) return;
            await logger.WriteAsync(LogLevel.Warning, phase, repository, code, CancellationToken.None).ConfigureAwait(false);
            progress?.Report(new(phase, repository, 0, 0, logger.GetTail()));
        }
        private async Task SaveCheckpointAsync(int totalRepositories, string? currentRepository, string checkpointPhase,
            bool pausedByRateLimit, DateTimeOffset? reset, CancellationToken token)
        {
            try
            {
                await checkpoints.WriteAsync(ownerRoot, new(1, request.Settings.Owner, runId, DateTimeOffset.UtcNow,
                    pausedByRateLimit, totalRepositories, completedRepositories.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    currentRepository, checkpointPhase, reset), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                warnings++;
                await LogAsync("CHECKPOINT_WRITE_FAILED", currentRepository).ConfigureAwait(false);
            }
        }
        private static bool IsItemFailure(Exception ex) => ex is not AtomicFileCleanupException && ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception;
        private BackupSummary Summary() => new(2, request.Mode, started, DateTimeOffset.UtcNow,
            failed.Count != 0 ? RunStatus.Fail : cancelled ? RunStatus.Cancelled : error.Length != 0 ? RunStatus.Fail : warnings != 0 ? RunStatus.Partial : RunStatus.Pass,
            cancelled, request.Settings.Owner, runId, repositoryCount, warnings, failed.Order(StringComparer.OrdinalIgnoreCase).ToArray(), skipped,
            error.Length != 0 ? phase : rateLimitedStop ? phase : failed.Count != 0 ? "mirror" : "", error.Length != 0 ? error : rateLimitedStop ? "HTTP_RATE_LIMITED" : failed.Count != 0 ? MirrorFailureCode() : "",
            ownerRoot, manifest, log);
        private async Task RecoverAsync()
        {
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var recovery = await new MirrorPromotion(runner).RecoverAllAsync(context!, bounded.Token).ConfigureAwait(false);
                if (recovery.ErrorCodes.Count != 0) throw new IOException("PROMOTION_RECOVERY_REQUIRED");
            }
            catch (AtomicFileCleanupException cleanup)
            {
                context!.ConsistencyPending = true;
                ownedFiles.Add(cleanup); throw new IOException("PROMOTION_RECOVERY_REQUIRED");
            }
        }

        internal async Task CleanupAsync()
        {
            try { await CleanupCoreAsync().ConfigureAwait(false); }
            catch (Exception)
            {
                summary = (summary ?? Summary()) with { Status = RunStatus.Fail, FailurePhase = "cleanup", ErrorCode = "BACKUP_CLEANUP_PENDING" };
                // A failed cleanup must never leave a PASS receipt; the matching marker stays until retry succeeds.
                if (writesStarted && owner is not null)
                {
                    try { await new SummaryStore(paths).WriteAsync(ownerRoot, summary, paths.DiagnosticFallbackRoot, CancellationToken.None).ConfigureAwait(false); }
                    catch (AtomicFileCleanupException cleanup) { ownedFiles.Add(cleanup); }
                    catch (Exception) { }
                }
                throw new IOException("BACKUP_CLEANUP_PENDING");
            }
        }

        private async Task CleanupCoreAsync()
        {
            foreach (var file in ownedFiles.ToArray()) { file.RetryCleanup(); ownedFiles.Remove(file); }
            // Keep recovery capability and owner alive after ordinary cancellation.
            if (context is not null && (context.ConsistencyPending || context.OwnedStaging.Count != 0)) await RecoverAsync().ConfigureAwait(false);
            if (context is not null) { await context.DisposeAsync().ConfigureAwait(false); context = null; }
            await request.Session.DisposeAsync().ConfigureAwait(false);
            if (!writesStarted && !finalized)
            {
                try { await new SummaryStore(paths).WriteFallbackAsync(summary ?? Summary(), CancellationToken.None).ConfigureAwait(false); }
                catch (AtomicFileCleanupException cleanup) { ownedFiles.Add(cleanup); throw; }
                finalized = true;
            }
            if (writesStarted && !finalized)
            {
                summary ??= Summary();
                if (logger is not null)
                {
                    foreach (var diagnostic in failureDiagnosticCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                        await logger.WriteAsync(LogLevel.Error, "mirror", null,
                            $"MIRROR_FAILURE_CATEGORY {diagnostic.Key} COUNT {diagnostic.Value}", CancellationToken.None).ConfigureAwait(false);
                    if (failed.Count != 0 && failureDiagnosticCounts.Count == 0)
                        await logger.WriteAsync(LogLevel.Error, "mirror", null,
                            $"MIRROR_FAILURE_CATEGORY MIRROR_BACKUP_FAILED COUNT {failed.Count}", CancellationToken.None).ConfigureAwait(false);
                    await logger.WriteAsync(LogLevel.Info, "complete", null,
                        summary.Status.ToString().ToUpperInvariant(), CancellationToken.None).ConfigureAwait(false);
                    progress?.Report(new("cleanup", null, 0, 0, logger.GetTail()));
                    await logger.DisposeAsync().ConfigureAwait(false); logger = null;
                }
                string summaryPath;
                try { summaryPath = await new SummaryStore(paths).WriteAsync(ownerRoot, summary, paths.DiagnosticFallbackRoot, CancellationToken.None).ConfigureAwait(false); }
                catch (AtomicFileCleanupException cleanup) { ownedFiles.Add(cleanup); throw; }
                if (ownsMarker)
                {
                    try { await RunningMarkerStore.RemoveCompletedAsync(owner!, runId, summaryPath, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The terminal receipt is already durable. A residue must not rewrite it.
                        try
                        {
                            using var diagnostic = AppDataPathPolicy.Acquire(paths, paths.DiagnosticLogRoot, AppDataEntryKind.Directory, true);
                            await AtomicFile.WriteAsync(Path.Combine(paths.DiagnosticLogRoot, "diagnostic-" + runId + ".log"),
                                (stream, ct) => stream.WriteAsync("RUNNING_MARKER_COMPLETED_RESIDUE\n"u8.ToArray(), ct).AsTask(), CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception) { } // Post-finalization diagnostics cannot change the terminal result.
                    }
                    ownsMarker = false;
                }
                finalized = true;
            }
            owner?.Dispose(); owner = null;
        }
    }
}
