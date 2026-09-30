namespace GitHubBackup.App;

internal sealed record RepositorySpaceEstimate(long RepositoryId, long ExistingMirrorBytes,
    long RemoteDiskUsageBytes, long StagingPeakBytes);
internal sealed record DiskSpaceRequirement(long SafetyReserveBytes, long SequentialMirrorPeakBytes,
    long EstimatedChangedReleaseBytes, long RequiredFreeBytes, IReadOnlyDictionary<long, RepositorySpaceEstimate> Repositories);
internal sealed class PathSafetyException(string errorCode) : IOException(errorCode)
{
    internal string ErrorCode { get; } = errorCode;
}

internal static class DiskSpacePolicy
{
    internal const long SafetyReserveBytes = 1L << 30;

    internal static DiskSpaceRequirement Calculate(BackupMode mode, IReadOnlyList<RepositoryDescriptor> repositories,
        IReadOnlyDictionary<long, long> existingMirrorBytes, long estimatedChangedReleaseBytes)
    {
        if (estimatedChangedReleaseBytes < 0 || mode is not (BackupMode.Daily or BackupMode.Full))
            throw new ArgumentOutOfRangeException(nameof(estimatedChangedReleaseBytes));
        var estimates = new Dictionary<long, RepositorySpaceEstimate>();
        long peak = 0;
        foreach (RepositoryDescriptor repository in repositories)
        {
            if (repository.RepositoryId <= 0 || repository.DiskUsage < 0
                || !existingMirrorBytes.TryGetValue(repository.RepositoryId, out long existing) || existing < 0)
                throw new PathSafetyException("BACKUP_MIRROR_SIZE_INVALID");
            long remote = checked(repository.DiskUsage * 1024);
            long staging = checked(existing + Math.Max(SafetyReserveBytes, remote));
            if (!estimates.TryAdd(repository.RepositoryId,
                new(repository.RepositoryId, existing, remote, staging)))
                throw new PathSafetyException("BACKUP_MIRROR_SIZE_INVALID");
            peak = Math.Max(peak, staging);
        }
        long changed = mode == BackupMode.Full ? estimatedChangedReleaseBytes : 0;
        return new(SafetyReserveBytes, peak, changed, checked(SafetyReserveBytes + peak + changed), estimates);
    }

    internal static bool HasEnoughFreeSpace(long availableBytes, DiskSpaceRequirement requirement) =>
        availableBytes >= requirement.RequiredFreeBytes;

    internal static long RequiredForRepository(DiskSpaceRequirement requirement, long repositoryId, long remainingChangedBytes)
    {
        if (remainingChangedBytes < 0 || !requirement.Repositories.TryGetValue(repositoryId, out RepositorySpaceEstimate? estimate))
            throw new ArgumentOutOfRangeException(nameof(repositoryId));
        return checked(requirement.SafetyReserveBytes + estimate.StagingPeakBytes + remainingChangedBytes);
    }

    internal static long RequiredForAssetBatch(long remainingChangedBytes)
    {
        if (remainingChangedBytes < 0) throw new ArgumentOutOfRangeException(nameof(remainingChangedBytes));
        return checked(SafetyReserveBytes + remainingChangedBytes);
    }

    internal static long MeasureExistingMirror(string mirrorPath)
    {
        string root = NativeFileSystem.CanonicalPath(mirrorPath);
        try
        {
            using var parents = NativeFileSystem.PinDirectories(Path.GetDirectoryName(root)!);
            try
            {
                using var mirror = NativeFileSystem.Open(root);
                NativeFileSystem.Inspect(mirror, root, directory: true);
            }
            catch (FileNotFoundException) { return 0; }
            return Walk(root);
        }
        catch (PathBoundaryException error) when (error.Code == "REPARSE_POINT_REJECTED")
        { throw new PathSafetyException("BACKUP_MIRROR_SIZE_REPARSE_REJECTED"); }
        catch (Exception error) when (error is not PathSafetyException && error is IOException or UnauthorizedAccessException)
        { throw new PathSafetyException("BACKUP_MIRROR_SIZE_INSPECTION_FAILED"); }

        static long Walk(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new PathSafetyException("BACKUP_MIRROR_SIZE_REPARSE_REJECTED");
            bool directory = (attributes & FileAttributes.Directory) != 0;
            using var handle = NativeFileSystem.Open(path);
            NativeFileSystem.Inspect(handle, path, directory);
            if (!directory)
            {
                using var file = new FileStream(handle, FileAccess.Read);
                if (file.Length < 0) throw new PathSafetyException("BACKUP_MIRROR_SIZE_INSPECTION_FAILED");
                return file.Length;
            }
            long length = 0;
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
                length = checked(length + Walk(NativeFileSystem.CanonicalPath(child)));
            return length;
        }
    }
}
