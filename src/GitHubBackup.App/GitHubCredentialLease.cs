using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace GitHubBackup.App;

// This lease is deliberately unverified. Task 3 must bind it with fixed GET /user
// before any discovery or backup request can receive it.
internal sealed class GitHubCredentialLease : IDisposable
{
    private readonly byte[] secret;
    private bool disposed;
    internal string Login { get; }

    internal GitHubCredentialLease(string login, byte[] ownedSecret)
    {
        Login = login;
        secret = ownedSecret;
    }

    internal void AttachAuthorization(HttpRequestMessage request)
    {
        lock (secret)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Uri? uri = request.RequestUri;
            if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
                || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || request.Headers.Authorization is not null)
                throw new AuthBoundaryException("AUTH_API_ORIGIN_REJECTED");
            // Only the owned byte array can be zeroed. The short-lived managed header
            // string, OS paging, and crash dumps cannot be reliably erased here.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.ASCII.GetString(secret));
        }
    }

    public void Dispose()
    {
        lock (secret)
        {
            if (disposed) return;
            disposed = true;
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
