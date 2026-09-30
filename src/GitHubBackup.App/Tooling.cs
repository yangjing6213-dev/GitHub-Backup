using System.Text.RegularExpressions;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GitHubBackup.App;

internal enum DependencyId { Git, GitHubCli, GitLfs, Winget }
internal sealed record RegisteredWingetPackage(string FullName, string Path);
internal sealed class WingetPackageResolver(Func<IReadOnlyList<RegisteredWingetPackage>>? packages = null)
{
    private const string Family = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
    internal string Resolve()
    {
        try
        {
            var registered = (packages ?? RegisteredPackages)();
            if (registered.Count != 1) throw new IOException("TOOL_ALIAS_PACKAGE_AMBIGUOUS");
            var package = registered[0];
            Match name = Regex.Match(package.FullName, @"\AMicrosoft\.DesktopAppInstaller_(\d+\.\d+\.\d+\.\d+)_x64__8wekyb3d8bbwe\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!name.Success) throw new IOException("TOOL_ALIAS_PACKAGE_REJECTED");
            string directory = NativeFileSystem.CanonicalPath(package.Path);
            string manifest = Path.Combine(directory, "AppxManifest.xml");
            // The package API and manifest are candidate sources, not trust bypasses.
            // Unreadable WindowsApps ancestor descriptors remain a blocking failure.
            using var manifestLease = ExecutableTrust.Acquire(manifest, null);
            using var handle = NativeFileSystem.Open(manifest);
            NativeFileSystem.Inspect(handle, manifest, false);
            using var stream = new FileStream(handle, FileAccess.Read);
            using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            XElement root = XElement.Load(xml);
            XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            XNamespace aliases = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
            XElement? identity = root.Element(foundation + "Identity");
            if (root.Name != foundation + "Package" || identity?.Attribute("Name")?.Value != "Microsoft.DesktopAppInstaller"
                || identity.Attribute("Publisher")?.Value != "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
                || identity.Attribute("Version")?.Value != name.Groups[1].Value || identity.Attribute("ProcessorArchitecture")?.Value != "x64")
                throw new IOException("TOOL_ALIAS_MANIFEST_REJECTED");
            XElement[] applications = root.Element(foundation + "Applications")?.Elements(foundation + "Application").Where(x => x.Attribute("Id")?.Value == "winget").ToArray() ?? [];
            if (applications.Length != 1 || applications[0].Attribute("Executable")?.Value != "winget.exe"
                || applications[0].Element(foundation + "Extensions")?.Elements(aliases + "Extension").Any(x => x.Attribute("Category")?.Value == "windows.appExecutionAlias"
                    && x.Element(aliases + "AppExecutionAlias")?.Elements(aliases + "ExecutionAlias").Any(a => a.Attribute("Alias")?.Value == "winget.exe") == true) != true)
                throw new IOException("TOOL_ALIAS_MANIFEST_REJECTED");
            string executable = Path.Combine(directory, "winget.exe");
            ExecutableTrust.CaptureTrustedIdentity(executable);
            return executable;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or XmlException or PathBoundaryException) { throw new IOException("TOOL_ALIAS_TRUST_REJECTED"); }
    }
    private static IReadOnlyList<RegisteredWingetPackage> RegisteredPackages()
    {
        uint count = 0, length = 0;
        int error = GetPackagesByPackageFamily(Family, ref count, 0, ref length, 0);
        if (error == 0 && count == 0) return [];
        if (error != 122 || count > 32 || length > 32768) throw new IOException("TOOL_ALIAS_QUERY_FAILED");
        nint names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size)); nint buffer = Marshal.AllocHGlobal(checked((int)length * 2));
        try
        {
            if (GetPackagesByPackageFamily(Family, ref count, names, ref length, buffer) != 0) throw new IOException("TOOL_ALIAS_QUERY_FAILED");
            var result = new List<RegisteredWingetPackage>();
            for (int i = 0; i < count; i++)
            {
                string full = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size))!;
                uint pathLength = 0;
                if (GetPackagePathByFullName(full, ref pathLength, null) != 122 || pathLength > 32768) throw new IOException("TOOL_ALIAS_QUERY_FAILED");
                var path = new StringBuilder((int)pathLength);
                if (GetPackagePathByFullName(full, ref pathLength, path) != 0) throw new IOException("TOOL_ALIAS_QUERY_FAILED");
                result.Add(new(full, path.ToString()));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(names); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, nint fullNames, ref uint bufferLength, nint buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string fullName, ref uint length, StringBuilder? path);
}
internal sealed record ToolInventory(ToolDetection? Git, ToolDetection? GitHubCli, ToolDetection? GitLfs, ToolDetection? Winget)
{
    internal static ToolInventory Empty { get; } = new(null, null, null, null);
    internal ToolDetection? Get(DependencyId id) => id switch { DependencyId.Git => Git, DependencyId.GitHubCli => GitHubCli, DependencyId.GitLfs => GitLfs, DependencyId.Winget => Winget, _ => null };
}
internal static class ToolInventoryRefresh
{
    internal static async Task<ToolInventory> AfterActionAsync(Func<OperationJob,CancellationToken,Task<ToolInventory>> detect,
        OperationJob job, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested && !job.IsCancellationRequested)
            return await detect(job, cancellationToken).ConfigureAwait(false);
        await job.CancelAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        using var cleanupJob = OperationJob.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task<ToolInventory>? detection = null;
        try
        {
            detection = detect(cleanupJob, deadline.Token);
            return await detection.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            try { await cleanupJob.CancelAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            finally
            {
                // Internal detectors must cooperate with cancellation and have no
                // uncontrolled external awaits. The deadline initiates cancellation;
                // Job emptiness does not mean managed runner/context cleanup ended.
                // Keep ownership even if Job draining fails; never abandon this task.
                if (detection is not null)
                {
                    try { await detection.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                }
            }
        }
    }
}
internal static class VersionPolicy
{
    internal static bool IsSupported(DependencyId id, string output)
    {
        string pattern = id switch
        {
            DependencyId.Git => @"\Agit version (\d+)\.(\d+)\.(\d+)\.windows\.(\d+)(?:\r?\n)?\z",
            DependencyId.GitHubCli => @"\Agh version (\d+)\.(\d+)\.(\d+)(?: \([^\r\n]*\))?(?:\r?\n|\z)",
            DependencyId.GitLfs => @"\Agit-lfs/(\d+)\.(\d+)\.(\d+)(?: \([^\r\n]*\))?(?:\r?\n)?\z",
            DependencyId.Winget => @"\Av(\d+)\.(\d+)\.(\d+)(?:\r?\n)?\z", _ => "(?!)"
        };
        Match match = Regex.Match(output, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) return false;
        int[] values = new int[4];
        for (int i = 1; i < match.Groups.Count; i++) if (!int.TryParse(match.Groups[i].Value, out values[i - 1])) return false;
        var version = new Version(values[0], values[1], values[2]);
        Version floor = id switch { DependencyId.Git => new(2,55,0), DependencyId.GitHubCli => new(2,100,0), DependencyId.GitLfs => new(3,7,1), _ => new(1,29,290) };
        return version > floor || version == floor && (id != DependencyId.Git || values[3] >= 3);
    }
}
internal sealed class ToolPathPolicy(string? currentDirectory = null, string? backupRoot = null)
{
    internal ToolDetection ResolveSystem32Executable(string frozenFileName)
    {
        if (frozenFileName != "explorer.exe") throw new ArgumentException("TOOL_SYSTEM_NAME_REJECTED");
        // Explorer is located in the Windows directory, despite this public entry point's historical name.
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), frozenFileName);
        return new(path, Capture(path), "", true);
    }
    internal ExecutableIdentity Capture(string path)
    {
        string canonical = NativeFileSystem.CanonicalPath(path);
        string current = NativeFileSystem.CanonicalPath(currentDirectory ?? Environment.CurrentDirectory);
        string backup = NativeFileSystem.CanonicalPath(backupRoot ?? AppSettings.Default.BackupRoot);
        if (string.Equals(Path.GetDirectoryName(canonical), current, StringComparison.OrdinalIgnoreCase)
            || canonical.StartsWith(backup + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("TOOL_PATH_LOCAL_CANDIDATE_REJECTED");
        if (!string.Equals(Path.GetExtension(canonical), ".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("TOOL_FILE_TYPE_REJECTED");
        try
        {
            // Assign diagnostics to the failing role, while the final capture retains
            // Task 6's full no-follow, role-aware executable trust check.
            foreach (string segment in NativeFileSystem.Segments(canonical))
            {
                bool file = segment.Equals(canonical, StringComparison.OrdinalIgnoreCase);
                using var handle = NativeFileSystem.Open(segment);
                if (file) NativeFileSystem.InspectExecutable(handle, segment); else NativeFileSystem.Inspect(handle, segment, true);
                try { ExecutableTrust.CheckDescriptor(NativeFileSystem.ReadSecurity(handle), file || segment.Equals(Path.GetDirectoryName(canonical), StringComparison.OrdinalIgnoreCase)); }
                catch (UnauthorizedAccessException) { throw new IOException(file ? "TOOL_FILE_WRITE_ACL_UNSAFE" : "TOOL_PATH_WRITE_ACL_UNSAFE"); }
            }
            return ExecutableTrust.CaptureTrustedIdentity(canonical);
        }
        catch (PathBoundaryException ex) { throw new IOException("TOOL_PATH_" + ex.Code); }
    }
}
internal sealed class ToolDetector(IProcessRunner runner, IReadOnlyDictionary<string,string?> environment,
    Func<DependencyId,IEnumerable<string>>? candidates = null, ToolPathPolicy? policy = null)
{
    internal Dictionary<DependencyId,string> Errors { get; } = [];
    internal async Task<ToolInventory> DetectAsync(OperationJob job, CancellationToken token)
    {
        Errors.Clear(); var found = new Dictionary<DependencyId,ToolDetection>();
        foreach (DependencyId id in Enum.GetValues<DependencyId>())
        {
            string[] paths;
            try { paths = (candidates ?? KnownCandidates)(id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Errors[id] = "TOOL_ALIAS_TRUST_REJECTED"; continue; }
            foreach (string candidate in paths)
            {
                token.ThrowIfCancellationRequested();
                GitRuntimeContext? context = null;
                try
                {
                    string path = NativeFileSystem.CanonicalPath(candidate);
                    ExecutableIdentity identity = (policy ?? new()).Capture(path);
                    context = await GitRuntimeContext.CreatePublicProbeAsync(environment, token).ConfigureAwait(false);
                    var child = ChildEnvironmentBuilder.Build(context.Environment, new Dictionary<string,string?> { ["GH_CONFIG_DIR"] = context.EmptyHooksDirectory });
                    string working = Path.GetDirectoryName(child["GIT_CONFIG_GLOBAL"])!;
                    ProcessResult result = await runner.RunAsync(new(path, ["--version"], working, child, TimeSpan.FromSeconds(15), ExpectedExecutableIdentity: identity), job, null, token).ConfigureAwait(false);
                    if (Directory.EnumerateFileSystemEntries(context.EmptyHooksDirectory).Any()) throw new IOException("TOOL_PROBE_CONFIG_CHANGED");
                    if (result.Cancelled) throw new OperationCanceledException(token);
                    if (ProcessOutcomeClassifier.Classify(result) != ProcessTerminalKind.Succeeded) { Errors[id] = "TOOL_VERSION_PROBE_FAILED"; continue; }
                    string raw = string.Join("\n", result.StandardOutput).TrimEnd('\r', '\n');
                    var detection = new ToolDetection(path, identity, raw, VersionPolicy.IsSupported(id, raw));
                    if (!found.ContainsKey(id) || detection.IsSupported) found[id] = detection;
                    if (detection.IsSupported) break;
                    Errors[id] = "TOOL_VERSION_UNSUPPORTED";
                }
                catch (GitRuntimeCleanupException) { throw; }
                catch (System.ComponentModel.Win32Exception)
                {
                    found.Remove(id);
                    Errors[id] = "TOOL_VERSION_PROBE_FAILED";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    found.Remove(id);
                    Errors[id] = ex is IOException && ex.Message.StartsWith("TOOL_", StringComparison.Ordinal) ? ex.Message : "TOOL_PATH_INACCESSIBLE";
                }
                finally
                {
                    if (context is not null)
                    {
                        try { await context.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                        { throw new GitRuntimeCleanupException(context); }
                    }
                }
            }
        }
        return new(found.GetValueOrDefault(DependencyId.Git), found.GetValueOrDefault(DependencyId.GitHubCli), found.GetValueOrDefault(DependencyId.GitLfs), found.GetValueOrDefault(DependencyId.Winget));
    }
    private static IEnumerable<string> KnownCandidates(DependencyId id)
    {
        if (id == DependencyId.Winget) { yield return new WingetPackageResolver().Resolve(); yield break; }
        string name = id switch { DependencyId.Git => "git.exe", DependencyId.GitHubCli => "gh.exe", DependencyId.GitLfs => "git-lfs.exe", _ => "winget.exe" };
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (id == DependencyId.Git) yield return Path.Combine(programFiles, "Git", "cmd", name);
        if (id == DependencyId.GitHubCli) yield return Path.Combine(programFiles, "GitHub CLI", name);
        if (id == DependencyId.GitLfs) { yield return Path.Combine(programFiles, "Git LFS", name); yield return Path.Combine(programFiles, "Git", "mingw64", "bin", name); }
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (Path.IsPathFullyQualified(directory)) yield return Path.Combine(directory, name);
    }
}
