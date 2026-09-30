using System.Security.AccessControl;
using System.Text;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class MirrorSafeCopyTests
{
    [TestMethod]
    public void Wiki_suffix_does_not_relax_original_repository_name_limits()
    {
        string tooLong=new('r',101);
        Assert.ThrowsExactly<InvalidDataException>(()=>MirrorSafeCopy.SafeConfig("https://github.com/fixture-user/"+tooLong+".git"));
        Assert.ThrowsExactly<InvalidDataException>(()=>MirrorSafeCopy.SafeConfig("https://github.com/fixture-user/"+tooLong+".wiki.git"));
        StringAssert.Contains(MirrorSafeCopy.SafeConfig("https://github.com/fixture-user/.wiki.git"),"url = https://github.com/fixture-user/.wiki.git");
    }
    [TestMethod]
    public async Task Copy_removes_untrusted_configuration_and_preserves_ordinary_bytes()
    {
        using var root = new StorageTestRoot();
        string parent = root.Child("mirrors"); AclPolicy.CreateRestrictedDirectory(parent, root.User);
        string source = Path.Combine(parent,"repo.git"), staging = Path.Combine(parent,".repo.git.staging-test");
        AclPolicy.CreateRestrictedDirectory(source,root.User);
        Write(source,"config","[credential]\nhelper = !malicious\n[include]\npath = outside\n");
        Write(source,"hooks/pre-fetch","malicious"); Write(source,"objects/info/alternates","outside");
        Write(source,"objects/info/http-alternates","https://attacker.invalid/"); Write(source,".lfsconfig","malicious");
        Write(source,"HEAD","ref: refs/heads/main\n"); Write(source,"objects/preserved","exact bytes");
        await MirrorSafeCopy.CopyAndSanitizeAsync(parent,source,staging,"https://github.com/fixture-user/repo.git",default);
        Assert.AreEqual("exact bytes",File.ReadAllText(Path.Combine(staging,"objects/preserved")));
        Assert.IsFalse(Directory.Exists(Path.Combine(staging,"hooks")));
        Assert.IsFalse(File.Exists(Path.Combine(staging,"objects/info/alternates")));
        Assert.IsFalse(File.Exists(Path.Combine(staging,".lfsconfig")));
        string config = File.ReadAllText(Path.Combine(staging,"config"));
        Assert.DoesNotContain("helper",config); Assert.DoesNotContain("include",config);
        StringAssert.Contains(config,"https://github.com/fixture-user/repo.git/info/lfs");
        using var original = NativeFileSystem.Open(Path.Combine(source,"objects","preserved"));
        using var copy = NativeFileSystem.Open(Path.Combine(staging,"objects","preserved"));
        Assert.AreNotEqual(NativeFileSystem.Inspect(original,Path.Combine(source,"objects","preserved"),false),NativeFileSystem.Inspect(copy,Path.Combine(staging,"objects","preserved"),false));
        Assert.IsTrue(new SourceIntegrityAudit().ValidateExistingTrees(staging,[],true).Allowed);
    }

    [TestMethod]
    [DataRow("commondir")][DataRow("gitdir")][DataRow(".git")]
    public async Task Indirection_is_rejected_before_staging_exists(string name)
    {
        using var root = new StorageTestRoot(); string parent = root.Child("mirrors");
        AclPolicy.CreateRestrictedDirectory(parent,root.User); string source=Path.Combine(parent,"repo.git"), destination=Path.Combine(parent,".repo.git.staging-test");
        AclPolicy.CreateRestrictedDirectory(source,root.User); Write(source,name,"outside");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorSafeCopy.CopyAndSanitizeAsync(parent,source,destination,"https://github.com/fixture-user/repo.git",default));
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task Broad_acl_and_reparse_child_fail_without_following_external_tree()
    {
        using var root = new StorageTestRoot(); string parent=root.Child("mirrors"), source=Path.Combine(parent,"repo.git"), destination=Path.Combine(parent,".repo.git.staging-test");
        AclPolicy.CreateRestrictedDirectory(source,root.User); string outside=root.Child("outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside,"sentinel"),"unchanged"); string link=Path.Combine(source,"objects"); StorageTestRoot.CreateJunction(link,outside);
        try { await Assert.ThrowsExactlyAsync<IOException>(() => MirrorSafeCopy.CopyAndSanitizeAsync(parent,source,destination,"https://github.com/fixture-user/repo.git",default)); }
        finally { Directory.Delete(link); }
        Assert.AreEqual("unchanged",File.ReadAllText(Path.Combine(outside,"sentinel"))); Assert.IsFalse(Directory.Exists(destination));
        StorageTestRoot.Grant(source,FileSystemRights.Write);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => MirrorSafeCopy.CopyAndSanitizeAsync(parent,source,destination,"https://github.com/fixture-user/repo.git",default));
        Assert.IsFalse(Directory.Exists(destination));
    }

    internal static void Write(string root,string relative,string text)
    {
        string path=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));
        AclPolicy.CreateRestrictedDirectory(Path.GetDirectoryName(path)!,System.Security.Principal.WindowsIdentity.GetCurrent().User!);
        using var file=AclPolicy.CreateRestrictedFile(path,System.Security.Principal.WindowsIdentity.GetCurrent().User!); file.Write(Encoding.UTF8.GetBytes(text));
    }
}
