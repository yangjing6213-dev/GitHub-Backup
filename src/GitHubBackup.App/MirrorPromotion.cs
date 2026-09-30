using System.Text.Json;

namespace GitHubBackup.App;
internal enum PromotionBoundary {AfterJournal,AfterFinalToPrevious,AfterStagingToFinal,AfterPromotedJournal}
internal sealed record PromotionResult(bool Completed,bool Cancelled,bool Critical,string ErrorCode);
internal sealed record PromotionRecoveryResult(int RecoveredCount,IReadOnlyList<string> ErrorCodes);
internal sealed class MirrorPromotion(IProcessRunner runner,Action<PromotionBoundary>? boundary=null)
{
    internal async Task<PromotionResult> PromoteAsync(BackupRunContext context,RepositoryDescriptor repository,OperationLockLease repositoryLock,bool wiki,CancellationToken token)
    {
        context.Require(repository,repositoryLock,true);var paths=context.Paths(repository,wiki);
        using var parent=SummaryStore.RequirePrivateDirectory(paths.Parent);
        MirrorSafeCopy.Audit(paths.Staging);
        if(Directory.Exists(paths.Final))MirrorSafeCopy.Audit(paths.Final);
        if(File.Exists(paths.Journal)||Directory.Exists(paths.Previous)||Directory.Exists(paths.Recovery)||Directory.Exists(paths.Discard))
            throw new IOException("PROMOTION_RECOVERY_REQUIRED");
        token.ThrowIfCancellationRequested();
        await WriteJournal(paths,repository.LocalName,wiki,false,token,true).ConfigureAwait(false);
        context.ConsistencyPending=true;
        bool failed=false;
        try
        {
            // No caller token is observed until all four durable/move boundaries finish.
            boundary?.Invoke(PromotionBoundary.AfterJournal);
            if(Directory.Exists(paths.Final))NativeFileSystem.RenameDirectory(paths.Final,paths.Previous);
            boundary?.Invoke(PromotionBoundary.AfterFinalToPrevious);
            NativeFileSystem.RenameDirectory(paths.Staging,paths.Final);
            boundary?.Invoke(PromotionBoundary.AfterStagingToFinal);
            await WriteJournal(paths,repository.LocalName,wiki,true,CancellationToken.None,false).ConfigureAwait(false);
            boundary?.Invoke(PromotionBoundary.AfterPromotedJournal);
            if(!token.IsCancellationRequested)
            {
                Cleanup(paths);context.ConsistencyPending=false;
                return new(true,false,false,"");
            }
        }
        catch(Exception ex) when(ex is not AtomicFileCleanupException && ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception){failed=true;}
        using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await RecoverAsync(context,repository,repositoryLock,wiki,paths,cleanup.Token).ConfigureAwait(false);
            context.ConsistencyPending=false;
            return token.IsCancellationRequested?new(false,true,false,"PROMOTION_CANCELLED_AFTER_STABLE_RECOVERY")
                :new(false,false,false,failed?"PROMOTION_MOVE_FAILED":"PROMOTION_FAILED");
        }
        catch(Exception ex) when(ex is not AtomicFileCleanupException && ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {return new(false,token.IsCancellationRequested,true,"PROMOTION_RECOVERY_REQUIRED");}
    }

    internal async Task<PromotionRecoveryResult> RecoverAllAsync(BackupRunContext context,CancellationToken token)
    {
        var candidates=new List<(RepositoryDescriptor Repository,bool Wiki,MirrorPaths Paths)>();
        int recovered=0;
        try
        {
            context.Recovery.Revalidate();_ = context.Owner.RequireOwnerRoot();
            // Whitelist the complete journal set before executing any candidate Git.
            foreach(bool wiki in new[]{false,true})
            {
                string parent=Path.Combine(context.OwnerRoot,wiki?"wikis":"mirrors");
                if(!Directory.Exists(parent))continue;
                using var pinned=SummaryStore.RequirePrivateDirectory(parent);
                MirrorSafeCopy.Audit(parent);
                foreach(string journal in Directory.EnumerateFileSystemEntries(parent,"*.swap-*.json"))
                {
                    token.ThrowIfCancellationRequested();
                    Journal record=await ReadJournal(journal,token).ConfigureAwait(false);
                    var repository=context.Repositories.SingleOrDefault(r=>r.LocalName==record.LocalName)
                        ??throw new InvalidDataException("PROMOTION_JOURNAL_UNLISTED");
                    var paths=context.Paths(repository,wiki,record.RunId);
                    if(record.Wiki!=wiki||journal!=paths.Journal)throw new InvalidDataException("PROMOTION_JOURNAL_PATH_INVALID");
                    AuditCandidates(paths);candidates.Add((repository,wiki,paths));
                }
            }
            foreach(var candidate in candidates)
            {
                using var repositoryLock=OperationLocks.AcquireRepository(context.Owner,candidate.Repository.LocalName,context.RunId);
                await RecoverAsync(context,candidate.Repository,repositoryLock,candidate.Wiki,candidate.Paths,token).ConfigureAwait(false);recovered++;
            }
            foreach(var owned in context.OwnedStaging.ToArray())
            {
                token.ThrowIfCancellationRequested();
                using var repositoryLock=OperationLocks.AcquireRepository(context.Owner,owned.Value.Repository.LocalName,context.RunId);
                context.Require(owned.Value.Repository,repositoryLock,false);
                if(owned.Key!=context.Paths(owned.Value.Repository,owned.Value.Wiki).Staging)throw new InvalidDataException("STAGING_CLEANUP_PATH_INVALID");
                if(Directory.Exists(owned.Key))MirrorSafeCopy.DeleteTree(owned.Key);
                context.OwnedStaging.Remove(owned.Key);
            }
            context.ConsistencyPending=false;return new(recovered,[]);
        }
        catch(Exception ex) when(ex is not AtomicFileCleanupException && ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or ArgumentException or JsonException or System.ComponentModel.Win32Exception)
        {context.ConsistencyPending=true;return new(recovered,["PROMOTION_RECOVERY_REQUIRED"]);}
    }

    private async Task RecoverAsync(BackupRunContext context,RepositoryDescriptor repository,OperationLockLease repositoryLock,bool wiki,MirrorPaths paths,CancellationToken token)
    {
        context.Require(repository,repositoryLock,false);
        using var parent=SummaryStore.RequirePrivateDirectory(paths.Parent);
        Journal journal=await ReadJournal(paths.Journal,token).ConfigureAwait(false);
        if(journal.LocalName!=repository.LocalName||journal.RunId!=paths.RunId||journal.Wiki!=wiki)throw new InvalidDataException("PROMOTION_JOURNAL_PATH_INVALID");
        AuditCandidates(paths);
        string endpoint=context.Endpoint(repository,wiki,recovery:true);
        // A prior interrupted recovery copy is evidence. Never overwrite it silently.
        if(Directory.Exists(paths.Recovery))throw new IOException("PROMOTION_RECOVERY_COPY_PENDING");
        string[] order=journal.Promoted?[paths.Final,paths.Previous,paths.Staging,paths.Discard]:[paths.Previous,paths.Final,paths.Staging,paths.Discard];
        bool valid=false;
        foreach(string candidate in order.Where(Directory.Exists))
        {
            token.ThrowIfCancellationRequested();
            await MirrorSafeCopy.CopyAndSanitizeAsync(paths.Parent,candidate,paths.Recovery,endpoint,token).ConfigureAwait(false);
            try
            {
                using var staging=StagingRepository.Inspect(context,repository,repositoryLock,wiki,paths.RunId,recovery:true);
                await GitCommands.ValidateIdentityAsync(runner,context,staging,endpoint,true,token).ConfigureAwait(false);
                if(!wiki&&(await LfsObjects.ScanAsync(staging.Root,token).ConfigureAwait(false)).Values.Any(x=>!x))throw new IOException("LFS_OBJECT_INTEGRITY_UNRESOLVED");
                await GitCommands.FsckAsync(runner,context,staging,endpoint,true,token).ConfigureAwait(false);
                valid=true;
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException){MirrorSafeCopy.DeleteTree(paths.Recovery);continue;}
            if(valid)break;
        }
        if(!valid)throw new IOException("PROMOTION_NO_VALID_CANDIDATE");
        token.ThrowIfCancellationRequested();
        // The journal continues to cover final/previous/staging/recovery/discard.
        if(Directory.Exists(paths.Final))
        {
            if(Directory.Exists(paths.Discard))MirrorSafeCopy.DeleteTree(paths.Discard);
            NativeFileSystem.RenameDirectory(paths.Final,paths.Discard);
        }
        NativeFileSystem.RenameDirectory(paths.Recovery,paths.Final);
        await WriteJournal(paths,repository.LocalName,wiki,true,CancellationToken.None,false).ConfigureAwait(false);
        Cleanup(paths);
    }

    private static void AuditCandidates(MirrorPaths paths)
    {
        foreach(string path in new[]{paths.Final,paths.Previous,paths.Staging,paths.Recovery,paths.Discard})
        {
            if(File.Exists(path))throw new InvalidDataException("PROMOTION_CANDIDATE_TYPE_INVALID");
            if(Directory.Exists(path)){MirrorSafeCopy.Audit(path);MirrorSafeCopy.ValidateLayout(path);}
        }
    }
    private static void Cleanup(MirrorPaths paths)
    {
        foreach(string path in new[]{paths.Previous,paths.Staging,paths.Recovery,paths.Discard})
            if(Directory.Exists(path))MirrorSafeCopy.DeleteTree(path);
        MirrorSafeCopy.DeleteFile(paths.Journal);
    }
    private sealed record Journal(int Version,string LocalName,string RunId,bool Wiki,bool Promoted);
    private static Task WriteJournal(MirrorPaths paths,string localName,bool wiki,bool promoted,CancellationToken token,bool requireNew)
        =>AtomicFile.WriteAsync(paths.Journal,(stream,ct)=>JsonSerializer.SerializeAsync(stream,new Journal(1,localName,paths.RunId,wiki,promoted),cancellationToken:ct),token,requireNew:requireNew);
    private static async Task<Journal> ReadJournal(string path,CancellationToken token)
    {
        using var handle=MirrorSafeCopy.OpenPrivate(path,shareWrite:false);
        using var stream=new FileStream(handle,FileAccess.Read);
        if(stream.Length>8192)throw new InvalidDataException("PROMOTION_JOURNAL_INVALID");
        using var document=await JsonDocument.ParseAsync(stream,cancellationToken:token).ConfigureAwait(false);
        var properties=document.RootElement.EnumerateObject().Select(x=>x.Name).ToArray();
        if(properties.Length!=5||!new[]{"Version","LocalName","RunId","Wiki","Promoted"}.All(name=>properties.Count(x=>x==name)==1))throw new InvalidDataException("PROMOTION_JOURNAL_INVALID");
        var result=document.RootElement.Deserialize<Journal>()??throw new InvalidDataException("PROMOTION_JOURNAL_INVALID");
        if(result.Version!=1||!RepositoryNameMapper.IsSafeLocalName(result.LocalName))throw new InvalidDataException("PROMOTION_JOURNAL_INVALID");
        BackupRunContext.RequireRunId(result.RunId);return result;
    }
}
