using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

internal sealed class ReleaseAssetService(IGitHubHttpTransport transport)
{
    private const int MaxPageBytes = 32 * 1024 * 1024;
    private const int MaxReleases = 100_000;
    private const int MaxAssets = 100_000;
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    internal async Task<ReleaseInventory> ReadInventoryAsync(string owner, string repository, CancellationToken token)
    {
        if (transport.BoundAccountId <= 0 || !string.Equals(owner, transport.BoundLogin, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException("RELEASE_IDENTITY_REJECTED");
        var releases = new List<ReleaseRecord>();
        var releaseIds = new HashSet<long>();
        var assetIds = new HashSet<long>();
        int page = 1;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            GitHubRequest request = GitHubRequest.ForMetadata(owner, repository, "releases.pages.json", page == 1 ? null : page);
            using GitHubResponse response = await transport.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode != 200) throw new ReleaseException("RELEASE_INVENTORY_INVALID");
            using var memory = new MemoryStream();
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = await response.Body.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (memory.Length > MaxPageBytes - read) throw new ReleaseException("RELEASE_INVENTORY_INVALID");
                await memory.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            if (response.Headers.TryGetValue("Content-Length", out string? lengthText)
                && (!long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out long declared)
                    || declared != memory.Length)) throw new ReleaseException("RELEASE_INVENTORY_INVALID");
            try
            {
                using JsonDocument json = JsonDocument.Parse(memory.ToArray());
                if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 100)
                    throw new ReleaseException("RELEASE_INVENTORY_INVALID");
                foreach (JsonElement item in json.RootElement.EnumerateArray())
                {
                    ReleaseRecord release = ParseRelease(item);
                    if (!releaseIds.Add(release.Id) || releases.Count == MaxReleases)
                        throw new ReleaseException("RELEASE_INVENTORY_INVALID");
                    foreach (ReleaseAsset asset in release.Assets)
                        if (!assetIds.Add(asset.Id) || assetIds.Count > MaxAssets)
                            throw new ReleaseException("RELEASE_INVENTORY_INVALID");
                    releases.Add(release);
                }
            }
            catch (JsonException) { throw new ReleaseException("RELEASE_INVENTORY_INVALID"); }
            if (response.NextPage is not int next) break;
            if (next != page + 1 || next > 1_000_000) throw new ReleaseException("RELEASE_INVENTORY_INVALID");
            page = next;
        }
        return new(releases);
    }

    internal async Task<ReleasePlan> PlanAsync(string repositoryDirectory, string owner, string repository,
        ReleaseInventory inventory, CancellationToken token)
    {
        _ = GitHubRequest.ForMetadata(owner, repository, "releases.pages.json");
        if (transport.BoundAccountId <= 0 || !string.Equals(owner, transport.BoundLogin, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException("RELEASE_IDENTITY_REJECTED");
        string root = NativeFileSystem.CanonicalPath(repositoryDirectory);
        if (Directory.Exists(root)) AuditTree(root);
        Dictionary<long, CurrentRelease> current = await ReadCurrentAsync(root, token).ConfigureAwait(false);
        var occupied = Directory.Exists(root)
            ? Directory.EnumerateFileSystemEntries(root).Select(path => Path.GetFileName(path)!).ToHashSet(Names)
            : new HashSet<string>(Names);
        var output = new List<ReleaseGeneration>();
        long changed = 0;
        foreach (ReleaseRecord release in inventory.Releases)
        {
            token.ThrowIfCancellationRequested();
            bool sameTag = current.TryGetValue(release.Id, out CurrentRelease? previous)
                && previous.TagName == release.TagName && previous.LocalTag.Length != 0
                && previous.Name == release.Name && previous.IsDraft == release.IsDraft
                && previous.IsPrerelease == release.IsPrerelease && previous.PublishedAt == release.PublishedAt
                && Directory.Exists(Path.Combine(root, previous.LocalTag))
                && await ValidTagSnapshotAsync(root, release.Id, previous, token).ConfigureAwait(false);
            int generation = sameTag ? previous!.Generation : checked((previous?.Generation ?? 0) + 1);
            string localTag = sameTag ? previous!.LocalTag : Allocate(occupied, release.TagName, "r", release.Id, ref generation);
            string tagDirectory = Path.Combine(root, localTag);
            var occupiedAssets = sameTag
                ? Directory.EnumerateFileSystemEntries(tagDirectory).Select(path => Path.GetFileName(path)!).ToHashSet(Names)
                : new HashSet<string>(Names);
            occupiedAssets.Add("release.json");
            var assets = new List<ReleaseAssetGeneration>();
            foreach (ReleaseAsset asset in release.Assets)
            {
                if (asset.Name.Equals("release.json", StringComparison.OrdinalIgnoreCase))
                    throw new ReleaseException("RELEASE_ASSET_RESERVED_NAME");
                ReleaseAssetExpectation expected = ContentIntegrity.Expect(asset);
                CurrentAsset? old = sameTag && previous!.Assets.TryGetValue(asset.Id, out CurrentAsset? existing) ? existing : null;
                bool reused = old is not null && old.OriginalName == asset.Name && old.UpdatedAt == asset.UpdatedAt
                    && old.Size == asset.Size && Names.Equals(old.Digest, asset.Digest)
                    && await ContentIntegrity.MatchesFileAsync(Path.Combine(tagDirectory, old.LocalName), expected, token).ConfigureAwait(false);
                int assetGeneration = reused ? old!.Generation : checked((old?.Generation ?? 0) + 1);
                string localName = reused ? old!.LocalName : Allocate(occupiedAssets, asset.Name, "a", asset.Id, ref assetGeneration);
                assets.Add(new(new AssetIdentity(owner, repository, asset.Id), asset,
                    Path.Combine(tagDirectory, localName), localName, assetGeneration, reused));
                if (!reused) changed = checked(changed + asset.Size);
            }
            output.Add(new(release, localTag, generation, sameTag, assets));
        }
        var plan = new ReleasePlan(root, owner, repository, output, changed);
        ValidatePlan(plan);
        return plan;
    }

    internal async Task<IReadOnlyList<AssetDownloadResult>> MaterializeAsync(ReleasePlan plan,
        Func<long> availableFreeBytes, CancellationToken token,
        AtomicFileCommitHooks? assetHooks = null, AtomicFileCommitHooks? snapshotHooks = null)
    {
        ValidatePlan(plan);
        string root = NativeFileSystem.CanonicalPath(plan.RepositoryDirectory);
        if (availableFreeBytes() < DiskSpacePolicy.RequiredForAssetBatch(plan.ChangedBytes))
            throw new ReleaseException("BACKUP_INSUFFICIENT_FREE_SPACE");
        if (!Directory.Exists(root))
        {
            using var parent = SummaryStore.RequirePrivateDirectory(Path.GetDirectoryName(root)!);
            AclPolicy.CreateRestrictedDirectory(root, WindowsIdentity.GetCurrent().User!, requireNew: true);
        }
        AuditTree(root);
        var results = new List<AssetDownloadResult>();
        long remaining = plan.ChangedBytes;
        foreach (ReleaseGeneration generation in plan.Generations)
        {
            token.ThrowIfCancellationRequested();
            if (availableFreeBytes() < DiskSpacePolicy.RequiredForAssetBatch(remaining))
                throw new ReleaseException("BACKUP_INSUFFICIENT_FREE_SPACE");
            string tagDirectory = Path.Combine(root, generation.LocalTag);
            if (!generation.Reused)
                AclPolicy.CreateRestrictedDirectory(tagDirectory, WindowsIdentity.GetCurrent().User!, requireNew: true);
            else using (SummaryStore.RequirePrivateDirectory(tagDirectory)) { }
            foreach (ReleaseAssetGeneration asset in generation.Assets.Where(x => !x.Reused))
            {
                token.ThrowIfCancellationRequested();
                if (availableFreeBytes() < DiskSpacePolicy.RequiredForAssetBatch(remaining))
                    throw new ReleaseException("BACKUP_INSUFFICIENT_FREE_SPACE");
                ReleaseAssetExpectation expected = ContentIntegrity.Expect(asset.Asset);
                AssetDownloadResult? result = null;
                await AtomicFile.WriteAsync(asset.FullPath, async (output, ct) =>
                {
                    using GitHubResponse response = await transport.DownloadAssetAsync(asset.Identity, ct).ConfigureAwait(false);
                    result = await ContentIntegrity.CopyAsync(response, output, asset.FullPath, expected, ct).ConfigureAwait(false);
                }, token, assetHooks, requireNew: true).ConfigureAwait(false);
                results.Add(result!);
                remaining -= asset.Asset.Size;
            }
            if (!generation.Reused)
            {
                string snapshot = Path.Combine(tagDirectory, "release.json");
                await AtomicFile.WriteAsync(snapshot, (output, ct) => JsonSerializer.SerializeAsync(output, new
                {
                    releaseId = generation.Release.Id,
                    tagName = generation.Release.TagName,
                    name = generation.Release.Name,
                    isDraft = generation.Release.IsDraft,
                    isPrerelease = generation.Release.IsPrerelease,
                    publishedAt = generation.Release.PublishedAt,
                    localTag = generation.LocalTag,
                    generation = generation.Generation,
                    assets = generation.Assets.Select(asset => new
                    {
                        id = asset.Asset.Id, originalName = asset.Asset.Name, localName = asset.LocalName,
                        generation = asset.Generation, size = asset.Asset.Size, digest = asset.Asset.Digest,
                        updatedAt = asset.Asset.UpdatedAt
                    })
                }, MetadataJson.Options, ct), token, snapshotHooks, requireNew: true).ConfigureAwait(false);
            }
        }
        return results;
    }

    internal async Task CommitIndexAsync(ReleasePlan plan, CancellationToken token, AtomicFileCommitHooks? hooks = null)
    {
        ValidatePlan(plan);
        string root = NativeFileSystem.CanonicalPath(plan.RepositoryDirectory);
        AuditTree(root);
        using PathLease rootPins = SummaryStore.RequirePrivateDirectory(root);
        using var tagPins = new PathLease();
        var filePins = new List<FileStream>();
        try
        {
            foreach (ReleaseGeneration generation in plan.Generations)
            {
                token.ThrowIfCancellationRequested();
                string tagDirectory = Path.Combine(root, generation.LocalTag);
                SafeFileHandle tag = NativeFileSystem.Open(tagDirectory, shareWrite: false);
                tagPins.Add(tag);
                NativeFileSystem.Inspect(tag, tagDirectory, directory: true);
                if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(tag), WindowsIdentity.GetCurrent().User!, true) != AclRisk.Safe)
                    throw new ReleaseException("RELEASE_GENERATION_INCOMPLETE");
                string snapshot = Path.Combine(tagDirectory, "release.json");
                FileStream snapshotStream = ContentIntegrity.OpenPinnedRead(snapshot);
                filePins.Add(snapshotStream);
                if (snapshotStream.Length > 8 * 1024 * 1024) throw new ReleaseException("RELEASE_GENERATION_INCOMPLETE");
                try
                {
                    using JsonDocument json = await JsonDocument.ParseAsync(snapshotStream, cancellationToken: token).ConfigureAwait(false);
                    if (!ValidTagSnapshot(json.RootElement, generation.Release.Id, generation.Release.TagName,
                        generation.Release.Name, generation.Release.IsDraft, generation.Release.IsPrerelease,
                        generation.Release.PublishedAt, generation.LocalTag, generation.Generation,
                        generation.Reused ? null : generation.Assets))
                        throw new ReleaseException("RELEASE_GENERATION_INCOMPLETE");
                }
                catch (JsonException) { throw new ReleaseException("RELEASE_GENERATION_INCOMPLETE"); }
                NativeFileSystem.Inspect(snapshotStream.SafeFileHandle, snapshot, directory: false);
                foreach (ReleaseAssetGeneration asset in generation.Assets)
                    filePins.Add(await ContentIntegrity.PinVerifiedAsync(asset.FullPath,
                        ContentIntegrity.Expect(asset.Asset), token).ConfigureAwait(false));
                NativeFileSystem.Inspect(tag, tagDirectory, directory: true);
            }
            string index = Path.Combine(root, "releases.json");
            await AtomicFile.WriteAsync(index, (output, ct) => JsonSerializer.SerializeAsync(output,
                plan.Generations.Select(g => new
                {
                    isDraft = g.Release.IsDraft, isPrerelease = g.Release.IsPrerelease,
                    name = g.Release.Name, publishedAt = g.Release.PublishedAt, tagName = g.Release.TagName,
                    releaseId = g.Release.Id, localTag = g.LocalTag, generation = g.Generation,
                    assets = g.Assets.Select(a => new
                    {
                        id = a.Asset.Id, originalName = a.Asset.Name, localName = a.LocalName,
                        generation = a.Generation, size = a.Asset.Size, digest = a.Asset.Digest,
                        updatedAt = a.Asset.UpdatedAt
                    })
                }), MetadataJson.Options, ct), token, hooks).ConfigureAwait(false);
        }
        finally { foreach (FileStream stream in filePins) stream.Dispose(); }
    }

    private static ReleaseRecord ParseRelease(JsonElement item)
    {
        RequireObject(item);
        long id = Positive(item, "id");
        string tag = RequiredString(item, "tag_name", allowEmpty: false);
        string name = RequiredString(item, "name", allowEmpty: true);
        bool draft = RequiredBoolean(item, "draft");
        bool prerelease = RequiredBoolean(item, "prerelease");
        DateTimeOffset? published = OptionalDate(item, "published_at");
        JsonElement list = Required(item, "assets", JsonValueKind.Array);
        var assets = new List<ReleaseAsset>();
        foreach (JsonElement child in list.EnumerateArray())
        {
            RequireObject(child);
            long assetId = Positive(child, "id");
            string assetName = RequiredString(child, "name", allowEmpty: false);
            long size = Nonnegative(child, "size");
            string? digest = OptionalString(child, "digest");
            DateTimeOffset updated = OptionalDate(child, "updated_at") ?? throw new ReleaseException("RELEASE_INVENTORY_INVALID");
            var asset = new ReleaseAsset(assetId, assetName, size, digest, updated);
            _ = ContentIntegrity.Expect(asset);
            assets.Add(asset);
        }
        return new(id, tag, name, draft, prerelease, published, assets);
    }

    private void ValidatePlan(ReleasePlan plan)
    {
        if (transport.BoundAccountId <= 0 || !string.Equals(transport.BoundLogin, plan.Owner, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException("RELEASE_ALLOCATION_INVALID");
        try { _ = GitHubRequest.ForMetadata(plan.Owner, plan.Repository, "releases.pages.json"); }
        catch (ArgumentException) { throw new ReleaseException("RELEASE_ALLOCATION_INVALID"); }
        string root = NativeFileSystem.CanonicalPath(plan.RepositoryDirectory);
        var releaseIds = new HashSet<long>();
        var assetIds = new HashSet<long>();
        var tags = new HashSet<string>(Names);
        long changed = 0;
        foreach (ReleaseGeneration generation in plan.Generations)
        {
            if (generation.Release.Id <= 0 || !releaseIds.Add(generation.Release.Id)
                || generation.Generation <= 0 || !SafeLocal(generation.LocalTag) || !tags.Add(generation.LocalTag)
                || generation.Assets.Count != generation.Release.Assets.Count)
                throw new ReleaseException("RELEASE_ALLOCATION_INVALID");
            var localNames = new HashSet<string>(Names) { "release.json" };
            int assetPosition = 0;
            foreach (ReleaseAssetGeneration asset in generation.Assets)
            {
                if (asset.Asset != generation.Release.Assets[assetPosition++]
                    || asset.Asset.Id <= 0 || !assetIds.Add(asset.Asset.Id) || asset.Generation <= 0
                    || !SafeLocal(asset.LocalName) || !localNames.Add(asset.LocalName)
                    || asset.Identity.Id != asset.Asset.Id || asset.Identity.Owner != plan.Owner
                    || asset.Identity.Repository != plan.Repository
                    || !string.Equals(NativeFileSystem.CanonicalPath(asset.FullPath),
                        Path.Combine(root, generation.LocalTag, asset.LocalName), StringComparison.OrdinalIgnoreCase))
                    throw new ReleaseException("RELEASE_ALLOCATION_INVALID");
                _ = ContentIntegrity.Expect(asset.Asset);
                if (!asset.Reused) changed = checked(changed + asset.Asset.Size);
            }
        }
        if (changed != plan.ChangedBytes) throw new ReleaseException("RELEASE_ALLOCATION_INVALID");
    }

    private static JsonElement Required(JsonElement item, string key, JsonValueKind kind)
    {
        if (!item.TryGetProperty(key, out JsonElement value) || value.ValueKind != kind)
            throw new ReleaseException("RELEASE_INVENTORY_INVALID");
        return value;
    }
    private static long Positive(JsonElement item, string key)
    {
        JsonElement value = Required(item, key, JsonValueKind.Number);
        if (!value.TryGetInt64(out long number) || number <= 0) throw new ReleaseException("RELEASE_INVENTORY_INVALID");
        return number;
    }
    private static long Nonnegative(JsonElement item, string key)
    {
        JsonElement value = Required(item, key, JsonValueKind.Number);
        if (!value.TryGetInt64(out long number) || number < 0) throw new ReleaseException("RELEASE_INVENTORY_INVALID");
        return number;
    }
    private static bool RequiredBoolean(JsonElement item, string key) => Required(item, key,
        item.TryGetProperty(key, out JsonElement value) ? value.ValueKind : JsonValueKind.Undefined).ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        _ => throw new ReleaseException("RELEASE_INVENTORY_INVALID")
    };
    private static string RequiredString(JsonElement item, string key, bool allowEmpty)
    {
        string text = Required(item, key, JsonValueKind.String).GetString()!;
        if (text.Length > 1024 || (!allowEmpty && text.Length == 0)) throw new ReleaseException("RELEASE_INVENTORY_INVALID");
        return text;
    }
    private static string? OptionalString(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ReleaseException("RELEASE_INVENTORY_INVALID");
        return value.GetString();
    }
    private static DateTimeOffset? OptionalDate(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out JsonElement value)) throw new ReleaseException("RELEASE_INVENTORY_INVALID");
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out DateTimeOffset date))
            throw new ReleaseException("RELEASE_INVENTORY_INVALID");
        return date;
    }
    private static void RequireObject(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new ReleaseException("RELEASE_INVENTORY_INVALID");
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (item.EnumerateObject().Any(property => !names.Add(property.Name)))
            throw new ReleaseException("RELEASE_INVENTORY_INVALID");
    }

    private static string Allocate(HashSet<string> occupied, string original, string type, long id, ref int generation)
    {
        string safe = new string(original.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray())
            .Trim('.');
        if (safe.Length == 0) safe = "item";
        if (safe.Length > 60) safe = safe[..60];
        while (true)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}:{generation}:{original}")))[..8];
            string candidate = $"{safe}-{type}{id}-g{generation}-{hash}";
            if (occupied.Add(candidate)) return candidate;
            generation = checked(generation + 1);
        }
    }

    private static void AuditTree(string root)
    {
        try
        {
            using var lease = NativeFileSystem.PinDirectories(root);
            Walk(root);
        }
        catch (PathBoundaryException error) { throw new ReleaseException("RELEASE_PATH_" + error.Code); }
        catch (Exception error) when (error is not ReleaseException && error is IOException or UnauthorizedAccessException)
        { throw new ReleaseException("RELEASE_SOURCE_INSPECTION_FAILED"); }
        static void Walk(string path)
        {
            bool directory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            using var handle = NativeFileSystem.Open(path);
            NativeFileSystem.Inspect(handle, path, directory);
            if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(handle), WindowsIdentity.GetCurrent().User!, true) != AclRisk.Safe)
                throw new ReleaseException("RELEASE_SOURCE_ACL_UNSAFE");
            if (directory)
                foreach (string child in Directory.EnumerateFileSystemEntries(path)) Walk(NativeFileSystem.CanonicalPath(child));
        }
    }

    private static async Task<Dictionary<long, CurrentRelease>> ReadCurrentAsync(string root, CancellationToken token)
    {
        string index = Path.Combine(root, "releases.json");
        if (!File.Exists(index)) return [];
        try
        {
            using JsonDocument json = await MetadataJson.ReadDocumentAsync(index, token, 8 * 1024 * 1024).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return [];
            var result = new Dictionary<long, CurrentRelease>();
            var localTags = new HashSet<string>(Names);
            var assetIds = new HashSet<long>();
            foreach (JsonElement item in json.RootElement.EnumerateArray())
            {
                if (!ValidIndexObject(item) || !item.TryGetProperty("releaseId", out JsonElement idValue)
                    || !idValue.TryGetInt64(out long id) || id <= 0
                    || !item.TryGetProperty("tagName", out JsonElement tagValue) || tagValue.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("localTag", out JsonElement localTagValue) || localTagValue.ValueKind != JsonValueKind.String
                    || !SafeLocal(localTagValue.GetString()) || !localTags.Add(localTagValue.GetString()!)
                    || !item.TryGetProperty("generation", out JsonElement generationValue)
                    || !generationValue.TryGetInt32(out int generation) || generation <= 0
                    || !item.TryGetProperty("assets", out JsonElement assetsValue) || assetsValue.ValueKind != JsonValueKind.Array)
                    return [];
                var assets = new Dictionary<long, CurrentAsset>();
                var localNames = new HashSet<string>(Names) { "release.json" };
                foreach (JsonElement asset in assetsValue.EnumerateArray())
                {
                    if (!ValidIndexObject(asset) || !asset.TryGetProperty("id", out JsonElement assetIdValue) || !assetIdValue.TryGetInt64(out long assetId) || assetId <= 0
                        || !assetIds.Add(assetId)
                        || !asset.TryGetProperty("originalName", out JsonElement nameValue) || nameValue.ValueKind != JsonValueKind.String
                        || !asset.TryGetProperty("localName", out JsonElement localNameValue) || localNameValue.ValueKind != JsonValueKind.String
                        || !SafeLocal(localNameValue.GetString()) || !localNames.Add(localNameValue.GetString()!)
                        || !asset.TryGetProperty("generation", out JsonElement assetGenerationValue)
                        || !assetGenerationValue.TryGetInt32(out int assetGeneration) || assetGeneration <= 0
                        || !asset.TryGetProperty("size", out JsonElement sizeValue) || !sizeValue.TryGetInt64(out long size) || size < 0
                        || !asset.TryGetProperty("updatedAt", out JsonElement updatedValue) || !updatedValue.TryGetDateTimeOffset(out DateTimeOffset updated))
                        return [];
                    string? digest = asset.TryGetProperty("digest", out JsonElement digestValue) && digestValue.ValueKind == JsonValueKind.String
                        ? digestValue.GetString() : null;
                    if (!asset.TryGetProperty("digest", out digestValue) || digestValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                        || digest is not null && (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                            || !digest.AsSpan(7).ToArray().All(Uri.IsHexDigit))) return [];
                    if (!assets.TryAdd(assetId, new(nameValue.GetString()!, localNameValue.GetString()!, assetGeneration, size, digest, updated))) return [];
                }
                DateTimeOffset? published = item.GetProperty("publishedAt").ValueKind == JsonValueKind.Null
                    ? null : item.GetProperty("publishedAt").GetDateTimeOffset();
                if (!result.TryAdd(id, new(tagValue.GetString()!, item.GetProperty("name").GetString()!,
                    item.GetProperty("isDraft").GetBoolean(), item.GetProperty("isPrerelease").GetBoolean(),
                    published, localTagValue.GetString()!, generation, assets))) return [];
            }
            foreach ((long id, CurrentRelease current) in result)
                if (!await ValidTagSnapshotAsync(root, id, current, token).ConfigureAwait(false)) return [];
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { return []; } // Untrusted/v1 metadata reserves names but cannot select current.
    }
    private static bool SafeLocal(string? name) => !string.IsNullOrEmpty(name) && name is not "." and not ".."
        && name.Length <= 200 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        && !name.EndsWith('.');
    private static async Task<bool> ValidTagSnapshotAsync(string root, long releaseId, CurrentRelease current,
        CancellationToken token)
    {
        try
        {
            using JsonDocument json = await MetadataJson.ReadDocumentAsync(
                Path.Combine(root, current.LocalTag, "release.json"), token, 8 * 1024 * 1024).ConfigureAwait(false);
            return ValidTagSnapshot(json.RootElement, releaseId, current.TagName, current.Name, current.IsDraft,
                current.IsPrerelease, current.PublishedAt, current.LocalTag, current.Generation, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return false; }
    }
    private static bool ValidTagSnapshot(JsonElement item, long releaseId, string tagName, string name,
        bool draft, bool prerelease, DateTimeOffset? publishedAt, string localTag, int generation,
        IReadOnlyList<ReleaseAssetGeneration>? initialAssets)
    {
        if (!ValidIndexObject(item)
            || !item.TryGetProperty("releaseId", out JsonElement id) || !id.TryGetInt64(out long storedId) || storedId != releaseId
            || !item.TryGetProperty("tagName", out JsonElement tag) || tag.ValueKind != JsonValueKind.String || tag.GetString() != tagName
            || !item.TryGetProperty("name", out JsonElement storedName) || storedName.ValueKind != JsonValueKind.String || storedName.GetString() != name
            || !item.TryGetProperty("isDraft", out JsonElement storedDraft) || storedDraft.GetBoolean() != draft
            || !item.TryGetProperty("isPrerelease", out JsonElement storedPrerelease) || storedPrerelease.GetBoolean() != prerelease
            || !item.TryGetProperty("publishedAt", out JsonElement published)
            || !(published.ValueKind == JsonValueKind.Null && publishedAt is null
                || published.ValueKind == JsonValueKind.String && published.TryGetDateTimeOffset(out DateTimeOffset date) && date == publishedAt)
            || !item.TryGetProperty("localTag", out JsonElement local) || local.ValueKind != JsonValueKind.String || local.GetString() != localTag
            || !item.TryGetProperty("generation", out JsonElement number) || !number.TryGetInt32(out int storedGeneration) || storedGeneration != generation
            || !item.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array
            || assets.GetArrayLength() > MaxAssets || initialAssets is not null && assets.GetArrayLength() != initialAssets.Count)
            return false;
        var ids = new HashSet<long>();
        var names = new HashSet<string>(Names) { "release.json" };
        int position = 0;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!ValidIndexObject(asset)
                || !asset.TryGetProperty("id", out JsonElement assetId) || !assetId.TryGetInt64(out long assetNumber) || assetNumber <= 0 || !ids.Add(assetNumber)
                || !asset.TryGetProperty("originalName", out JsonElement original) || original.ValueKind != JsonValueKind.String
                || original.GetString() is not string originalName || originalName.Length is 0 or > 1024
                || !asset.TryGetProperty("localName", out JsonElement localNameValue) || localNameValue.ValueKind != JsonValueKind.String
                || !SafeLocal(localNameValue.GetString()) || !names.Add(localNameValue.GetString()!)
                || !asset.TryGetProperty("generation", out JsonElement assetGeneration) || !assetGeneration.TryGetInt32(out int assetGenerationNumber) || assetGenerationNumber <= 0
                || !asset.TryGetProperty("size", out JsonElement sizeValue) || !sizeValue.TryGetInt64(out long size) || size < 0
                || !asset.TryGetProperty("digest", out JsonElement digestValue) || digestValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                || !asset.TryGetProperty("updatedAt", out JsonElement updated) || !updated.TryGetDateTimeOffset(out DateTimeOffset updatedAt))
                return false;
            string? digest = digestValue.ValueKind == JsonValueKind.String ? digestValue.GetString() : null;
            if (digest is not null && (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                || !digest.AsSpan(7).ToArray().All(Uri.IsHexDigit))) return false;
            if (initialAssets is not null)
            {
                ReleaseAssetGeneration expected = initialAssets[position];
                if (expected.Asset.Id != assetNumber || expected.Asset.Name != originalName
                    || expected.LocalName != localNameValue.GetString() || expected.Generation != assetGenerationNumber
                    || expected.Asset.Size != size || !Names.Equals(expected.Asset.Digest, digest)
                    || expected.Asset.UpdatedAt != updatedAt) return false;
            }
            position++;
        }
        return true;
    }
    private static bool ValidIndexObject(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (item.EnumerateObject().Any(property => !names.Add(property.Name))) return false;
        if (item.TryGetProperty("releaseId", out _))
        {
            if (!item.TryGetProperty("isDraft", out JsonElement draft) || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !item.TryGetProperty("isPrerelease", out JsonElement prerelease) || prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !item.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("publishedAt", out JsonElement published) || published.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                || published.ValueKind == JsonValueKind.String && !published.TryGetDateTimeOffset(out _)) return false;
        }
        return true;
    }
    private sealed record CurrentAsset(string OriginalName, string LocalName, int Generation, long Size,
        string? Digest, DateTimeOffset UpdatedAt);
    private sealed record CurrentRelease(string TagName, string Name, bool IsDraft, bool IsPrerelease,
        DateTimeOffset? PublishedAt, string LocalTag, int Generation,
        IReadOnlyDictionary<long, CurrentAsset> Assets);
}
