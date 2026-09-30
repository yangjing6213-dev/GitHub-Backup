using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

internal static class MirrorSafeCopy
{
    internal static async Task CopyAndSanitizeAsync(string expectedParent,string source,string staging,string endpoint,CancellationToken token)
    {
        RequireChild(expectedParent,source); RequireChild(expectedParent,staging);
        using var parent=SummaryStore.RequirePrivateDirectory(expectedParent);
        using var sourceLease=SummaryStore.RequirePrivateDirectory(source);
        Audit(source); ValidateLayout(source); _=SafeConfig(endpoint);
        token.ThrowIfCancellationRequested();
        AclPolicy.CreateRestrictedDirectory(staging,WindowsIdentity.GetCurrent().User!,requireNew:true);
        try
        {
            await CopyTree(source,staging,"",token).ConfigureAwait(false);
            await SanitizeAsync(staging,endpoint,token).ConfigureAwait(false);
        }
        catch { DeleteTree(staging); throw; }
    }

    internal static void RequireChild(string parent,string path)
    {
        if (!string.Equals(Path.GetDirectoryName(NativeFileSystem.CanonicalPath(path)),NativeFileSystem.CanonicalPath(parent),StringComparison.OrdinalIgnoreCase))
            throw new PathBoundaryException("MIRROR_PATH_OUTSIDE_PARENT");
    }

    internal static void Audit(string root)
    {
        var result=new SourceIntegrityAudit().ValidateExistingTrees(root,[],true);
        if (!result.Allowed) throw new IOException(result.UnsafePaths[0].ErrorCode);
    }

    internal static void ValidateLayout(string root)
    {
        foreach(string entry in Directory.EnumerateFileSystemEntries(root))
            if (new[]{".git","gitdir","commondir"}.Contains(Path.GetFileName(entry),StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("MIRROR_SOURCE_LAYOUT_UNSAFE");
    }

    private static bool Skip(string relative) => relative.Equals("config",StringComparison.OrdinalIgnoreCase)
        || relative.Equals("hooks",StringComparison.OrdinalIgnoreCase) || relative.Equals(".lfsconfig",StringComparison.OrdinalIgnoreCase)
        || relative.Equals("objects\\info\\alternates",StringComparison.OrdinalIgnoreCase)
        || relative.Equals("objects\\info\\http-alternates",StringComparison.OrdinalIgnoreCase);

    private static async Task CopyTree(string source,string destination,string relative,CancellationToken token)
    {
        using var lease=SummaryStore.RequirePrivateDirectory(source);
        foreach(string entry in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            string childRelative=Path.Combine(relative,Path.GetFileName(entry));
            using var handle=OpenPrivate(entry,shareWrite:false);
            bool directory=(File.GetAttributes(entry)&FileAttributes.Directory)!=0;
            NativeFileSystem.Inspect(handle,entry,directory);
            if (Skip(childRelative)) continue;
            string target=Path.Combine(destination,Path.GetFileName(entry));
            if(directory)
            {
                AclPolicy.CreateRestrictedDirectory(target,WindowsIdentity.GetCurrent().User!,requireNew:true);
                await CopyTree(entry,target,childRelative,token).ConfigureAwait(false);
            }
            else
            {
                using var input=new FileStream(handle,FileAccess.Read);
                await using var output=AclPolicy.CreateRestrictedFile(target,WindowsIdentity.GetCurrent().User!);
                await input.CopyToAsync(output,token).ConfigureAwait(false);
                output.Flush(true);
            }
        }
    }

    internal static SafeFileHandle OpenPrivate(string path,bool shareWrite=true,uint access=NativeFileSystem.ReadControl|NativeFileSystem.ReadAttributes|1)
    {
        var handle=NativeFileSystem.Open(path,access,shareWrite:shareWrite);
        try
        {
            NativeFileSystem.Inspect(handle,path);
            if(AclPolicy.Evaluate(AclPolicy.ReadDescriptor(handle),WindowsIdentity.GetCurrent().User!,true)!=AclRisk.Safe)
                throw new UnauthorizedAccessException("MIRROR_SOURCE_ACL_UNSAFE");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static string SafeConfig(string endpoint)
    {
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="github.com"||!uri.IsDefaultPort
            ||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!=""||!endpoint.StartsWith("https://github.com/",StringComparison.Ordinal)
            ||!endpoint.EndsWith(".git",StringComparison.Ordinal)) throw new InvalidDataException("MIRROR_ENDPOINT_INVALID");
        string[] parts=endpoint[19..^4].Split('/');
        if(parts.Length!=2||!AuthConfigLease.IsLogin(parts[0])||
            !(ValidName(parts[1])||(parts[1].EndsWith(".wiki",StringComparison.Ordinal)&&ValidName(parts[1][..^5]))))
            throw new InvalidDataException("MIRROR_ENDPOINT_INVALID");
        return "[core]\n\trepositoryformatversion = 0\n\tfilemode = false\n\tbare = true\n[remote \"origin\"]\n\turl = "+endpoint+"\n\tfetch = +refs/*:refs/*\n\tmirror = true\n[lfs]\n\turl = "+endpoint+"/info/lfs\n";
        bool ValidName(string name)=>RepositoryEndpointPolicy.ValidateAndCreate(parts[0],parts[0],name,parts[0]+"/"+name,"https://github.com/"+parts[0]+"/"+name).Allowed;
    }

    internal static async Task SanitizeAsync(string root,string endpoint,CancellationToken token)
    {
        using var lease=SummaryStore.RequirePrivateDirectory(root);
        Audit(root); ValidateLayout(root);
        foreach(string relative in new[]{"hooks",".lfsconfig","objects\\info\\alternates","objects\\info\\http-alternates"})
        {
            string path=Path.Combine(root,relative);
            if(Directory.Exists(path)) DeleteTree(path);
            else if(File.Exists(path)) DeleteFile(path);
        }
        await AtomicFile.WriteAsync(Path.Combine(root,"config"),(stream,ct)=>stream.WriteAsync(Encoding.UTF8.GetBytes(SafeConfig(endpoint)),ct).AsTask(),token).ConfigureAwait(false);
    }

    internal static void DeleteFile(string path)
    {
        using var parents=NativeFileSystem.PinDirectories(Path.GetDirectoryName(path)!);
        using var handle=OpenPrivate(path,access:NativeFileSystem.ReadControl|NativeFileSystem.ReadAttributes|NativeFileSystem.DeleteAccess|1);
        File.SetAttributes(path,File.GetAttributes(path)&~FileAttributes.ReadOnly);
        NativeFileSystem.DeleteByHandle(handle);
    }

    internal static void DeleteTree(string root)
    {
        using var parents=NativeFileSystem.PinDirectories(Path.GetDirectoryName(root)!);
        Audit(root);
        NativeFileIdentity identity;
        using(var handle=OpenPrivate(root))
        {
            identity=NativeFileSystem.Inspect(handle,root,true);
            foreach(string child in Directory.EnumerateFileSystemEntries(root))
                if((File.GetAttributes(child)&FileAttributes.Directory)!=0) DeleteTree(child); else DeleteFile(child);
        }
        using var removal=OpenPrivate(root,access:NativeFileSystem.ReadControl|NativeFileSystem.ReadAttributes|NativeFileSystem.DeleteAccess|1);
        if(NativeFileSystem.Inspect(removal,root,true)!=identity)throw new PathBoundaryException("IDENTITY_CHANGED");
        NativeFileSystem.DeleteByHandle(removal);
    }
}
