using System.Security.Cryptography;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class ContentIntegrityTests
{
    [TestMethod]
    public void Unknown_digest_algorithm_is_rejected_before_download()
    {
        Assert.AreEqual("RELEASE_INVENTORY_INVALID", Assert.ThrowsExactly<ReleaseException>(() =>
            ContentIntegrity.Expect(new ReleaseAsset(1, "a.bin", 3, "md5:abcd", DateTimeOffset.UnixEpoch))).Code);
    }

    [TestMethod]
    public async Task Copy_reports_exact_hash_and_distinguishes_absent_remote_digest()
    {
        using var root = new StorageTestRoot();
        string path = root.Child("asset.bin");
        byte[] bytes = [0, 27, 128, 255];
        await using var output = AclPolicy.CreateRestrictedFile(path, root.User);
        using var response = new GitHubResponse(200, new Dictionary<string, string> { ["Content-Length"] = "4" },
            new MemoryStream(bytes));

        AssetDownloadResult result = await ContentIntegrity.CopyAsync(response, output, path, new(4, null), default);

        Assert.AreEqual(4L, result.Length);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), result.Sha256);
        Assert.IsFalse(result.RemoteDigestVerified);
    }

    [TestMethod]
    public async Task Declared_content_length_mismatch_rejects_before_writing()
    {
        using var root = new StorageTestRoot();
        string path = root.Child("asset.bin");
        await using var output = AclPolicy.CreateRestrictedFile(path, root.User);
        using var response = new GitHubResponse(200, new Dictionary<string, string> { ["Content-Length"] = "5" },
            new MemoryStream([1, 2, 3]));

        Assert.AreEqual("RELEASE_ASSET_SIZE_MISMATCH", (await Assert.ThrowsExactlyAsync<ReleaseException>(() =>
            ContentIntegrity.CopyAsync(response, output, path, new(3, null), default))).Code);
        Assert.AreEqual(0L, output.Length);
    }
}
