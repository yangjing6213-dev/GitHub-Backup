namespace GitHubBackup.App;
internal enum DependencyInstallStatus { Installed, Cancelled, Failed }
internal sealed record DependencyInstallResult(DependencyInstallStatus Status, ToolInventory Inventory, string ErrorCode);
internal sealed class DependencySetupService(IProcessRunner runner, Func<OperationJob,CancellationToken,Task<ToolInventory>> detectTools)
{
    private static readonly IReadOnlyDictionary<DependencyId,string> PackageIds = new Dictionary<DependencyId,string>
    { [DependencyId.Git] = "Git.Git", [DependencyId.GitHubCli] = "GitHub.cli", [DependencyId.GitLfs] = "GitHub.GitLFS" };
    internal async Task<DependencyInstallResult> InstallAsync(DependencyId id, bool userConfirmed, OperationJob job, CancellationToken cancellationToken)
    {
        if (!userConfirmed) return new(DependencyInstallStatus.Cancelled, ToolInventory.Empty, "DEPENDENCY_CONFIRMATION_REQUIRED");
        if (!PackageIds.TryGetValue(id, out string? package)) return new(DependencyInstallStatus.Failed, ToolInventory.Empty, "DEPENDENCY_NOT_ALLOWLISTED");
        if (cancellationToken.IsCancellationRequested || job.IsCancellationRequested) return new(DependencyInstallStatus.Cancelled, ToolInventory.Empty, "DEPENDENCY_CANCELLED");
        ToolInventory inventory;
        try { inventory = await detectTools(job, cancellationToken).ConfigureAwait(false); }
        catch (GitRuntimeCleanupException) { throw; }
        catch (OperationCanceledException) { return new(DependencyInstallStatus.Cancelled, ToolInventory.Empty, "DEPENDENCY_CANCELLED"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return new(DependencyInstallStatus.Failed, ToolInventory.Empty, "DEPENDENCY_DETECTION_FAILED"); }
        if (inventory.Winget is not { IsSupported: true } winget) return new(DependencyInstallStatus.Failed, inventory, "DEPENDENCY_WINGET_UNAVAILABLE");
        ProcessResult? result = null; string error = "DEPENDENCY_INSTALL_FAILED"; GitRuntimeContext? context = null;
        try
        {
            var child = ChildEnvironmentBuilder.CreateCurrentBase([Path.GetDirectoryName(winget.AbsolutePath)!]);
            context = await GitRuntimeContext.CreatePublicProbeAsync(child, cancellationToken).ConfigureAwait(false);
            result = await runner.RunAsync(new(winget.AbsolutePath,
                ["install", "--id", package, "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements"],
                Path.GetDirectoryName(context.Environment["GIT_CONFIG_GLOBAL"])!, context.Environment, TimeSpan.FromMinutes(20), ExpectedExecutableIdentity: winget.Identity), job, null, cancellationToken).ConfigureAwait(false);
        }
        catch (GitRuntimeCleanupException) { throw; }
        catch (OperationCanceledException) { error = "DEPENDENCY_CANCELLED"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        finally
        {
            if (context is not null)
            {
                try { await context.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { throw new GitRuntimeCleanupException(context); }
            }
        }
        bool cancelled = cancellationToken.IsCancellationRequested || job.IsCancellationRequested || result?.Cancelled == true || result?.ExitCode == unchecked((int)0x800704C7);
        try
        {
            inventory = await ToolInventoryRefresh.AfterActionAsync(detectTools, job, cancellationToken).ConfigureAwait(false);
        }
        catch (GitRuntimeCleanupException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { return new(cancelled ? DependencyInstallStatus.Cancelled : DependencyInstallStatus.Failed, ToolInventory.Empty, "DEPENDENCY_REDETECTION_FAILED"); }
        if (cancelled) return new(DependencyInstallStatus.Cancelled, inventory, "DEPENDENCY_CANCELLED");
        if (result is not null && ProcessOutcomeClassifier.Classify(result) == ProcessTerminalKind.Succeeded)
            return inventory.Get(id)?.IsSupported == true ? new(DependencyInstallStatus.Installed, inventory, "") : new(DependencyInstallStatus.Failed, inventory, "DEPENDENCY_NOT_DETECTED");
        return new(DependencyInstallStatus.Failed, inventory, result?.TimedOut == true ? "DEPENDENCY_INSTALL_TIMEOUT" : error);
    }
}
