using System.Text.Json;
using System.Globalization;

namespace GitHubBackup.App;

internal sealed class RepositoryDiscoveryService(IGitHubHttpTransport transport)
{
    private const int MaxDiscoveryPageBytes = 8 * 1024 * 1024;
    internal Task<IReadOnlyList<RepositoryDescriptor>> DiscoverAsync(string owner, CancellationToken token) =>
        DiscoverAsync(owner, "", false, token);

    internal async Task<IReadOnlyList<RepositoryDescriptor>> DiscoverAsync(string owner, string scopeOwner,
        bool collaborators, CancellationToken token)
    {
        if (!AuthConfigLease.IsLogin(owner) || transport.BoundAccountId <= 0
            || !string.Equals(owner, transport.BoundLogin, StringComparison.OrdinalIgnoreCase))
            throw new HttpTransferException("REPOSITORY_DISCOVERY_INVALID");
        if (scopeOwner.Length > 0 && !AuthConfigLease.IsLogin(scopeOwner))
            throw new HttpTransferException("REPOSITORY_DISCOVERY_INVALID");
        bool ownScope = !collaborators && (scopeOwner.Length == 0 || string.Equals(scopeOwner, owner, StringComparison.OrdinalIgnoreCase));
        var repositories = new List<RepositoryDescriptor>();
        var ids = new HashSet<long>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int page = 1;
        do
        {
            token.ThrowIfCancellationRequested();
            using GitHubResponse response = await transport.SendAsync(GitHubRequest.ForRepositoryScope(owner, scopeOwner, collaborators,
                page == 1 ? null : page), token).ConfigureAwait(false);
            if (response.StatusCode != 200) throw new HttpTransferException("REPOSITORY_DISCOVERY_INVALID");
            try
            {
                long? declared = null;
                if (response.Headers.TryGetValue("Content-Length", out string? contentLength))
                {
                    if (!long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
                        || parsed > MaxDiscoveryPageBytes)
                        throw new HttpTransferException("REPOSITORY_DISCOVERY_INVALID");
                    declared = parsed;
                }
                using var bounded = new MemoryStream();
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = await response.Body.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    if (bounded.Length > MaxDiscoveryPageBytes - read)
                        throw new HttpTransferException("REPOSITORY_DISCOVERY_INVALID");
                    bounded.Write(buffer, 0, read);
                }
                if (declared is not null && declared != bounded.Length)
                    throw new HttpTransferException("REPOSITORY_DISCOVERY_INVALID");
                bounded.Position = 0;
                using JsonDocument json = await JsonDocument.ParseAsync(bounded, cancellationToken: token).ConfigureAwait(false);
                if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 100)
                    throw new JsonException();
                foreach (JsonElement entry in json.RootElement.EnumerateArray())
                {
                    long id = Required(entry, "id").GetInt64();
                    string name = Required(entry, "name").GetString()!;
                    string fullName = Required(entry, "full_name").GetString()!;
                    string htmlUrl = Required(entry, "html_url").GetString()!;
                    JsonElement ownerObject = Required(entry, "owner");
                    string actualOwner = Required(ownerObject, "login").GetString()!;
                    long ownerId = Required(ownerObject, "id").GetInt64();
                    long size = Required(entry, "size").GetInt64();
                    bool isPrivate = Required(entry, "private").GetBoolean();
                    bool archived = Required(entry, "archived").GetBoolean();
                    bool fork = Required(entry, "fork").GetBoolean();
                    bool wiki = Required(entry, "has_wiki").GetBoolean();
                    DateTimeOffset? updated = null;
                    if (entry.TryGetProperty("updated_at", out JsonElement date) && date.ValueKind != JsonValueKind.Null)
                        updated = date.GetDateTimeOffset();
                    RepositoryEndpointValidation endpoint = ownScope
                        ? RepositoryEndpointPolicy.ValidateAndCreate(owner, actualOwner, name, fullName, htmlUrl)
                        : RepositoryEndpointPolicy.ValidateAndCreateForScope(owner, actualOwner, name, fullName, htmlUrl);
                    bool ownerAllowed = ownScope
                        ? ownerId == transport.BoundAccountId && string.Equals(actualOwner, owner, StringComparison.OrdinalIgnoreCase)
                        : collaborators || string.Equals(actualOwner, scopeOwner, StringComparison.OrdinalIgnoreCase);
                    if (id <= 0 || !ids.Add(id) || size < 0 || ownerId <= 0 || !ownerAllowed
                        || !endpoint.Allowed || !names.Add(endpoint.CanonicalNameWithOwner))
                        throw new JsonException();
                    repositories.Add(new(id, name, endpoint.CanonicalNameWithOwner, endpoint.CanonicalHtmlUrl,
                        isPrivate, archived, fork, wiki, updated, size, name, "active"));
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or NullReferenceException)
            { throw new HttpTransferException("REPOSITORY_DISCOVERY_INVALID"); }
            int? next = response.NextPage;
            if (next is null) break;
            if (next != page + 1 || next > 1_000_000) throw new HttpTransferException("HTTP_PAGINATION_REJECTED");
            page = next.Value;
        } while (true);
        return repositories.AsReadOnly();
    }

    private static JsonElement Required(JsonElement item, string key)
    {
        if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count(property => property.NameEquals(key)) != 1)
            throw new JsonException();
        return item.GetProperty(key);
    }

}
