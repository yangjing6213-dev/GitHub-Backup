using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SetupWriteBehaviorHarnessTests
{
    private const string CompilerEnvironmentVariable = "SETUP_WRITE_BEHAVIOR_NSIS_COMPILER";

    [TestMethod]
    public async Task Synthetic_harness_probes_locked_placeholder_write_and_file_identity()
    {
        string repoRoot = RepoRoot();
        string fixtureSource = Path.Combine(repoRoot, "tests", "GitHubBackup.App.Tests", "Fixtures",
            "SetupWriteBehaviorHarness.nsi");
        string source = File.ReadAllText(fixtureSource).Replace("\r\n", "\n", StringComparison.Ordinal);
        AssertHarnessBoundary(source);

        string compiler = Environment.GetEnvironmentVariable(CompilerEnvironmentVariable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(compiler))
            Assert.Inconclusive($"Set {CompilerEnvironmentVariable} to the verified portable NSIS 3.12 compiler to run this synthetic probe.");
        Assert.IsTrue(File.Exists(compiler), "VERIFIED_NSIS_COMPILER_NOT_FOUND: " + compiler);

        string versionOutput = await RunProcess(compiler, "-VERSION", TimeSpan.FromSeconds(15));
        StringAssert.Contains(versionOutput, "v3.12", "NSIS_312_VERSION_REQUIRED");

        string artifactsRoot = Path.GetFullPath(Path.Combine(repoRoot, "artifacts", "setup"));
        Directory.CreateDirectory(artifactsRoot);
        string fixtureParent = Path.Combine(artifactsRoot, "write-behavior-" + Guid.NewGuid().ToString("N"));
        string fixturePrefix = Path.GetFullPath(fixtureParent) + Path.DirectorySeparatorChar;
        Assert.IsTrue(fixturePrefix.StartsWith(artifactsRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "SYNTHETIC_FIXTURE_MUST_STAY_UNDER_ARTIFACTS_SETUP");

        string compileTemp = Path.Combine(fixtureParent, "compile-temp");
        string runtimeTemp = Path.Combine(fixtureParent, "runtime-temp");
        Directory.CreateDirectory(compileTemp);
        Directory.CreateDirectory(runtimeTemp);
        string harnessExe = Path.Combine(fixtureParent, "SetupWriteBehaviorHarness.exe");

        var compile = new ProcessStartInfo(compiler)
        {
            WorkingDirectory = fixtureParent,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        compile.ArgumentList.Add("-V2");
        compile.ArgumentList.Add($"-DSETUP_WRITE_FIXTURE_PARENT={fixtureParent}");
        compile.ArgumentList.Add($"-DSETUP_WRITE_HARNESS_OUTPUT={harnessExe}");
        compile.ArgumentList.Add(fixtureSource);
        compile.Environment["TEMP"] = compileTemp;
        compile.Environment["TMP"] = compileTemp;

        string compileOutput = await RunProcess(compile, TimeSpan.FromSeconds(30));
        Assert.IsTrue(File.Exists(harnessExe), "SYNTHETIC_HARNESS_COMPILE_FAILED: " + compileOutput);

        var runHarness = new ProcessStartInfo(harnessExe)
        {
            WorkingDirectory = fixtureParent,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        runHarness.ArgumentList.Add("/S");
        runHarness.Environment["TEMP"] = runtimeTemp;
        runHarness.Environment["TMP"] = runtimeTemp;

        string runtimeOutput = await RunProcess(runHarness, TimeSpan.FromSeconds(30));
        string resultPath = Path.Combine(fixtureParent, "write-behavior-results.txt");
        Assert.IsTrue(File.Exists(resultPath), "SYNTHETIC_HARNESS_RESULT_MISSING: " + runtimeOutput);
        string[] results = await File.ReadAllLinesAsync(resultPath);
        Assert.HasCount(2, results, "SYNTHETIC_HARNESS_MUST_REPORT_BOTH_WRITE_OPERATIONS");
        AssertProbeResult(results[0], "WriteUninstaller");
        AssertProbeResult(results[1], "CreateShortCut");
    }

    private static void AssertHarnessBoundary(string source)
    {
        string[] required =
        [
            "SetEnvironmentVariableW(w \"TEMP\"",
            "SetEnvironmentVariableW(w \"TMP\"",
            "InitPluginsDir",
            "StrCpy $R0 \"$HarnessOwnerSid\"",
            "O:$R0D:P(A;;GA;;;$R0)(A;;GA;;;SY)(A;;GA;;;BA)",
            "CreateFileW(w \"$HarnessCreatePath\", i 0xC0010000, i 7, p $HarnessSecurityAttributes, i 1, i 0x80, p 0)",
            "WriteUninstaller \"$HarnessUninstallerPath\"",
            "CreateShortCut \"$HarnessShortcutPath\"",
            "GetFileInformationByHandleEx(p $HarnessIdentityHandle, i 18, p r0, i 24)",
            "target-not-launched.txt",
            "OriginalHandleOpen=yes"
        ];
        foreach (string fragment in required)
            StringAssert.Contains(source, fragment, "SYNTHETIC_WRITE_PROBE_REQUIREMENT_MISSING: " + fragment);

        int tempSetup = source.IndexOf("SetEnvironmentVariableW(w \"TEMP\"", StringComparison.Ordinal);
        int pluginInit = source.IndexOf("InitPluginsDir", StringComparison.Ordinal);
        int writeUninstaller = source.IndexOf("WriteUninstaller \"$HarnessUninstallerPath\"", StringComparison.Ordinal);
        int shortcut = source.IndexOf("CreateShortCut \"$HarnessShortcutPath\"", StringComparison.Ordinal);
        Assert.IsTrue(tempSetup >= 0 && pluginInit > tempSetup && writeUninstaller > pluginInit && shortcut > writeUninstaller,
            "TEMP_AND_TMP_MUST_BE_REDIRECTED_BEFORE_PLUGIN_INIT_AND_BOTH_PROBES_MUST_RUN_AFTER");
        Assert.IsFalse(source.Contains("ExecWait", StringComparison.Ordinal), "THE_SHORTCUT_TARGET_MUST_NEVER_BE_LAUNCHED");
        Assert.IsFalse(source.Contains("ShellExecute", StringComparison.Ordinal), "THE_SHORTCUT_TARGET_MUST_NEVER_BE_LAUNCHED");
        Assert.IsFalse(source.Contains("HKLM", StringComparison.OrdinalIgnoreCase), "PROBE_MUST_NOT_TOUCH_REGISTRY");
        Assert.IsFalse(source.Contains("$LOCALAPPDATA", StringComparison.OrdinalIgnoreCase), "PROBE_MUST_NOT_READ_USER_PROFILE_PATHS");
    }

    private static void AssertProbeResult(string result, string operation)
    {
        Assert.IsTrue(result.StartsWith(operation + "=", StringComparison.Ordinal),
            "PROBE_RESULT_OPERATION_MISSING: " + operation);
        StringAssert.Contains(result, "PathIdentity=", "PROBE_RESULT_IDENTITY_MISSING: " + operation);
        StringAssert.Contains(result, "OriginalHandleOpen=yes", "PROBE_MUST_KEEP_ORIGINAL_HANDLE_OPEN: " + operation);
        Assert.IsTrue(result.Contains("PathIdentity=unchanged", StringComparison.Ordinal) ||
            result.Contains("PathIdentity=replaced", StringComparison.Ordinal) ||
            result.Contains("PathIdentity=missing", StringComparison.Ordinal),
            "PROBE_RESULT_IDENTITY_VALUE_INVALID: " + operation);
        Assert.IsTrue(result.Contains(operation + "=success", StringComparison.Ordinal) ||
            result.Contains(operation + "=error", StringComparison.Ordinal),
            "PROBE_RESULT_STATUS_INVALID: " + operation);
    }

    private static async Task<string> RunProcess(string executable, string argument, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(argument);
        return await RunProcess(startInfo, timeout);
    }

    private static async Task<string> RunProcess(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start(), "PROCESS_START_FAILED: " + startInfo.FileName);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeoutToken = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutToken.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("PROCESS_TIMEOUT: " + startInfo.FileName);
        }

        string output = (await stdout) + (await stderr);
        Assert.AreEqual(0, process.ExitCode, "PROCESS_FAILED: " + startInfo.FileName + Environment.NewLine + output);
        return output;
    }

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
}
