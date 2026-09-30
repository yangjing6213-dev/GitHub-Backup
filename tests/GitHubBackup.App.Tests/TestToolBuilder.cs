using System.Diagnostics;
using System.Security.Principal;
using GitHubBackup.App;
namespace GitHubBackup.App.Tests;

internal sealed class TestToolBuilder : IDisposable
{
    private static readonly Lazy<Task<TestToolBuilder>> Template = new(BuildTemplateAsync);
    internal string Root { get; } = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "GitHubBackup-process-tests-" + Guid.NewGuid().ToString("N"));
    internal string Executable { get; private set; } = "";
    internal IReadOnlyDictionary<string,string?> Environment { get; private set; } = null!;
    internal static async Task<TestToolBuilder> CreateAsync()
    {
        TestToolBuilder template = await Template.Value;
        var tools = CreateRoot();
        foreach (string source in Directory.GetFiles(Path.GetDirectoryName(template.Executable)!)) File.Copy(source, Path.Combine(tools.Root, Path.GetFileName(source)));
        tools.Executable = Path.Combine(tools.Root, "FakeTool.exe");
        return tools;
    }
    private static TestToolBuilder CreateRoot()
    {
        var tools = new TestToolBuilder();
        AclPolicy.CreateRestrictedDirectory(tools.Root, WindowsIdentity.GetCurrent().User!, true);
        tools.Environment = ChildEnvironmentBuilder.CreateBase(new Dictionary<string,string?> { ["SystemRoot"] = System.Environment.GetEnvironmentVariable("SystemRoot"), ["TEMP"] = tools.Root, ["TMP"] = tools.Root }, []);
        return tools;
    }
    private static async Task<TestToolBuilder> BuildTemplateAsync()
    {
        var tools = CreateRoot();
        string project = Path.Combine(tools.Root, "FakeTool.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><RuntimeIdentifier>win-x64</RuntimeIdentifier><ImplicitUsings>enable</ImplicitUsings><UseAppHost>true</UseAppHost></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(tools.Root, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        await File.WriteAllTextAsync(Path.Combine(tools.Root, "Program.cs"), Source);
        var start = new ProcessStartInfo(@"C:\Program Files\dotnet\dotnet.exe") { WorkingDirectory = tools.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in new[] { "build", project, "--nologo", "-v:q", "-p:NuGetAudit=false" }) start.ArgumentList.Add(arg);
        start.Environment["DOTNET_CLI_HOME"] = tools.Root; start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"; start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        start.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(); Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
        if (process.ExitCode != 0) throw new AssertFailedException(await output + await error);
        tools.Executable = Path.Combine(tools.Root, "bin", "Debug", "net10.0", "win-x64", "FakeTool.exe");
        foreach (string name in new[] { "git.exe", "gh.exe", "git-lfs.exe", "winget.exe" }) File.Copy(tools.Executable, Path.Combine(Path.GetDirectoryName(tools.Executable)!, name));
        return tools;
    }
    internal static bool ProcessExists(int id) { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch (ArgumentException) { return false; } }
    internal static async Task UntilAsync(Func<bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate()) { if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(); await Task.Delay(20); }
    }
    internal static void CleanupTemplate() { if (Template.IsValueCreated && Template.Value.IsCompletedSuccessfully) Template.Value.Result.Dispose(); }
    public void Dispose()
    {
        string canonical = Path.GetFullPath(Root);
        string prefix = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "GitHubBackup-process-tests-");
        if (!canonical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || new DirectoryInfo(canonical).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("TEST_ROOT_CLEANUP_REJECTED");
        Directory.Delete(canonical, true);
    }
    private const string Source = """
using System.Diagnostics;
using System.Text.Json;
string mode = args.FirstOrDefault() ?? "";
switch (mode)
{
case "echo-args": Console.Write(JsonSerializer.Serialize(args.Skip(1).ToArray())); return 0;
case "dump-env": Console.Write(JsonSerializer.Serialize(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(x => (string)x.Key, x => (string)x.Value))); return 0;
case "stream": foreach (string chunk in args.Skip(1)) { if (chunk.StartsWith("err:")) { Console.Error.Write(chunk[4..]); Console.Error.Flush(); } else { Console.Out.Write(chunk.StartsWith("out:") ? chunk[4..] : chunk); Console.Out.Flush(); } await Task.Delay(10); } return 0;
case "write-bytes": await Console.OpenStandardOutput().WriteAsync(Convert.FromBase64String(args[1])); return 0;
case "wait": await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
case "write-sentinel-then-wait": await File.WriteAllTextAsync(args[1], "EXECUTED"); await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
case "spawn-child":
case "orphan-pipe":
  var start = new ProcessStartInfo(Environment.ProcessPath!, "wait") { UseShellExecute = false };
  using (var child = Process.Start(start)!) { await File.WriteAllLinesAsync(args[1]+".tmp", [Environment.ProcessId.ToString(), child.Id.ToString()]); File.Move(args[1]+".tmp", args[1]); if (mode == "spawn-child") await Task.Delay(Timeout.InfiniteTimeSpan); }
  return 0;
case "scenario":
  using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]))) {
    if (!args.Skip(2).SequenceEqual(doc.RootElement.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()))) return 65;
    foreach (var chunk in doc.RootElement.GetProperty("stdoutChunks").EnumerateArray()) { Console.Write(chunk.GetString()); Console.Out.Flush(); await Task.Delay(10); }
    foreach (var chunk in doc.RootElement.GetProperty("stderrChunks").EnumerateArray()) { Console.Error.Write(chunk.GetString()); Console.Error.Flush(); await Task.Delay(10); }
    return doc.RootElement.GetProperty("exitCode").GetInt32(); }
case "exit-code": return int.Parse(args[1]);
case "probe-handle":
  bool inherited = Probe.GetFileInformationByHandle((IntPtr)long.Parse(args[1]), out var info) && (((ulong)info.High << 32) | info.Low) == ulong.Parse(args[2]);
  Console.Write(inherited ? "INHERITED" : "NOT_INHERITED"); return 0;
case "auth":
  if (args[1] == "git-credential") { await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL"))!, "helper-args.json"), JsonSerializer.Serialize(args)); Console.Write("username=TESTONLY\npassword=TESTONLY\n\n"); return 0; }
  return 64;
default: Console.Error.Write("UNKNOWN_TEST_MODE"); return 64;
}

static class Probe {
  [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] internal struct Info { internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, High, Low; }
  [System.Runtime.InteropServices.DllImport("kernel32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static extern bool GetFileInformationByHandle(IntPtr handle, out Info info);
}
""";
}

[TestClass]
public sealed class ProcessToolAssemblyLifecycle
{
    [AssemblyCleanup] public static void Cleanup() => TestToolBuilder.CleanupTemplate();
}
