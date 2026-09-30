using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class StagingCapabilityTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Recovery_mint_rejects_hooks_only_and_lfs_only_snapshot_forgery(bool alterHooks)
    {
        await using var f=await MirrorFixture.CreateAsync();
        var runtime=((RuntimeEnvironment)f.Session.Snapshot.ChildEnvironment).Owner!;
        using var auth=f.Preflight.Auth.AcquireConfig();
        var original=f.Session.Snapshot;
        var snapshot=alterHooks?original with{EmptyHooksDirectory=f.Preflight.Root.Path}
            :original with{Tools=original.Tools with{GitLfs=LocalGitRunner.Git}};
        var forged=new PreflightSession(snapshot,runtime,auth,f.Preflight.Job);
        RecoveryLease? lease=null;bool rejected=false;
        try{lease=forged.MintRecoveryLease();}catch(InvalidOperationException){rejected=true;}
        finally{if(lease is not null)await lease.DisposeAsync();}
        Assert.IsTrue(rejected,"Snapshot-only changes must not mint a recovery capability.");
        await using var valid=f.Session.MintRecoveryLease();
    }
    [TestMethod]
    public async Task Original_final_cannot_be_adopted_as_a_staging_capability()
    {
        await using var f=await MirrorFixture.CreateAsync();f.Seed();
        await MirrorSafeCopy.SanitizeAsync(f.Final,"https://github.com/fixture-user/repo.git",default);
        StagingRepository? adopted=null;bool rejected=false;
        try{adopted=StagingRepository.Inspect(f.Context,f.Repository,f.RepositoryLock,false);}
        catch(FileNotFoundException){rejected=true;}
        finally{adopted?.Dispose();}
        Assert.IsTrue(rejected,"A safe final must still never become process staging.");
    }
    [TestMethod]
    public async Task Recovery_mint_rejects_forged_session_tool_and_proxy_snapshot()
    {
        await using var f=await MirrorFixture.CreateAsync();
        var runtime=((RuntimeEnvironment)f.Session.Snapshot.ChildEnvironment).Owner!;
        using var auth=f.Preflight.Auth.AcquireConfig();
        foreach(bool alterTool in new[]{true,false})
        {
            var snapshot=f.Session.Snapshot;
            snapshot=alterTool?snapshot with{Tools=snapshot.Tools with{Git=snapshot.Tools.Git! with{AbsolutePath=@"C:\untrusted\git.exe"}}}
                :snapshot with{ChildEnvironment=ChildEnvironmentBuilder.Build(snapshot.ChildEnvironment,new Dictionary<string,string?>{["HTTPS_PROXY"]="http://127.0.0.1:9"})};
            var forged=new PreflightSession(snapshot,runtime,auth,f.Preflight.Job);
            RecoveryLease? lease=null;bool rejected=false;
            try{lease=forged.MintRecoveryLease();}catch(InvalidOperationException){rejected=true;}
            finally{if(lease is not null)await lease.DisposeAsync();}
            Assert.IsTrue(rejected,"Forged snapshot must not mint a runtime recovery capability.");
        }
    }
    [TestMethod]
    public async Task Real_git_recovery_fsck_works_after_normal_job_closes_and_rejects_forgery()
    {
        await using var f=await MirrorFixture.CreateAsync(realGit:true);
        AclPolicy.CreateRestrictedDirectory(Path.GetDirectoryName(f.Staging)!,f.Preflight.User);
        await LocalGitRunner.InitializeAsync(f.Staging,f.Preflight.Environment);
        await MirrorSafeCopy.SanitizeAsync(f.Staging,"https://github.com/fixture-user/repo.git",default);
        using var stage=StagingRepository.Inspect(f.Context,f.Repository,f.RepositoryLock,false);
        var normal=f.Context.EnvironmentFor("https://github.com/fixture-user/repo.git",stage);
        Assert.ThrowsExactly<ArgumentException>(()=>ChildEnvironmentBuilder.Build(f.Session.CreateEnvironment(),new Dictionary<string,string?>{["GIT_DIR"]=stage.Root}));
        Assert.ThrowsExactly<ArgumentException>(()=>ChildEnvironmentBuilder.ValidateKeys(new Dictionary<string,string?>(normal)));
        await f.Preflight.Job.CancelAllAsync(TimeSpan.FromSeconds(5));
        await GitCommands.ValidateIdentityAsync(new ProcessRunner(),f.Context,stage,"https://github.com/fixture-user/repo.git",true,default);
        await GitCommands.FsckAsync(new ProcessRunner(),f.Context,stage,"https://github.com/fixture-user/repo.git",true,default);
        var environment=f.Context.EnvironmentFor("https://github.com/fixture-user/repo.git",stage,true);
        var request=new ProcessRequest(LocalGitRunner.Git.AbsolutePath,["-C",stage.Root,"fsck","--full","--no-dangling"],stage.Root,environment,TimeSpan.FromSeconds(10),ExpectedExecutableIdentity:LocalGitRunner.Git.Identity);
        using var arbitrary=OperationJob.Create();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>new ProcessRunner().RunAsync(request,arbitrary,null,default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>new ProcessRunner().RunAsync(request with{FilePath=@"C:\forged\git.exe"},f.Context.Recovery.Job,null,default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>new ProcessRunner().RunAsync(request with{Arguments=["-C",stage.Root,"fetch","origin"]},f.Context.Recovery.Job,null,default));
        var changed=new Dictionary<string,string?>(environment){["GIT_CONFIG_VALUE_0"]="C:\\untrusted-hooks"};
        var tagged=(RuntimeEnvironment)environment;
        var forged=new RuntimeEnvironment(changed,tagged.Owner,tagged.Authentication,tagged.AuthenticatedLogin,stage,f.Context.Recovery);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>new ProcessRunner().RunAsync(request with{Environment=forged},f.Context.Recovery.Job,null,default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>f.Session.DisposeAsync().AsTask());
        stage.Dispose();await f.Context.DisposeAsync();await f.Session.DisposeAsync();
        Assert.ThrowsExactly<ObjectDisposedException>(()=>f.Session.MintRecoveryLease());
        Assert.ThrowsExactly<ObjectDisposedException>(()=>ChildEnvironmentBuilder.ValidateKeys(environment));
    }
}
