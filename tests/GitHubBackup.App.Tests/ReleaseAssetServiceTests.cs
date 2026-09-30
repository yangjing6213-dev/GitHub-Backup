using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Net;
using System.Security.AccessControl;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class ReleaseAssetServiceTests
{
    [TestMethod]
    public async Task Fresh_inventory_reads_every_page_and_only_control_fields()
    {
        var requested = new List<int>();
        var transport = new ReleaseTransport(request =>
        {
            requested.Add(request.Page);
            string items = string.Join(',', Enumerable.Range((request.Page - 1) * 100 + 1, request.Page == 3 ? 5 : 100)
                .Select(id => $"{{\"id\":{id},\"tag_name\":\"v{id}\",\"name\":\"Release\",\"draft\":false,\"prerelease\":false,\"published_at\":null,\"assets\":[{{\"id\":{id + 1000},\"name\":\"a.bin\",\"size\":2,\"digest\":null,\"updated_at\":\"2026-01-01T00:00:00Z\",\"browser_download_url\":\"https://evil.invalid/unused\"}}]}}"));
            return new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream(Encoding.UTF8.GetBytes('[' + items + ']')),
                request.Page < 3 ? request.Page + 1 : null);
        });

        ReleaseInventory inventory = await new ReleaseAssetService(transport).ReadInventoryAsync("fixture-user", "repo", default);

        Assert.HasCount(205, inventory.Releases);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, requested);
        Assert.AreEqual(1101L, inventory.Releases[100].Assets[0].Id);
    }

    [TestMethod]
    [DataRow("[]", "")]
    [DataRow("[{\"id\":1,\"tag_name\":\"v\",\"name\":\"n\",\"draft\":false,\"prerelease\":false,\"published_at\":null,\"assets\":[{\"id\":2,\"name\":\"a\",\"size\":-1,\"updated_at\":\"2026-01-01T00:00:00Z\"}]}]", "RELEASE_INVENTORY_INVALID")]
    [DataRow("[{\"id\":1,\"tag_name\":\"v\",\"name\":\"n\",\"draft\":false,\"prerelease\":false,\"published_at\":null,\"assets\":[{\"id\":2,\"name\":\"a\",\"size\":1,\"digest\":\"md5:abcd\",\"updated_at\":\"2026-01-01T00:00:00Z\"}]}]", "RELEASE_INVENTORY_INVALID")]
    public async Task Invalid_control_field_fails_before_storage_write(string body, string errorCode)
    {
        var transport = new ReleaseTransport(_ => new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream(Encoding.UTF8.GetBytes(body))));
        if (errorCode.Length == 0)
        {
            Assert.IsEmpty((await new ReleaseAssetService(transport).ReadInventoryAsync("fixture-user", "repo", default)).Releases);
            return;
        }
        var error = await Assert.ThrowsExactlyAsync<ReleaseException>(() =>
            new ReleaseAssetService(transport).ReadInventoryAsync("fixture-user", "repo", default));
        Assert.AreEqual(errorCode, error.Code);
    }

    [TestMethod]
    public async Task Asset_bytes_are_preserved_and_index_flips_once_after_all_generations()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[] bytes = [0, 27, 129, 255, 1];
        var transport = new ReleaseTransport(_ => throw new AssertFailedException("Unexpected metadata request"),
            _ => new GitHubResponse(200, new Dictionary<string, string> { ["Content-Length"] = bytes.Length.ToString() }, new MemoryStream(bytes)));
        var service = new ReleaseAssetService(transport);
        var asset = new ReleaseAsset(22, "release.json.bin", bytes.Length,
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), DateTimeOffset.UnixEpoch);
        ReleaseInventory inventory = new([new ReleaseRecord(11, "v1", "name", false, false, null, [asset])]);

        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);
        Assert.AreEqual(bytes.Length, plan.ChangedBytes);
        await service.MaterializeAsync(plan, () => 1L << 32, default);
        Assert.IsFalse(File.Exists(Path.Combine(directory, "releases.json")));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(plan.Generations[0].Assets[0].FullPath));
        await service.CommitIndexAsync(plan, default);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "releases.json")));

        ReleasePlan unchanged = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);
        Assert.AreEqual(0L, unchanged.ChangedBytes);
        Assert.AreEqual(plan.Generations[0].Assets[0].FullPath, unchanged.Generations[0].Assets[0].FullPath);
    }

    [TestMethod]
    public async Task V1_opaque_history_is_reserved_and_cannot_select_current()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        string legacyTag = Path.Combine(directory, "v1");
        AclPolicy.CreateRestrictedDirectory(legacyTag, root.User);
        await File.WriteAllTextAsync(Path.Combine(directory, "releases.json"),
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "releases-v1.json")));
        await File.WriteAllTextAsync(Path.Combine(legacyTag, "release.json"),
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "release-v1.json")));
        await File.WriteAllBytesAsync(Path.Combine(legacyTag, "a.zip"), [1, 2, 3]);
        byte[] prior = await File.ReadAllBytesAsync(Path.Combine(legacyTag, "a.zip"));
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException()));
        var inventory = new ReleaseInventory([new ReleaseRecord(1, "v1", "Old release", false, false, null,
            [new ReleaseAsset(2, "a.zip", 3, null, DateTimeOffset.UnixEpoch)])]);

        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);

        Assert.AreNotEqual("v1", plan.Generations[0].LocalTag, ignoreCase: true);
        Assert.IsFalse(plan.Generations[0].Assets[0].Reused);
        CollectionAssert.AreEqual(prior, await File.ReadAllBytesAsync(Path.Combine(legacyTag, "a.zip")));
    }

    [TestMethod]
    public async Task Digest_failure_keeps_prior_index_and_owned_temp_is_removed()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[] old = [1, 2, 3], wrong = [8, 8, 8];
        byte[] served = old;
        var transport = new ReleaseTransport(_ => throw new AssertFailedException(),
            _ => new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream(served)));
        var service = new ReleaseAssetService(transport);
        ReleaseInventory first = Inventory(old, DateTimeOffset.UnixEpoch);
        ReleasePlan previous = await service.PlanAsync(directory, "fixture-user", "repo", first, default);
        await service.MaterializeAsync(previous, () => 1L << 32, default);
        await service.CommitIndexAsync(previous, default);
        byte[] index = await File.ReadAllBytesAsync(Path.Combine(directory, "releases.json"));
        served = wrong;
        ReleasePlan changed = await service.PlanAsync(directory, "fixture-user", "repo",
            Inventory([4, 5, 6], DateTimeOffset.UnixEpoch.AddDays(1)), default);

        Assert.AreEqual("RELEASE_ASSET_DIGEST_MISMATCH", (await Assert.ThrowsExactlyAsync<ReleaseException>(() =>
            service.MaterializeAsync(changed, () => 1L << 32, default))).Code);
        CollectionAssert.AreEqual(index, await File.ReadAllBytesAsync(Path.Combine(directory, "releases.json")));
        CollectionAssert.AreEqual(old, await File.ReadAllBytesAsync(previous.Generations[0].Assets[0].FullPath));
        Assert.IsEmpty(Directory.EnumerateFiles(Path.GetDirectoryName(changed.Generations[0].Assets[0].FullPath)!, ".atomic-*.tmp"));
    }

    [TestMethod]
    public async Task Incomplete_v2_like_index_is_historical_not_current()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        string tag = Path.Combine(directory, "tag");
        AclPolicy.CreateRestrictedDirectory(tag, root.User);
        await File.WriteAllBytesAsync(Path.Combine(tag, "asset"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(directory, "releases.json"),
            "[{\"releaseId\":11,\"tagName\":\"v1\",\"localTag\":\"tag\",\"generation\":1,\"assets\":[" +
            "{\"id\":22,\"originalName\":\"asset.bin\",\"localName\":\"asset\",\"generation\":1," +
            "\"size\":3,\"digest\":null,\"updatedAt\":\"1970-01-01T00:00:00Z\"}]}]");
        var inventory = new ReleaseInventory([new ReleaseRecord(11, "v1", "name", false, false, null,
            [new ReleaseAsset(22, "asset.bin", 3, null, DateTimeOffset.UnixEpoch)])]);

        ReleasePlan plan = await new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException()))
            .PlanAsync(directory, "fixture-user", "repo", inventory, default);

        Assert.IsFalse(plan.Generations[0].Reused);
        Assert.IsFalse(plan.Generations[0].Assets[0].Reused);
    }

    [TestMethod]
    public async Task Reserved_snapshot_name_is_rejected_but_unsafe_asset_name_is_mapped()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException()));
        ReleaseInventory reserved = new([new ReleaseRecord(11, "v1", "name", false, false, null,
            [new ReleaseAsset(22, "release.json", 0, null, DateTimeOffset.UnixEpoch)])]);
        Assert.AreEqual("RELEASE_ASSET_RESERVED_NAME", (await Assert.ThrowsExactlyAsync<ReleaseException>(() =>
            service.PlanAsync(directory, "fixture-user", "repo", reserved, default))).Code);
        ReleaseInventory unsafeName = new([new ReleaseRecord(11, "v1", "name", false, false, null,
            [new ReleaseAsset(22, "CON:bad.", 0, null, DateTimeOffset.UnixEpoch)])]);
        ReleasePlan mapped = await service.PlanAsync(directory, "fixture-user", "repo", unsafeName, default);
        Assert.DoesNotContain(':', mapped.Generations[0].Assets[0].LocalName);
        StringAssert.Contains(mapped.Generations[0].Assets[0].LocalName, "a22");
    }

    [TestMethod]
    public async Task Missing_tag_snapshot_blocks_reuse_of_current_asset_path()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[] bytes = [1, 2, 3];
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(),
            _ => new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream(bytes))));
        ReleaseInventory inventory = Inventory(bytes, DateTimeOffset.UnixEpoch);
        ReleasePlan prior = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);
        await service.MaterializeAsync(prior, () => 1L << 32, default);
        await service.CommitIndexAsync(prior, default);
        File.Delete(Path.Combine(directory, prior.Generations[0].LocalTag, "release.json"));

        ReleasePlan next = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);

        Assert.AreNotEqual(prior.Generations[0].LocalTag, next.Generations[0].LocalTag, ignoreCase: true);
        Assert.IsFalse(next.Generations[0].Assets[0].Reused);
    }

    [TestMethod]
    public async Task Duplicate_local_tag_in_v2_index_invalidates_all_current_mappings()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[] bytes = [1, 2, 3];
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(),
            _ => new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream(bytes))));
        ReleaseInventory inventory = new([new ReleaseRecord(11, "v1", "one", false, false, null,
            [new ReleaseAsset(22, "a.bin", 3, null, DateTimeOffset.UnixEpoch)]),
            new ReleaseRecord(12, "v2", "two", false, false, null,
            [new ReleaseAsset(23, "b.bin", 3, null, DateTimeOffset.UnixEpoch)])]);
        ReleasePlan first = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);
        await service.MaterializeAsync(first, () => 1L << 32, default);
        await service.CommitIndexAsync(first, default);
        await RewriteIndexAsync(directory, array => array[1]!["localTag"] = array[0]!["localTag"]!.GetValue<string>());

        ReleasePlan next = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);

        Assert.IsTrue(next.Generations.All(g => !g.Reused && g.Assets.All(a => !a.Reused)));
    }

    [TestMethod]
    public async Task Duplicate_asset_local_name_in_v2_index_invalidates_all_current_mappings()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[] bytes = [1, 2, 3];
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(),
            _ => new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream(bytes))));
        ReleaseInventory inventory = new([new ReleaseRecord(11, "v1", "one", false, false, null,
            [new ReleaseAsset(22, "a.bin", 3, null, DateTimeOffset.UnixEpoch),
             new ReleaseAsset(23, "b.bin", 3, null, DateTimeOffset.UnixEpoch)])]);
        ReleasePlan first = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);
        await service.MaterializeAsync(first, () => 1L << 32, default);
        await service.CommitIndexAsync(first, default);
        await RewriteIndexAsync(directory, array => array[0]!["assets"]![1]!["localName"] =
            array[0]!["assets"]![0]!["localName"]!.GetValue<string>());

        ReleasePlan next = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);

        Assert.IsFalse(next.Generations[0].Reused);
        Assert.IsTrue(next.Generations[0].Assets.All(a => !a.Reused));
    }

    [TestMethod]
    public async Task Release_metadata_change_allocates_new_tag_generation()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException()));
        ReleaseInventory first = new([new ReleaseRecord(11, "v1", "old", false, false, null, [])]);
        ReleasePlan prior = await service.PlanAsync(directory, "fixture-user", "repo", first, default);
        await service.MaterializeAsync(prior, () => 1L << 32, default);
        await service.CommitIndexAsync(prior, default);

        ReleasePlan next = await service.PlanAsync(directory, "fixture-user", "repo",
            new([new ReleaseRecord(11, "v1", "new", false, false, null, [])]), default);

        Assert.AreNotEqual(prior.Generations[0].LocalTag, next.Generations[0].LocalTag, ignoreCase: true);
    }

    [TestMethod]
    public async Task Tag_rename_back_never_reuses_old_generation()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException()));
        async Task<ReleasePlan> SaveAsync(string tag)
        {
            ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo",
                new([new ReleaseRecord(11, tag, "name", false, false, null, [])]), default);
            await service.MaterializeAsync(plan, () => 1L << 32, default);
            await service.CommitIndexAsync(plan, default);
            return plan;
        }
        ReleasePlan first = await SaveAsync("v1");
        ReleasePlan second = await SaveAsync("v2");
        ReleasePlan third = await SaveAsync("v1");

        Assert.AreNotEqual(first.Generations[0].LocalTag, second.Generations[0].LocalTag, ignoreCase: true);
        Assert.AreNotEqual(first.Generations[0].LocalTag, third.Generations[0].LocalTag, ignoreCase: true);
        Assert.IsTrue(File.Exists(Path.Combine(directory, first.Generations[0].LocalTag, "release.json")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Production_transport_materializes_json_labeled_binary_without_cdn_credentials(bool redirect)
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[] bytes = Enumerable.Range(0, 1024 * 1024 + 3).Select(i => (byte)(i * 83)).ToArray();
        var requests = new List<string>();
        var handler = new AssetHandler(request =>
        {
            Uri uri = request.RequestUri!;
            requests.Add(uri.AbsoluteUri);
            if (uri.AbsolutePath == "/user")
            {
                Assert.AreEqual("Bearer SYNTHETIC_RELEASE_CANARY", request.Headers.Authorization?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("{\"login\":\"fixture-user\",\"id\":7}"u8.ToArray()) };
            }
            if (uri.Host == "api.github.com")
            {
                Assert.AreEqual("Bearer SYNTHETIC_RELEASE_CANARY", request.Headers.Authorization?.ToString());
                Assert.AreEqual("application/octet-stream", request.Headers.Accept.Single().MediaType);
                if (redirect)
                {
                    var moved = new HttpResponseMessage(HttpStatusCode.Found);
                    moved.Headers.Location = new Uri("https://objects.githubusercontent.com/blob?sig=SYNTHETIC_SIGNED_QUERY");
                    return moved;
                }
            }
            else
            {
                Assert.IsNull(request.Headers.Authorization);
                Assert.IsFalse(request.Headers.Contains("X-GitHub-Api-Version"));
                Assert.AreEqual("objects.githubusercontent.com", uri.Host);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new("application/json");
            return response;
        });
        using var transport = await GitHubHttpTransport.BindAsync(
            new GitHubCredentialLease("fixture-user", "SYNTHETIC_RELEASE_CANARY"u8.ToArray()),
            "fixture-user", ProxyProfile.Direct, default, _ => handler);
        var service = new ReleaseAssetService(transport);
        ReleaseInventory inventory = Inventory(bytes, DateTimeOffset.UnixEpoch);
        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);

        IReadOnlyList<AssetDownloadResult> results = await service.MaterializeAsync(plan, () => 1L << 32, default);

        Assert.HasCount(1, results);
        Assert.IsTrue(results[0].RemoteDigestVerified);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(results[0].FullPath));
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), results[0].Sha256);
        Assert.HasCount(redirect ? 3 : 2, requests);
        Assert.IsFalse(File.Exists(Path.Combine(directory, "releases.json")));
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(4)]
    public async Task Short_or_extra_body_aborts_without_index_or_stacked_retry(int suppliedLength)
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        int calls = 0;
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(), _ =>
        {
            calls++;
            return new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream(new byte[suppliedLength]));
        }));
        ReleaseInventory inventory = new([new ReleaseRecord(11, "v1", "name", false, false, null,
            [new ReleaseAsset(22, "a.bin", 3, null, DateTimeOffset.UnixEpoch)])]);
        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);

        Assert.AreEqual("RELEASE_ASSET_SIZE_MISMATCH", (await Assert.ThrowsExactlyAsync<ReleaseException>(() =>
            service.MaterializeAsync(plan, () => 1L << 32, default))).Code);
        Assert.AreEqual(1, calls);
        Assert.IsFalse(File.Exists(Path.Combine(directory, "releases.json")));
        Assert.IsFalse(File.Exists(plan.Generations[0].Assets[0].FullPath));
    }

    [TestMethod]
    public async Task Falling_free_space_stops_before_new_generation_write()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        int calls = 0;
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(), _ =>
        {
            calls++;
            return new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream([1, 2, 3]));
        }));
        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", Inventory([1, 2, 3], DateTimeOffset.UnixEpoch), default);

        Assert.AreEqual("BACKUP_INSUFFICIENT_FREE_SPACE", (await Assert.ThrowsExactlyAsync<ReleaseException>(() =>
            service.MaterializeAsync(plan, () => DiskSpacePolicy.SafetyReserveBytes + plan.ChangedBytes - 1, default))).Code);
        Assert.AreEqual(0, calls);
        Assert.IsFalse(Directory.Exists(Path.Combine(directory, plan.Generations[0].LocalTag)));
    }

    [TestMethod]
    public async Task Cancelled_body_keeps_index_absent_and_does_not_retry()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        using var cancel = new CancellationTokenSource();
        int calls = 0;
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(), _ =>
        {
            calls++;
            return new GitHubResponse(200, new Dictionary<string, string>(), new CancelOnReadStream([1, 2, 3], cancel));
        }));
        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", Inventory([1, 2, 3], DateTimeOffset.UnixEpoch), default);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.MaterializeAsync(plan, () => 1L << 32, cancel.Token));
        Assert.AreEqual(1, calls);
        Assert.IsFalse(File.Exists(plan.Generations[0].Assets[0].FullPath));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "releases.json")));
    }

    [TestMethod]
    public async Task Index_flush_interruption_preserves_old_current_and_new_file_is_only_history()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[] served = [1, 2, 3];
        var transport = new ReleaseTransport(_ => throw new AssertFailedException(),
            _ => new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream(served)));
        var service = new ReleaseAssetService(transport);
        ReleaseInventory original = Inventory(served, DateTimeOffset.UnixEpoch);
        ReleasePlan prior = await service.PlanAsync(directory, "fixture-user", "repo", original, default);
        await service.MaterializeAsync(prior, () => 1L << 32, default);
        await service.CommitIndexAsync(prior, default);
        string index = Path.Combine(directory, "releases.json");
        byte[] oldIndex = await File.ReadAllBytesAsync(index);
        served = [4, 5, 6];
        ReleasePlan next = await service.PlanAsync(directory, "fixture-user", "repo",
            Inventory(served, DateTimeOffset.UnixEpoch.AddDays(1)), default);
        await service.MaterializeAsync(next, () => 1L << 32, default);

        await Assert.ThrowsExactlyAsync<IOException>(() => service.CommitIndexAsync(next, default,
            new AtomicFileCommitHooks(AfterFlush: () => throw new IOException("synthetic stop"))));
        CollectionAssert.AreEqual(oldIndex, await File.ReadAllBytesAsync(index));
        Assert.IsTrue(File.Exists(next.Generations[0].Assets[0].FullPath));
        ReleasePlan recovered = await new ReleaseAssetService(transport).PlanAsync(
            directory, "fixture-user", "repo", original, default);
        Assert.AreEqual(prior.Generations[0].Assets[0].FullPath, recovered.Generations[0].Assets[0].FullPath);
        await service.CommitIndexAsync(next, default);
        Assert.AreNotEqual(Convert.ToHexString(oldIndex), Convert.ToHexString(await File.ReadAllBytesAsync(index)));
        ReleasePlan resumed = await new ReleaseAssetService(transport).PlanAsync(directory, "fixture-user", "repo",
            Inventory(served, DateTimeOffset.UnixEpoch.AddDays(1)), default);
        Assert.AreEqual(next.Generations[0].Assets[0].FullPath, resumed.Generations[0].Assets[0].FullPath);
    }

    [TestMethod]
    public async Task Materializer_rejects_forged_asset_path_outside_repository()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        string outside = root.Child("outside");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        AclPolicy.CreateRestrictedDirectory(outside, root.User);
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(),
            _ => new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream([1, 2, 3]))));
        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", Inventory([1, 2, 3], DateTimeOffset.UnixEpoch), default);
        string target = Path.Combine(outside, "forged.bin");
        ReleaseGeneration generation = plan.Generations[0];
        ReleasePlan forged = plan with { Generations = [generation with {
            Assets = [generation.Assets[0] with { FullPath = target }]
        }] };

        Assert.AreEqual("RELEASE_ALLOCATION_INVALID", (await Assert.ThrowsExactlyAsync<ReleaseException>(() =>
            service.MaterializeAsync(forged, () => 1L << 32, default))).Code);
        Assert.IsFalse(File.Exists(target));
    }

    [TestMethod]
    public async Task No_digest_reports_only_length_and_response_hash()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[] bytes = [0, 27, 128, 255];
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(),
            _ => new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream(bytes))));
        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo",
            new([new ReleaseRecord(11, "v1", "name", false, false, null,
                [new ReleaseAsset(22, "a.bin", bytes.Length, null, DateTimeOffset.UnixEpoch)])]), default);

        AssetDownloadResult result = (await service.MaterializeAsync(plan, () => 1L << 32, default)).Single();

        Assert.IsFalse(result.RemoteDigestVerified);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), result.Sha256);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(result.FullPath));
    }

    [TestMethod]
    public async Task Orphan_tag_path_is_reserved_when_generating_new_history()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException()));
        ReleaseInventory inventory = new([new ReleaseRecord(11, "v1", "name", false, false, null, [])]);
        ReleasePlan first = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);
        string occupied = Path.Combine(directory, first.Generations[0].LocalTag);
        AclPolicy.CreateRestrictedDirectory(occupied, root.User);
        await File.WriteAllBytesAsync(Path.Combine(occupied, "unknown.bin"), [7, 7]);

        ReleasePlan next = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);

        Assert.AreNotEqual(first.Generations[0].LocalTag, next.Generations[0].LocalTag, ignoreCase: true);
        CollectionAssert.AreEqual(new byte[] { 7, 7 }, await File.ReadAllBytesAsync(Path.Combine(occupied, "unknown.bin")));
    }

    [TestMethod]
    public async Task Reparse_child_blocks_planning_before_download()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        string junction = Path.Combine(directory, "old-tag");
        StorageTestRoot.CreateJunction(junction, root.Path);
        int calls = 0;
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(), _ =>
        {
            calls++;
            throw new AssertFailedException("Unsafe tree must not download");
        }));
        try
        {
            Assert.AreEqual("RELEASE_PATH_REPARSE_POINT_REJECTED", (await Assert.ThrowsExactlyAsync<ReleaseException>(() =>
                service.PlanAsync(directory, "fixture-user", "repo", Inventory([1], DateTimeOffset.UnixEpoch), default))).Code);
            Assert.AreEqual(0, calls);
        }
        finally { Directory.Delete(junction); }
    }

    [TestMethod]
    public async Task Broad_write_release_tree_blocks_planning()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        StorageTestRoot.Grant(directory, FileSystemRights.CreateFiles);
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException()));

        Assert.AreEqual("RELEASE_SOURCE_ACL_UNSAFE", (await Assert.ThrowsExactlyAsync<ReleaseException>(() =>
            service.PlanAsync(directory, "fixture-user", "repo", ReleaseInventory.Empty, default))).Code);
    }

    [TestMethod]
    public async Task Newly_planned_tag_directory_occupied_before_materialization_is_never_used()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(), _ =>
            new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream([1, 2, 3]))));
        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", Inventory([1, 2, 3], DateTimeOffset.UnixEpoch), default);
        string tag = Path.Combine(directory, plan.Generations[0].LocalTag);
        AclPolicy.CreateRestrictedDirectory(tag, root.User);
        await File.WriteAllBytesAsync(Path.Combine(tag, "unknown.bin"), [7]);

        await Assert.ThrowsAsync<IOException>(() => service.MaterializeAsync(plan, () => 1L << 32, default));

        CollectionAssert.AreEqual(new byte[] { 7 }, await File.ReadAllBytesAsync(Path.Combine(tag, "unknown.bin")));
        Assert.IsFalse(File.Exists(plan.Generations[0].Assets[0].FullPath));
        Assert.IsFalse(File.Exists(Path.Combine(tag, "release.json")));
    }

    [TestMethod]
    public async Task Snapshot_without_creation_assets_invalidates_current_index()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[] bytes = [1, 2, 3];
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(), _ =>
            new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream(bytes))));
        ReleaseInventory inventory = Inventory(bytes, DateTimeOffset.UnixEpoch);
        ReleasePlan first = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);
        await service.MaterializeAsync(first, () => 1L << 32, default);
        await service.CommitIndexAsync(first, default);
        string snapshot = Path.Combine(directory, first.Generations[0].LocalTag, "release.json");
        JsonNode body = JsonNode.Parse(await File.ReadAllTextAsync(snapshot))!;
        body.AsObject().Remove("assets");
        await AtomicFile.WriteAsync(snapshot, (stream, token) =>
            System.Text.Json.JsonSerializer.SerializeAsync(stream, body, cancellationToken: token), default);

        ReleasePlan next = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);

        Assert.IsFalse(next.Generations[0].Reused);
        Assert.IsFalse(next.Generations[0].Assets[0].Reused);
    }

    [TestMethod]
    public async Task Commit_rejects_corrupt_snapshot_without_creating_current_index()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(), _ =>
            new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream([1, 2, 3]))));
        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", Inventory([1, 2, 3], DateTimeOffset.UnixEpoch), default);
        await service.MaterializeAsync(plan, () => 1L << 32, default);
        string snapshot = Path.Combine(directory, plan.Generations[0].LocalTag, "release.json");
        await File.WriteAllTextAsync(snapshot, "{}");

        await Assert.ThrowsAsync<IOException>(() => service.CommitIndexAsync(plan, default));

        Assert.IsFalse(File.Exists(Path.Combine(directory, "releases.json")));
    }

    [TestMethod]
    public async Task Commit_rejects_malformed_snapshot_with_incomplete_generation_error()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(), _ =>
            new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream([1, 2, 3]))));
        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", Inventory([1, 2, 3], DateTimeOffset.UnixEpoch), default);
        await service.MaterializeAsync(plan, () => 1L << 32, default);
        await File.WriteAllTextAsync(Path.Combine(directory, plan.Generations[0].LocalTag, "release.json"), "{");

        Assert.AreEqual("RELEASE_GENERATION_INCOMPLETE", (await Assert.ThrowsExactlyAsync<ReleaseException>(() =>
            service.CommitIndexAsync(plan, default))).Code);
        Assert.IsFalse(File.Exists(Path.Combine(directory, "releases.json")));
    }

    [TestMethod]
    public async Task Commit_holds_verified_asset_snapshot_and_tag_handles_until_index_replace()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[] bytes = [1, 2, 3];
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(), _ =>
            new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream(bytes))));
        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", Inventory(bytes, DateTimeOffset.UnixEpoch), default);
        await service.MaterializeAsync(plan, () => 1L << 32, default);
        string asset = plan.Generations[0].Assets[0].FullPath;
        string tag = Path.GetDirectoryName(asset)!;
        string snapshot = Path.Combine(tag, "release.json");
        int blocked = 0;
        await service.CommitIndexAsync(plan, default, new AtomicFileCommitHooks(AfterFlush: () =>
        {
            if (WriteBlocked(() => File.WriteAllBytes(asset, [9, 9, 9]))) blocked++;
            if (WriteBlocked(() => File.WriteAllText(snapshot, "{}"))) blocked++;
            if (WriteBlocked(() => Directory.Move(tag, tag + "-moved"))) blocked++;
            Assert.AreEqual(3, blocked);
            return Task.CompletedTask;
        }));

        Assert.AreEqual(3, blocked);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(asset));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "releases.json")));
        static bool WriteBlocked(Action action)
        {
            try { action(); return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }
    }

    [TestMethod]
    public async Task Asset_refresh_and_name_rename_back_keep_prior_generations_and_historical_snapshot()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[] served = [1];
        var transport = new ReleaseTransport(_ => throw new AssertFailedException(), _ =>
            new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream(served)));
        var service = new ReleaseAssetService(transport);
        async Task<ReleasePlan> SaveAsync(string name, byte[] bytes, int day)
        {
            served = bytes;
            ReleaseInventory inventory = new([new ReleaseRecord(11, "v1", "name", false, false, null,
                [new ReleaseAsset(22, name, bytes.Length,
                    "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UnixEpoch.AddDays(day))])]);
            ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);
            await service.MaterializeAsync(plan, () => 1L << 32, default);
            await service.CommitIndexAsync(plan, default);
            return plan;
        }
        ReleasePlan first = await SaveAsync("a.bin", [1], 0);
        ReleasePlan refreshed = await SaveAsync("a.bin", [2], 1);
        ReleasePlan renamed = await SaveAsync("b.bin", [3], 2);
        ReleasePlan back = await SaveAsync("a.bin", [4], 3);
        string[] paths = [first.Generations[0].Assets[0].FullPath, refreshed.Generations[0].Assets[0].FullPath,
            renamed.Generations[0].Assets[0].FullPath, back.Generations[0].Assets[0].FullPath];

        Assert.HasCount(4, paths.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.AreEqual(first.Generations[0].LocalTag, back.Generations[0].LocalTag);
        CollectionAssert.AreEqual(new byte[] { 1 }, await File.ReadAllBytesAsync(paths[0]));
        CollectionAssert.AreEqual(new byte[] { 2 }, await File.ReadAllBytesAsync(paths[1]));
        CollectionAssert.AreEqual(new byte[] { 3 }, await File.ReadAllBytesAsync(paths[2]));
        CollectionAssert.AreEqual(new byte[] { 4 }, await File.ReadAllBytesAsync(paths[3]));
        using var snapshot = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, first.Generations[0].LocalTag, "release.json")));
        Assert.AreEqual("a.bin", snapshot.RootElement.GetProperty("assets")[0].GetProperty("originalName").GetString());
        Assert.AreEqual(paths[3], (await new ReleaseAssetService(transport).PlanAsync(directory, "fixture-user", "repo",
            new([new ReleaseRecord(11, "v1", "name", false, false, null,
                [new ReleaseAsset(22, "a.bin", 1, "sha256:" + Convert.ToHexString(SHA256.HashData([4])),
                    DateTimeOffset.UnixEpoch.AddDays(3))])]), default)).Generations[0].Assets[0].FullPath);
    }

    [TestMethod]
    [DataRow("assetFlush")]
    [DataRow("snapshotFlush")]
    [DataRow("afterSnapshot")]
    public async Task Interrupted_promotion_leaves_unreferenced_paths_reserved_after_restart(string stop)
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        var transport = new ReleaseTransport(_ => throw new AssertFailedException(), _ =>
            new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream([1, 2, 3])));
        var service = new ReleaseAssetService(transport);
        ReleaseInventory inventory = Inventory([1, 2, 3], DateTimeOffset.UnixEpoch);
        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", inventory, default);
        AtomicFileCommitHooks fault = new(AfterFlush: () => throw new IOException("synthetic stop"));
        if (stop == "afterSnapshot")
            await service.MaterializeAsync(plan, () => 1L << 32, default);
        else
            await Assert.ThrowsExactlyAsync<IOException>(() => service.MaterializeAsync(plan, () => 1L << 32,
                default, stop == "assetFlush" ? fault : null, stop == "snapshotFlush" ? fault : null));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "releases.json")));

        ReleasePlan restarted = await new ReleaseAssetService(transport).PlanAsync(directory, "fixture-user", "repo", inventory, default);

        Assert.AreNotEqual(plan.Generations[0].LocalTag, restarted.Generations[0].LocalTag, ignoreCase: true);
        Assert.IsFalse(restarted.Generations[0].Assets[0].Reused);
        Assert.IsEmpty(Directory.EnumerateFiles(directory, ".atomic-*.tmp", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task Precommit_same_size_asset_tamper_keeps_index_absent()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("releases");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        var service = new ReleaseAssetService(new ReleaseTransport(_ => throw new AssertFailedException(), _ =>
            new GitHubResponse(200, new Dictionary<string, string>(), new MemoryStream([1, 2, 3]))));
        ReleasePlan plan = await service.PlanAsync(directory, "fixture-user", "repo", Inventory([1, 2, 3], DateTimeOffset.UnixEpoch), default);
        await service.MaterializeAsync(plan, () => 1L << 32, default);
        await File.WriteAllBytesAsync(plan.Generations[0].Assets[0].FullPath, [9, 9, 9]);

        Assert.AreEqual("RELEASE_GENERATION_INCOMPLETE", (await Assert.ThrowsExactlyAsync<ReleaseException>(() =>
            service.CommitIndexAsync(plan, default))).Code);
        Assert.IsFalse(File.Exists(Path.Combine(directory, "releases.json")));
    }

    private static async Task RewriteIndexAsync(string directory, Action<JsonArray> change)
    {
        string index = Path.Combine(directory, "releases.json");
        var array = JsonNode.Parse(await File.ReadAllTextAsync(index))!.AsArray();
        change(array);
        await AtomicFile.WriteAsync(index, async (stream, token) =>
            await System.Text.Json.JsonSerializer.SerializeAsync(stream, array, cancellationToken: token), default);
    }

    private static ReleaseInventory Inventory(byte[] bytes, DateTimeOffset updated) => new([
        new ReleaseRecord(11, "v1", "name", false, false, null,
            [new ReleaseAsset(22, "asset.bin", bytes.Length,
                "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), updated)])]);

    private sealed class ReleaseTransport(Func<GitHubRequest, GitHubResponse> metadata,
        Func<AssetIdentity, GitHubResponse>? asset = null) : IGitHubHttpTransport
    {
        public string BoundLogin => "fixture-user";
        public long BoundAccountId => 7;
        public Task<GitHubResponse> SendAsync(GitHubRequest request, CancellationToken token) => Task.FromResult(metadata(request));
        public Task<GitHubResponse> DownloadAssetAsync(AssetIdentity identity, CancellationToken token) =>
            Task.FromResult((asset ?? (_ => throw new AssertFailedException("Unexpected asset request")))(identity));
        public void Dispose() { }
    }

    private sealed class AssetHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(reply(request));
    }
    private sealed class CancelOnReadStream(byte[] bytes, CancellationTokenSource cancel) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ValueTask<int> read = base.ReadAsync(buffer, cancellationToken);
            cancel.Cancel();
            return read;
        }
    }
}
