using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SetupHostHarnessTests
{
    [TestMethod]
    public void Host_harness_only_reads_native_host_facts_and_writes_the_bounded_result_file()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "GitHubBackup.App.Tests",
            "Fixtures", "SetupHostHarness.nsi"));
        StringAssert.Contains(source, "RequestExecutionLevel user");
        StringAssert.Contains(source, "Call ValidateHost");
        StringAssert.Contains(source, "FileOpen $HarnessResult \"${SETUP_HOST_RESULT_FILE}\" w");
        foreach (string forbidden in new[]
        {
            "CreateDirectory", "CreateFileW", "SetFileInformation", "WriteUninstaller", "CreateShortCut",
            "DeleteFile", "RMDir", "WriteReg", "ExecWait", "ShellExecute", "FileWrite $HarnessResult \"$Setup",
            "InstallFresh", "Rollback", "FileWrite $HarnessResult \"root=", "FileWrite $HarnessResult \"sid="
        })
            Assert.IsFalse(source.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                "READ_ONLY_HOST_HARNESS_BOUNDARY: " + forbidden);
    }

    [TestMethod]
    [DataRow(26200, 34404, 0)]
    [DataRow(26201, 34404, 10)]
    [DataRow(26200, 43620, 10)]
    public async Task Native_host_outputs_allow_only_the_exact_supported_non_elevated_x64_host(
        int supportedBuild, int nativeMachine, int expectedHostCode)
    {
        string? nsisRoot = Environment.GetEnvironmentVariable("GITHUBBACKUP_NSIS_ROOT");
        if (string.IsNullOrWhiteSpace(nsisRoot) || !File.Exists(Path.Combine(nsisRoot, "makensis.exe")))
            Assert.Inconclusive("Set GITHUBBACKUP_NSIS_ROOT to the already verified portable NSIS 3.12 folder.");

        string repoRoot = RepoRoot();
        string artifactsRoot = Path.GetFullPath(Path.Combine(repoRoot, "artifacts", "setup"));
        string fixtureParent = Path.Combine(artifactsRoot, "host-read-" + Guid.NewGuid().ToString("N"));
        Assert.IsTrue(fixtureParent.StartsWith(artifactsRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "HOST_FIXTURE_MUST_STAY_UNDER_ARTIFACTS_SETUP");
        Directory.CreateDirectory(fixtureParent);
        string compileTemp = Path.Combine(fixtureParent, "compile-temp");
        string runtimeTemp = Path.Combine(fixtureParent, "runtime-temp");
        Directory.CreateDirectory(compileTemp);
        Directory.CreateDirectory(runtimeTemp);
        string outputExe = Path.Combine(fixtureParent, "HostReader.exe");
        string resultPath = Path.Combine(fixtureParent, "host-result.txt");
        string rejectedTarget = Path.Combine(fixtureParent, "must-not-be-created");

        var compile = NewProcess(Path.Combine(nsisRoot!, "makensis.exe"), fixtureParent, compileTemp);
        compile.Environment["NSISDIR"] = nsisRoot!;
        compile.Environment["NSISCONFDIR"] = nsisRoot!;
        foreach (string arg in new[]
        {
            "-V2", "-DSETUP_HOST_HARNESS_OUTPUT=" + outputExe, "-DSETUP_HOST_RESULT_FILE=" + resultPath,
            "-DSETUP_GUARD_SOURCE=" + Path.Combine(repoRoot, "publish", "Setup", "SetupGuards.nsh"),
            "-DSETUP_SUPPORTED_BUILD=" + supportedBuild.ToString(CultureInfo.InvariantCulture),
            "-DSETUP_NATIVE_MACHINE=" + nativeMachine.ToString(CultureInfo.InvariantCulture),
            Path.Combine(repoRoot, "tests", "GitHubBackup.App.Tests", "Fixtures", "SetupHostHarness.nsi")
        })
            compile.ArgumentList.Add(arg);
        await RunProcess(compile);
        var run = NewProcess(outputExe, fixtureParent, runtimeTemp);
        run.ArgumentList.Add("/S");
        run.ArgumentList.Add("/D=" + rejectedTarget);
        await RunProcess(run);

        string output = File.ReadAllText(resultPath);
        Console.WriteLine("READ_ONLY_HOST_RESULT " + output.Replace("\r\n", " | ", StringComparison.Ordinal));
        Dictionary<string, string> facts = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1]);
        Assert.AreEqual("1,332,34404", facts["architecture"], "NATIVE_ARCHITECTURE_QUERY_MUST_RETURN_X86_ON_X64");
        Assert.AreEqual("1,10,0,26200,2,1", facts["version"], "ACTUAL_VERSION_AND_WORKSTATION_PRODUCT_TYPE");
        Assert.AreEqual("1", facts["openToken"]);
        Assert.AreEqual("1,0,4", facts["elevation"], "ACTUAL_TOKEN_MUST_BE_NON_ELEVATED");
        Assert.IsFalse(Directory.Exists(rejectedTarget), "READ_ONLY_HARNESS_MUST_NEVER_CREATE_REJECTED_TARGET");
        Assert.AreEqual(expectedHostCode.ToString(CultureInfo.InvariantCulture), facts["hostCode"],
            "PRODUCTION_HOST_CHECK_RESULT: " + output);
        Assert.AreEqual(expectedHostCode == 0 ? "1" : "0", facts["hostHasRoot"]);
        Assert.AreEqual(expectedHostCode == 0 ? "1" : "0", facts["hostHasOwner"]);
        if (expectedHostCode == 0)
            Assert.AreEqual("11", facts["argumentCode"], "WRONG_DIRECTORY_ARGUMENT_MUST_FAIL_AFTER_HOST_VALIDATION");
    }

    private static ProcessStartInfo NewProcess(string file, string workingDirectory, string temporaryDirectory)
    {
        var result = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        result.Environment["TEMP"] = temporaryDirectory;
        result.Environment["TMP"] = temporaryDirectory;
        return result;
    }

    private static async Task RunProcess(ProcessStartInfo startInfo)
    {
        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("READ_ONLY_HOST_PROCESS_TIMEOUT");
        }
        Assert.AreEqual(0, process.ExitCode, (await stdout) + (await stderr));
    }

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
}
