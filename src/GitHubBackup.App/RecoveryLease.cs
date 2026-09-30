namespace GitHubBackup.App;

// Minted while the original job is live. The fresh job can only run bounded local
// identity/fsck requests using the original session's Git and runtime capability.
internal sealed class RecoveryLease : IAsyncDisposable
{
    private readonly PreflightSession session;
    private readonly GitRuntimeContext runtime;
    private readonly ToolDetection git;
    private bool disposed;
    internal OperationJob Job {get;}=OperationJob.Create();
    internal IReadOnlyDictionary<string,string?> Environment {get;}
    internal RecoveryLease(PreflightSession session,GitRuntimeContext runtime,OperationJob normal,ToolDetection git,IReadOnlyDictionary<string,string?> environment)
    {
        this.session=session;this.runtime=runtime;this.git=git;Environment=environment;
        runtime.RegisterRecovery(this,normal);
    }
    internal void Revalidate(){ObjectDisposedException.ThrowIf(disposed,this);session.RevalidateRecovery(this);}
    internal void ValidateRequest(ProcessRequest request,OperationJob job)
    {
        Revalidate();
        if(!ReferenceEquals(Job,job)||request.FilePath!=git.AbsolutePath||request.ExpectedExecutableIdentity!=git.Identity
            ||request.Timeout>TimeSpan.FromSeconds(30)||request.Environment is not RuntimeEnvironment e
            ||!ReferenceEquals(e.Owner,runtime)||!ReferenceEquals(e.Recovery,this)||e.Staging is null
            ||request.WorkingDirectory!=e.Staging.Root
            ||(!request.Arguments.SequenceEqual(new[]{"-C",e.Staging.Root,"rev-parse","--is-bare-repository","--git-dir","--git-common-dir"})
                &&!request.Arguments.SequenceEqual(new[]{"-C",e.Staging.Root,"fsck","--full","--no-dangling"})))
            throw new InvalidOperationException("RECOVERY_REQUEST_REJECTED");
        var expected=BackupRunContext.BuildGitEnvironment(Environment,session.Snapshot.EmptyHooksDirectory!,e.Staging.Endpoint,e.Staging,this);
        if(request.Environment.Count!=expected.Count||expected.Any(pair=>!request.Environment.TryGetValue(pair.Key,out var value)||value!=pair.Value))
            throw new InvalidOperationException("RECOVERY_ENVIRONMENT_MISMATCH");
        ChildEnvironmentBuilder.ValidateKeys(request.Environment);
    }
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;
        await Job.CancelAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if(Job.ActiveLeaseCount!=0)throw new IOException("RECOVERY_CLEANUP_PENDING");
        runtime.ReleaseRecovery(this);session.ReleaseRecovery(this);disposed=true;Job.Dispose();
    }
}
