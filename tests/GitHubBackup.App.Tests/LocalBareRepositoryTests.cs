using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class LocalBareRepositoryTests
{
    [TestMethod]
    public async Task Real_local_clone_update_and_prune_preserve_refs_and_pass_fsck()
    {
        await using var f=await MirrorFixture.CreateAsync(realGit:true);
        string origin=f.Preflight.Root.Child("origin.git");await LocalGitRunner.InitializeAsync(origin,f.Preflight.Environment);
        string first=await LocalGitRunner.ObjectAsync(origin,"first",f.Preflight.Environment);
        await LocalGitRunner.LocalAsync(origin,["update-ref","refs/tags/keep",first],f.Preflight.Environment);
        await LocalGitRunner.LocalAsync(origin,["update-ref","refs/tags/remove",first],f.Preflight.Environment);
        var runner=new LocalGitRunner(origin);
        var result=await new RepositoryBackupService(runner).BackupAsync(f.Repository,f.Context,f.RepositoryLock,default);
        Assert.IsFalse(result.CoreFailed,string.Join(',',result.ErrorCodes));
        string second=await LocalGitRunner.ObjectAsync(origin,"second",f.Preflight.Environment);
        await LocalGitRunner.LocalAsync(origin,["update-ref","refs/tags/keep",second],f.Preflight.Environment);
        await LocalGitRunner.LocalAsync(origin,["update-ref","-d","refs/tags/remove"],f.Preflight.Environment);
        await LocalGitRunner.LocalAsync(origin,["update-ref","refs/tags/added",second],f.Preflight.Environment);
        result=await new RepositoryBackupService(runner).BackupAsync(f.Repository,f.Context,f.RepositoryLock,default);
        Assert.IsFalse(result.CoreFailed,string.Join(',',result.ErrorCodes));
        // Application-copy inspection, never Git on final.
        string inspect=Path.Combine(Path.GetDirectoryName(f.Final)!,".inspect.git.staging-check");
        await MirrorSafeCopy.CopyAndSanitizeAsync(Path.GetDirectoryName(f.Final)!,f.Final,inspect,"https://github.com/fixture-user/repo.git",default);
        string refs=await LocalGitRunner.LocalAsync(inspect,["show-ref"],f.Preflight.Environment);
        Assert.Contains(second+" refs/tags/keep",refs);Assert.Contains(second+" refs/tags/added",refs);Assert.DoesNotContain("refs/tags/remove",refs);
        await LocalGitRunner.LocalAsync(inspect,["fsck","--full","--no-dangling"],f.Preflight.Environment);
    }
}

internal sealed class LocalGitRunner(string origin):IProcessRunner
{
    internal static ToolDetection Git {get;}=new(@"C:\Program Files\Git\cmd\git.exe",ExecutableTrust.CaptureTrustedIdentity(@"C:\Program Files\Git\cmd\git.exe"),"local installed Git",true);
    private readonly ProcessRunner runner=new();
    public async Task<ProcessResult> RunAsync(ProcessRequest request,OperationJob job,IProgress<string>? progress,CancellationToken token)
    {
        if(request.FilePath.EndsWith("git-lfs.exe",StringComparison.OrdinalIgnoreCase))return new(0,false,false,[],[]);
        if(request.Arguments.Contains("clone")||request.Arguments.Contains("fetch"))
        {
            Assert.IsTrue(origin.StartsWith(Path.GetTempPath(),StringComparison.OrdinalIgnoreCase));
            bool clone=request.Arguments.Contains("clone");
            var arguments=request.Arguments.Select(a=>a=="https://github.com/fixture-user/repo.git"||(!clone&&a=="origin")?origin:a).ToList();
            arguments.InsertRange(0,["-c","protocol.file.allow=always"]);
            if(clone)arguments.Insert(4,"--no-hardlinks");
            request=request with{Arguments=arguments};
        }
        return await runner.RunAsync(request,job,progress,token);
    }
    internal static async Task InitializeAsync(string origin,IReadOnlyDictionary<string,string?> environment)
    {
        AclPolicy.CreateRestrictedDirectory(origin,System.Security.Principal.WindowsIdentity.GetCurrent().User!);
        await LocalAsync(origin,["init","--bare","."],environment);
    }
    internal static async Task<string> ObjectAsync(string origin,string text,IReadOnlyDictionary<string,string?> environment)
    {
        string blob=Path.Combine(origin,"fixture-blob");File.WriteAllText(blob,text);
        return (await LocalAsync(origin,["hash-object","-w",blob],environment)).Trim();
    }
    internal static async Task<string> LocalAsync(string directory,IReadOnlyList<string> arguments,IReadOnlyDictionary<string,string?> environment)
    {
        using var job=OperationJob.Create();await using var runtime=await GitRuntimeContext.CreatePublicProbeAsync(environment,default);
        string output=Path.Combine(directory,".test-output-"+Guid.NewGuid().ToString("N"));
        var request=new ProcessRequest(Git.AbsolutePath,arguments,directory,runtime.Environment,TimeSpan.FromSeconds(15),ProcessOutputMode.CapturedFile,output,65536,Git.Identity);
        var result=await new ProcessRunner().RunAsync(request,job,null,default);
        Assert.AreEqual(0,result.ExitCode,string.Join("\n",result.StandardError));
        string text=File.ReadAllText(output);File.Delete(output);return text;
    }
}
