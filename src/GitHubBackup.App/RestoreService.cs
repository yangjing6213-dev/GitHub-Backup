using System.Security.Principal;

namespace GitHubBackup.App;

internal sealed record RestoreRepositoryResult(
    string LocalName,
    bool MirrorFound,
    bool HistoryVerified,
    bool WorkingCopyCreated,
    bool MetadataCopied,
    IReadOnlyList<string> MissingItems,
    string ErrorCode);

internal sealed record RestoreReport(
    string Owner,
    string DestinationRoot,
    int RepositoryCount,
    int VerifiedCount,
    int RestoredCount,
    IReadOnlyList<RestoreRepositoryResult> Repositories,
    IReadOnlyList<string> Warnings)
{
    internal bool Success => Warnings.Count == 0 && Repositories.All(item => item.WorkingCopyCreated && string.IsNullOrEmpty(item.ErrorCode))
        && (RepositoryCount == 0 || RestoredCount == RepositoryCount);
}

/// <summary>
/// Validates the newest local manifest and restores each bare mirror into a
/// brand-new working-copy directory. It never writes into the backup tree.
/// </summary>
internal sealed class RestoreService(IProcessRunner runner)
{
    private static readonly string[] MetadataFiles =
    [
        "repository.json", "issues.pages.json", "pull-requests.pages.json", "issue-comments.pages.json",
        "review-comments.pages.json", "releases.pages.json", "labels.pages.json", "milestones.pages.json",
        "workflows.pages.json"
    ];

    internal async Task<RestoreReport> RestoreLatestAsync(
        string ownerRoot,
        string owner,
        string destinationParent,
        ToolDetection git,
        IReadOnlyDictionary<string, string?> baseEnvironment,
        OperationJob job,
        CancellationToken token)
    {
        if (!AuthConfigLease.IsLogin(owner)) throw new ArgumentException("OWNER_INVALID");
        if (!git.IsSupported) throw new InvalidOperationException("RESTORE_GIT_REQUIRED");

        string sourceRoot = NativeFileSystem.CanonicalPath(ownerRoot);
        if (!string.Equals(Path.GetFileName(sourceRoot), owner, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("RESTORE_OWNER_ROOT_INVALID");
        using PathLease sourceLease = SummaryStore.RequirePrivateDirectory(sourceRoot);

        string parent = NativeFileSystem.CanonicalPath(destinationParent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("RESTORE_DESTINATION_PARENT_MISSING");
        if (IsSameOrChild(parent, sourceRoot)) throw new UnauthorizedAccessException("RESTORE_DESTINATION_INSIDE_BACKUP");
        using PathLease parentLease = SummaryStore.RequirePrivateDirectory(parent);
        string destination = Path.Combine(parent, "GitHubBackup-Restore-" + RunIdFactory.Create(TimeProvider.System));
        AclPolicy.CreateRestrictedDirectory(destination, WindowsIdentity.GetCurrent().User!, requireNew: true);

        IReadOnlyList<RepositoryDescriptor> manifest = await new ManifestStore().ReadLatestAsync(sourceRoot, token).ConfigureAwait(false);
        var active = manifest.Where(repository => repository.RemoteState == "active").ToArray();
        var results = new List<RestoreRepositoryResult>(active.Length);
        var warnings = new List<string>();

        await using GitRuntimeContext runtime = await GitRuntimeContext.CreatePublicProbeAsync(
            ChildEnvironmentBuilder.CreateCurrentBase([Path.GetDirectoryName(git.AbsolutePath)!]), token).ConfigureAwait(false);
        var environment = ChildEnvironmentBuilder.Build(runtime.Environment, new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "core.hooksPath",
            ["GIT_CONFIG_VALUE_0"] = runtime.EmptyHooksDirectory,
            ["GIT_CONFIG_KEY_1"] = "protocol.file.allow",
            ["GIT_CONFIG_VALUE_1"] = "always"
        });

        foreach (RepositoryDescriptor repository in active)
        {
            token.ThrowIfCancellationRequested();
            results.Add(await RestoreRepositoryAsync(sourceRoot, destination, repository, git, environment, job, token).ConfigureAwait(false));
        }

        int verified = results.Count(item => item.HistoryVerified);
        int restored = results.Count(item => item.WorkingCopyCreated);
        if (results.Any(item => !item.WorkingCopyCreated)) warnings.Add("RESTORE_INCOMPLETE");
        return new(owner, destination, active.Length, verified, restored, results.AsReadOnly(), warnings.AsReadOnly());
    }

    private async Task<RestoreRepositoryResult> RestoreRepositoryAsync(
        string sourceRoot,
        string destination,
        RepositoryDescriptor repository,
        ToolDetection git,
        IReadOnlyDictionary<string, string?> environment,
        OperationJob job,
        CancellationToken token)
    {
        var missing = new List<string>();
        string mirror = Path.Combine(sourceRoot, "mirrors", repository.LocalName + ".git");
        bool mirrorFound = Directory.Exists(mirror);
        if (!mirrorFound)
            return new(repository.LocalName, false, false, false, false, ["mirror"], "RESTORE_MIRROR_MISSING");

        bool history = false;
        bool workingCopy = false;
        bool metadata = false;
        string error = "";
        try
        {
            MirrorSafeCopy.Audit(mirror);
            using PathLease mirrorLease = SummaryStore.RequirePrivateDirectory(mirror);
            string target = Path.Combine(destination, repository.LocalName);
            AclPolicy.CreateRestrictedDirectory(target, WindowsIdentity.GetCurrent().User!, requireNew: true);
            ProcessResult fsck = await RunGitAsync(git, environment, ["--git-dir", mirror, "fsck", "--full"],
                sourceRoot, job, token).ConfigureAwait(false);
            if (ProcessOutcomeClassifier.Classify(fsck) != ProcessTerminalKind.Succeeded)
                throw new IOException("RESTORE_HISTORY_INVALID");
            history = true;

            ProcessResult clone = await RunGitAsync(git, environment, ["clone", "--local", "--no-hardlinks", mirror, target],
                destination, job, token).ConfigureAwait(false);
            if (ProcessOutcomeClassifier.Classify(clone) != ProcessTerminalKind.Succeeded)
                throw new IOException("RESTORE_CLONE_FAILED");
            using (SummaryStore.RequirePrivateDirectory(target)) { }
            workingCopy = true;
            metadata = await CopyMetadataAsync(sourceRoot, destination, repository, missing, token).ConfigureAwait(false);
            await CopyActionsAsync(sourceRoot, destination, repository, missing, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex.Message is { Length: > 0 and <= 80 } value && value.All(c => c is >= 'A' and <= 'Z' || char.IsAsciiDigit(c) || c == '_')
                ? ex.Message : "RESTORE_FAILED";
            if (history && !workingCopy) missing.Add("working-copy");
            if (!metadata && !missing.Contains("metadata", StringComparer.OrdinalIgnoreCase)) missing.Add("metadata");
        }
        return new(repository.LocalName, mirrorFound, history, workingCopy, metadata, missing.AsReadOnly(), error);
    }

    private async Task<bool> CopyMetadataAsync(string sourceRoot, string destination, RepositoryDescriptor repository,
        List<string> missing, CancellationToken token)
    {
        string source = Path.Combine(sourceRoot, "metadata", repository.LocalName);
        if (!Directory.Exists(source)) { missing.Add("metadata"); return false; }
        string target = Path.Combine(destination, "metadata", repository.LocalName);
        string metadataRoot = Path.Combine(destination, "metadata");
        if (Directory.Exists(metadataRoot)) using (SummaryStore.RequirePrivateDirectory(metadataRoot)) { }
        else AclPolicy.CreateRestrictedDirectory(metadataRoot, WindowsIdentity.GetCurrent().User!, requireNew: true);
        AclPolicy.CreateRestrictedDirectory(target, WindowsIdentity.GetCurrent().User!, requireNew: true);
        int copied = 0;
        foreach (string file in MetadataFiles)
        {
            token.ThrowIfCancellationRequested();
            string inputPath = Path.Combine(source, file);
            if (!File.Exists(inputPath)) { missing.Add(file); continue; }
            using var inputHandle = MirrorSafeCopy.OpenPrivate(inputPath, shareWrite: false);
            string outputPath = Path.Combine(target, file);
            await using (FileStream input = new(inputHandle, FileAccess.Read))
            await using (FileStream output = AclPolicy.CreateRestrictedFile(outputPath, WindowsIdentity.GetCurrent().User!))
            {
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                output.Flush(true);
            }
            copied++;
        }
        return copied > 0;
    }

    private static async Task CopyActionsAsync(string sourceRoot, string destination, RepositoryDescriptor repository,
        List<string> missing, CancellationToken token)
    {
        string source = Path.Combine(sourceRoot, "actions", repository.LocalName);
        if (!Directory.Exists(source)) return;
        SourceIntegrityResult integrity = new SourceIntegrityAudit().ValidateExistingTrees(source, [], true);
        if (!integrity.Allowed) { missing.Add("actions"); return; }
        string target = Path.Combine(destination, "actions", repository.LocalName);
        AclPolicy.CreateRestrictedDirectory(Path.Combine(destination, "actions"), WindowsIdentity.GetCurrent().User!, requireNew: true);
        EnsurePrivateDirectories(Path.Combine(destination, "actions"), target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            string canonical = NativeFileSystem.CanonicalPath(file);
            if (!canonical.StartsWith(NativeFileSystem.CanonicalPath(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
            { missing.Add("actions"); return; }
            string relative = Path.GetRelativePath(source, canonical);
            string output = Path.Combine(target, relative);
            string parent = Path.GetDirectoryName(output)!;
            EnsurePrivateDirectories(target, parent);
            using var inputHandle = MirrorSafeCopy.OpenPrivate(canonical, shareWrite: false);
            await using FileStream input = new(inputHandle, FileAccess.Read);
            await using FileStream stream = AclPolicy.CreateRestrictedFile(output, WindowsIdentity.GetCurrent().User!);
            await input.CopyToAsync(stream, token).ConfigureAwait(false);
            stream.Flush(true);
        }
    }

    private static void EnsurePrivateDirectories(string root, string target)
    {
        string relative = Path.GetRelativePath(root, target);
        string current = root;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current)) AclPolicy.CreateRestrictedDirectory(current, WindowsIdentity.GetCurrent().User!, requireNew: true);
            else using (SummaryStore.RequirePrivateDirectory(current)) { }
        }
    }

    private async Task<ProcessResult> RunGitAsync(ToolDetection git, IReadOnlyDictionary<string, string?> environment,
        IReadOnlyList<string> arguments, string workingDirectory, OperationJob job, CancellationToken token)
    {
        return await runner.RunAsync(new(git.AbsolutePath, arguments, workingDirectory, environment,
            TimeSpan.FromMinutes(5), ProcessOutputMode.EphemeralText, ExpectedExecutableIdentity: git.Identity,
            EphemeralStandardError: true), job, null, token).ConfigureAwait(false);
    }

    private static bool IsSameOrChild(string path, string parent)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(path);
        string normalizedParent = Path.TrimEndingDirectorySeparator(parent);
        return string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
