using System.Security.Cryptography;
using System.Text;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SummaryStoreTests
{
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task Latest_record_provenance_tracks_actual_filtered_receipt(bool fallback)
    {
        await WriteSummaryAsync("01-success", RunStatus.Pass);
        await WriteSummaryAsync("02-failure", RunStatus.Fail, fallback);
        var store = new SummaryStore(_paths);
        await store.WriteFallbackAsync(BackupSummary.PreflightFailure(BackupMode.Daily, "unrelated-owner", "newer-other", "", "preflight", "FIXTURE"), default);
        var history = await store.ReadLatestAsync(_ownerRoot, _fallbackRoot, default, "sample-owner");
        Assert.AreEqual(fallback, history.LatestIsFallback);
        Assert.AreEqual("02-failure", history.Latest!.StartedRunId);
        Assert.AreEqual("01-success", history.LatestCoreSuccess!.StartedRunId);
        var empty = await store.ReadLatestAsync(_ownerRoot, _fallbackRoot, default, "absent-owner");
        Assert.IsNull(empty.Latest); Assert.IsFalse(empty.LatestIsFallback);
    }

    private StorageTestRoot _root = null!;
    private AppPaths _paths = null!;
    private string _ownerRoot = null!;
    private string _fallbackRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = new StorageTestRoot();
        _paths = AppPaths.Create(_root.Path);
        _ownerRoot = _root.Child("sample-owner");
        AclPolicy.CreateRestrictedDirectory(Path.Combine(_ownerRoot, "manifests"), _root.User);
        _fallbackRoot = _paths.DiagnosticFallbackRoot;
    }

    [TestCleanup]
    public void Cleanup() => _root.Dispose();

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private string ManifestPath(string name) => Path.Combine(_ownerRoot, "manifests", name);

    private async Task WriteFixtureAsync(string name, string fixture)
    {
        byte[] json = await File.ReadAllBytesAsync(Fixture(fixture));
        await File.WriteAllBytesAsync(ManifestPath(name), json);
    }

    private async Task WriteSummaryAsync(string runId, RunStatus status, bool fallback = false)
    {
        var summary = new BackupSummary(2, BackupMode.Daily,
            DateTimeOffset.Parse("2026-09-20T00:00:00Z").AddMinutes(int.Parse(runId[..2])),
            DateTimeOffset.Parse("2026-09-20T00:00:00Z").AddMinutes(int.Parse(runId[..2])),
            status, status == RunStatus.Cancelled, "sample-owner", runId, 1, 0, [], 0, "", "", _ownerRoot, "", "");
        string root = fallback ? _fallbackRoot : _ownerRoot;
        if (fallback)
        {
            AclPolicy.CreateRestrictedDirectory(root, _root.User);
            await using FileStream stream = AclPolicy.CreateRestrictedFile(Path.Combine(root, "summary-" + runId + ".json"), _root.User);
            await stream.WriteAsync(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(summary, MetadataJson.Options));
        }
        else await new SummaryStore(_paths).WriteAsync(root, summary, _fallbackRoot, CancellationToken.None);
    }

    [TestMethod]
    public async Task V1_backupRoot_is_already_the_owner_root()
    {
        string ownerRoot = Path.Combine(_root.Path, "sample-owner");
        string manifests = Directory.CreateDirectory(Path.Combine(ownerRoot, "manifests")).FullName;
        string destination = Path.Combine(manifests, "summary-20260920-010203.json");
        byte[] json = await File.ReadAllBytesAsync(Fixture("summary-v1.json"));
        await File.WriteAllBytesAsync(destination, Encoding.UTF8.GetPreamble().Concat(json).ToArray());
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(destination));

        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(
            ownerRoot, _fallbackRoot, CancellationToken.None);

        Assert.IsNotNull(result.Latest);
        Assert.AreEqual(@"C:\BackupRoot\sample-owner", result.Latest.BackupRoot);
        CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(destination)));
    }

    [TestMethod]
    public async Task Corrupt_latest_falls_back_to_previous_recognized_summary()
    {
        await WriteFixtureAsync("summary-20260920-010203.json", "summary-v1.json");
        await File.WriteAllTextAsync(ManifestPath("summary-20260920-020304.json"), "{");
        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(
            _ownerRoot, _fallbackRoot, CancellationToken.None);
        Assert.AreEqual("20260920-010203", result.Latest!.StartedRunId);
        Assert.HasCount(1, result.Warnings);
    }

    [TestMethod]
    public async Task Future_schema_is_not_interpreted_as_success()
    {
        await WriteFixtureAsync("summary-20260920-010203.json", "summary-v1.json");
        await File.WriteAllTextAsync(
            ManifestPath("summary-20260920-020304.json"),
            "{\"schemaVersion\":999,\"status\":\"PASS\",\"startedRunId\":\"future\"}");
        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(
            _ownerRoot, _fallbackRoot, CancellationToken.None);
        Assert.AreEqual("20260920-010203", result.Latest!.StartedRunId);
        StringAssert.Contains(result.Warnings.Single(), "schema");
    }

    [TestMethod]
    [DataRow("2147483648")]
    [DataRow("1.5")]
    public async Task Unrepresentable_schema_warns_and_uses_previous_summary(string schema)
    {
        await WriteFixtureAsync("summary-20260920-010203.json", "summary-v1.json");
        await File.WriteAllTextAsync(ManifestPath("summary-20260920-020304.json"),
            "{\"schemaVersion\":" + schema + ",\"status\":\"PASS\"}");

        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(_ownerRoot, _fallbackRoot, CancellationToken.None);

        Assert.AreEqual("20260920-010203", result.Latest!.StartedRunId);
        Assert.HasCount(1, result.Warnings);
        StringAssert.Contains(result.Warnings[0], "schema");
    }

    [TestMethod]
    public async Task Duplicate_schema_cannot_hide_future_version()
    {
        string json = await File.ReadAllTextAsync(Fixture("summary-v2.json"));
        await File.WriteAllTextAsync(ManifestPath("summary-duplicate.json"),
            json.Replace("\"schemaVersion\":2", "\"schemaVersion\":2,\"schemaVersion\":999"));

        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(_ownerRoot, _fallbackRoot, CancellationToken.None);

        Assert.IsNull(result.Latest);
        Assert.HasCount(1, result.Warnings);
    }

    [TestMethod]
    public async Task Only_pass_and_partial_count_as_core_success()
    {
        await WriteSummaryAsync("01-pass", RunStatus.Pass);
        await WriteSummaryAsync("02-partial", RunStatus.Partial);
        await WriteSummaryAsync("03-fail", RunStatus.Fail);
        await WriteSummaryAsync("04-cancelled", RunStatus.Cancelled);
        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(
            _ownerRoot, _fallbackRoot, CancellationToken.None);
        Assert.AreEqual("04-cancelled", result.Latest!.StartedRunId);
        Assert.AreEqual("02-partial", result.LatestCoreSuccess!.StartedRunId);
    }

    [TestMethod]
    [DataRow((int)RunStatus.Fail)]
    [DataRow((int)RunStatus.Cancelled)]
    public async Task Newer_fallback_is_latest_but_does_not_replace_core_success(int fallbackStatus)
    {
        await WriteSummaryAsync("01-owner-pass", RunStatus.Pass);
        await WriteSummaryAsync("02-fallback", (RunStatus)fallbackStatus, fallback: true);
        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(
            _ownerRoot, _fallbackRoot, CancellationToken.None);
        Assert.AreEqual("02-fallback", result.Latest!.StartedRunId);
        Assert.AreEqual("01-owner-pass", result.LatestCoreSuccess!.StartedRunId);
    }

    [TestMethod]
    public async Task Preflight_failure_writes_fixed_empty_values()
    {
        BackupSummary value = BackupSummary.PreflightFailure(
            BackupMode.Daily, "sample-owner", "run-1", _ownerRoot,
            failurePhase: "preflight", errorCode: "PREFLIGHT_GIT_MISSING");
        string path = await new SummaryStore(_paths).WriteAsync(_ownerRoot, value, _fallbackRoot, CancellationToken.None);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        BackupSummary written = System.Text.Json.JsonSerializer.Deserialize<BackupSummary>(bytes, MetadataJson.Options)!;
        Assert.AreEqual(RunStatus.Fail, written.Status);
        Assert.AreEqual(0, written.RepositoryCount);
        Assert.AreEqual(0, written.WarningCount);
        Assert.IsEmpty(written.FailedRepositories);
        Assert.AreEqual(string.Empty, written.Manifest);
    }

    [TestMethod]
    public async Task Bom_and_unknown_property_do_not_change_legacy_data()
    {
        byte[] plain = await File.ReadAllBytesAsync(Fixture("summary-v1.json"));
        byte[] extended = Encoding.UTF8.GetPreamble().Concat(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(plain).TrimEnd()[..^1] + ",\"futureField\":42}" )).ToArray();
        string first = ManifestPath("summary-first.json");
        string second = ManifestPath("summary-second.json");
        await File.WriteAllBytesAsync(first, plain);
        await File.WriteAllBytesAsync(second, extended);
        byte[] firstHash = SHA256.HashData(await File.ReadAllBytesAsync(first));
        byte[] secondHash = SHA256.HashData(await File.ReadAllBytesAsync(second));
        var store = new SummaryStore(_paths);
        SummaryReadResult result = await store.ReadLatestAsync(_ownerRoot, _fallbackRoot, CancellationToken.None);
        Assert.AreEqual("20260920-010203", result.Latest!.StartedRunId);
        Assert.IsEmpty(result.Warnings);
        CollectionAssert.AreEqual(firstHash, SHA256.HashData(await File.ReadAllBytesAsync(first)));
        CollectionAssert.AreEqual(secondHash, SHA256.HashData(await File.ReadAllBytesAsync(second)));
    }

    [TestMethod]
    public async Task Unapproved_fallback_root_is_ignored()
    {
        await WriteFixtureAsync("summary-20260920-010203.json", "summary-v1.json");
        string foreign = _root.Child("foreign");
        Directory.CreateDirectory(foreign);
        await File.WriteAllBytesAsync(Path.Combine(foreign, "summary-20260920-020304.json"),
            await File.ReadAllBytesAsync(Fixture("summary-v2.json")));
        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(_ownerRoot, foreign, CancellationToken.None);
        Assert.AreEqual("20260920-010203", result.Latest!.StartedRunId);
        Assert.HasCount(1, result.Warnings);
    }

    [TestMethod]
    public async Task Safe_inherited_manifest_directory_accepts_owner_write()
    {
        Directory.Delete(Path.Combine(_ownerRoot, "manifests"));
        Directory.CreateDirectory(Path.Combine(_ownerRoot, "manifests"));
        BackupSummary value = BackupSummary.PreflightFailure(
            BackupMode.Daily, "sample-owner", "run-inherited", _ownerRoot,
            "preflight", "PREFLIGHT_GIT_MISSING");

        string path = await new SummaryStore(_paths).WriteAsync(_ownerRoot, value, _fallbackRoot, CancellationToken.None);

        Assert.AreEqual(ManifestPath("summary-run-inherited.json"), path);
    }

    [TestMethod]
    public async Task Broad_read_owner_root_is_ignored_even_when_manifests_is_private()
    {
        await WriteFixtureAsync("summary-20260920-010203.json", "summary-v1.json");
        StorageTestRoot.Grant(_ownerRoot, System.Security.AccessControl.FileSystemRights.Read);

        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(_ownerRoot, _fallbackRoot, CancellationToken.None);

        Assert.IsNull(result.Latest);
        Assert.HasCount(1, result.Warnings);
    }

    [TestMethod]
    public async Task Cancelled_write_sets_wasCancelled_and_uppercase_status()
    {
        var value = new BackupSummary(2, BackupMode.Full, DateTimeOffset.Parse("2026-09-20T01:00:00Z"),
            DateTimeOffset.Parse("2026-09-20T01:01:00Z"), RunStatus.Cancelled, false,
            "sample-owner", "cancelled-run", 1, 0, [], 0, "", "", _ownerRoot, "", "");

        string path = await new SummaryStore(_paths).WriteAsync(_ownerRoot, value, _fallbackRoot, CancellationToken.None);

        string json = await File.ReadAllTextAsync(path);
        StringAssert.Contains(json, "\"status\":\"CANCELLED\"");
        StringAssert.Contains(json, "\"wasCancelled\":true");
        StringAssert.Contains(json, "\"mode\":\"full\"");
    }

    [TestMethod]
    public async Task Unwritable_owner_uses_approved_private_fallback()
    {
        string missing = _root.Child("missing-owner");
        BackupSummary value = BackupSummary.PreflightFailure(
            BackupMode.Daily, "sample-owner", "fallback-run", missing, "preflight", "PREFLIGHT_ROOT_MISSING");

        string path = await new SummaryStore(_paths).WriteAsync(missing, value, _fallbackRoot, CancellationToken.None);

        Assert.AreEqual(Path.Combine(_fallbackRoot, "summary-fallback-run.json"), path);
        Assert.IsTrue(AppDataPathPolicy.Validate(_paths, path, AppDataEntryKind.File).Allowed);
    }

    [TestMethod]
    public async Task Repeated_fallback_write_atomically_replaces_same_run()
    {
        string missing = _root.Child("missing-owner");
        var first = BackupSummary.PreflightFailure(
            BackupMode.Daily, "sample-owner", "same-run", missing, "preflight", "PREFLIGHT_ROOT_MISSING");
        var second = first with { ErrorCode = "PREFLIGHT_GIT_MISSING" };
        var store = new SummaryStore(_paths);

        string path = await store.WriteAsync(missing, first, _fallbackRoot, CancellationToken.None);
        string secondPath = await store.WriteAsync(missing, second, _fallbackRoot, CancellationToken.None);

        Assert.AreEqual(path, secondPath);
        BackupSummary actual = System.Text.Json.JsonSerializer.Deserialize<BackupSummary>(
            await File.ReadAllBytesAsync(path), MetadataJson.Options)!;
        Assert.AreEqual("PREFLIGHT_GIT_MISSING", actual.ErrorCode);
        Assert.IsTrue(AppDataPathPolicy.Validate(_paths, path, AppDataEntryKind.File).Allowed);
    }

    [TestMethod]
    public async Task Unsafe_existing_fallback_file_is_not_replaced()
    {
        string missing = _root.Child("missing-owner");
        AclPolicy.CreateRestrictedDirectory(_fallbackRoot, _root.User);
        string path = Path.Combine(_fallbackRoot, "summary-same-run.json");
        await File.WriteAllTextAsync(path, "original");
        var value = BackupSummary.PreflightFailure(
            BackupMode.Daily, "sample-owner", "same-run", missing, "preflight", "PREFLIGHT_ROOT_MISSING");

        await Assert.ThrowsExactlyAsync<PathBoundaryException>(() =>
            new SummaryStore(_paths).WriteAsync(missing, value, _fallbackRoot, CancellationToken.None));

        Assert.AreEqual("original", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task V2_fixture_reads_camel_mode_and_uppercase_status()
    {
        await WriteFixtureAsync("summary-20260920-020304.json", "summary-v2.json");

        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(_ownerRoot, _fallbackRoot, CancellationToken.None);

        Assert.AreEqual(2, result.Latest!.SchemaVersion);
        Assert.AreEqual(BackupMode.Daily, result.Latest.Mode);
        Assert.AreEqual(RunStatus.Pass, result.Latest.Status);
    }

    [TestMethod]
    public async Task Missing_legacy_field_is_not_a_recognized_summary()
    {
        await File.WriteAllTextAsync(ManifestPath("summary-incomplete.json"),
            "{\"status\":\"PASS\",\"owner\":\"sample-owner\",\"startedRunId\":\"incomplete\",\"completedAt\":\"2026-09-20T02:00:00Z\"}");

        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(_ownerRoot, _fallbackRoot, CancellationToken.None);

        Assert.IsNull(result.Latest);
        Assert.HasCount(1, result.Warnings);
    }

    [TestMethod]
    public async Task Oversized_summary_is_ignored()
    {
        await File.WriteAllTextAsync(ManifestPath("summary-oversized.json"),
            "{\"padding\":\"" + new string('x', MetadataJson.MaxFileBytes) + "\"}");

        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(_ownerRoot, _fallbackRoot, CancellationToken.None);

        Assert.IsNull(result.Latest);
        Assert.HasCount(1, result.Warnings);
    }

    [TestMethod]
    public async Task Hardlinked_summary_is_ignored_without_modifying_source()
    {
        string source = ManifestPath("source.json");
        await File.WriteAllBytesAsync(source, await File.ReadAllBytesAsync(Fixture("summary-v1.json")));
        string link = ManifestPath("summary-linked.json");
        StorageTestRoot.CreateHardLink(link, source);
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(source));

        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(_ownerRoot, _fallbackRoot, CancellationToken.None);

        Assert.IsNull(result.Latest);
        Assert.HasCount(1, result.Warnings);
        CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));
    }

    [TestMethod]
    public async Task Live_legacy_run_preserves_all_historical_files()
    {
        string? root = Environment.GetEnvironmentVariable("GITHUB_BACKUP_LIVE_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            Assert.Inconclusive("SKIPPED: GITHUB_BACKUP_LIVE_ROOT is absent; Task 16 acceptance remains partial.");
            return;
        }
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Dictionary<string, byte[]> hashes = files.ToDictionary(x => x, x => SHA256.HashData(File.ReadAllBytes(x)));
        string legacy = Path.Combine(root, "manifests", "summary-20260920-163940.json");
        Assert.IsTrue(File.Exists(legacy));
        string copy = ManifestPath("summary-20260920-163940.json");
        await File.WriteAllBytesAsync(copy, await File.ReadAllBytesAsync(legacy));
        SummaryReadResult result = await new SummaryStore(_paths).ReadLatestAsync(_ownerRoot, _fallbackRoot, CancellationToken.None);
        Assert.AreEqual(14, result.Latest?.RepositoryCount);
        Assert.AreEqual("20260920-163940", result.Latest?.StartedRunId);
        foreach (var file in files)
            CollectionAssert.AreEqual(hashes[file], SHA256.HashData(File.ReadAllBytes(file)), file);
    }
}
