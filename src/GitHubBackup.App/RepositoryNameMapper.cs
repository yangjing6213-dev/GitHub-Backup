using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GitHubBackup.App;

internal static class RepositoryNameMapper
{
    private static readonly string[] Trees = ["mirrors", "wikis", "metadata", "releases"];

    internal static IReadOnlyList<RepositoryDescriptor> Reconcile(string ownerRoot,
        IReadOnlyList<RepositoryDescriptor> previous, IReadOnlyList<RepositoryDescriptor> current,
        IReadOnlyList<LegacyBinding> bindings)
    {
        string owner = NativeFileSystem.CanonicalPath(ownerRoot);
        using PathLease ownerLease = SummaryStore.RequirePrivateDirectory(owner);

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RepositoryDescriptor historical in previous)
        {
            if (!IsSafeLocalName(historical.LocalName) || !reserved.Add(historical.LocalName))
                throw new InvalidDataException("HISTORICAL_LOCAL_NAME_INVALID");
        }
        foreach (string tree in Trees)
        {
            string directory = Path.Combine(owner, tree);
            PathLease lease;
            try { lease = SummaryStore.RequirePrivateDirectory(directory); }
            catch (FileNotFoundException) { continue; }
            using (lease)
            {
                if (!new SourceIntegrityAudit().ValidateExistingTrees(directory, [], true).Allowed)
                    throw new UnauthorizedAccessException("SOURCE_INTEGRITY_UNSAFE");
                foreach (string child in Directory.EnumerateFileSystemEntries(directory))
                {
                    string name = Path.GetFileName(child);
                    if (tree == "mirrors" && name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                    if (tree == "wikis" && name.EndsWith(".wiki.git", StringComparison.OrdinalIgnoreCase)) name = name[..^9];
                    if (!string.IsNullOrEmpty(name)) reserved.Add(name);
                }
            }
        }

        foreach (LegacyBinding binding in bindings.Where(x => x.Bound))
            if (!string.Equals(NativeFileSystem.CanonicalPath(binding.OwnerRoot), owner, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("LEGACY_BINDING_ROOT_MISMATCH");
        var bound = bindings.Where(x => x.Bound).ToDictionary(x => x.LocalName, x => x.BoundRepositoryId,
            StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<long, RepositoryDescriptor>();
        var result = new List<RepositoryDescriptor>();
        foreach (RepositoryDescriptor old in previous)
        {
            if (old.RepositoryId < 0) throw new InvalidDataException("HISTORICAL_REPOSITORY_ID_INVALID");
            long id = old.RepositoryId == 0 && bound.TryGetValue(old.LocalName, out long binding) ? binding : old.RepositoryId;
            if (id > 0 && !byId.TryAdd(id, old)) throw new InvalidDataException("HISTORICAL_REPOSITORY_ID_DUPLICATE");
            result.Add(old with { RepositoryId = id, RemoteState = id == 0 ? "legacy-unresolved" : "deleted" });
        }
        var currentIds = new HashSet<long>();
        var collidingNames = current.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (RepositoryDescriptor discovered in current)
        {
            if (discovered.RepositoryId <= 0 || !currentIds.Add(discovered.RepositoryId))
                throw new InvalidDataException("CURRENT_REPOSITORY_ID_INVALID");
            string localName = byId.TryGetValue(discovered.RepositoryId, out RepositoryDescriptor? old)
                ? old.LocalName : MapNew(discovered.RepositoryId, discovered.Name, reserved,
                    collidingNames.Contains(discovered.Name));
            ValidateMappedPaths(owner, localName);
            RepositoryDescriptor mapped = discovered with { LocalName = localName, RemoteState = "active" };
            if (old is null) result.Add(mapped);
            else result[result.FindIndex(x => x.RepositoryId == discovered.RepositoryId)] = mapped;
            reserved.Add(localName);
        }
        return new Reconciled(owner,result);
    }

    // Only Reconcile can mint write authority; discovery descriptors are proposals.
    private sealed class Reconciled(string owner,List<RepositoryDescriptor> items)
        : System.Collections.ObjectModel.ReadOnlyCollection<RepositoryDescriptor>(items.ToArray())
    { internal string Owner {get;}=owner; }

    internal static void RequireReconciled(string owner,IReadOnlyList<RepositoryDescriptor> repositories)
    {
        if(repositories is not Reconciled frozen || !string.Equals(frozen.Owner,NativeFileSystem.CanonicalPath(owner),StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("RECONCILED_MAPPING_REQUIRED");
    }

    internal static string MapNew(long repositoryId, string name, ISet<string> reserved, bool forceHash = false)
    {
        if (repositoryId <= 0 || string.IsNullOrEmpty(name)) throw new ArgumentException("REPOSITORY_NAME_INVALID");
        bool safe = IsSafeLocalName(name);
        if (safe && !forceHash && !reserved.Contains(name)) return name;
        string stem = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray())
            .Trim('.', '-', ' ');
        if (stem.Length == 0) stem = "repository";
        if (stem.Length > 90) stem = stem[..90].TrimEnd('.');
        if (!IsSafeLocalName(stem + "-x")) stem = stem.Replace('.', '-');
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(repositoryId.ToString(CultureInfo.InvariantCulture))));
        for (int length = 8; length <= hash.Length; length += 8)
        {
            string prefix = stem[..Math.Min(stem.Length, 99 - length)].TrimEnd('.');
            string candidate = prefix + "-" + hash[..length];
            if (IsSafeLocalName(candidate) && !reserved.Contains(candidate)) return candidate;
        }
        throw new InvalidDataException("REPOSITORY_LOCAL_NAME_COLLISION");
    }

    internal static bool IsSafeLocalName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 100 || name is "." or ".." || name[0] == '-'
            || name.EndsWith('.') || name.EndsWith(' ')
            || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) return false;
        string device = name.Split('.')[0];
        return !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(device, StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateMappedPaths(string owner, string localName)
    {
        if (!IsSafeLocalName(localName)) throw new InvalidDataException("REPOSITORY_LOCAL_NAME_INVALID");
        foreach (string tree in Trees)
        {
            string parent = NativeFileSystem.CanonicalPath(Path.Combine(owner, tree));
            string suffix = tree switch { "mirrors" => ".git", "wikis" => ".wiki.git", _ => "" };
            string child = NativeFileSystem.CanonicalPath(Path.Combine(parent, localName + suffix));
            if (!child.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("REPOSITORY_PATH_OUTSIDE_OWNER");
        }
    }
}
