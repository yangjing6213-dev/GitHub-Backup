using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;

namespace GitHubBackup.App;

internal sealed record RepositoryStepResult(bool CoreFailed,int WarningCount,int SkippedWikiCount,IReadOnlyList<string> ErrorCodes,bool Critical=false,bool Cancelled=false);

internal sealed class BackupRunContext : IAsyncDisposable
{
    private readonly PreflightSession session;
    internal OperationLockLease Owner {get;}
    internal IReadOnlyList<RepositoryDescriptor> Repositories {get;}
    internal string RunId {get;}
    internal string OwnerRoot=>Owner.RequireOwnerRoot();
    internal ToolInventory Tools=>session.Snapshot.Tools;
    internal OperationJob Job=>session.Job;
    internal RecoveryLease Recovery {get;}
    internal HashSet<string> FailedRepositories {get;}=[];
    internal Dictionary<string,(RepositoryDescriptor Repository,bool Wiki)> OwnedStaging {get;}=[];
    internal bool ConsistencyPending {get;set;}
    private bool disposed;
    private BackupRunContext(PreflightSession session,OperationLockLease owner,IReadOnlyList<RepositoryDescriptor> mappings,string runId)
    {this.session=session;Owner=owner;Repositories=mappings;RunId=runId;Recovery=session.MintRecoveryLease();}
    internal static BackupRunContext Create(PreflightSession session,OperationLockLease owner,IReadOnlyList<RepositoryDescriptor> mappings,string runId)
    {
        session.Revalidate();RepositoryNameMapper.RequireReconciled(owner.RequireOwnerRoot(),mappings);RequireRunId(runId);
        if(!session.Snapshot.Report.CanStartBackup)throw new InvalidOperationException("BACKUP_SESSION_INVALID");
        if(session.Snapshot.ChildEnvironment is not RuntimeEnvironment environment
            ||!string.Equals(environment.AuthenticatedLogin,Path.GetFileName(owner.RequireOwnerRoot()),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("BACKUP_OWNER_SESSION_MISMATCH");
        return new(session,owner,mappings,runId);
    }
    internal static void RequireRunId(string runId)
    {if(string.IsNullOrEmpty(runId)||runId.Length>100||runId.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('-' or '_')))throw new ArgumentException("BACKUP_RUN_ID_INVALID");}
    internal void Require(RepositoryDescriptor repository,OperationLockLease lease,bool transfer)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!Repositories.Any(r=>ReferenceEquals(r,repository)))throw new ArgumentException("RECONCILED_REPOSITORY_REQUIRED");
        lease.RequireRepository(Owner,repository.LocalName);
        if(transfer)
        {
            session.Revalidate();
            if(ConsistencyPending)throw new InvalidOperationException("PROMOTION_RECOVERY_REQUIRED");
            if(repository.RemoteState!="active")throw new InvalidOperationException("ACTIVE_REPOSITORY_REQUIRED");
        }
        else Recovery.Revalidate();
    }
    internal string Endpoint(RepositoryDescriptor repository,bool wiki,bool recovery=false)
    {
        string owner=Path.GetFileName(OwnerRoot);
        string[] name=repository.NameWithOwner.Split('/');
        var endpoint=RepositoryEndpointPolicy.ValidateAndCreate(owner,name.Length==2?name[0]:"",repository.Name,repository.NameWithOwner,repository.Url);
        if(!endpoint.Allowed)
        {
            if(recovery)return "https://github.com/"+owner+"/recovery.git";
            throw new InvalidDataException("REPOSITORY_ENDPOINT_INVALID");
        }
        return wiki?endpoint.CanonicalWikiUrl:endpoint.CanonicalCloneUrl;
    }
    internal MirrorPaths Paths(RepositoryDescriptor repository,bool wiki,string? runId=null)=>new(OwnerRoot,repository.LocalName,wiki,runId??RunId);
    internal IReadOnlyDictionary<string,string?> EnvironmentFor(string endpoint,StagingRepository? staging,bool recovery=false)
    {
        var environment=recovery?Recovery.Environment:session.CreateEnvironment();
        return BuildGitEnvironment(environment,session.Snapshot.EmptyHooksDirectory!,endpoint,staging,recovery?Recovery:null);
    }
    internal static IReadOnlyDictionary<string,string?> BuildGitEnvironment(IReadOnlyDictionary<string,string?> environment,string hooks,string endpoint,StagingRepository? staging,RecoveryLease? recovery)
    {
        string proxy=environment.TryGetValue("HTTPS_PROXY",out var https)?https??"":environment.TryGetValue("ALL_PROXY",out var all)?all??"":"";
        var config=new List<(string Key,string Value)>
        {
            ("core.hooksPath",hooks), ("protocol.allow","never"),("protocol.https.allow","always"),
            ("protocol.ext.allow","never"),("http.proxy",proxy),("http.sslVerify","true"),("http.followRedirects","false"),
            ("fetch.recurseSubmodules","false"),("submodule.recurse","false"),("lfs.url",endpoint+"/info/lfs"),
            ("remote.origin.lfsurl",endpoint+"/info/lfs"),("lfs.fetchinclude",""),("lfs.fetchexclude","")
        };
        if(staging is not null)config.Add(("lfs.storage",Path.Combine(staging.Root,"lfs")));
        var additions=new Dictionary<string,string?> { ["GIT_CONFIG_COUNT"]=config.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),["LC_ALL"]="C" };
        for(int i=0;i<config.Count;i++){additions[$"GIT_CONFIG_KEY_{i}"]=config[i].Key;additions[$"GIT_CONFIG_VALUE_{i}"]=config[i].Value;}
        var result=ChildEnvironmentBuilder.Build(environment,additions);
        return staging is null?result:ChildEnvironmentBuilder.PinStaging(result,staging,recovery);
    }
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;
        if(ConsistencyPending||OwnedStaging.Count!=0)throw new IOException("PROMOTION_RECOVERY_PENDING");
        await Recovery.DisposeAsync().ConfigureAwait(false);disposed=true;
    }
}

internal sealed record MirrorPaths
{
    internal string Parent {get;}
    internal string Final {get;}
    internal string Staging {get;}
    internal string Previous {get;}
    internal string Journal {get;}
    internal string Recovery {get;}
    internal string Discard {get;}
    internal string RunId {get;}
    internal MirrorPaths(string owner,string localName,bool wiki,string runId)
    {
        BackupRunContext.RequireRunId(runId);
        if(!RepositoryNameMapper.IsSafeLocalName(localName))throw new ArgumentException("MIRROR_MAPPING_INVALID");
        RunId=runId;Parent=Path.Combine(owner,wiki?"wikis":"mirrors");string name=localName+(wiki?".wiki.git":".git");
        Final=Path.Combine(Parent,name);string prefix=Path.Combine(Parent,"."+name);
        Staging=prefix+".staging-"+runId;Previous=prefix+".previous-"+runId;Journal=prefix+".swap-"+runId+".json";
        Recovery=prefix+".recovery-"+runId;Discard=prefix+".discard-"+runId;
    }
}

internal sealed class RepositoryBackupService(IProcessRunner runner,Action<PromotionBoundary>? boundary=null)
{
    internal Task<RepositoryStepResult> BackupAsync(RepositoryDescriptor repository,BackupRunContext context,OperationLockLease repositoryLock,CancellationToken token)
        =>BackupCoreAsync(repository,context,repositoryLock,false,token);
    internal Task<RepositoryStepResult> BackupWikiAsync(RepositoryDescriptor repository,BackupRunContext context,OperationLockLease repositoryLock,CancellationToken token)
        =>BackupCoreAsync(repository,context,repositoryLock,true,token);
    private async Task<RepositoryStepResult> BackupCoreAsync(RepositoryDescriptor repository,BackupRunContext context,OperationLockLease repositoryLock,bool wiki,CancellationToken token)
    {
        context.Require(repository,repositoryLock,true);
        if(wiki&&(context.FailedRepositories.Contains(repository.LocalName)||!repository.HasWikiEnabled))return new(false,0,1,[]);
        var paths=context.Paths(repository,wiki);string endpoint=context.Endpoint(repository,wiki);
        var warnings=new List<string>();bool owned=false;
        try
        {
            token.ThrowIfCancellationRequested();
            AclPolicy.CreateRestrictedDirectory(paths.Parent,WindowsIdentity.GetCurrent().User!);
            using var parent=SummaryStore.RequirePrivateDirectory(paths.Parent);
            if(Directory.EnumerateFileSystemEntries(paths.Parent,"."+Path.GetFileName(paths.Final)+".swap-*.json").Any())
                throw new IOException("PROMOTION_RECOVERY_REQUIRED");
            if(File.Exists(paths.Staging)||Directory.Exists(paths.Staging))throw new IOException("STAGING_ALREADY_EXISTS");
            bool existing=Directory.Exists(paths.Final);
            owned=true;context.OwnedStaging.Add(paths.Staging,(repository,wiki));
            if(existing)
            {
                await MirrorSafeCopy.CopyAndSanitizeAsync(paths.Parent,paths.Final,paths.Staging,endpoint,token).ConfigureAwait(false);
            }
            else
            {
                var clone=await GitCommands.RunAsync(runner,context,null,endpoint,["clone","--mirror",endpoint,paths.Staging],paths.Parent,false,false,true,token).ConfigureAwait(false);
                if(!clone.Success)
                {
                    if(wiki&&WikiFailureClassifier.IsMissing(clone,endpoint,paths.Staging)){Cleanup();return new(false,0,1,[]);}
                    throw new IOException("MIRROR_CLONE_FAILED");
                }
                await MirrorSafeCopy.SanitizeAsync(paths.Staging,endpoint,token).ConfigureAwait(false);
            }
            bool missingWiki=false;
            using(var staging=StagingRepository.Inspect(context,repository,repositoryLock,wiki))
            {
                await GitCommands.ValidateIdentityAsync(runner,context,staging,endpoint,false,token).ConfigureAwait(false);
                if(existing)
                {
                    var fetch=await GitCommands.RunAsync(runner,context,staging,endpoint,["-C",staging.Root,"fetch","--atomic","--prune","origin","+refs/*:refs/*"],staging.Root,false,false,true,token).ConfigureAwait(false);
                    if(!fetch.Success)
                    {
                        missingWiki=wiki&&WikiFailureClassifier.IsMissing(fetch,endpoint,paths.Staging);
                        if(!missingWiki)throw new IOException("MIRROR_FETCH_FAILED");
                    }
                }
                if(!missingWiki)
                {
                    if(!wiki)await LfsAsync(context,staging,endpoint,warnings,token).ConfigureAwait(false);
                    await GitCommands.FsckAsync(runner,context,staging,endpoint,false,token).ConfigureAwait(false);
                }
            }
            if(missingWiki){Cleanup();return new(false,0,1,[]);}
            var promotion=await new MirrorPromotion(runner,boundary).PromoteAsync(context,repository,repositoryLock,wiki,token).ConfigureAwait(false);
            if(promotion.Critical)return new(true,0,0,[promotion.ErrorCode],true,promotion.Cancelled);
            context.OwnedStaging.Remove(paths.Staging);
            if(promotion.Cancelled)return new(false,0,0,[promotion.ErrorCode],false,true);
            if(!promotion.Completed)throw new IOException(promotion.ErrorCode);
            return new(false,warnings.Count,0,warnings.AsReadOnly());
        }
        catch(OperationCanceledException)
        {
            try{Cleanup();return new(false,0,0,["BACKUP_CANCELLED"],false,true);}
            catch(Exception cleanup)when(cleanup is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {context.ConsistencyPending=true;return new(true,0,0,["STAGING_CLEANUP_FAILED"],true,true);}
        }
        catch(Exception ex) when(ex is not AtomicFileCleanupException && ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if(context.ConsistencyPending)return new(true,0,0,["PROMOTION_RECOVERY_REQUIRED"],true);
            try{Cleanup();}catch(Exception cleanup) when(cleanup is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {context.ConsistencyPending=true;return new(true,0,0,["STAGING_CLEANUP_FAILED"],true);}
            if(!wiki)context.FailedRepositories.Add(repository.LocalName);
            string code=ex.Message is "LFS_OBJECT_INTEGRITY_UNRESOLVED" or "MIRROR_CLONE_FAILED" or "MIRROR_FETCH_FAILED" or "MIRROR_FSCK_FAILED" or "MIRROR_IDENTITY_INVALID"?ex.Message:"MIRROR_BACKUP_FAILED";
            return new(!wiki,wiki?1:0,0,[code]);
        }
        void Cleanup()
        {
            if(!owned)return;
            if(Directory.Exists(paths.Staging))MirrorSafeCopy.DeleteTree(paths.Staging);
            context.OwnedStaging.Remove(paths.Staging);
        }
    }
    private async Task LfsAsync(BackupRunContext context,StagingRepository staging,string endpoint,List<string> warnings,CancellationToken token)
    {
        var before=await LfsObjects.ScanAsync(staging.Root,token).ConfigureAwait(false);
        string[] corrupt=before.Where(x=>!x.Value).Select(x=>x.Key).ToArray();
        foreach(string oid in corrupt)MirrorSafeCopy.DeleteFile(LfsObjects.PathFor(staging.Root,oid));
        var fetch=await GitCommands.RunAsync(runner,context,staging,endpoint,["fetch","--all"],staging.Root,true,false,true,token).ConfigureAwait(false);
        bool lfsOk=true;
        if(fetch.Success)lfsOk=(await GitCommands.RunAsync(runner,context,staging,endpoint,["fsck","--objects"],staging.Root,true,false,false,token).ConfigureAwait(false)).Success;
        var after=await LfsObjects.ScanAsync(staging.Root,token).ConfigureAwait(false);
        if(!lfsOk||after.Values.Any(valid=>!valid)||corrupt.Any(oid=>!after.TryGetValue(oid,out bool valid)||!valid)||(!fetch.Success&&corrupt.Length!=0))
            throw new IOException("LFS_OBJECT_INTEGRITY_UNRESOLVED");
        if(!fetch.Success)
        {
            if(fetch.Failure is not (NetworkFailureKind.Timeout or NetworkFailureKind.ConnectionRefused or NetworkFailureKind.ConnectionReset or NetworkFailureKind.Http5xx))
                throw new IOException("LFS_OBJECT_INTEGRITY_UNRESOLVED");
            warnings.Add("LFS_FETCH_NETWORK_FAILED");
        }
        if(corrupt.Length!=0)warnings.Add("LFS_OBJECT_CORRUPTION_REPAIRED");
    }
}

internal static class LfsObjects
{
    internal static string PathFor(string staging,string oid)=>Path.Combine(staging,"lfs","objects",oid[..2],oid[2..4],oid);
    internal static async Task<Dictionary<string,bool>> ScanAsync(string staging,CancellationToken token)
    {
        var result=new Dictionary<string,bool>(StringComparer.Ordinal);string root=Path.Combine(staging,"lfs","objects");
        if(!Directory.Exists(root))return result;
        MirrorSafeCopy.Audit(root);using var lease=SummaryStore.RequirePrivateDirectory(root);
        await Walk(root,0).ConfigureAwait(false);return result;
        async Task Walk(string path,int depth)
        {
            using var directory=SummaryStore.RequirePrivateDirectory(path);
            foreach(string entry in Directory.EnumerateFileSystemEntries(path))
            {
                token.ThrowIfCancellationRequested();using var handle=MirrorSafeCopy.OpenPrivate(entry,shareWrite:false);
                string name=Path.GetFileName(entry);bool dir=(File.GetAttributes(entry)&FileAttributes.Directory)!=0;
                if(dir)
                {
                    if(depth>=2||name.Length!=2||name.Any(c=>!char.IsAsciiHexDigitLower(c)))throw new IOException("LFS_OBJECT_INTEGRITY_UNRESOLVED");
                    await Walk(entry,depth+1).ConfigureAwait(false);
                }
                else
                {
                    if(depth!=2||name.Length!=64||name.Any(c=>!char.IsAsciiHexDigitLower(c))||entry!=PathFor(staging,name))throw new IOException("LFS_OBJECT_INTEGRITY_UNRESOLVED");
                    using var input=new FileStream(handle,FileAccess.Read);
                    result.Add(name,Convert.ToHexStringLower(await SHA256.HashDataAsync(input,token).ConfigureAwait(false))==name);
                }
            }
        }
    }
}

internal sealed record GitCommandResult(bool Success,NetworkFailureKind Failure,int? ExitCode,string Diagnostic);
internal static class GitCommands
{
    internal static async Task<GitCommandResult> RunAsync(IProcessRunner runner,BackupRunContext context,StagingRepository? staging,string endpoint,IReadOnlyList<string> arguments,string working,bool lfs,bool recovery,bool retry,CancellationToken token,string? output=null)
    {
        for(int attempt=1;;attempt++)
        {
            token.ThrowIfCancellationRequested();staging?.Revalidate();
            ToolDetection tool=(lfs?context.Tools.GitLfs:context.Tools.Git)!;
            var request=new ProcessRequest(tool.AbsolutePath,arguments,working,context.EnvironmentFor(endpoint,staging,recovery),TimeSpan.FromSeconds(recovery?30:600),
                output is null?ProcessOutputMode.EphemeralText:ProcessOutputMode.CapturedFile,output,65536,tool.Identity,true);
            using var diagnostic=new GitDiagnostic();
            if(recovery)context.Recovery.ValidateRequest(request,context.Recovery.Job);
            var result=await runner.RunAsync(request,recovery?context.Recovery.Job:context.Job,diagnostic,token).ConfigureAwait(false);
            if(result.Cancelled)throw new OperationCanceledException(token);
            token.ThrowIfCancellationRequested();
            string text=diagnostic.Text;NetworkFailureKind kind=result.TimedOut?NetworkFailureKind.Timeout:NetworkProbe.Classify(text);
            if(result.ExitCode==0&&!result.TimedOut)return new(true,NetworkFailureKind.None,result.ExitCode,"");
            if(!retry||!RetryPolicy.ShouldRetry(kind,attempt))return new(false,kind,result.ExitCode,text);
            await Task.Delay(TimeSpan.FromMilliseconds(250*attempt),token).ConfigureAwait(false);
        }
    }
    internal static async Task ValidateIdentityAsync(IProcessRunner runner,BackupRunContext context,StagingRepository staging,string endpoint,bool recovery,CancellationToken token)
    {
        string output=Path.Combine(staging.Root,".identity-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            var result=await RunAsync(runner,context,staging,endpoint,["-C",staging.Root,"rev-parse","--is-bare-repository","--git-dir","--git-common-dir"],staging.Root,false,recovery,false,token,output).ConfigureAwait(false);
            if(!result.Success)throw new IOException("MIRROR_IDENTITY_INVALID");
            using var handle=MirrorSafeCopy.OpenPrivate(output,shareWrite:false);
            using var reader=new StreamReader(new FileStream(handle,FileAccess.Read),new UTF8Encoding(false,true));
            string[] lines=(await reader.ReadToEndAsync(token).ConfigureAwait(false)).TrimEnd('\r','\n').Split('\n').Select(x=>x.TrimEnd('\r')).ToArray();
            if(lines.Length!=3||lines[0]!="true"||lines.Skip(1).Any(line=>!Path.IsPathFullyQualified(line)||!string.Equals(Path.GetFullPath(line.Replace('/',Path.DirectorySeparatorChar)),staging.Root,StringComparison.OrdinalIgnoreCase)))
                throw new IOException("MIRROR_IDENTITY_INVALID");
        }
        finally{if(File.Exists(output))MirrorSafeCopy.DeleteFile(output);}
    }
    internal static async Task FsckAsync(IProcessRunner runner,BackupRunContext context,StagingRepository staging,string endpoint,bool recovery,CancellationToken token)
    {
        if(!(await RunAsync(runner,context,staging,endpoint,["-C",staging.Root,"fsck","--full","--no-dangling"],staging.Root,false,recovery,false,token).ConfigureAwait(false)).Success)
            throw new IOException("MIRROR_FSCK_FAILED");
    }
    private sealed class GitDiagnostic:IProgress<string>,IDisposable
    {
        private readonly StringBuilder text=new();private bool overflow;
        public void Report(string value){if(overflow)return;if(text.Length+value.Length>8192){text.Clear();overflow=true;}else text.Append(value);}
        internal string Text=>overflow?"":text.ToString();
        public void Dispose()=>text.Clear();
    }
}

internal static class WikiFailureClassifier
{
    internal static bool IsMissing(GitCommandResult result,string endpoint,string staging)
    {
        string diagnostic=result.Diagnostic.Replace("\r\n","\n",StringComparison.Ordinal).TrimEnd('\n');
        string preamble="Cloning into bare repository '"+staging+"'...\n";
        if(diagnostic.StartsWith(preamble,StringComparison.Ordinal))diagnostic=diagnostic[preamble.Length..];
        return result.ExitCode==128&&diagnostic=="remote: Repository not found.\nfatal: repository '"+endpoint+"/' not found";
    }
}
