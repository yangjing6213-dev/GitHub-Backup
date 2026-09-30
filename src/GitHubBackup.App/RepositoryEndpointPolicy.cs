namespace GitHubBackup.App;

internal sealed record RepositoryEndpointValidation(bool Allowed, string CanonicalNameWithOwner,
    string CanonicalHtmlUrl, string CanonicalCloneUrl, string CanonicalWikiUrl, string ErrorCode);

internal static class RepositoryEndpointPolicy
{
    internal static RepositoryEndpointValidation ValidateAndCreate(string authenticatedOwner,
        string ownerLogin, string name, string fullName, string htmlUrl)
    {
        const string invalid = "REPOSITORY_DISCOVERY_INVALID";
        if (!AuthConfigLease.IsLogin(authenticatedOwner) || !AuthConfigLease.IsLogin(ownerLogin)
            || !string.Equals(authenticatedOwner, ownerLogin, StringComparison.OrdinalIgnoreCase)
            || name.Length is < 1 or > 100 || name is "." or ".." || name[0] == '-'
            || name.EndsWith('.') || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            || fullName != ownerLogin + "/" + name
            || htmlUrl != "https://github.com/" + ownerLogin + "/" + name)
            return new(false, "", "", "", "", invalid);
        string canonical = "https://github.com/" + ownerLogin + "/" + name;
        return new(true, fullName, canonical, canonical + ".git", canonical + ".wiki.git", "");
    }
}
