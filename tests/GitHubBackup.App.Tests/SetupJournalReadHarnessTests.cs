using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SetupJournalReadHarnessTests
{
    [TestMethod]
    public void Existing_journal_reader_is_read_only_and_has_an_explicit_classification_result()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        int begin = source.IndexOf("Function ${PREFIX}InspectFreshInstallJournal", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, begin, "CROSS_PROCESS_JOURNAL_READER_MISSING");
        int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
        Assert.IsGreaterThan(begin, end, "CROSS_PROCESS_JOURNAL_READER_UNTERMINATED");
        string body = source[begin..end];

        foreach (string required in new[]
        {
            "SetupJournalReadStatus", "SetupJournalReadPhase", "SetupOwnerSid",
            "SetupFixedRoot", "GuardPathPins", "GuardPathPinCount",
            "OpenPathIdentityLease", "ValidatePrivateHandleAcl",
            "ReadFreshInstallJournalRecord", ".GitHubBackupTool.state\\journal.ini"
        })
            StringAssert.Contains(body, required, "READ_ONLY_CLASSIFIER_CONTRACT_MISSING: " + required);

        foreach (string forbidden in new[]
        {
            "WriteFile(", "SetFileInformationByHandle", "DeleteFileW(", "MoveFileExW(",
            "CreateDirectory2W", "RMDir ", "Call ${PREFIX}RollbackFreshInstallJournal"
        })
            Assert.IsFalse(body.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                "JOURNAL_CLASSIFIER_MUST_NEVER_MUTATE_OR_ROLL_BACK: " + forbidden);
    }

    [TestMethod]
    public void Synthetic_cross_process_reader_harness_stays_under_the_test_fixture_boundary()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "GitHubBackup.App.Tests",
            "Fixtures", "SetupJournalReadHarness.nsi")).Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (string required in new[]
        {
            "SETUP_JOURNAL_FIXTURE_PARENT_REQUIRED", "SETUP_JOURNAL_HARNESS_OUTPUT_REQUIRED",
            "SETUP_GUARD_SOURCE_REQUIRED", "SETUP_JOURNAL_WRITE_MODE", "Call HarnessResolveOwnerSid",
            "Call HarnessCreatePrivateDirectory", "CreateFileW(w \"$SetupFixedRoot\\.GitHubBackupTool.state\\journal.ini\"",
            "FlushFileBuffers", "Call PinExistingInstallAncestors", "Call InspectFreshInstallJournal",
            "read-result.txt", "SetupJournalReadStatus", "SetErrorLevel 0"
        })
            StringAssert.Contains(source, required, "TWO_PROCESS_JOURNAL_HARNESS_REQUIREMENT_MISSING: " + required);

        foreach (string forbidden in new[]
        {
            "ValidateHost", "SHGetKnownFolderPath", "$LOCALAPPDATA", "Registry::", "HKCU", "HKLM",
            "GitHubBackup.nsi", "WriteUninstaller", "CreateShortCut", "ExecWait", "ShellExecute",
            "RollbackFreshInstallJournal", "RollbackFreshInstallPayloadCopies", "AbortFreshInstallCopyTransaction",
            "DeleteFileW", "RMDir /r"
        })
            Assert.IsFalse(source.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                "SYNTHETIC_READER_MUST_NOT_TOUCH_REAL_PROFILE_OR_CLEAN_UP: " + forbidden);
    }

    [TestMethod]
    [DataRow("PREPARED", "PREPARED", "valid")]
    [DataRow("FILES_WRITTEN", "FILES_WRITTEN", "valid")]
    [DataRow("PREPARED", "", "truncated")]
    [DataRow("PREPARED", "", "duplicate-key")]
    [DataRow("PREPARED", "", "unknown-phase")]
    [DataRow("PREPARED", "", "sid-mismatch")]
    [DataRow("PREPARED", "", "hash-mismatch")]
    [DataRow("PREPARED", "", "invalid-bom")]
    [DataRow("PREPARED", "", "empty-line")]
    [DataRow("PREPARED", "", "extra-line")]
    [DataRow("PREPARED", "PREPARED", "shared-read-only")]
    [DataRow("PREPARED", "PREPARED", "shared-inherited")]
    [DataRow("PREPARED", "", "shared-write")]
    [DataRow("PREPARED", "", "private-read")]
    [DataRow("PREPARED", "", "private-inherited")]
    [DataRow("PREPARED", "", "reparse-point")]
    public async Task Two_independent_harness_processes_classify_journal_and_preserve_rejected_objects(
        string writerPhase, string expectedPhase, string mutation)
    {
        string? nsisRoot = Environment.GetEnvironmentVariable("GITHUBBACKUP_NSIS_ROOT");
        if (string.IsNullOrWhiteSpace(nsisRoot) || !File.Exists(Path.Combine(nsisRoot, "makensis.exe")))
            Assert.Inconclusive("Set GITHUBBACKUP_NSIS_ROOT to the already verified portable NSIS 3.12 folder.");

        string compiler = Path.Combine(nsisRoot!, "makensis.exe");
        string repoRoot = RepoRoot();
        string harnessSource = Path.Combine(repoRoot, "tests", "GitHubBackup.App.Tests", "Fixtures",
            "SetupJournalReadHarness.nsi");
        AssertHarnessBoundary(File.ReadAllText(harnessSource));

        string artifactsRoot = Path.GetFullPath(Path.Combine(repoRoot, "artifacts", "setup"));
        Directory.CreateDirectory(artifactsRoot);
        string fixtureParent = Path.Combine(artifactsRoot, "journal-read-" + Guid.NewGuid().ToString("N"));
        string fixturePrefix = Path.GetFullPath(fixtureParent) + Path.DirectorySeparatorChar;
        Assert.IsTrue(fixturePrefix.StartsWith(artifactsRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "SYNTHETIC_FIXTURE_MUST_STAY_UNDER_ARTIFACTS_SETUP");
        Directory.CreateDirectory(fixtureParent);
        string compileTemp = Path.Combine(fixtureParent, "compile-temp");
        string runtimeTemp = Path.Combine(fixtureParent, "runtime-temp");
        Directory.CreateDirectory(compileTemp);
        Directory.CreateDirectory(runtimeTemp);

        string writerExe = Path.Combine(fixtureParent, "JournalWriter.exe");
        string readerExe = Path.Combine(fixtureParent, "JournalReader.exe");
        await CompileHarness(compiler, nsisRoot!, harnessSource, writerExe, fixtureParent, compileTemp,
            writerMode: true, writerPhase);
        await CompileHarness(compiler, nsisRoot!, harnessSource, readerExe, fixtureParent, compileTemp,
            writerMode: false, writerPhase);

        await RunHarness(writerExe, fixtureParent, runtimeTemp);
        string journalPath = Path.Combine(fixtureParent, "LocalData", "Programs",
            "GitHubBackupToolSynthetic", ".GitHubBackupTool.state", "journal.ini");
        Assert.IsTrue(File.Exists(journalPath), "SYNTHETIC_WRITER_DID_NOT_CREATE_JOURNAL");
        byte[] beforeReader;
        string? reparseTarget = null;
        byte[]? reparseTargetBytes = null;
        try
        {
            ApplyMutation(journalPath, fixtureParent, mutation, out beforeReader, out reparseTarget, out reparseTargetBytes);
        }
        catch (UnauthorizedAccessException ex) when (mutation == "reparse-point")
        {
            Assert.Inconclusive("The synthetic Windows reparse-point fixture is unavailable to this process: " + ex.Message);
            return;
        }
        catch (PlatformNotSupportedException ex) when (mutation == "reparse-point")
        {
            Assert.Inconclusive("The synthetic Windows reparse-point fixture is unavailable to this process: " + ex.Message);
            return;
        }
        catch (IOException ex) when (mutation == "reparse-point" && ex.HResult == unchecked((int)0x80070522))
        {
            Assert.Inconclusive("Windows denied creating the synthetic reparse-point fixture (ERROR_PRIVILEGE_NOT_HELD): " + ex.Message);
            return;
        }

        await RunHarness(readerExe, fixtureParent, runtimeTemp);
        string resultPath = Path.Combine(fixtureParent, "read-result.txt");
        Assert.IsTrue(File.Exists(resultPath), "SYNTHETIC_READER_RESULT_MISSING");
        string result = (await File.ReadAllTextAsync(resultPath)).Trim();
        if (mutation is "valid" or "shared-read-only" or "shared-inherited")
        {
            Assert.AreEqual("PARSED|" + expectedPhase + "|0", result, "VALID_JOURNAL_PHASE_NOT_CLASSIFIED");
            CollectionAssert.AreEqual(beforeReader, await File.ReadAllBytesAsync(journalPath),
                "PARSED_JOURNAL_BYTES_MUST_REMAIN_UNCHANGED");
        }
        else if (mutation == "shared-write")
        {
            Assert.AreEqual("READER_REJECTED|pins|11", result, "WRITABLE_SHARED_PARENT_MUST_FAIL_CLOSED");
            CollectionAssert.AreEqual(beforeReader, await File.ReadAllBytesAsync(journalPath),
                "UNSAFE_PARENT_JOURNAL_BYTES_MUST_REMAIN_UNCHANGED");
        }
        else
        {
            StringAssert.StartsWith(result, "REJECTED||", "INVALID_JOURNAL_MUST_FAIL_CLOSED");
            if (mutation == "reparse-point")
            {
                var linkInfo = new FileInfo(journalPath);
                Assert.IsNotNull(linkInfo.LinkTarget,
                    "REPARSE_POINT_MUST_REMAIN_A_REPARSE_POINT_AFTER_REFUSAL");
                Assert.AreEqual(reparseTarget, Path.GetFullPath(linkInfo.ResolveLinkTarget(returnFinalTarget: true)!.FullName),
                    "REPARSE_TARGET_MUST_NOT_BE_REPLACED");
                CollectionAssert.AreEqual(reparseTargetBytes!, await File.ReadAllBytesAsync(reparseTarget!),
                    "REPARSE_TARGET_CONTENT_MUST_REMAIN_UNCHANGED");
            }
            else
            {
                Assert.AreNotEqual(0, beforeReader.Length, "REJECTED_FIXTURE_OBJECT_MUST_REMAIN_PRESENT");
                CollectionAssert.AreEqual(beforeReader, await File.ReadAllBytesAsync(journalPath),
                    "REJECTED_JOURNAL_BYTES_MUST_REMAIN_UNCHANGED");
            }
        }
    }

    private static void AssertHarnessBoundary(string source)
    {
        foreach (string required in new[]
        {
            "CreateDirectoryW", "ConvertStringSecurityDescriptorToSecurityDescriptorW",
            "CreateFileW(w \"$SetupFixedRoot\\.GitHubBackupTool.state\\journal.ini\"",
            "FlushFileBuffers", "Call PinExistingInstallAncestors", "Call InspectFreshInstallJournal",
            "FileWrite $0 \"$HarnessRecord$\\r$\\n\""
        })
            StringAssert.Contains(source, required, "SYNTHETIC_HARNESS_BOUNDARY_MISSING: " + required);
        foreach (string forbidden in new[]
        {
            "ValidateHost", "SHGetKnownFolderPath", "Registry::", "HKCU", "HKLM", "$LOCALAPPDATA",
            "WriteUninstaller", "CreateShortCut", "ExecWait", "ShellExecute", "DeleteFileW", "RMDir /r"
        })
            Assert.IsFalse(source.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                "SYNTHETIC_HARNESS_MUST_NOT_TOUCH_PROFILE_OR_RUN_PRODUCT_CODE: " + forbidden);
    }

    private static async Task CompileHarness(string compiler, string nsisRoot, string source, string output,
        string fixtureParent, string compileTemp, bool writerMode, string writerPhase)
    {
        var start = new ProcessStartInfo(compiler)
        {
            WorkingDirectory = fixtureParent,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-V2");
        start.ArgumentList.Add($"-DSETUP_JOURNAL_FIXTURE_PARENT={fixtureParent}");
        start.ArgumentList.Add($"-DSETUP_JOURNAL_HARNESS_OUTPUT={output}");
        start.ArgumentList.Add($"-DSETUP_GUARD_SOURCE={Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh")}");
        start.ArgumentList.Add($"-DSETUP_JOURNAL_PHASE={writerPhase}");
        if (writerMode)
            start.ArgumentList.Add("-DSETUP_JOURNAL_WRITE_MODE");
        start.ArgumentList.Add(source);
        start.Environment["NSISDIR"] = nsisRoot;
        start.Environment["NSISCONFDIR"] = nsisRoot;
        start.Environment["TEMP"] = compileTemp;
        start.Environment["TMP"] = compileTemp;
        string outputText = await RunProcess(start, TimeSpan.FromSeconds(45));
        Assert.IsTrue(File.Exists(output), "SYNTHETIC_HARNESS_COMPILE_FAILED: " + outputText);
    }

    private static async Task RunHarness(string executable, string fixtureParent, string runtimeTemp)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = fixtureParent,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("/S");
        start.Environment["TEMP"] = runtimeTemp;
        start.Environment["TMP"] = runtimeTemp;
        await RunProcess(start, TimeSpan.FromSeconds(30));
    }

    private static void ApplyMutation(string journalPath, string fixtureParent, string mutation,
        out byte[] beforeReader, out string? reparseTarget, out byte[]? reparseTargetBytes)
    {
        reparseTarget = null;
        reparseTargetBytes = null;
        byte[] original = File.ReadAllBytes(journalPath);
        Assert.IsTrue(original.Length >= 2 && original[0] == 0xFF && original[1] == 0xFE,
            "SYNTHETIC_WRITER_MUST_EMIT_UTF16LE_BOM");
        if (mutation == "valid")
        {
            beforeReader = original;
            return;
        }
        if (mutation is "shared-read-only" or "shared-write" or "shared-inherited")
        {
            var programs = new DirectoryInfo(Path.Combine(fixtureParent, "LocalData", "Programs"));
            DirectorySecurity security = programs.GetAccessControl();
            if (mutation == "shared-inherited")
                security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            else
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"),
                    mutation == "shared-write" ? FileSystemRights.WriteData : FileSystemRights.ReadAndExecute,
                    AccessControlType.Allow));
            programs.SetAccessControl(security);
            beforeReader = original;
            return;
        }
        if (mutation is "private-read" or "private-inherited")
        {
            var journal = new FileInfo(journalPath);
            FileSecurity security = journal.GetAccessControl();
            if (mutation == "private-read")
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"),
                    FileSystemRights.Read, AccessControlType.Allow));
            else
                security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            journal.SetAccessControl(security);
            beforeReader = original;
            return;
        }
        if (mutation == "reparse-point")
        {
            string target = Path.Combine(fixtureParent, "journal-target.bin");
            byte[] targetBytes = [0x47, 0x42, 0x54, 0x2D, 0x53, 0x59, 0x4E, 0x54, 0x48];
            File.WriteAllBytes(target, targetBytes);
            File.Delete(journalPath);
            File.CreateSymbolicLink(journalPath, target);
            reparseTarget = Path.GetFullPath(target);
            reparseTargetBytes = targetBytes;
            beforeReader = [];
            return;
        }

        string text = Encoding.Unicode.GetString(original, 2, original.Length - 2);
        string changed = mutation switch
        {
            "invalid-bom" => text,
            "empty-line" => text.Replace("schema=1\r\n", "\r\n", StringComparison.Ordinal),
            "extra-line" => text + "extra=value\r\n",
            "truncated" => text[..^1],
            "duplicate-key" => text.Replace("schema=1\r\n", "schema=1\r\nschema=1\r\n", StringComparison.Ordinal),
            "unknown-phase" => text.Replace("phase=PREPARED\r\n", "phase=UNKNOWN\r\n", StringComparison.Ordinal),
            "sid-mismatch" => text.Replace("ownerSid=", "ownerSid=S-1-5-21-999999999-", StringComparison.Ordinal),
            "hash-mismatch" => text.Replace("payloadSha256=7DE80C706E1C4BA74764D19CF62950733D147D093161C8A556C0C6A1106027A1",
                "payloadSha256=" + new string('0', 64), StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown synthetic mutation")
        };
        byte[] corrupted = [0xFF, mutation == "invalid-bom" ? (byte)0xFF : (byte)0xFE, .. Encoding.Unicode.GetBytes(changed)];
        File.WriteAllBytes(journalPath, corrupted);
        beforeReader = corrupted;
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
