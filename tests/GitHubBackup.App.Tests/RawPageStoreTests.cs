using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class RawPageStoreTests
{
    [TestMethod]
    public async Task Single_object_body_is_stored_without_byte_changes()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[] bytes = Encoding.UTF8.GetBytes(" \n{\"body\":\"\\u001b é\",\"c1\":\"\u0081\"}\t");
        using var response = Response(bytes);

        RawPageWriteResult result = await new RawPageStore(directory)
            .SaveJsonPageAsync("repository.json", response, PageEnvelope.SingleObject, default);

        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(directory, "repository.json")));
        Assert.AreEqual(bytes.Length, result.Length);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), result.Sha256);
    }

    [TestMethod]
    public async Task Array_and_object_pages_add_only_outer_delimiters()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        byte[] first = Encoding.UTF8.GetBytes(" [ {\"body\":\"\\u001b\",\"nul\":\"\\u0000\"} ]\n");
        byte[] second = Encoding.UTF8.GetBytes("\t[ {\"text\":\"é\"} ] ");
        var transport = new PageTransport(page => page == 1 ? Response(first, 2) : Response(second));

        await new RawPageStore(directory).SaveMetadataAsync("fixture-user", "repo", "issues.pages.json", transport, default);

        CollectionAssert.AreEqual("["u8.ToArray().Concat(first).Concat(","u8.ToArray()).Concat(second).Concat("]"u8.ToArray()).ToArray(),
            await File.ReadAllBytesAsync(Path.Combine(directory, "issues.pages.json")));
        byte[] objectPage = "{\"total_count\":0,\"workflows\":[]}"u8.ToArray();
        using var objectResponse = Response(objectPage);
        await new RawPageStore(directory).SaveJsonPageAsync("workflows.pages.json", objectResponse, PageEnvelope.ObjectPages, default);
        CollectionAssert.AreEqual("["u8.ToArray().Concat(objectPage).Concat("]"u8.ToArray()).ToArray(),
            await File.ReadAllBytesAsync(Path.Combine(directory, "workflows.pages.json")));
    }

    [TestMethod]
    public async Task Bad_json_or_incomplete_page_keeps_previous_file()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        string target = Path.Combine(directory, "issues.pages.json");
        await File.WriteAllBytesAsync(target, "[[{\"old\":true}]]"u8.ToArray());
        var transport = new PageTransport(page => page == 1 ? Response("[]"u8.ToArray(), 2) : Response("[invalid]"u8.ToArray()));

        await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            new RawPageStore(directory).SaveMetadataAsync("fixture-user", "repo", "issues.pages.json", transport, default));
        CollectionAssert.AreEqual("[[{\"old\":true}]]"u8.ToArray(), await File.ReadAllBytesAsync(target));
        using var incomplete = Response("[]"u8.ToArray(), 2);
        await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            new RawPageStore(directory).SaveJsonPageAsync("issues.pages.json", incomplete, PageEnvelope.ArrayPages, default));
        CollectionAssert.AreEqual("[[{\"old\":true}]]"u8.ToArray(), await File.ReadAllBytesAsync(target));
    }

    [TestMethod]
    public async Task Literal_nul_in_invalid_json_does_not_replace_previous_page()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        string target = Path.Combine(directory, "issues.pages.json");
        await File.WriteAllBytesAsync(target, "[[]]"u8.ToArray());
        byte[] invalid = Encoding.UTF8.GetBytes("[{\"body\":\"x\0y\"}]");
        using var response = Response(invalid);

        var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            new RawPageStore(directory).SaveJsonPageAsync("issues.pages.json", response, PageEnvelope.ArrayPages, default));

        Assert.AreEqual("HTTP_METADATA_INVALID", error.Code);
        CollectionAssert.AreEqual("[[]]"u8.ToArray(), await File.ReadAllBytesAsync(target));
    }

    [TestMethod]
    public async Task Skipped_page_does_not_replace_previous_metadata()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        string target = Path.Combine(directory, "issues.pages.json");
        await File.WriteAllBytesAsync(target, "[[{\"old\":true}]]"u8.ToArray());
        int requests = 0;
        var transport = new PageTransport(_ => ++requests == 1
            ? Response("[]"u8.ToArray(), 3) : Response("[]"u8.ToArray()));

        var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            new RawPageStore(directory).SaveMetadataAsync("fixture-user", "repo", "issues.pages.json", transport, default));

        Assert.AreEqual("HTTP_PAGINATION_REJECTED", error.Code);
        Assert.AreEqual(1, requests);
        CollectionAssert.AreEqual("[[{\"old\":true}]]"u8.ToArray(), await File.ReadAllBytesAsync(target));
    }

    [TestMethod]
    public async Task Oversized_streamed_metadata_page_fails_closed_and_keeps_previous_file()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        string target = Path.Combine(directory, "issues.pages.json");
        await File.WriteAllBytesAsync(target, "[[]]"u8.ToArray());
        using var response = new GitHubResponse(200, new Dictionary<string,string>(),
            new RepeatStream(32L * 1024 * 1024 + 1));

        var error = await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            new RawPageStore(directory).SaveJsonPageAsync("issues.pages.json", response, PageEnvelope.ArrayPages, default));

        Assert.AreEqual("HTTP_METADATA_INVALID", error.Code);
        CollectionAssert.AreEqual("[[]]"u8.ToArray(), await File.ReadAllBytesAsync(target));
    }

    [TestMethod]
    public async Task Bound_production_transport_streams_exact_pages_and_keeps_previous_file_on_bad_followup()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        string target = Path.Combine(directory, "issues.pages.json");
        byte[] first = Encoding.UTF8.GetBytes(" \n[{\"body\":\"\\u001b é\",\"c1\":\"\u0081\"}]\t");
        byte[] second = Encoding.UTF8.GetBytes("\t[{\"body\":\"\\u0000\"}] \n");
        bool badSecond = false;
        var requests = new List<string>();
        var handler = new ScriptedHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            Assert.AreEqual("Bearer SYNTHETIC_RAW_PAGE_CANARY", request.Headers.Authorization?.ToString());
            Assert.AreEqual("identity", request.Headers.AcceptEncoding.Single().Value);
            if (request.RequestUri.AbsolutePath == "/user") return Ok("{\"login\":\"fixture-user\",\"id\":7}"u8.ToArray());
            var response = Ok(request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal)
                ? badSecond ? "[invalid]"u8.ToArray() : second : first);
            if (!request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal))
                response.Headers.TryAddWithoutValidation("Link",
                    "<https://api.github.com/repos/fixture-user/repo/issues?per_page=100&page=2&state=all>; rel=\"next\"");
            return response;
        });
        using var transport = await GitHubHttpTransport.BindAsync(
            new GitHubCredentialLease("fixture-user", "SYNTHETIC_RAW_PAGE_CANARY"u8.ToArray()),
            "fixture-user", ProxyProfile.Direct, default, _ => handler);
        var store = new RawPageStore(directory);

        await store.SaveMetadataAsync("fixture-user", "repo", "issues.pages.json", transport, default);
        byte[] expected = "["u8.ToArray().Concat(first).Concat(","u8.ToArray()).Concat(second).Concat("]"u8.ToArray()).ToArray();
        CollectionAssert.AreEqual(expected, await File.ReadAllBytesAsync(target));
        badSecond = true;
        Assert.AreEqual("HTTP_METADATA_INVALID", (await Assert.ThrowsExactlyAsync<HttpTransferException>(() =>
            store.SaveMetadataAsync("fixture-user", "repo", "issues.pages.json", transport, default))).Code);
        CollectionAssert.AreEqual(expected, await File.ReadAllBytesAsync(target));
        CollectionAssert.AreEqual(new[] {
            "/user",
            "/repos/fixture-user/repo/issues?state=all&per_page=100",
            "/repos/fixture-user/repo/issues?state=all&per_page=100&page=2",
            "/repos/fixture-user/repo/issues?state=all&per_page=100",
            "/repos/fixture-user/repo/issues?state=all&per_page=100&page=2" }, requests);
    }

    [TestMethod]
    public async Task Summary_is_not_accepted_as_raw_server_response()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        using var response = Response("{}"u8.ToArray());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new RawPageStore(directory).SaveJsonPageAsync("repository-summary.json", response, PageEnvelope.SingleObject, default));
    }

    [TestMethod]
    public async Task Locally_generated_summary_has_the_nine_legacy_control_fields()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("metadata");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        var repository = new RepositoryDescriptor(42, "repo", "fixture-user/repo", "https://github.com/fixture-user/repo",
            true, false, false, true, DateTimeOffset.Parse("2026-09-20T00:00:00Z"), 7, "repo", "active");

        await new RawPageStore(directory).SaveRepositorySummaryAsync(repository, default);

        using JsonDocument json = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "repository-summary.json")));
        CollectionAssert.AreEquivalent(new[] { "name", "nameWithOwner", "url", "isPrivate", "isArchived", "isFork",
            "hasWikiEnabled", "updatedAt", "diskUsage" },
            json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual("fixture-user/repo", json.RootElement.GetProperty("nameWithOwner").GetString());
    }

    private static GitHubResponse Response(byte[] bytes, int? next = null) => new(200,
        new Dictionary<string,string> { ["Content-Length"] = bytes.Length.ToString() }, new MemoryStream(bytes), next);

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class ScriptedHandler(Func<HttpRequestMessage,HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(reply(request)); }
    }

    private sealed class PageTransport(Func<int,GitHubResponse> reply) : IGitHubHttpTransport
    {
        public string BoundLogin => "fixture-user";
        public long BoundAccountId => 7;
        public Task<GitHubResponse> SendAsync(GitHubRequest request, CancellationToken token) => Task.FromResult(reply(request.Page));
        public Task<GitHubResponse> DownloadAssetAsync(AssetIdentity asset, CancellationToken token) => throw new AssertFailedException();
        public void Dispose() { }
    }

    private sealed class RepeatStream(long totalLength) : Stream
    {
        private long consumed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = (int)Math.Min(buffer.Length, totalLength - consumed);
            buffer.Span[..count].Fill((byte)' ');
            if (count > 0 && consumed == 0) buffer.Span[0] = (byte)'[';
            if (count > 0 && consumed + count == totalLength) buffer.Span[count - 1] = (byte)']';
            consumed += count;
            return ValueTask.FromResult(count);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
