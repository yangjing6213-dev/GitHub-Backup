using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class ManifestStoreTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static TimeZoneInfo LocalTestZone() => TimeZoneInfo.CreateCustomTimeZone(
        "synthetic-plus-eight", TimeSpan.FromHours(8), "Synthetic +08", "Synthetic +08");

    private static async Task WriteNamedManifestAsync(string owner, string runId, string name)
    {
        string fixture = await File.ReadAllTextAsync(Fixture("repositories-v1.json"));
        await File.WriteAllTextAsync(Path.Combine(owner, "manifests", "repositories-" + runId + ".json"),
            fixture.Replace("alpha", name, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task First_run_without_manifests_is_empty_and_does_not_create_it()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(owner, root.User);

        IReadOnlyList<RepositoryDescriptor> found = await new ManifestStore().ReadLatestAsync(owner, CancellationToken.None);

        Assert.IsEmpty(found);
        Assert.IsFalse(Directory.Exists(Path.Combine(owner, "manifests")));
    }

    [TestMethod]
    public async Task Existing_broad_acl_manifests_still_blocks_read()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, "manifests"), root.User);
        StorageTestRoot.Grant(Path.Combine(owner, "manifests"), System.Security.AccessControl.FileSystemRights.Write);

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            new ManifestStore().ReadLatestAsync(owner, CancellationToken.None));
    }

    [TestMethod]
    public async Task Existing_reparse_manifests_blocks_read_without_following_target()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        string external = root.Child("external");
        AclPolicy.CreateRestrictedDirectory(owner, root.User);
        AclPolicy.CreateRestrictedDirectory(external, root.User);
        string sentinel = Path.Combine(external, "sentinel");
        await File.WriteAllTextAsync(sentinel, "untouched");
        string junction = Path.Combine(owner, "manifests");
        StorageTestRoot.CreateJunction(junction, external);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new ManifestStore().ReadLatestAsync(owner, default));
            Assert.AreEqual("untouched", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            using var handle = NativeFileSystem.Open(junction, NativeFileSystem.DeleteAccess, shareDelete: true);
            NativeFileSystem.DeleteByHandle(handle);
        }
    }

    [TestMethod]
    public async Task Later_legacy_local_run_beats_earlier_new_utc_run()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, "manifests"), root.User);
        await WriteNamedManifestAsync(owner, "20260920T010000000Z-ABC", "new-early"); // 09:00 local
        await WriteNamedManifestAsync(owner, "20260920-230000", "legacy-late"); // 15:00 UTC

        IReadOnlyList<RepositoryDescriptor> found = await new ManifestStore(LocalTestZone()).ReadLatestAsync(owner, CancellationToken.None);

        Assert.AreEqual("legacy-late", found[0].Name);
    }

    [TestMethod]
    public async Task Later_new_utc_run_beats_earlier_legacy_local_run()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, "manifests"), root.User);
        await WriteNamedManifestAsync(owner, "20260920-230000", "legacy-early"); // 15:00 UTC
        await WriteNamedManifestAsync(owner, "20260920T160000000Z-ABC", "new-late"); // 00:00 next local day

        IReadOnlyList<RepositoryDescriptor> found = await new ManifestStore(LocalTestZone()).ReadLatestAsync(owner, CancellationToken.None);

        Assert.AreEqual("new-late", found[0].Name);
    }

    [TestMethod]
    public async Task Equal_instant_manifest_runs_use_ordinal_name_tie_break()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, "manifests"), root.User);
        await WriteNamedManifestAsync(owner, "20260920-090000", "legacy-tie");
        await WriteNamedManifestAsync(owner, "20260920T010000000Z-ABC", "new-tie");

        IReadOnlyList<RepositoryDescriptor> found = await new ManifestStore(LocalTestZone()).ReadLatestAsync(owner, CancellationToken.None);

        Assert.AreEqual("new-tie", found[0].Name);
    }

    [TestMethod]
    public async Task Unparsed_run_names_remain_readable_but_rank_after_timed_runs()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, "manifests"), root.User);
        await WriteNamedManifestAsync(owner, "run-999", "unknown");
        await WriteNamedManifestAsync(owner, "20260920-090000", "timed");

        IReadOnlyList<RepositoryDescriptor> found = await new ManifestStore(LocalTestZone()).ReadLatestAsync(owner, CancellationToken.None);

        Assert.AreEqual("timed", found[0].Name);
    }

    [TestMethod]
    public async Task Unparsed_run_name_is_read_when_it_is_the_only_candidate()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, "manifests"), root.User);
        await WriteNamedManifestAsync(owner, "run-1", "unknown-only");

        IReadOnlyList<RepositoryDescriptor> found = await new ManifestStore(LocalTestZone()).ReadLatestAsync(owner, CancellationToken.None);

        Assert.AreEqual("unknown-only", found[0].Name);
    }

    [TestMethod]
    public async Task Legacy_manifest_reads_top_level_array_without_rewriting()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, "manifests"), root.User);
        string path = Path.Combine(owner, "manifests", "repositories-20260920-010203.json");
        byte[] fixture = await File.ReadAllBytesAsync(Fixture("repositories-v1.json"));
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetPreamble().Concat(fixture).ToArray());
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(path));

        IReadOnlyList<RepositoryDescriptor> found = await new ManifestStore().ReadLatestAsync(owner, CancellationToken.None);

        Assert.HasCount(2, found);
        Assert.AreEqual("sample-owner/alpha", found[0].NameWithOwner);
        Assert.AreEqual(0L, found[0].RepositoryId);
        Assert.AreEqual("beta", found[1].Name);
        CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }

    [TestMethod]
    public async Task New_manifest_is_array_with_only_legacy_and_three_added_fields()
    {
        using var root = new StorageTestRoot();
        string owner = root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(Path.Combine(owner, "manifests"), root.User);
        var repository = new RepositoryDescriptor(123, "alpha", "sample-owner/alpha", "https://github.com/sample-owner/alpha",
            false, false, false, true, DateTimeOffset.Parse("2026-09-20T00:00:00Z"), 12, "alpha", "present");

        string path = await new ManifestStore().WriteAsync(owner, "run-1", [repository], CancellationToken.None);

        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.AreEqual(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.AreEqual(12, json.RootElement[0].EnumerateObject().Count());
        Assert.AreEqual(123L, json.RootElement[0].GetProperty("repositoryId").GetInt64());
        Assert.AreEqual("sample-owner/alpha", json.RootElement[0].GetProperty("nameWithOwner").GetString());
        Assert.AreEqual("repositories-run-1.json", Path.GetFileName(path));
    }

    [TestMethod]
    public async Task Pages_reader_preserves_outer_pages_and_empty_page()
    {
        using var root = new StorageTestRoot();
        string metadata = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        string path = Path.Combine(metadata, "issues.pages.json");
        byte[] fixture = await File.ReadAllBytesAsync(Fixture("pages-v1.json"));
        await File.WriteAllBytesAsync(path, fixture);
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(path));

        JsonElement pages = await ManifestStore.ReadPagesAsync(path, CancellationToken.None);

        Assert.AreEqual(JsonValueKind.Array, pages.ValueKind);
        Assert.AreEqual(2, pages.GetArrayLength());
        Assert.AreEqual(1, pages[0].GetArrayLength());
        Assert.AreEqual(0, pages[1].GetArrayLength());
        CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }

    [TestMethod]
    public async Task Workflows_reader_accepts_legacy_outer_array_of_object_pages_only_for_workflows()
    {
        using var root = new StorageTestRoot();
        string metadata = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        string workflows = Path.Combine(metadata, "workflows.pages.json");
        string issues = Path.Combine(metadata, "issues.pages.json");
        byte[] bytes = "[{\"total_count\":1,\"workflows\":[{\"id\":1}]},{\"total_count\":0,\"workflows\":[]}]"u8.ToArray();
        await File.WriteAllBytesAsync(workflows, bytes);
        await File.WriteAllBytesAsync(issues, bytes);

        JsonElement pages = await ManifestStore.ReadPagesAsync(workflows, default);

        Assert.AreEqual(2, pages.GetArrayLength());
        Assert.AreEqual(1, pages[0].GetProperty("workflows").GetArrayLength());
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(workflows));
        await Assert.ThrowsExactlyAsync<JsonException>(() => ManifestStore.ReadPagesAsync(issues, default));
    }

    [TestMethod]
    public async Task Pages_reader_accepts_payload_larger_than_summary_limit()
    {
        using var root = new StorageTestRoot();
        string metadata = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(metadata, root.User);
        string path = Path.Combine(metadata, "issues.pages.json");
        await File.WriteAllTextAsync(path, "[[{\"body\":\"" + new string('x', MetadataJson.MaxFileBytes) + "\"}]]");

        JsonElement pages = await ManifestStore.ReadPagesAsync(path, CancellationToken.None);

        Assert.AreEqual(1, pages.GetArrayLength());
        Assert.AreEqual(1, pages[0].GetArrayLength());
    }
}
