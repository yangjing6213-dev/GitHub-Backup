using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace GitHubBackup.App;

internal sealed class AuthBoundaryException(string code) : IOException(code) { internal string Code { get; } = code; }

// A retained capability, not a boolean check. CLI writes in place under READ_DATA
// handles with read/write sharing, but no delete sharing. Call Revalidate after
// every child, before consuming output, and retain this object until it finishes.
internal sealed class AuthConfigLease : IDisposable
{
    private readonly AppPaths paths;
    private readonly AppDataPathLease directory;
    private readonly Dictionary<string,(AppDataPathLease Boundary, SafeFileHandle Handle, NativeFileIdentity Identity)> files = new(StringComparer.Ordinal);
    private bool disposed;
    internal string Login { get; private set; } = "";
    internal static ReadOnlySpan<byte> InertConfig => "version: \"1\"\n"u8;

    internal AuthConfigLease(AppPaths paths, bool create = false)
    {
        this.paths = paths;
        if (!string.Equals(paths.AppGhConfigDirectory, Path.Combine(paths.LocalAppDataRoot, "gh"), StringComparison.OrdinalIgnoreCase))
            throw new AuthBoundaryException("AUTH_CONFIG_LOCATION_REJECTED");
        try
        {
            directory = AppDataPathPolicy.Acquire(paths, paths.AppGhConfigDirectory, AppDataEntryKind.Directory, create);
            try { OpenFiles(); Revalidate(); }
            catch { Dispose(); throw; }
        }
        catch (PathBoundaryException ex) { throw new AuthBoundaryException("AUTH_CONFIG_" + ex.Code); }
        catch (FileNotFoundException) { throw new AuthBoundaryException("AUTH_APP_LOGIN_REQUIRED"); }
        catch (UnauthorizedAccessException) { throw new AuthBoundaryException("AUTH_CONFIG_INACCESSIBLE"); }
        catch (IOException ex) when (ex is not AuthBoundaryException) { throw new AuthBoundaryException("AUTH_CONFIG_INACCESSIBLE"); }
    }
    private string PathFor(string name) => Path.Combine(paths.AppGhConfigDirectory, name);
    private string[] Names()
    {
        string[] names = Directory.EnumerateFileSystemEntries(paths.AppGhConfigDirectory).Select(Path.GetFileName).Select(n => n!).ToArray();
        if (names.Any(n => n is not ("config.yml" or "hosts.yml"))) throw new AuthBoundaryException("AUTH_CONFIG_UNEXPECTED_ENTRY");
        return names;
    }
    private void OpenFiles()
    {
        foreach (string name in Names())
        {
            string path = PathFor(name);
            var boundary = AppDataPathPolicy.Acquire(paths, path, AppDataEntryKind.File);
            SafeFileHandle? handle = null;
            try
            {
                handle = NativeFileSystem.Open(path);
                NativeFileIdentity identity = NativeFileSystem.Inspect(handle, path, false);
                if (identity != boundary.Identity) throw new AuthBoundaryException("AUTH_CONFIG_IDENTITY_CHANGED");
                files.Add(name, (boundary, handle, identity));
            }
            catch { handle?.Dispose(); boundary.Dispose(); throw; }
        }
    }
    private void ValidateIdentities()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var now = AppDataPathPolicy.Acquire(paths, paths.AppGhConfigDirectory, AppDataEntryKind.Directory);
        if (now.Identity != directory.Identity) throw new AuthBoundaryException("AUTH_CONFIG_IDENTITY_CHANGED");
        if (!Names().ToHashSet(StringComparer.Ordinal).SetEquals(files.Keys)) throw new AuthBoundaryException("AUTH_CONFIG_IDENTITY_CHANGED");
        foreach (var (name, file) in files)
        {
            using var check = AppDataPathPolicy.Acquire(paths, PathFor(name), AppDataEntryKind.File);
            if (check.Identity != file.Identity || NativeFileSystem.Inspect(file.Handle, PathFor(name), false) != file.Identity)
                throw new AuthBoundaryException("AUTH_CONFIG_IDENTITY_CHANGED");
        }
    }
    internal void Revalidate()
    {
        try
        {
            ValidateIdentities(); Login = "";
            if (files.TryGetValue("config.yml", out var config))
            {
                byte[] bytes = ReadLimited(config.Handle);
                try { if (!bytes.AsSpan().SequenceEqual(InertConfig)) throw new AuthBoundaryException("AUTH_CONFIG_CONTENT_REJECTED"); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            if (!files.TryGetValue("hosts.yml", out var hosts)) return;
            byte[] data = ReadLimited(hosts.Handle);
            bool plaintext;
            try
            {
                // No secret value is decoded, extracted, or formatted. A conservative
                // key spelling match is enough to reject; all other YAML is strict below.
                plaintext = data.AsSpan().IndexOf("oauth_token"u8) >= 0;
                if (!plaintext)
                {
                    if (!files.ContainsKey("config.yml")) throw new AuthBoundaryException("AUTH_CONFIG_CONTENT_REJECTED");
                    Login = ParseMetadata(data);
                }
            }
            finally { CryptographicOperations.ZeroMemory(data); }
            if (plaintext)
            {
                RemoveHosts();
                throw new AuthBoundaryException("AUTH_PLAINTEXT_RESIDUE_REMOVED");
            }
        }
        catch (PathBoundaryException ex) { throw new AuthBoundaryException("AUTH_CONFIG_" + ex.Code); }
        catch (UnauthorizedAccessException) { throw new AuthBoundaryException("AUTH_CONFIG_INACCESSIBLE"); }
        catch (FileNotFoundException) { throw new AuthBoundaryException("AUTH_CONFIG_IDENTITY_CHANGED"); }
        catch (IOException ex) when (ex is not AuthBoundaryException) { throw new AuthBoundaryException("AUTH_CONFIG_INACCESSIBLE"); }
    }
    internal IReadOnlyDictionary<string,string?> CreateEnvironment(IReadOnlyDictionary<string,string?> environment, bool bindIdentity = true)
    {
        Revalidate();
        var inherited = environment as RuntimeEnvironment;
        inherited?.RevalidateAuthentication();
        var result = ChildEnvironmentBuilder.Build(environment, new Dictionary<string,string?> { ["GH_CONFIG_DIR"] = paths.AppGhConfigDirectory });
        // Official confirmed Login may change the selected account. Its caller
        // already retains/revalidates this lease; read-only operations freeze login.
        return bindIdentity && Login.Length != 0 ? new RuntimeEnvironment(result,inherited?.Owner,this,Login) : result;
    }
    internal void SeedForConfirmedLogin()
    {
        Revalidate();
        foreach (string name in new[] { "config.yml", "hosts.yml" })
        {
            if (files.ContainsKey(name)) continue;
            using var stream = AclPolicy.CreateRestrictedFile(PathFor(name), WindowsIdentity.GetCurrent().User!);
            if (name == "config.yml") stream.Write(InertConfig);
        }
        foreach (var file in files.Values) { file.Handle.Dispose(); file.Boundary.Dispose(); }
        files.Clear(); OpenFiles(); Revalidate();
    }
    internal void RemoveHosts()
    {
        try
        {
            ValidateIdentities();
            if (!files.Remove("hosts.yml", out var file)) return;
            file.Handle.Dispose(); file.Boundary.Dispose();
            using (var handle = NativeFileSystem.Open(PathFor("hosts.yml"), NativeFileSystem.ReadControl | NativeFileSystem.ReadAttributes | NativeFileSystem.DeleteAccess | 1))
            {
                if (NativeFileSystem.Inspect(handle, PathFor("hosts.yml"), false) != file.Identity) throw new AuthBoundaryException("AUTH_CONFIG_CLEANUP_FAILED");
                AclPolicy.VerifyRestricted(handle, WindowsIdentity.GetCurrent().User!);
                NativeFileSystem.DeleteByHandle(handle);
            }
            if (Names().Contains("hosts.yml")) throw new AuthBoundaryException("AUTH_CONFIG_CLEANUP_FAILED");
            Login = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new AuthBoundaryException("AUTH_CONFIG_CLEANUP_FAILED"); }
    }
    internal static byte[] ReadLimited(SafeFileHandle handle)
    {
        long length = RandomAccess.GetLength(handle);
        if (length > 1024 * 1024) throw new AuthBoundaryException("AUTH_CONFIG_SIZE_LIMIT");
        byte[] bytes = new byte[(int)length];
        try
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
                if (count == 0) throw new AuthBoundaryException("AUTH_CONFIG_CONTENT_CHANGED");
                offset += count;
            }
            if (RandomAccess.GetLength(handle) != length) throw new AuthBoundaryException("AUTH_CONFIG_CONTENT_CHANGED");
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }
    internal static bool IsLogin(string value) => Regex.IsMatch(value, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static string ParseMetadata(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        ReadOnlySpan<byte> remaining = bytes;
        if (!remaining.StartsWith("github.com:\n"u8)) throw new AuthBoundaryException("AUTH_CONFIG_CONTENT_REJECTED");
        remaining = remaining[12..];
        bool protocol = false, users = false, seenUsers = false; string login = ""; var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!remaining.IsEmpty)
        {
            int end = remaining.IndexOf((byte)'\n');
            if (end < 0) throw new AuthBoundaryException("AUTH_CONFIG_CONTENT_REJECTED");
            ReadOnlySpan<byte> line = remaining[..end]; remaining = remaining[(end + 1)..];
            if (line.SequenceEqual("    git_protocol: https"u8) && !protocol) { protocol = true; users = false; }
            else if (line.SequenceEqual("    users:"u8) && !seenUsers) { users = true; seenUsers = true; }
            else if (line.StartsWith("    user: "u8) && login.Length == 0) { login = DecodeLogin(line[10..]); users = false; }
            else if (users && line.StartsWith("        "u8))
            {
                ReadOnlySpan<byte> entry = line[8..];
                ReadOnlySpan<byte> name = entry.EndsWith(": {}"u8) ? entry[..^4] : entry.EndsWith(":"u8) ? entry[..^1] : [];
                if (!logins.Add(DecodeLogin(name))) throw new AuthBoundaryException("AUTH_CONFIG_CONTENT_REJECTED");
            }
            else throw new AuthBoundaryException("AUTH_CONFIG_CONTENT_REJECTED");
        }
        if (!protocol || !IsLogin(login) || !logins.Contains(login)) throw new AuthBoundaryException("AUTH_CONFIG_CONTENT_REJECTED");
        return login;
    }
    private static string DecodeLogin(ReadOnlySpan<byte> value)
    {
        if (value.Length >= 2 && value[0] is (byte)'"' or (byte)'\'' && value[^1] == value[0]) value = value[1..^1];
        static bool AlphaNumeric(byte c) => c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9';
        if (value.Length is < 1 or > 39 || !AlphaNumeric(value[0]) || !AlphaNumeric(value[^1])) throw new AuthBoundaryException("AUTH_CONFIG_CONTENT_REJECTED");
        foreach (byte c in value) if (!AlphaNumeric(c) && c != (byte)'-') throw new AuthBoundaryException("AUTH_CONFIG_CONTENT_REJECTED");
        // Only a grammar-validated, bounded public login reaches a string decoder.
        // Unknown keys, escapes, tags and arbitrary values never become strings.
        return Encoding.ASCII.GetString(value);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var file in files.Values) { file.Handle.Dispose(); file.Boundary.Dispose(); }
        directory.Dispose();
    }
}
