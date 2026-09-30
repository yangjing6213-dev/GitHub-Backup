using System.Diagnostics;
using System.Text.Json;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class MirrorPromotionCrashProcessHarness
{
    [TestMethod]
    [DataRow(0)][DataRow(1)][DataRow(2)][DataRow(3)]
    public async Task Killed_transaction_recovers_deleted_repository_in_fresh_process(int boundary)
    {
        using var root=new StorageTestRoot();string safe=root.Child("private");AclPolicy.CreateRestrictedDirectory(safe,root.User);
        string spec=Path.Combine(safe,"spec.json"),signal=Path.Combine(safe,"boundary"),result=Path.Combine(safe,"result");
        File.WriteAllText(spec,JsonSerializer.Serialize(new CrashSpec(safe,boundary,"crash")));
        try
        {
            using(var child=StartWorker(spec))
            {
                Task<string> output=child.StandardOutput.ReadToEndAsync(),error=child.StandardError.ReadToEndAsync();
                var elapsed=Stopwatch.StartNew();
                while(!File.Exists(signal))
                {
                    if(child.HasExited)Assert.Fail("Crash worker exited early: "+await output+await error);
                    if(elapsed.Elapsed>TimeSpan.FromSeconds(40)){child.Kill(true);Assert.Fail("Crash worker did not reach journal boundary.");}
                    await Task.Delay(25);
                }
                child.Kill(entireProcessTree:true);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await Task.WhenAll(output,error).WaitAsync(TimeSpan.FromSeconds(10));
            }
            File.WriteAllText(spec,JsonSerializer.Serialize(new CrashSpec(safe,boundary,"recover")));
            using(var child=StartWorker(spec))
            {
                Task<string> output=child.StandardOutput.ReadToEndAsync(),error=child.StandardError.ReadToEndAsync();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
                Assert.AreEqual(0,child.ExitCode,await output+await error);
            }
            Assert.AreEqual("RECOVERED_DELETED_VALID_FINAL",File.ReadAllText(result));
            string parent=Path.Combine(safe,"backup","fixture-user","mirrors");
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(parent).Any(x=>Path.GetFileName(x).StartsWith(".repo.git.",StringComparison.Ordinal)));
        }
        finally{MirrorFixture.ClearReadOnly(root.Path);}
    }

    [TestMethod]
    public async Task CrashWorker()
    {
        string? path=Environment.GetEnvironmentVariable("GITHUB_BACKUP_TEST_CRASH_SPEC");if(path is null)return;
        var spec=JsonSerializer.Deserialize<CrashSpec>(File.ReadAllText(path))!;
        Assert.IsTrue(spec.Root.StartsWith(Path.GetTempPath(),StringComparison.OrdinalIgnoreCase));
        await using var f=await MirrorFixture.CreateAsync(deleted:spec.Mode=="recover",realGit:true,
            backupRoot:Path.Combine(spec.Root,"backup"),fixtureRoot:Path.Combine(spec.Root,"fixture-"+spec.Mode));
        string origin=Path.Combine(spec.Root,"origin.git");
        if(spec.Mode=="crash")
        {
            await LocalGitRunner.InitializeAsync(origin,f.Preflight.Environment);
            string oldOid=await LocalGitRunner.ObjectAsync(origin,"old",f.Preflight.Environment);
            await LocalGitRunner.LocalAsync(origin,["update-ref","refs/tags/value",oldOid],f.Preflight.Environment);
            var first=await new RepositoryBackupService(new LocalGitRunner(origin)).BackupAsync(f.Repository,f.Context,f.RepositoryLock,default);
            Assert.IsFalse(first.CoreFailed,string.Join(',',first.ErrorCodes));
            string newOid=await LocalGitRunner.ObjectAsync(origin,"new",f.Preflight.Environment);
            await LocalGitRunner.LocalAsync(origin,["update-ref","refs/tags/value",newOid],f.Preflight.Environment);
            File.WriteAllLines(Path.Combine(spec.Root,"expected-oids"),[oldOid,newOid]);
            await new RepositoryBackupService(new LocalGitRunner(origin),point=>
            {
                if((int)point!=spec.Boundary)return;
                using(var signal=new FileStream(Path.Combine(spec.Root,"boundary"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                {signal.Write("durable"u8);signal.Flush(true);}
                using var parked=new ManualResetEventSlim(false);parked.Wait();
            }).BackupAsync(f.Repository,f.Context,f.RepositoryLock,default);
            Assert.Fail("Parent must terminate this worker without managed cleanup.");
        }
        else
        {
            f.RepositoryLock.Dispose();
            var recovered=await new MirrorPromotion(new ProcessRunner()).RecoverAllAsync(f.Context,default);
            Assert.HasCount(0,recovered.ErrorCodes);Assert.AreEqual(1,recovered.RecoveredCount);
            using var repositoryLock=OperationLocks.AcquireRepository(f.OwnerLock,"repo","verify");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>f.Service.BackupAsync(f.Repository,f.Context,repositoryLock,default));
            string inspection=Path.Combine(Path.GetDirectoryName(f.Final)!,".inspection.git.staging-check");
            await MirrorSafeCopy.CopyAndSanitizeAsync(Path.GetDirectoryName(f.Final)!,f.Final,inspection,"https://github.com/fixture-user/repo.git",default);
            string refs=await LocalGitRunner.LocalAsync(inspection,["show-ref"],f.Preflight.Environment);
            string[] expected=File.ReadAllLines(Path.Combine(spec.Root,"expected-oids"));
            Assert.IsTrue(expected.Any(oid=>refs.Trim()==oid+" refs/tags/value"));
            await LocalGitRunner.LocalAsync(inspection,["fsck","--full","--no-dangling"],f.Preflight.Environment);
            MirrorSafeCopy.DeleteTree(inspection);
            File.WriteAllText(Path.Combine(spec.Root,"result"),"RECOVERED_DELETED_VALID_FINAL");
        }
    }
    internal static Process StartWorker(string spec)
    {
        var start=new ProcessStartInfo(@"C:\Program Files\dotnet\dotnet.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(spec)!};
        foreach(string argument in new[]{"vstest",typeof(MirrorPromotionCrashProcessHarness).Assembly.Location,"/Tests:GitHubBackup.App.Tests.MirrorPromotionCrashProcessHarness.CrashWorker","/logger:console;verbosity=minimal"})start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach(string key in new[]{"SystemRoot","WINDIR","TEMP","TMP","USERPROFILE","APPDATA","LOCALAPPDATA","ProgramFiles"})start.Environment[key]=Environment.GetEnvironmentVariable(key);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]="1";start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"]="false";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"]="1";start.Environment["GITHUB_BACKUP_TEST_CRASH_SPEC"]=spec;
        return Process.Start(start)!;
    }
    internal sealed record CrashSpec(string Root,int Boundary,string Mode);
}
