using System.Security.Cryptography;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class RepositoryBackupServiceTests
{
    [TestMethod]
    [DataRow(96)][DataRow(97)][DataRow(98)][DataRow(99)][DataRow(100)]
    public async Task Legal_long_wiki_names_transfer_and_recover_with_canonical_sanitization(int length)
    {
        string name=new('r',length),endpoint="https://github.com/fixture-user/"+name+".wiki.git";
        var repository=PreflightFixture.Repository with{Name=name,NameWithOwner="fixture-user/"+name,Url="https://github.com/fixture-user/"+name};
        // Keep this endpoint-length regression inside the existing native path-length envelope.
        string fixtureRoot=Path.Combine(Path.GetTempPath(),"wiki-"+Guid.NewGuid().ToString("N")[..16]);
        await using var f=await MirrorFixture.CreateAsync(repository:repository,fixtureRoot:fixtureRoot);
        var paths=f.Context.Paths(f.Repository,true);
        var result=await f.Service.BackupWikiAsync(f.Repository,f.Context,f.RepositoryLock,default);
        Assert.AreEqual(0,result.WarningCount,string.Join(',',result.ErrorCodes));Assert.IsFalse(result.CoreFailed);
        Assert.AreEqual(MirrorSafeCopy.SafeConfig(endpoint),File.ReadAllText(Path.Combine(paths.Final,"config")));
        using var cancel=new CancellationTokenSource();
        var recovered=await new RepositoryBackupService(f.Runner,point=>{if(point==PromotionBoundary.AfterJournal)cancel.Cancel();})
            .BackupWikiAsync(f.Repository,f.Context,f.RepositoryLock,cancel.Token);
        Assert.IsTrue(recovered.Cancelled);Assert.IsFalse(recovered.Critical,string.Join(',',recovered.ErrorCodes));
        Assert.IsFalse(f.Context.ConsistencyPending);Assert.IsFalse(File.Exists(paths.Journal));
        Assert.IsTrue(f.Runner.Requests.Any(r=>r.WorkingDirectory==paths.Recovery));
        Assert.AreEqual(MirrorSafeCopy.SafeConfig(endpoint),File.ReadAllText(Path.Combine(paths.Final,"config")));
    }
    [TestMethod]
    public async Task Native_process_start_exception_after_staging_preserves_final_and_cleans_owned_copy()
    {
        await using var f=await MirrorFixture.CreateAsync();f.Seed();
        f.Runner.BeforeRun=_=>throw new System.ComponentModel.Win32Exception(5);
        try
        {
            var result=await f.Service.BackupAsync(f.Repository,f.Context,f.RepositoryLock,default);
            Assert.IsTrue(result.CoreFailed);Assert.IsFalse(result.Critical);
            Assert.IsFalse(Directory.Exists(f.Staging));Assert.HasCount(0,f.Context.OwnedStaging);
            Assert.AreEqual("old",File.ReadAllText(Path.Combine(f.Final,"marker")));
        }
        finally
        {
            f.Runner.BeforeRun=null;f.RepositoryLock.Dispose();
            await new MirrorPromotion(f.Runner).RecoverAllAsync(f.Context,default);
        }
    }
    [TestMethod]
    [DataRow("commondir")][DataRow("gitdir")][DataRow(".git")]
    public async Task Unsafe_source_starts_zero_processes_and_preserves_final(string fault)
    {
        await using var f=await MirrorFixture.CreateAsync();f.Seed();MirrorSafeCopyTests.Write(f.Final,fault,"outside");
        var result=await f.Service.BackupAsync(f.Repository,f.Context,f.RepositoryLock,default);
        Assert.IsTrue(result.CoreFailed);Assert.HasCount(0,f.Runner.Requests);Assert.IsFalse(Directory.Exists(f.Staging));
        Assert.AreEqual("old",File.ReadAllText(Path.Combine(f.Final,"marker")));
    }

    [TestMethod]
    [DataRow("not-found",1,0)][DataRow("other",0,1)][DataRow("not-found-wrong-endpoint",0,1)][DataRow("fetch",0,1)]
    public async Task Existing_wiki_fetch_classifies_only_precise_missing_and_retains_old_bytes(string fault,int skipped,int warnings)
    {
        await using var f=await MirrorFixture.CreateAsync();string final=f.Context.Paths(f.Repository,true).Final;
        AclPolicy.CreateRestrictedDirectory(final,f.Preflight.User);MirrorSafeCopyTests.Write(final,"marker","old wiki");
        MirrorSafeCopyTests.Write(final,"HEAD","ref: refs/heads/main\n");f.Runner.Fault=fault;
        var result=await f.Service.BackupWikiAsync(f.Repository,f.Context,f.RepositoryLock,default);
        Assert.IsFalse(result.CoreFailed);Assert.AreEqual(warnings,result.WarningCount);Assert.AreEqual(skipped,result.SkippedWikiCount);
        Assert.AreEqual("old wiki",File.ReadAllText(Path.Combine(final,"marker")));
        Assert.IsFalse(Directory.Exists(f.Context.Paths(f.Repository,true).Staging));Assert.HasCount(0,f.Context.OwnedStaging);
    }

    [TestMethod]
    public async Task Noncanonical_or_new_corrupt_lfs_bytes_cannot_promote()
    {
        await using var f=await MirrorFixture.CreateAsync();f.Seed();
        f.Runner.OnLfsFetch=path=>MirrorSafeCopyTests.Write(path,"lfs/objects/not-an-oid","corrupt");
        var result=await f.Service.BackupAsync(f.Repository,f.Context,f.RepositoryLock,default);
        Assert.IsTrue(result.CoreFailed);Assert.AreEqual("old",File.ReadAllText(Path.Combine(f.Final,"marker")));
    }
    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task Mirror_commands_use_canonical_endpoint_and_only_sanitized_staging(bool existing)
    {
        await using var f=await MirrorFixture.CreateAsync();
        if(existing) f.Seed();
        var result=await f.Service.BackupAsync(f.Repository,f.Context,f.RepositoryLock,default);
        Assert.IsFalse(result.CoreFailed,string.Join(',',result.ErrorCodes)); Assert.AreEqual(0,result.WarningCount);
        string[] actual=f.Runner.Requests.Select(r=>r.Arguments.Contains("rev-parse")?"identity":r.Arguments.Contains("clone")?"clone":r.FilePath.EndsWith("git-lfs.exe")?"lfs-"+r.Arguments[0]:r.Arguments.Contains("fetch")?"fetch":"fsck").ToArray();
        CollectionAssert.AreEqual(existing?new[]{"identity","fetch","lfs-fetch","lfs-fsck","fsck"}:new[]{"clone","identity","lfs-fetch","lfs-fsck","fsck"},actual);
        foreach(var request in f.Runner.Requests)
        {
            Assert.AreNotEqual(f.Final,request.WorkingDirectory); Assert.IsFalse(request.Arguments.Contains(f.Final));
            Assert.IsNotNull(request.ExpectedExecutableIdentity);
            if(request.Arguments.Contains("clone"))
            {
                CollectionAssert.AreEqual(new[]{"clone","--mirror","https://github.com/fixture-user/repo.git",f.Staging},request.Arguments.ToArray());
                Assert.IsFalse(request.Environment.ContainsKey("GIT_DIR"));
            }
            else { Assert.AreEqual(f.Staging,request.Environment["GIT_DIR"]); Assert.AreEqual(f.Staging,request.Environment["GIT_COMMON_DIR"]); }
        }
        Assert.IsTrue(Directory.Exists(f.Final)); Assert.IsFalse(Directory.Exists(f.Staging));
        Assert.DoesNotContain("malicious",File.ReadAllText(Path.Combine(f.Final,"config")));
    }

    [TestMethod]
    public async Task Raw_discovery_and_deleted_mappings_never_authorize_new_transfer()
    {
        await using var f=await MirrorFixture.CreateAsync();
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>f.Service.BackupAsync(PreflightFixture.Repository,f.Context,f.RepositoryLock,default));
        await using var deleted=await MirrorFixture.CreateAsync(deleted:true);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>deleted.Service.BackupAsync(deleted.Repository,deleted.Context,deleted.RepositoryLock,default));
        Assert.HasCount(0,f.Runner.Requests); Assert.HasCount(0,deleted.Runner.Requests);
    }

    [TestMethod]
    [DataRow("clone")][DataRow("identity")][DataRow("fetch")][DataRow("core-fsck")][DataRow("lfs-fsck")]
    public async Task Core_failure_preserves_previous_bytes_and_skips_wiki(string fault)
    {
        await using var f=await MirrorFixture.CreateAsync(); if(fault!="clone")f.Seed();
        f.Runner.Fault=fault;
        var result=await f.Service.BackupAsync(f.Repository,f.Context,f.RepositoryLock,default);
        Assert.IsTrue(result.CoreFailed); Assert.IsFalse(Directory.Exists(f.Staging));
        if(fault!="clone") Assert.AreEqual("old",File.ReadAllText(Path.Combine(f.Final,"marker")));
        int count=f.Runner.Requests.Count;
        var wiki=await f.Service.BackupWikiAsync(f.Repository,f.Context,f.RepositoryLock,default);
        Assert.HasCount(count,f.Runner.Requests); Assert.AreEqual(1,wiki.SkippedWikiCount);
    }

    [TestMethod]
    [DataRow(false,false,false)][DataRow(true,false,false)][DataRow(true,true,false)][DataRow(true,true,true)]
    public async Task Lfs_integrity_freezes_corruption_and_requires_exact_repair(bool corrupt,bool repair,bool fetchFails)
    {
        await using var f=await MirrorFixture.CreateAsync(); f.Seed();
        byte[] bytes="LFS-ORIGINAL"u8.ToArray(); string oid=Convert.ToHexStringLower(SHA256.HashData(bytes));
        string relative=Path.Combine("lfs","objects",oid[..2],oid[2..4],oid);
        MirrorSafeCopyTests.Write(f.Final,relative,corrupt?"LFS-CORRUPT!":"LFS-ORIGINAL");
        f.Runner.Fault=fetchFails||!corrupt?"lfs-fetch":"";
        if(repair) f.Runner.OnLfsFetch=path=>MirrorSafeCopyTests.Write(path,relative,"LFS-ORIGINAL");
        var result=await f.Service.BackupAsync(f.Repository,f.Context,f.RepositoryLock,default);
        bool failed=corrupt&&(!repair||fetchFails);
        Assert.AreEqual(failed,result.CoreFailed,string.Join(',',result.ErrorCodes));
        Assert.AreEqual(failed?0:1,result.WarningCount);
        Assert.AreEqual(failed?"LFS-CORRUPT!":"LFS-ORIGINAL",File.ReadAllText(Path.Combine(f.Final,relative)));
        if(corrupt&&!failed) CollectionAssert.Contains(result.ErrorCodes.ToArray(),"LFS_OBJECT_CORRUPTION_REPAIRED");
    }

    [TestMethod]
    [DataRow("not-found",1,0)][DataRow("other",0,1)][DataRow("",0,0)]
    public async Task Wiki_not_found_is_narrow_and_wiki_never_uses_lfs(string fault,int skipped,int warnings)
    {
        await using var f=await MirrorFixture.CreateAsync(); f.Runner.Fault=fault;
        var result=await f.Service.BackupWikiAsync(f.Repository,f.Context,f.RepositoryLock,default);
        Assert.AreEqual(skipped,result.SkippedWikiCount);Assert.AreEqual(warnings,result.WarningCount);Assert.IsFalse(result.CoreFailed);
        Assert.IsFalse(f.Runner.Requests.Any(x=>x.FilePath.EndsWith("git-lfs.exe")));
        Assert.IsTrue(f.Runner.Requests[0].Arguments.Contains("https://github.com/fixture-user/repo.wiki.git"));
    }
}

internal sealed class MirrorFixture : IAsyncDisposable
{
    internal PreflightFixture Preflight {get;}
    private MirrorFixture(string? backupRoot,string? fixtureRoot){Preflight=new(backupRoot,fixtureRoot);}
    internal PreflightSession Session=null!;
    internal OperationLockLease OwnerLock=null!,RepositoryLock=null!;
    internal BackupRunContext Context=null!;
    internal RepositoryDescriptor Repository=null!;
    internal MirrorRunner Runner {get;}=new();
    internal RepositoryBackupService Service=>new(Runner);
    internal string Final=>Path.Combine(Preflight.OwnerRoot,"mirrors","repo.git");
    internal string Staging=>Path.Combine(Preflight.OwnerRoot,"mirrors",".repo.git.staging-test");
    internal static async Task<MirrorFixture> CreateAsync(bool deleted=false,bool realGit=false,string? backupRoot=null,string? fixtureRoot=null,RepositoryDescriptor? repository=null)
    {
        repository??=PreflightFixture.Repository;
        var f=new MirrorFixture(backupRoot,fixtureRoot); f.Preflight.Repositories=[repository];
        if(realGit)f.Preflight.Tools=f.Preflight.Tools with{Git=LocalGitRunner.Git};
        f.Session=(await f.Preflight.Check()).LiveSession!;
        var mappings=RepositoryNameMapper.Reconcile(f.Preflight.OwnerRoot,deleted?[repository]:[],deleted?[]:[repository],[]);
        f.Repository=mappings.Single(); f.OwnerLock=OperationLocks.AcquireStorage(f.Preflight.OwnerRoot,"test");
        f.Context=BackupRunContext.Create(f.Session,f.OwnerLock,mappings,"test");
        f.RepositoryLock=OperationLocks.AcquireRepository(f.OwnerLock,f.Repository.LocalName,"test");
        return f;
    }
    internal void Seed()
    {
        AclPolicy.CreateRestrictedDirectory(Final,Preflight.User);
        MirrorSafeCopyTests.Write(Final,"HEAD","ref: refs/heads/main\n");
        MirrorSafeCopyTests.Write(Final,"config","[credential]\nhelper = !malicious\n");
        MirrorSafeCopyTests.Write(Final,"marker","old");
    }
    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync(); RepositoryLock.Dispose(); OwnerLock.Dispose(); await Session.DisposeAsync();
        ClearReadOnly(Preflight.Root.Path);Preflight.Dispose();
    }
    internal static void ClearReadOnly(string directory)
    {
        foreach(string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes=File.GetAttributes(entry);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("TEST_CLEANUP_REPARSE_REJECTED");
            if((attributes&FileAttributes.Directory)!=0)ClearReadOnly(entry);else File.SetAttributes(entry,attributes&~FileAttributes.ReadOnly);
        }
    }
}

internal sealed class MirrorRunner : IProcessRunner
{
    internal List<ProcessRequest> Requests {get;}=[];
    internal string Fault="";
    internal Action<string>? OnLfsFetch;
    internal Action<ProcessRequest>? BeforeRun;
    public Task<ProcessResult> RunAsync(ProcessRequest request,OperationJob job,IProgress<string>? progress,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Requests.Add(request);BeforeRun?.Invoke(request);
        bool clone=request.Arguments.Contains("clone"), identity=request.Arguments.Contains("rev-parse"), lfs=request.FilePath.EndsWith("git-lfs.exe");
        string step=clone?"clone":identity?"identity":lfs?"lfs-"+request.Arguments[0]:request.Arguments.Contains("fetch")?"fetch":"core-fsck";
        if((clone||step=="fetch")&&Fault is "not-found" or "other" or "not-found-wrong-endpoint")
        {
            progress?.Report(Fault=="not-found"?"remote: Repository not found.\nfatal: repository 'https://github.com/fixture-user/repo.wiki.git/' not found\n"
                :Fault=="not-found-wrong-endpoint"?"remote: Repository not found.\nfatal: repository 'https://github.com/fixture-user/other.wiki.git/' not found\n":"remote: unexpected Repository not found text\n");
            return Task.FromResult(new ProcessResult(128,false,false,[],[]));
        }
        if(step==Fault)
        {
            if(step=="lfs-fetch")progress?.Report("fatal: unable to access 'https://github.com/fixture-user/repo.git': The requested URL returned error: 503");
            return Task.FromResult(new ProcessResult(1,false,false,[],[]));
        }
        if(clone)
        {
            string staging=request.Arguments[^1]; AclPolicy.CreateRestrictedDirectory(staging,System.Security.Principal.WindowsIdentity.GetCurrent().User!,true);
            MirrorSafeCopyTests.Write(staging,"config","[credential]\nhelper = !malicious\n");
            MirrorSafeCopyTests.Write(staging,"HEAD","ref: refs/heads/main\n");
        }
        else
        {
            ChildEnvironmentBuilder.ValidateKeys(request.Environment);
            Assert.DoesNotContain("malicious",File.ReadAllText(Path.Combine(request.WorkingDirectory,"config")));
            Assert.AreEqual(request.WorkingDirectory,request.Environment["GIT_DIR"]);
            Assert.AreEqual(request.WorkingDirectory,request.Environment["GIT_COMMON_DIR"]);
        }
        if(identity)MirrorSafeCopyTests.Write(request.WorkingDirectory,Path.GetFileName(request.StandardOutputFile!),"true\n"+request.WorkingDirectory+"\n"+request.WorkingDirectory+"\n");
        if(step=="lfs-fetch")OnLfsFetch?.Invoke(request.WorkingDirectory);
        return Task.FromResult(new ProcessResult(0,false,false,[],[]));
    }
}
