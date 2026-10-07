using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace GitHubBackup.App;

internal enum PageEnvelope { SingleObject, ArrayPages, ObjectPages }
internal sealed record RawPageWriteResult(long Length, string Sha256);

internal sealed class RawPageStore(string metadataDirectory)
{
    private const long MaxMetadataPageBytes = 32L * 1024 * 1024;
    internal Task<RawPageWriteResult> SaveJsonPageAsync(string fileName, GitHubResponse response,
        PageEnvelope envelope, CancellationToken token)
    {
        if (response.NextPage is not null) throw new HttpTransferException("HTTP_PAGINATION_REJECTED");
        ValidateFile(fileName, envelope);
        return SaveAsync(fileName, envelope, async (output, ct) =>
        {
            await WritePageAsync(output, response, envelope, ct).ConfigureAwait(false);
        }, token);
    }

    internal Task<RawPageWriteResult> SaveMetadataAsync(string owner, string repository, string fileName,
        IGitHubHttpTransport transport, CancellationToken token)
        => SaveMetadataAsync(owner, owner, repository, fileName, transport, token);

    internal Task<RawPageWriteResult> SaveMetadataAsync(string authenticatedOwner, string repositoryOwner, string repository, string fileName,
        IGitHubHttpTransport transport, CancellationToken token)
    {
        PageEnvelope envelope = EnvelopeFor(fileName);
        if (transport.BoundAccountId <= 0 || !string.Equals(transport.BoundLogin, authenticatedOwner, StringComparison.OrdinalIgnoreCase))
            throw new HttpTransferException("HTTP_REPOSITORY_IDENTITY_REJECTED");
        return SaveAsync(fileName, envelope, async (output, ct) =>
        {
            int page = 1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                GitHubRequest request = GitHubRequest.ForMetadata(authenticatedOwner, repositoryOwner, repository, fileName, page == 1 ? null : page);
                using GitHubResponse response = await transport.SendAsync(request, ct).ConfigureAwait(false);
                if (page != 1) await output.WriteAsync(","u8.ToArray(), ct).ConfigureAwait(false);
                await WritePageAsync(output, response, envelope, ct).ConfigureAwait(false);
                if (response.NextPage is not int next) break;
                if (envelope == PageEnvelope.SingleObject || next != page + 1 || next > 1_000_000)
                    throw new HttpTransferException("HTTP_PAGINATION_REJECTED");
                page = next;
            }
        }, token);
    }

    internal Task<RawPageWriteResult> SaveRepositorySummaryAsync(RepositoryDescriptor repository, CancellationToken token)
    {
        string[] parts = repository.NameWithOwner.Split('/');
        if (parts.Length != 2 || repository.RepositoryId <= 0 || repository.DiskUsage < 0
            || parts[1] != repository.Name || repository.Url != "https://github.com/" + repository.NameWithOwner)
            throw new ArgumentException("REPOSITORY_SUMMARY_INVALID");
        _ = GitHubRequest.ForMetadata(parts[0], parts[1], "repository.json");
        return SaveAsync("repository-summary.json", PageEnvelope.SingleObject,
            (output, ct) => JsonSerializer.SerializeAsync(output, new
            {
                repository.Name, repository.NameWithOwner, repository.Url, repository.IsPrivate,
                repository.IsArchived, repository.IsFork, repository.HasWikiEnabled,
                repository.UpdatedAt, repository.DiskUsage
            }, MetadataJson.Options, ct), token);
    }

    private async Task<RawPageWriteResult> SaveAsync(string fileName, PageEnvelope envelope,
        Func<FileStream,CancellationToken,Task> write, CancellationToken token)
    {
        string directory = NativeFileSystem.CanonicalPath(metadataDirectory);
        using PathLease lease = SummaryStore.RequirePrivateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        try
        {
            using var existing = NativeFileSystem.Open(path);
            NativeFileSystem.Inspect(existing, path, directory: false);
            if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(existing), WindowsIdentity.GetCurrent().User!, true) != AclRisk.Safe)
                throw new UnauthorizedAccessException("ACL_PRIVATE_BOUNDARY_REQUIRED");
        }
        catch (FileNotFoundException) { }
        RawPageWriteResult? result = null;
        await AtomicFile.WriteAsync(path, async (output, ct) =>
        {
            if (envelope != PageEnvelope.SingleObject) await output.WriteAsync("["u8.ToArray(), ct).ConfigureAwait(false);
            await write(output, ct).ConfigureAwait(false);
            if (envelope != PageEnvelope.SingleObject) await output.WriteAsync("]"u8.ToArray(), ct).ConfigureAwait(false);
            long length = output.Position;
            output.Position = 0;
            string sha = Convert.ToHexString(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false));
            output.Position = length;
            result = new(length, sha);
        }, token).ConfigureAwait(false);
        return result!;
    }

    private static async Task WritePageAsync(FileStream output, GitHubResponse response, PageEnvelope envelope, CancellationToken token)
    {
        if (response.StatusCode != 200) throw new HttpTransferException("HTTP_METADATA_INVALID");
        long? declared = null;
        if (response.Headers.TryGetValue("Content-Length", out string? contentLength))
        {
            if (!long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
                || parsed > MaxMetadataPageBytes) throw new HttpTransferException("HTTP_METADATA_INVALID");
            declared = parsed;
        }
        long start = output.Position;
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await response.Body.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Position - start > MaxMetadataPageBytes - read)
                throw new HttpTransferException("HTTP_METADATA_INVALID");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        long end = output.Position;
        if (declared is not null && declared != end - start) throw new HttpTransferException("HTTP_METADATA_INVALID");
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Position = start;
        try
        {
            using JsonDocument json = await JsonDocument.ParseAsync(output, cancellationToken: token).ConfigureAwait(false);
            if (json.RootElement.ValueKind != (envelope == PageEnvelope.ObjectPages || envelope == PageEnvelope.SingleObject
                ? JsonValueKind.Object : JsonValueKind.Array)) throw new JsonException();
        }
        catch (JsonException) { throw new HttpTransferException("HTTP_METADATA_INVALID"); }
        finally { output.Position = end; }
    }

    private static void ValidateFile(string fileName, PageEnvelope envelope)
    {
        if (EnvelopeFor(fileName) != envelope) throw new ArgumentException("HTTP_METADATA_FILE_INVALID");
    }

    private static PageEnvelope EnvelopeFor(string fileName) => fileName switch
    {
        "repository.json" => PageEnvelope.SingleObject,
        "workflows.pages.json" or "actions-runs.pages.json" or "actions-artifacts.pages.json" => PageEnvelope.ObjectPages,
        "issues.pages.json" or "pull-requests.pages.json" or "issue-comments.pages.json" or
            "review-comments.pages.json" or "releases.pages.json" or "labels.pages.json" or
            "milestones.pages.json" => PageEnvelope.ArrayPages,
        _ => throw new ArgumentException("HTTP_METADATA_FILE_INVALID")
    };
}
