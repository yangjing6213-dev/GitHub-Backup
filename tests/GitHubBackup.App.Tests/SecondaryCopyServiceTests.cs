using System.Text;
using System.Security.Principal;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SecondaryCopyServiceTests
{
    [TestMethod]
    public async Task Copy_verifies_the_source_and_writes_a_private_new_generation()
    {
        using var root = new StorageTestRoot();
        string source = Path.Combine(root.Child("backup"), "fixture-user"), parent = root.Child("secondary");
        AclPolicy.CreateRestrictedDirectory(source, root.User); AclPolicy.CreateRestrictedDirectory(parent, root.User);
        AclPolicy.CreateRestrictedDirectory(Path.Combine(source, "manifests"), root.User);
        AclPolicy.CreateRestrictedDirectory(Path.Combine(source, "metadata", "repo"), root.User);
        AclPolicy.CreateRestrictedDirectory(Path.Combine(source, "mirrors", "repo.git"), root.User);
        AclPolicy.CreateRestrictedDirectory(Path.Combine(source, "actions", "repo"), root.User);
        await new ManifestStore().WriteAsync(source, "20261007T120000000Z-ABC", [PreflightFixture.Repository with { RemoteState = "active" }], default);
        Write(Path.Combine(source, "metadata", "repo", "repository.json"), "{}");
        Write(Path.Combine(source, "mirrors", "repo.git", "HEAD"), "ref: refs/heads/main\n");
        Write(Path.Combine(source, "actions", "repo", "index.json"), "{}");

        SecondaryCopyReport report = await new SecondaryCopyService().CopyLatestAsync(source, "fixture-user", parent, OperationJob.Create(), default);

        Assert.IsTrue(report.Success, string.Join(',', report.Warnings));
        Assert.AreEqual(4, report.FileCount);
        Assert.IsTrue(File.Exists(Path.Combine(report.DestinationRoot, "manifests", "repositories-20261007T120000000Z-ABC.json")));
        Assert.IsTrue(File.Exists(Path.Combine(report.DestinationRoot, "metadata", "repo", "repository.json")));
        Assert.IsTrue(File.Exists(Path.Combine(report.DestinationRoot, "actions", "repo", "index.json")));
        Assert.IsFalse(string.Equals(report.DestinationRoot, parent, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Copy_rejects_a_destination_inside_the_primary_backup()
    {
        using var root = new StorageTestRoot();
        string source = Path.Combine(root.Child("backup"), "fixture-user");
        AclPolicy.CreateRestrictedDirectory(source, root.User);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => new SecondaryCopyService().CopyLatestAsync(
            source, "fixture-user", source, OperationJob.Create(), default));
    }

    private static void Write(string path, string value)
    {
        AclPolicy.CreateRestrictedDirectory(Path.GetDirectoryName(path)!, WindowsIdentity.GetCurrent().User!);
        using FileStream output = AclPolicy.CreateRestrictedFile(path, WindowsIdentity.GetCurrent().User!);
        output.Write(Encoding.UTF8.GetBytes(value));
    }
}
