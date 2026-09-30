using System.Text;

namespace GitHubBackup.App;

// Non-serializable path capability. Its deny-delete lease lives through every process.
internal sealed class StagingRepository : IDisposable
{
    private readonly PathLease lease;
    private readonly NativeFileIdentity identity;
    private readonly string endpoint;
    private bool disposed;
    internal string Root {get;}
    internal string Endpoint=>endpoint;
    private StagingRepository(string path,string endpoint)
    {
        Root=NativeFileSystem.CanonicalPath(path);this.endpoint=endpoint;
        lease=SummaryStore.RequirePrivateDirectory(Root);
        try { using var handle=MirrorSafeCopy.OpenPrivate(Root);identity=NativeFileSystem.Inspect(handle,Root,true);Revalidate(); }
        catch { lease.Dispose();throw; }
    }
    internal static StagingRepository Inspect(BackupRunContext context,RepositoryDescriptor repository,OperationLockLease repositoryLock,bool wiki,string? runId=null,bool recovery=false)
    {
        context.Require(repository,repositoryLock,false);
        MirrorPaths paths=context.Paths(repository,wiki,runId);
        string path=recovery?paths.Recovery:paths.Staging;
        MirrorSafeCopy.RequireChild(paths.Parent,path);
        return new(path,context.Endpoint(repository,wiki,recovery));
    }
    internal void Revalidate()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        using var root=MirrorSafeCopy.OpenPrivate(Root);
        if(NativeFileSystem.Inspect(root,Root,true)!=identity)throw new PathBoundaryException("STAGING_IDENTITY_CHANGED");
        MirrorSafeCopy.Audit(Root);MirrorSafeCopy.ValidateLayout(Root);
        using var config=MirrorSafeCopy.OpenPrivate(Path.Combine(Root,"config"),shareWrite:false);
        using var reader=new StreamReader(new FileStream(config,FileAccess.Read),new UTF8Encoding(false,true));
        if(reader.ReadToEnd()!=MirrorSafeCopy.SafeConfig(endpoint))throw new InvalidDataException("STAGING_CONFIG_CHANGED");
    }
    internal void ValidateEnvironment(IReadOnlyDictionary<string,string?> values)
    {
        Revalidate();
        if(!values.TryGetValue("GIT_DIR",out var git)||git!=Root||!values.TryGetValue("GIT_COMMON_DIR",out var common)||common!=Root
            ||!values.TryGetValue("GIT_IMPLICIT_WORK_TREE",out var implicitTree)||implicitTree!="0")
            throw new ArgumentException("STAGING_ENVIRONMENT_MISMATCH");
    }
    public void Dispose(){if(disposed)return;disposed=true;lease.Dispose();}
}
