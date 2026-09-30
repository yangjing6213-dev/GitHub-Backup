using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class MirrorPromotionTests
{
    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task Native_recovery_execution_exception_retains_critical_evidence(bool startupRecovery)
    {
        await using var f=await MirrorFixture.CreateAsync();f.Seed();
        var paths=f.Context.Paths(f.Repository,false);
        f.Runner.BeforeRun=request=>{if(request.WorkingDirectory.Contains(".recovery-",StringComparison.Ordinal))throw new System.ComponentModel.Win32Exception(5);};
        try
        {
            if(startupRecovery)
            {
                MirrorSafeCopyTests.Write(paths.Parent,Path.GetFileName(paths.Journal),"{\"Version\":1,\"LocalName\":\"repo\",\"RunId\":\"test\",\"Wiki\":false,\"Promoted\":false}");
                f.RepositoryLock.Dispose();
                var result=await new MirrorPromotion(f.Runner).RecoverAllAsync(f.Context,default);
                CollectionAssert.Contains(result.ErrorCodes.ToArray(),"PROMOTION_RECOVERY_REQUIRED");
            }
            else
            {
                var result=await new RepositoryBackupService(f.Runner,point=>{if(point==PromotionBoundary.AfterJournal)throw new IOException("injected promotion failure");})
                    .BackupAsync(f.Repository,f.Context,f.RepositoryLock,default);
                Assert.IsTrue(result.Critical);
            }
            Assert.IsTrue(f.Context.ConsistencyPending);Assert.IsTrue(File.Exists(paths.Journal));
            Assert.IsTrue(Directory.Exists(paths.Recovery));Assert.AreEqual("old",File.ReadAllText(Path.Combine(f.Final,"marker")));
            await Assert.ThrowsExactlyAsync<IOException>(()=>f.Context.DisposeAsync().AsTask());
        }
        finally
        {
            // This test owns the injected evidence; remove only its interrupted copy to permit fixture cleanup.
            if(Directory.Exists(paths.Recovery))MirrorSafeCopy.DeleteTree(paths.Recovery);
            f.Runner.BeforeRun=null;f.RepositoryLock.Dispose();
            await new MirrorPromotion(f.Runner).RecoverAllAsync(f.Context,default);
        }
    }
    [TestMethod]
    public async Task Failed_prejournal_cleanup_stays_owned_until_explicit_retry_removes_staging()
    {
        await using var f=await MirrorFixture.CreateAsync();f.Seed();FileStream? blocker=null;
        f.Runner.BeforeRun=request=>
        {
            if(request.Arguments.Contains("fsck")&&!request.FilePath.EndsWith("git-lfs.exe"))
                blocker=new FileStream(Path.Combine(f.Staging,"marker"),FileMode.Open,FileAccess.Read,FileShare.Read);
        };
        f.Runner.Fault="core-fsck";
        var failed=await f.Service.BackupAsync(f.Repository,f.Context,f.RepositoryLock,default);
        Assert.IsTrue(failed.Critical);Assert.IsTrue(f.Context.ConsistencyPending);
        await Assert.ThrowsExactlyAsync<IOException>(()=>f.Context.DisposeAsync().AsTask());
        blocker!.Dispose();f.RepositoryLock.Dispose();
        var retried=await new MirrorPromotion(f.Runner).RecoverAllAsync(f.Context,default);
        Assert.HasCount(0,retried.ErrorCodes);Assert.IsFalse(Directory.Exists(f.Staging));
    }
    [TestMethod]
    public void Directory_rename_by_handle_is_same_parent_and_preserves_identity()
    {
        using var root=new StorageTestRoot();string parent=root.Child("safe");AclPolicy.CreateRestrictedDirectory(parent,root.User);
        string source=Path.Combine(parent,"source"),destination=Path.Combine(parent,"destination");AclPolicy.CreateRestrictedDirectory(source,root.User);
        NativeFileIdentity identity;using(var h=NativeFileSystem.Open(source))identity=NativeFileSystem.Inspect(h,source,true);
        NativeFileSystem.RenameDirectory(source,destination);
        using var final=NativeFileSystem.Open(destination);Assert.AreEqual(identity,NativeFileSystem.Inspect(final,destination,true));
    }
    [TestMethod]
    [DataRow(0)][DataRow(1)][DataRow(2)][DataRow(3)]
    public async Task Cancellation_after_durable_boundary_returns_only_with_stable_final(int boundary)
    {
        await using var f=await MirrorFixture.CreateAsync();f.Seed();using var cancel=new CancellationTokenSource();
        var result=await new RepositoryBackupService(f.Runner,point=>{if((int)point==boundary){cancel.Cancel();f.Preflight.Job.BeginCancellation();}})
            .BackupAsync(f.Repository,f.Context,f.RepositoryLock,cancel.Token);
        Assert.IsTrue(result.Cancelled);Assert.IsFalse(result.Critical,string.Join(',',result.ErrorCodes));
        Assert.IsTrue(Directory.Exists(f.Final));Assert.IsFalse(f.Context.ConsistencyPending);
        Assert.IsFalse(File.Exists(f.Context.Paths(f.Repository,false).Journal));
        Assert.IsTrue(f.Runner.Requests.Any(r=>r.WorkingDirectory.Contains(".recovery-",StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Move_exception_synchronously_recovers_and_reports_failure()
    {
        await using var f=await MirrorFixture.CreateAsync();f.Seed();
        var result=await new RepositoryBackupService(f.Runner,point=>{if(point==PromotionBoundary.AfterFinalToPrevious)throw new IOException("simulated move failure");})
            .BackupAsync(f.Repository,f.Context,f.RepositoryLock,default);
        Assert.IsTrue(result.CoreFailed);Assert.IsFalse(result.Critical,string.Join(',',result.ErrorCodes));
        Assert.IsTrue(Directory.Exists(f.Final));Assert.IsFalse(f.Context.ConsistencyPending);
        Assert.AreEqual("old",File.ReadAllText(Path.Combine(f.Final,"marker")));
    }

    [TestMethod]
    public async Task Cancellation_before_journal_leaves_old_final_and_no_journal()
    {
        await using var f=await MirrorFixture.CreateAsync();f.Seed();using var cancel=new CancellationTokenSource();cancel.Cancel();
        var result=await f.Service.BackupAsync(f.Repository,f.Context,f.RepositoryLock,cancel.Token);
        Assert.IsTrue(result.Cancelled);Assert.AreEqual("old",File.ReadAllText(Path.Combine(f.Final,"marker")));
        Assert.IsFalse(File.Exists(f.Context.Paths(f.Repository,false).Journal));Assert.HasCount(0,f.Runner.Requests);
    }

    [TestMethod]
    public async Task Recovery_rejects_unlisted_journal_without_running_git()
    {
        await using var f=await MirrorFixture.CreateAsync();f.RepositoryLock.Dispose();
        string parent=Path.Combine(f.Preflight.OwnerRoot,"mirrors");AclPolicy.CreateRestrictedDirectory(parent,f.Preflight.User);
        MirrorSafeCopyTests.Write(parent,".unlisted.git.swap-test.json","{}");
        var result=await new MirrorPromotion(f.Runner).RecoverAllAsync(f.Context,default);
        Assert.HasCount(1,result.ErrorCodes);Assert.HasCount(0,f.Runner.Requests);
        Assert.IsTrue(f.Context.ConsistencyPending);
        // Remove only this test's injected invalid journal before retrying cleanup.
        File.Delete(Path.Combine(parent,".unlisted.git.swap-test.json"));
        Assert.HasCount(0,(await new MirrorPromotion(f.Runner).RecoverAllAsync(f.Context,default)).ErrorCodes);
    }
}
