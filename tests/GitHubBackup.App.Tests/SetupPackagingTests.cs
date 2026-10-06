using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SetupPackagingTests
{
    private static readonly string PowerShell = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "powershell", "pwsh.exe");

    [TestMethod]
    public void Shipped_notice_contains_exact_component_license_payloads()
    {
        byte[] notice = File.ReadAllBytes(Path.Combine(RepoRoot(), "publish", "Setup", "NOTICE.txt"));
        string originalLicense = File.ReadAllText(Path.Combine(RepoRoot(), "LICENSE"), new UTF8Encoding(false, true))
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        originalLicense = System.Text.RegularExpressions.Regex.Replace(originalLicense, "[ \\t]+(?=\\n)", "");
        string header = "GitHubBackup setup - local unsigned build\n"
            + "Copyright (c) 2026 yangjing6213-dev.\n"
            + "Original GitHub Backup code is licensed under the MIT License below.\n"
            + "Third-party components retain their own licenses reproduced in this notice.\n"
            + "The current-user installer is intended to retain settings, logs, credentials, and backups on uninstall.\n\n"
            + "----- BEGIN GitHub Backup MIT License -----\n"
            + originalLicense
            + (originalLicense.EndsWith('\n') ? "" : "\n")
            + "----- END GitHub Backup MIT License -----\n\n"
            + "Raw upstream SHA-256 values:\n"
            + "NSIS 3.12 COPYING: 388357C1215FF403C5EBDE3A5ECD273E68F8B79A579996775245D1EE65442ABA\n"
            + "Microsoft.NETCore.App.Runtime.win-x64 10.0.12 LICENSE.TXT: D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B\n"
            + "Microsoft.NETCore.App.Runtime.win-x64 10.0.12 THIRD-PARTY-NOTICES.TXT: 6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21\n"
            + "Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.12 LICENSE: A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB\n"
            + "Component text normalization: UTF-8 without BOM; LF line endings; ASCII spaces/tabs immediately before LF removed. No other text changed.\n\n";
        Assert.IsTrue(notice.AsSpan().StartsWith(Encoding.UTF8.GetBytes(header)), "NOTICE_HEADER_INVALID");
        Assert.IsFalse(Encoding.UTF8.GetString(notice).Contains("PENDING_LICENSE", StringComparison.Ordinal));
        int offset = Encoding.UTF8.GetByteCount(header);
        var sections = new (string Name, int Length, string Sha256)[]
        {
            ("NSIS 3.12 COPYING", 15475, "9AFAB55B28917F993049218D52D5ED9EC56498BC6D53FE422AEA5B60C867EE4B"),
            ("Microsoft.NETCore.App.Runtime.win-x64 10.0.12 LICENSE.TXT", 1116, "CFC21F5E8BD655AE997EEC916138B707B1D290B83272C02A95C9F821B8C87310"),
            ("Microsoft.NETCore.App.Runtime.win-x64 10.0.12 THIRD-PARTY-NOTICES.TXT", 76623, "66F1D4E44973185519BB4AA8A9718EB22FC7AF2CC532E3AE9CFC4C127EE7FC54"),
            ("Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.12 LICENSE", 1115, "AE48DF11A335DC1A615F4F938B69CBA73BCF4485C4F97AF49B38EFB0F216353B")
        };
        foreach (var section in sections)
        {
            byte[] begin = Encoding.ASCII.GetBytes($"----- BEGIN {section.Name} ({section.Length} bytes) -----\n");
            byte[] end = Encoding.ASCII.GetBytes($"\n----- END {section.Name} -----\n");
            Assert.IsTrue(notice.AsSpan(offset).StartsWith(begin), $"NOTICE_SECTION_BEGIN_MISSING: {section.Name}");
            offset += begin.Length;
            Assert.IsGreaterThanOrEqualTo(section.Length + end.Length, notice.Length - offset,
                $"NOTICE_SECTION_TRUNCATED: {section.Name}");
            Assert.AreEqual(section.Sha256, Convert.ToHexString(SHA256.HashData(notice.AsSpan(offset, section.Length))),
                $"NOTICE_PAYLOAD_HASH_MISMATCH: {section.Name}");
            offset += section.Length;
            Assert.IsTrue(notice.AsSpan(offset).StartsWith(end), $"NOTICE_SECTION_END_MISSING: {section.Name}");
            offset += end.Length;
        }
        Assert.AreEqual(notice.Length, offset, "NOTICE_TRAILING_CONTENT");
    }

    [TestMethod]
    public void Shipped_notice_preserves_each_license_text_start_and_end()
    {
        string notice = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "NOTICE.txt"),
            new UTF8Encoding(false, true));
        var sentinels = new (string Name, string Start, string End)[]
        {
            ("NSIS 3.12 COPYING", "COPYRIGHT\n", "Common Public License version 1.0.\n"),
            ("Microsoft.NETCore.App.Runtime.win-x64 10.0.12 LICENSE.TXT", "The MIT License (MIT)\n", "SOFTWARE.\n"),
            ("Microsoft.NETCore.App.Runtime.win-x64 10.0.12 THIRD-PARTY-NOTICES.TXT", ".NET Runtime uses", "within the United States.\n\n"),
            ("Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.12 LICENSE", "The MIT License (MIT)\n", "SOFTWARE.")
        };
        foreach (var section in sentinels)
        {
            int begin = notice.IndexOf("----- BEGIN " + section.Name + " (", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, $"NOTICE_SECTION_MISSING: {section.Name}");
            int payload = notice.IndexOf('\n', begin) + 1;
            int end = notice.IndexOf("\n----- END " + section.Name + " -----", payload, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(payload, end, $"NOTICE_SECTION_END_MISSING: {section.Name}");
            string text = notice[payload..end];
            Assert.IsTrue(text.StartsWith(section.Start, StringComparison.Ordinal), $"NOTICE_SECTION_FIRST_TEXT_MISMATCH: {section.Name}");
            Assert.IsTrue(text.EndsWith(section.End, StringComparison.Ordinal), $"NOTICE_SECTION_LAST_TEXT_MISMATCH: {section.Name}");
        }
    }

    [TestMethod]
    public void Harness_compile_contract_requires_inputs_and_selects_zlib()
    {
        string path = Path.Combine(RepoRoot(), "tests", "GitHubBackup.App.Tests", "Fixtures", "SetupHarness.nsi");
        string[] directives = File.ReadAllLines(path).Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith(';')).ToArray();
        int compressor = Array.FindIndex(directives, line => line.Equals("SetCompressor zlib", StringComparison.OrdinalIgnoreCase));
        int inputGuard = Array.IndexOf(directives, "!ifndef SETUP_INPUTS");
        int outputGuard = Array.IndexOf(directives, "!ifndef SETUP_HARNESS_OUTPUT");
        int inputInclude = Array.IndexOf(directives, "!include \"${SETUP_INPUTS}\"");
        int guardInclude = Array.IndexOf(directives, "!include \"..\\..\\..\\publish\\Setup\\SetupGuards.nsh\"");
        int section = Array.IndexOf(directives, "Section");
        Assert.IsTrue(compressor >= 0 && compressor < section, "HARNESS_ZLIB_NOT_SELECTED_BEFORE_SECTION");
        Assert.AreEqual(1, directives.Count(line => line.StartsWith("SetCompressor ", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(inputGuard >= 0 && inputGuard < inputInclude && outputGuard >= 0 && outputGuard < inputInclude);
        Assert.IsGreaterThan(inputGuard, Array.IndexOf(directives, "!error \"SETUP_INPUTS_REQUIRED\""));
        Assert.IsGreaterThan(outputGuard, Array.IndexOf(directives, "!error \"SETUP_HARNESS_OUTPUT_REQUIRED\""));
        Assert.IsTrue(inputInclude < guardInclude && guardInclude < section);
    }

    [TestMethod]
    public void Harness_reports_fixed_unique_failure_stage_and_zero_only_after_all_checks()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "GitHubBackup.App.Tests",
            "Fixtures", "SetupHarness.nsi")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var stages = new (string Name, int Code, string Call, string FailureBranch)[]
        {
            ("HOST", 40, "ValidateHost", "done"),
            ("ARGUMENTS", 41, "ValidateDirectoryArguments", "done"),
            ("CAPACITY", 42, "CheckShortcutCapacity", "done"),
            ("MUTEX", 43, "AcquireSetupMutex", "done"),
            ("PLUGIN_DIR", 44, "InitPluginsDir", "plugin_dir_failed"),
            ("PRIVATE_FILE", 45, "CheckOwnedFileFixture", "done"),
            ("RECEIPT", 46, "CheckOwnedFileFixture", "done"),
            ("PUBLIC_FILE", 47, "CheckOwnedFileFixture", "done"),
            ("UNPROTECTED_FILE", 48, "CheckOwnedFileFixture", "done"),
            ("ANCESTORS", 49, "PinExistingInstallAncestors", "done"),
            ("FILE_ID", 50, "OpenPathIdentityLease", "release_pins")
        };
        var definitions = System.Text.RegularExpressions.Regex.Matches(source,
            @"(?m)^!define HARNESS_FAIL_([A-Z_]+) ([0-9]+)$");
        CollectionAssert.AreEquivalent(new[] { "ENTRY:39" }.Concat(stages.Select(stage => $"{stage.Name}:{stage.Code}")).ToArray(),
            definitions.Select(match => $"{match.Groups[1].Value}:{match.Groups[2].Value}").ToArray(),
            "HARNESS_STAGE_DEFINITIONS_NOT_FIXED");
        CollectionAssert.AreEquivalent(new[] { 39 }.Concat(stages.Select(stage => stage.Code)).ToArray(),
            definitions.Select(match => int.Parse(match.Groups[2].Value)).Distinct().ToArray(),
            "HARNESS_STAGE_CODES_NOT_FIXED_AND_UNIQUE");
        StringAssert.Contains(source, "Function .onInit\n    SetErrorLevel ${HARNESS_FAIL_ENTRY}\nFunctionEnd",
            "HARNESS_ENTRY_SENTINEL_MISSING");
        int sectionStart = source.IndexOf("\nSection\n", StringComparison.Ordinal);
        int sectionEnd = source.IndexOf("\nSectionEnd", sectionStart, StringComparison.Ordinal);
        Assert.IsTrue(sectionStart >= 0 && sectionEnd > sectionStart, "HARNESS_SECTION_MISSING");
        string section = source[sectionStart..sectionEnd];
        int previous = -1;
        foreach (var stage in stages)
        {
            StringAssert.Contains(source, $"!define HARNESS_FAIL_{stage.Name} {stage.Code}");
            string marker = $"StrCpy $HarnessFailureStage ${{HARNESS_FAIL_{stage.Name}}}";
            int at = section.IndexOf(marker, StringComparison.Ordinal);
            if (at <= previous) Assert.Fail($"HARNESS_STAGE_ORDER_OR_MARKER_WRONG:{stage.Name}");
            StringAssert.Contains(section[at..], marker + "\n    SetErrorLevel $HarnessFailureStage",
                $"HARNESS_STAGE_EXIT_SENTINEL_NOT_SET:{stage.Name}");
            int next = section.IndexOf("StrCpy $HarnessFailureStage ${HARNESS_FAIL_", at + marker.Length,
                StringComparison.Ordinal);
            string block = section[at..(next < 0 ? section.Length : next)];
            StringAssert.Contains(block, stage.Name == "PLUGIN_DIR" ? stage.Call : $"Call {stage.Call}",
                $"HARNESS_STAGE_CALL_MISSING:{stage.Name}");
            if (stage.Name == "PLUGIN_DIR")
                StringAssert.Contains(block, "IfErrors plugin_dir_failed\n    Goto plugin_dir_ready\nplugin_dir_failed:\n    StrCpy $SetupCode 11\n    Goto done",
                    "HARNESS_PLUGIN_DIR_FAILURE_NOT_CLASSIFIED");
            else
                StringAssert.Contains(block, $"StrCmp $SetupCode 0 0 {stage.FailureBranch}",
                    $"HARNESS_STAGE_FAILURE_BRANCH_MISSING:{stage.Name}");
            string? fixtureName = stage.Name switch
            {
                "PRIVATE_FILE" => "private.dat", "RECEIPT" => "receipt.dat",
                "PUBLIC_FILE" => "public.dat", "UNPROTECTED_FILE" => "unprotected.dat",
                _ => null
            };
            if (fixtureName is not null)
                StringAssert.Contains(block, $"StrCpy $HarnessFixtureName \"{fixtureName}\"",
                    $"HARNESS_STAGE_FIXTURE_MISMATCH:{stage.Name}");
            previous = at;
        }
        StringAssert.Contains(section, "done:\n    StrCmp $SetupCode 0 harness_success\n    SetErrorLevel $HarnessFailureStage\n    Goto harness_done\nharness_success:\n    SetErrorLevel 0\nharness_done:");
        Assert.IsFalse(source.Contains("GitHubBackup.nsi", StringComparison.Ordinal),
            "HARNESS_MUST_NOT_CALL_FORMAL_INSTALLER");
    }

    [TestMethod]
    public void Formal_installer_source_is_fixed_and_delegates_to_guarded_managed_lifecycle()
    {
        string setupPath = Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi");
        Assert.IsTrue(File.Exists(setupPath), "SETUP_INSTALLER_SOURCE_MISSING");
        string source = File.ReadAllText(setupPath);
        foreach (string required in new[]
        {
            "Unicode true", "RequestExecutionLevel user", "!include \"MUI2.nsh\"",
            "!include \"SetupGuards.nsh\"", "!insertmacro SetupNativeFoundation \"\"",
            "!insertmacro SetupNativeFoundation \"un.\"", "SetShellVarContext current",
            "SetRegView 64", "Call ValidateHost", "Call ValidateDirectoryArguments"
        })
            StringAssert.Contains(source, required);
        AssertManagedLifecycleDelegation(source);
        Assert.IsLessThan(source.IndexOf("Call ValidateDirectoryArguments", StringComparison.Ordinal),
            source.IndexOf("Call ValidateHost", StringComparison.Ordinal));
        StringAssert.Contains(source, "Function .onInit");
        StringAssert.Contains(source, "Function un.onInit");
        StringAssert.Contains(source, "Call un.ValidateHost");
        StringAssert.Contains(source, "Call un.ValidateDirectoryArguments");
        Assert.IsFalse(source.Contains("MUI_PAGE_DIRECTORY", StringComparison.Ordinal) ||
            source.Contains("MUI_FINISHPAGE_RUN", StringComparison.Ordinal),
            "NO_DIRECTORY_OVERRIDE_OR_DEFAULT_APP_LAUNCH");
        foreach (string line in source.Split('\n').Select(line => line.TrimStart()))
            Assert.IsFalse(new[] { "Delete ", "RMDir ", "WriteReg", "CreateShortCut ",
                "Exec ", "ExecShell " }.Any(line.StartsWith),
                "WRAPPER_MUST_NOT_MUTATE_PRODUCT_OR_LAUNCH_ITS_UI_DIRECTLY");

        string harness = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "GitHubBackup.App.Tests",
            "Fixtures", "SetupHarness.nsi"));
        StringAssert.Contains(harness, "!include \"..\\..\\..\\publish\\Setup\\SetupGuards.nsh\"");
        StringAssert.Contains(harness, "!insertmacro SetupNativeFoundation \"\"");
        Assert.IsFalse(harness.Contains("GitHubBackup.nsi", StringComparison.Ordinal),
            "TEST_HARNESS_MUST_NOT_IMPORT_PRODUCT_ENTRY");
        foreach (string line in harness.Split('\n').Select(line => line.TrimStart()))
            Assert.IsFalse(new[] { "File ", "WriteUninstaller ", "WriteReg", "CreateShortCut ",
                "Exec ", "ExecWait " }.Any(line.StartsWith), "TEST_HARNESS_MUST_NOT_INSTALL_PRODUCT");
    }

    [TestMethod]
    public void Formal_installer_defaults_to_known_folder_and_explains_current_user_flow_in_chinese()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        StringAssert.Contains(source,
            "!define MUI_WELCOMEPAGE_TITLE \"欢迎使用 GitHub 备份工具安装向导\"");
        StringAssert.Contains(source,
            "!define MUI_WELCOMEPAGE_TEXT \"此向导只为当前用户安装，不会请求管理员权限，也不会自动启动程序。\"");
        StringAssert.Contains(source,
            "!define MUI_FINISHPAGE_TEXT \"安装完成后，程序不会自动启动。\"");
        StringAssert.Contains(source, "!insertmacro MUI_LANGUAGE \"SimpChinese\"");
        Assert.IsFalse(source.Contains("MUI_PAGE_DIRECTORY", StringComparison.Ordinal),
            "INSTALL_DIRECTORY_MUST_NOT_BE_USER_SELECTABLE");
        Assert.IsFalse(source.Contains("MUI_FINISHPAGE_RUN", StringComparison.Ordinal),
            "APP_MUST_NOT_AUTO_LAUNCH_OR_SHOW_RUN_CHECKBOX_IN_THIS_SLICE");

        int installStart = source.IndexOf("Function .onInit", StringComparison.Ordinal);
        int installEnd = source.IndexOf("FunctionEnd", installStart, StringComparison.Ordinal);
        Assert.IsTrue(installStart >= 0 && installEnd > installStart, "INSTALL_ONINIT_MISSING");
        string installInit = source[installStart..installEnd];
        int validateHost = installInit.IndexOf("Call ValidateHost", StringComparison.Ordinal);
        int setFixedDirectory = installInit.IndexOf("StrCpy $INSTDIR $SetupFixedRoot", StringComparison.Ordinal);
        int validateArguments = installInit.IndexOf("Call ValidateDirectoryArguments", StringComparison.Ordinal);
        Assert.IsTrue(validateHost >= 0 && setFixedDirectory > validateHost &&
            validateArguments > setFixedDirectory,
            "INSTALL_DIRECTORY_MUST_BE_DERIVED_FROM_VALIDATED_KNOWN_FOLDER_BEFORE_ARGUMENT_CHECK");

        int uninstallStart = source.IndexOf("Function un.onInit", StringComparison.Ordinal);
        int uninstallEnd = source.IndexOf("FunctionEnd", uninstallStart, StringComparison.Ordinal);
        Assert.IsTrue(uninstallStart >= 0 && uninstallEnd > uninstallStart, "UNINSTALL_ONINIT_MISSING");
        string uninstallInit = source[uninstallStart..uninstallEnd];
        int setUninstallMode = uninstallInit.IndexOf("StrCpy $SetupMode \"uninstall\"", StringComparison.Ordinal);
        int validateUninstallHost = uninstallInit.IndexOf("Call un.ValidateHost", StringComparison.Ordinal);
        int validateUninstallArguments = uninstallInit.IndexOf("Call un.ValidateDirectoryArguments", StringComparison.Ordinal);
        Assert.IsTrue(setUninstallMode >= 0 && validateUninstallHost > setUninstallMode &&
            validateUninstallArguments > validateUninstallHost,
            "UNINSTALL_ARGUMENTS_MUST_BE_CHECKED_ONLY_AFTER_CURRENT_USER_KNOWN_FOLDER_DERIVATION");

        string guards = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"));
        StringAssert.Contains(guards, "SHGetKnownFolderPath");
        StringAssert.Contains(guards, "IsWow64Process2");
        StringAssert.Contains(guards, "GetVersionExW");
        StringAssert.Contains(guards, "GetTokenInformation");
        StringAssert.Contains(guards, "GetCommandLineW");
        StringAssert.Contains(guards, "StrCmpS $4 '/D='");
        StringAssert.Contains(guards, "StrCmpS $4 '_?='");
        StringAssert.Contains(guards, "StrCmp $SetupMode 'uninstall' 0 args_done");
    }

    [TestMethod]
    public void Commented_or_conditional_fail_stop_cannot_satisfy_synthetic_compile_contract()
    {
        const string stop = "!error \"SETUP_LIFECYCLE_NOT_IMPLEMENTED\"";
        string source = "Unicode true\n" + stop + "\nOutFile \"${SETUP_OUTPUT_FILE}\"\n";
        Assert.IsTrue(HasActiveFailStopBeforeOutput(source));
        foreach (string replacement in new[]
        {
            "; " + stop, "# " + stop, "/* " + stop + " */",
            "!ifdef NEVER_ENABLED\n" + stop + "\n!endif",
            "!macro NeverCalled\n" + stop + "\n!macroend"
        })
        {
            string mutated = source.Replace(stop, replacement, StringComparison.Ordinal);
            Assert.AreNotEqual(source, mutated, "FAIL_STOP_MUTATION_DID_NOT_APPLY");
            Assert.IsFalse(HasActiveFailStopBeforeOutput(mutated), "COMMENTED_OR_CONDITIONAL_FAIL_STOP_ACCEPTED");
        }
    }

    [TestMethod]
    public void Continued_comment_and_tab_macro_cannot_hide_synthetic_fail_stop()
    {
        const string stop = "!error \"SETUP_LIFECYCLE_NOT_IMPLEMENTED\"";
        string source = "Unicode true\n" + stop + "\nOutFile \"${SETUP_OUTPUT_FILE}\"\n";
        string[] accepted = new[]
        {
            (Name: "tab macro", Replacement: "!macro\tNeverCalled\n" + stop + "\n!macroend"),
            (Name: "continued comment", Replacement: "# comment " + '\\' + "\n" + stop)
        }
            .Where(test => HasActiveFailStopBeforeOutput(source.Replace(stop, test.Replacement, StringComparison.Ordinal)))
            .Select(test => test.Name).ToArray();
        Assert.IsEmpty(accepted, "FALSE_ACTIVE_FAIL_STOP: " + string.Join(", ", accepted));
    }

    private static bool HasActiveFailStopBeforeOutput(string source)
    {
        const string stop = "!error \"SETUP_LIFECYCLE_NOT_IMPLEMENTED\"";
        bool inBlockComment = false;
        bool sawUnconditionalStop = false;
        int conditionalDepth = 0;
        int macroDepth = 0;
        string? continued = null;
        foreach (string raw in source.Split('\n'))
        {
            continued += raw.TrimEnd('\r');
            if (continued.EndsWith("\\", StringComparison.Ordinal))
            {
                continued = continued[..^1];
                continue;
            }
            string line = continued;
            continued = null;
            if (inBlockComment)
            {
                int end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0) continue;
                line = line[(end + 2)..];
                inBlockComment = false;
            }
            while (true)
            {
                int block = line.IndexOf("/*", StringComparison.Ordinal);
                int semicolon = line.IndexOf(';');
                int hash = line.IndexOf('#');
                int comment = new[] { semicolon, hash }.Where(index => index >= 0).DefaultIfEmpty(-1).Min();
                if (block >= 0 && (comment < 0 || block < comment))
                {
                    int end = line.IndexOf("*/", block + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        line = line[..block];
                        inBlockComment = true;
                        break;
                    }
                    line = line.Remove(block, end + 2 - block);
                    continue;
                }
                if (comment >= 0) line = line[..comment];
                break;
            }
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("!if", StringComparison.OrdinalIgnoreCase)) { conditionalDepth++; continue; }
            if (line.Equals("!endif", StringComparison.OrdinalIgnoreCase)) { conditionalDepth--; continue; }
            if (line.StartsWith("!macro", StringComparison.OrdinalIgnoreCase) &&
                line.Length > 6 && char.IsWhiteSpace(line[6])) { macroDepth++; continue; }
            if (line.Equals("!macroend", StringComparison.OrdinalIgnoreCase)) { macroDepth--; continue; }
            if (line.Equals(stop, StringComparison.Ordinal) && conditionalDepth == 0 && macroDepth == 0)
                sawUnconditionalStop = true;
            if (line.Equals("OutFile \"${SETUP_OUTPUT_FILE}\"", StringComparison.Ordinal))
                return sawUnconditionalStop;
        }
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr BeginUpdateResource(string fileName, bool deleteExistingResources);

    [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateResource(IntPtr handle, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);

    [DllImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndUpdateResource(IntPtr handle, bool discard);

    [TestMethod]
    public void Native_guard_source_is_present_for_later_authorized_compilation()
    {
        // Source availability only. This does not compile or exercise a native guard.
        Assert.IsTrue(File.Exists(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh")),
            "SETUP_NATIVE_GUARD_SOURCE_MISSING");
    }

    [TestMethod]
    public void Staged_file_copy_contract_uses_create_new_same_handles_and_fail_closed_cleanup()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        static string FunctionBody(string source, string name)
        {
            int begin = source.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return source[begin..end];
        }
        string copy = FunctionBody(guard, "CopyTrustedStageFileToFixedRoot");
        foreach (string required in new[]
        {
            "Call ${PREFIX}ValidateCopyManifestName",
            "StrCmp $SetupCopyTransactionActive 1 copy_transaction_active",
            "StrCmp $SetupCopyRootHandle 0 copy_failure copy_root_reuse",
            "Call ${PREFIX}OpenPathIdentityLease",
            "StrCpy $SetupCopySourceHandle $GuardHandle",
            "StrCpy $GuardHandle $SetupCopySourceHandle",
            "Call ${PREFIX}HashHandleSha256",
            "StrCmp $GuardHash $SetupCopyExpectedHash",
            "StrCpy $GuardHandle $SetupCopyRootHandle",
            "StrCmp $GuardIdentity $SetupCopyRootIdentity",
            "StrCpy $SetupCopyStagingName \".GitHubBackupTool.setup-stage\"",
            "CreateFileW(w \"$SetupFixedRoot\\$SetupCopyStagingName\", i 0xC0030000, i 0, p $SetupCopySecurityAttributes, i 1, i 0x00200080, p 0)",
            "StrCmp $1 \"\\\\?\\$SetupFixedRoot\\$SetupCopyStagingName\" 0 copy_failure",
            "StrCpy $SetupCopyTargetHandle $0",
            "StrCpy $GuardHandle $SetupCopyTargetHandle",
            "ReadFile(p $SetupCopySourceHandle, p $SetupCopyBuffer, i 65536",
            "WriteFile(p $SetupCopyTargetHandle, p r0, i r5",
            "IntOp $SetupCopyWriteOffset $SetupCopyWriteOffset + $6",
            "FlushFileBuffers(p $SetupCopyTargetHandle)",
            "StrCmp $GuardHash $SetupCopyExpectedHash",
            "StrLen $3 $SetupCopyTargetName",
            "IntOp $5 $3 * 2",
            "IntOp $6 $5 + 12",
            "IntOp $7 $6 + 2",
            "StrCpy $SetupCopyRenameBufferBytes $6",
            "System::Call '*$SetupCopyRenameBuffer(i 0, p $SetupCopyRootHandle, i r5)'",
            "System::Call 'kernel32::lstrcpyW(p r8, w \"$SetupCopyTargetName\") p.r9'",
            "System::Call 'kernel32::SetFileInformationByHandle(p $SetupCopyTargetHandle, i 3, p $SetupCopyRenameBuffer, i $SetupCopyRenameBufferBytes) i.r1'",
            "StrCmp $1 0 copy_failure",
            "StrCmp $1 \"\\\\?\\$SetupFixedRoot\\$SetupCopyTargetName\" 0 copy_failure",
            "StrCmp $GuardIdentity $SetupCopyTargetIdentity 0 copy_failure",
            "StrCpy $SetupCopyTxnAppHandle $SetupCopyTargetHandle",
            "StrCpy $SetupCopyTxnNoticeHandle $SetupCopyTargetHandle",
            "StrCpy $SetupCopyTargetDelete 0",
            "StrCpy $SetupCode 0"
        })
            StringAssert.Contains(copy, required, "STAGED_COPY_CONTRACT_MISSING: " + required);
        Assert.IsFalse(copy.Contains(
            "StrCmp $1 \"\\\\?\\$SetupFixedRoot\\\\$SetupCopyStagingName\" 0 copy_failure",
            StringComparison.Ordinal),
            "STAGING_FINAL_PATH_MUST_USE_THE_SAME_SINGLE_SEPARATOR_AS_CREATEFILEW");

        int validateName = copy.IndexOf("Call ${PREFIX}ValidateCopyManifestName", StringComparison.Ordinal);
        int openSource = copy.IndexOf("Call ${PREFIX}OpenPathIdentityLease", StringComparison.Ordinal);
        int hashSource = copy.IndexOf("Call ${PREFIX}HashHandleSha256", StringComparison.Ordinal);
        int createTarget = copy.IndexOf("CreateFileW(w \"$SetupFixedRoot\\$SetupCopyStagingName\"", StringComparison.Ordinal);
        int readSource = copy.IndexOf("ReadFile(p $SetupCopySourceHandle", StringComparison.Ordinal);
        int writeTarget = copy.IndexOf("WriteFile(p $SetupCopyTargetHandle", StringComparison.Ordinal);
        int flushTarget = copy.IndexOf("FlushFileBuffers(p $SetupCopyTargetHandle)", StringComparison.Ordinal);
        int hashTarget = copy.IndexOf("StrCpy $GuardHandle $SetupCopyTargetHandle", flushTarget, StringComparison.Ordinal);
        int finalHash = copy.IndexOf("StrCmp $GuardHash $SetupCopyExpectedHash", flushTarget, StringComparison.Ordinal);
        int filenameLength = copy.IndexOf("StrLen $3 $SetupCopyTargetName", finalHash, StringComparison.Ordinal);
        int filenameLengthBytes = copy.IndexOf("IntOp $5 $3 * 2", filenameLength, StringComparison.Ordinal);
        int renameBufferHeaderBytes = copy.IndexOf("IntOp $6 $5 + 12", filenameLengthBytes, StringComparison.Ordinal);
        int renameAllocationBytes = copy.IndexOf("IntOp $7 $6 + 2", renameBufferHeaderBytes, StringComparison.Ordinal);
        int renameAllocation = copy.IndexOf("System::Alloc $7", renameAllocationBytes, StringComparison.Ordinal);
        int renameBufferBytes = copy.IndexOf("StrCpy $SetupCopyRenameBufferBytes $6", renameAllocation, StringComparison.Ordinal);
        int renameBuffer = copy.IndexOf("System::Call '*$SetupCopyRenameBuffer(i 0, p $SetupCopyRootHandle, i r5)'", StringComparison.Ordinal);
        int renameCall = copy.IndexOf("SetFileInformationByHandle(p $SetupCopyTargetHandle, i 3, p $SetupCopyRenameBuffer", StringComparison.Ordinal);
        int finalPathCheck = copy.IndexOf("StrCmp $1 \"\\\\?\\$SetupFixedRoot\\$SetupCopyTargetName\"", renameCall, StringComparison.Ordinal);
        int deleteAuthorizationReleased = copy.IndexOf("StrCpy $SetupCopyTargetDelete 0", createTarget, StringComparison.Ordinal);
        Assert.IsTrue(validateName < openSource && openSource < hashSource && hashSource < createTarget &&
            createTarget < readSource && readSource < writeTarget && writeTarget < flushTarget && flushTarget < hashTarget &&
            hashTarget < finalHash && finalHash < filenameLength && filenameLength < filenameLengthBytes &&
            filenameLengthBytes < renameBufferHeaderBytes && renameBufferHeaderBytes < renameAllocationBytes &&
            renameAllocationBytes < renameAllocation && renameAllocation < renameBufferBytes &&
            renameBufferBytes < renameBuffer &&
            renameBuffer < renameCall && renameCall < finalPathCheck &&
            finalPathCheck < deleteAuthorizationReleased,
            "STAGED_COPY_ORDER_OR_SAME_HANDLE_VERIFICATION_WRONG");
        StringAssert.Contains(copy, "IntOp $0 $SetupCopyBuffer + $SetupCopyWriteOffset",
            "SHORT_WRITE_MUST_ADVANCE_WITHIN_THE_SAME_BUFFER");
        Assert.IsFalse(copy.Contains("CREATE_ALWAYS", StringComparison.Ordinal) ||
            copy.Contains("CopyFile", StringComparison.Ordinal) ||
            copy.Contains("CreateFileW(w \"$SetupFixedRoot\\$SetupCopyTargetName\"", StringComparison.Ordinal) ||
            copy.Contains("DeleteFileW", StringComparison.Ordinal) ||
            copy.Contains("RMDir", StringComparison.Ordinal),
            "STAGED_COPY_MUST_NOT_OVERWRITE_OR_REOPEN_PATH_FOR_CLEANUP");

        string release = FunctionBody(guard, "ReleaseStagedCopyHandles");
        Assert.IsFalse(release.Contains("SetFileInformationByHandle(p $SetupCopyTargetHandle, i 4", StringComparison.Ordinal),
            "RESOURCE_RELEASE_MUST_NOT_DELETE_WITHOUT_ROLLBACK_REVALIDATION");
        StringAssert.Contains(release, "StrCmp $SetupCopyTargetHandle 0 staged_release_target_empty");
        StringAssert.Contains(release, "CloseHandle(p $SetupCopySourceHandle)");
        StringAssert.Contains(release, "StrCpy $SetupCopySourceHandle 0");
        StringAssert.Contains(release, "System::Free $SetupCopyRenameBuffer");
        StringAssert.Contains(release, "StrCpy $SetupCopyRenameBuffer 0");

        string manifest = FunctionBody(guard, "ValidateCopyManifestName");
        foreach (string name in new[]
        {
            "${SETUP_APP_NAME}", "${SETUP_UNINSTALLER_NAME}", "${SETUP_NOTICE_NAME}", "${SETUP_RECEIPT_NAME}"
        })
            StringAssert.Contains(manifest, name, "STAGED_COPY_TARGET_MUST_BE_FIXED_MANIFEST_ONLY: " + name);
        StringAssert.Contains(manifest, "StrCpy $SetupCopySourcePath \"$PLUGINSDIR\\${SETUP_APP_NAME}\"");
        StringAssert.Contains(manifest, "StrCpy $SetupCopyExpectedHash \"${SETUP_APP_SHA256}\"");
        StringAssert.Contains(manifest, "StrCpy $SetupCopySourcePath \"$PLUGINSDIR\\${SETUP_NOTICE_NAME}\"");
        StringAssert.Contains(manifest, "StrCpy $SetupCopyExpectedHash \"${SETUP_NOTICE_SHA256}\"");
        StringAssert.Contains(manifest, "copy_manifest_refuse");

        string installer = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        AssertManagedLifecycleDelegation(installer);
        Assert.IsFalse(installer.Contains("Call CopyTrustedStageFileToFixedRoot", StringComparison.Ordinal) ||
            installer.Contains("Call un.CopyTrustedStageFileToFixedRoot", StringComparison.Ordinal),
            "PRODUCT_COPY_MUST_BE_DELEGATED_TO_MANAGED_LIFECYCLE");
    }

    [TestMethod]
    public void Fresh_install_payload_copy_transaction_holds_one_root_and_rolls_back_only_matching_handles()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        static string FunctionBody(string source, string name)
        {
            int begin = source.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return source[begin..end];
        }

        string transaction = FunctionBody(guard, "CopyFreshInstallPayloadFilesToFixedRoot");
        int emptyRoot = transaction.IndexOf("Call ${PREFIX}ValidateEmptyFixedRootDirectory", StringComparison.Ordinal);
        int pendingRootLease = transaction.IndexOf("StrCpy $SetupCopyRootLeasePending 1", StringComparison.Ordinal);
        int appName = transaction.IndexOf("StrCpy $SetupCopyTargetName \"${SETUP_APP_NAME}\"", StringComparison.Ordinal);
        int firstCopy = transaction.IndexOf("Call ${PREFIX}CopyTrustedStageFileToFixedRoot", StringComparison.Ordinal);
        int noticeName = transaction.IndexOf("StrCpy $SetupCopyTargetName \"${SETUP_NOTICE_NAME}\"", StringComparison.Ordinal);
        int secondCopy = transaction.IndexOf("Call ${PREFIX}CopyTrustedStageFileToFixedRoot", firstCopy + 1, StringComparison.Ordinal);
        Assert.IsTrue(emptyRoot >= 0 && emptyRoot < appName && appName < firstCopy &&
            firstCopy < noticeName && noticeName < secondCopy,
            "COPY_TRANSACTION_MUST_VALIDATE_ONE_EMPTY_ROOT_THEN_COPY_APP_AND_NOTICE_IN_ORDER");
        Assert.IsLessThan(emptyRoot, pendingRootLease,
            "ROOT_LEASE_MUST_BE_MARKED_PENDING_BEFORE_EMPTY_ROOT_VALIDATION_CAN_FAIL");
        Assert.AreEqual(2, CountOccurrences(transaction, "Call ${PREFIX}CopyTrustedStageFileToFixedRoot"),
            "COPY_TRANSACTION_MUST_REGISTER_EXACTLY_THE_TWO_APPROVED_PAYLOAD_FILES");
        StringAssert.Contains(transaction, "Call ${PREFIX}AbortFreshInstallCopyTransaction",
            "ANY_FILE_COPY_FAILURE_MUST_ROLL_BACK_THE_CURRENT_TRANSACTION");
        StringAssert.Contains(transaction, "StrCpy $SetupCopyRootHandle $GuardHandle",
            "EMPTY_ROOT_LEASE_MUST_BE_TRANSFERRED_AND_REUSED");
        StringAssert.Contains(transaction, "StrCpy $SetupCopyRootIdentity $GuardIdentity",
            "EMPTY_ROOT_IDENTITY_MUST_REMAIN_BOUND_TO_THE_RETAINED_HANDLE");
        int rootTransfer = transaction.IndexOf("StrCpy $SetupCopyRootHandle $GuardHandle", StringComparison.Ordinal);
        int rootTransferComplete = transaction.IndexOf("StrCpy $SetupCopyRootLeasePending 0", rootTransfer, StringComparison.Ordinal);
        Assert.IsGreaterThan(rootTransfer, rootTransferComplete,
            "PENDING_ROOT_LEASE_MUST_CLEAR_ONLY_AFTER_HANDLE_AND_IDENTITY_TRANSFER");

        string copy = FunctionBody(guard, "CopyTrustedStageFileToFixedRoot");
        StringAssert.Contains(copy, "StrCmp $SetupCopyTransactionActive 1", "COPY_MUST_REQUIRE_ACTIVE_TRANSACTION");
        Assert.IsFalse(copy.Contains("Call ${PREFIX}AcquireFixedRootOwnershipLease", StringComparison.Ordinal),
            "EACH_FILE_MUST_REUSE_THE_SINGLE_TRANSACTION_ROOT_LEASE");
        StringAssert.Contains(copy, "StrCmp $SetupCopyRootHandle 0 copy_failure copy_root_reuse",
            "SECOND_COPY_MUST_REUSE_THE_RETAINED_ROOT_HANDLE");
        StringAssert.Contains(copy, "StrCmp $GuardPathPins \"\" copy_done",
            "PER_FILE_COPY_MUST_RETAIN_THE_EMPTY_ROOT_SCAN_ANCESTOR_PINS");
        StringAssert.Contains(copy, "StrCmp $GuardPathPinCount 0 copy_done",
            "PER_FILE_COPY_MUST_REQUIRE_THE_RETAINED_ANCESTOR_PIN_SET");
        StringAssert.Contains(copy, "StrCpy $SetupCopyTxnAppHandle $SetupCopyTargetHandle",
            "APP_HANDLE_MUST_MOVE_INTO_ITS_OWN_TRANSACTION_LEDGER_SLOT");
        StringAssert.Contains(copy, "StrCpy $SetupCopyTxnNoticeHandle $SetupCopyTargetHandle",
            "NOTICE_HANDLE_MUST_MOVE_INTO_ITS_OWN_TRANSACTION_LEDGER_SLOT");
        StringAssert.Contains(copy, "StrCpy $SetupCopyTxnAppIdentity $SetupCopyTargetIdentity");
        StringAssert.Contains(copy, "StrCpy $SetupCopyTxnNoticeIdentity $SetupCopyTargetIdentity");
        StringAssert.Contains(copy, "StrCpy $SetupCopyTxnAppHash $SetupCopyResultHash");
        StringAssert.Contains(copy, "StrCpy $SetupCopyTxnNoticeHash $SetupCopyResultHash");
        StringAssert.Contains(copy, "StrCpy $SetupCopyTxnAppCreated 1");
        StringAssert.Contains(copy, "StrCpy $SetupCopyTxnNoticeCreated 1");
        Assert.IsFalse(copy.Contains("Call ${PREFIX}ReleaseStagedCopyHandles", StringComparison.Ordinal),
            "SUCCESS_OR_COPY_FAILURE_MUST_NOT_DISCARD_TRANSACTION_ROLLBACK_RIGHTS");
        int createTarget = copy.IndexOf("StrCpy $SetupCopyTargetHandle $0", StringComparison.Ordinal);
        int firstTargetIdentity = createTarget < 0 ? -1 : copy.IndexOf(
            "StrCpy $SetupCopyTargetIdentity \"$3:$4:$5:$6:$7:$8\"", createTarget, StringComparison.Ordinal);
        int freeSecurityDescriptor = copy.IndexOf("LocalFree(p $SetupCopySecurityDescriptor)", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, createTarget,
            "CREATE_NEW_HANDLE_MUST_BE_STORED_BEFORE_IDENTITY_CAPTURE");
        Assert.IsGreaterThan(createTarget, firstTargetIdentity,
            "CREATE_NEW_HANDLE_IDENTITY_MUST_BE_CAPTURED_BEFORE_LATER_FAILURE_POINTS");
        Assert.IsGreaterThan(firstTargetIdentity, freeSecurityDescriptor,
            "CREATE_NEW_HANDLE_IDENTITY_MUST_BE_CAPTURED_BEFORE_LATER_FAILURE_POINTS");
        StringAssert.Contains(copy,
            "IntOp $2 $1 & 0x10 ; refuse directories\n    StrCmp $2 0 0 copy_failure",
            "CREATE_NEW_TARGET_MUST_ACCEPT_ORDINARY_FILES_AND_REJECT_DIRECTORIES");
        StringAssert.Contains(copy,
            "IntOp $2 $1 & 0x10 ; must be an ordinary file\n    StrCmp $2 0 0 copy_failure",
            "RENAMED_TARGET_MUST_ACCEPT_ORDINARY_FILES_AND_REJECT_DIRECTORIES");

        string abort = FunctionBody(guard, "AbortFreshInstallCopyTransaction");
        int rollback = abort.IndexOf("Call ${PREFIX}RollbackFreshInstallPayloadCopies", StringComparison.Ordinal);
        int release = abort.IndexOf("Call ${PREFIX}ReleaseStagedCopyHandles", StringComparison.Ordinal);
        Assert.IsTrue(rollback >= 0 && release > rollback,
            "ROOT_AND_SOURCE_HANDLES_MAY_ONLY_BE_RELEASED_AFTER_FILE_ROLLBACK_SUCCEEDS");
        StringAssert.Contains(abort, "StrCpy $SetupCopyTransactionActive 0",
            "TRANSACTION_MUST_STAY_ACTIVE_WHEN_ROLLBACK_OR_RELEASE_FAILS");

        string rollbackFiles = FunctionBody(guard, "RollbackFreshInstallPayloadCopies");
        int noticeRollback = rollbackFiles.IndexOf("StrCmp $SetupCopyTxnNoticeCreated 1", StringComparison.Ordinal);
        int appRollback = rollbackFiles.IndexOf("StrCmp $SetupCopyTxnAppCreated 1", StringComparison.Ordinal);
        Assert.IsTrue(noticeRollback >= 0 && appRollback > noticeRollback,
            "ROLLBACK_MUST_PROCESS_CREATED_FILES_IN_REVERSE_ORDER");
        StringAssert.Contains(rollbackFiles, "Call ${PREFIX}ValidateAndRevokeCopyHandle");
        StringAssert.Contains(rollbackFiles, "StrCpy $SetupCopyTxnNoticeHandle $SetupTxnCheckHandle");
        StringAssert.Contains(rollbackFiles, "StrCpy $SetupCopyTxnAppHandle $SetupTxnCheckHandle");
        StringAssert.Contains(rollbackFiles, "StrCpy $SetupCopyTxnNoticeCreated 0");
        StringAssert.Contains(rollbackFiles, "StrCpy $SetupCopyTxnAppCreated 0");
        StringAssert.Contains(rollbackFiles, "StrCpy $SetupCopyTargetIdentity $SetupTxnCheckIdentity",
            "RETRY_MUST_PRESERVE_A_RECOVERED_IDENTITY_FOR_THE_ORIGINAL_CREATE_NEW_HANDLE");
        int emptyStateStart = rollbackFiles.IndexOf("rollback_txn_active:", StringComparison.Ordinal);
        int firstFileState = rollbackFiles.IndexOf("rollback_current_owned:", StringComparison.Ordinal);
        Assert.IsLessThan(firstFileState, emptyStateStart);
        Assert.IsFalse(rollbackFiles[emptyStateStart..firstFileState]
                .Contains("StrCmp $SetupCopyRootHandle", StringComparison.Ordinal) ||
            rollbackFiles[emptyStateStart..firstFileState]
                .Contains("StrCmp $GuardPathPins", StringComparison.Ordinal),
            "PURE_RESOURCE_RETRY_MUST_NOT_REQUIRE_ROOT_OR_PIN_HANDLES");
        foreach (string ownedLabel in new[]
        {
            "rollback_current_owned:",
            "rollback_notice_owned:",
            "rollback_app_owned:"
        })
        {
            int ownedStart = rollbackFiles.IndexOf(ownedLabel, StringComparison.Ordinal);
            int validate = rollbackFiles.IndexOf("Call ${PREFIX}ValidateAndRevokeCopyHandle", ownedStart, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, ownedStart, ownedLabel + "_MISSING");
            Assert.IsGreaterThan(ownedStart, validate, ownedLabel + "_VALIDATION_MISSING");
            StringAssert.Contains(rollbackFiles[ownedStart..validate], "StrCmp $SetupCopyRootHandle",
                "FILE_ROLLBACK_MUST_REQUIRE_THE_RETAINED_ROOT_HANDLE");
            StringAssert.Contains(rollbackFiles[ownedStart..validate], "StrCmp $GuardPathPins",
                "FILE_ROLLBACK_MUST_REQUIRE_RETAINED_PATH_PINS");
        }

        string validateRevoke = FunctionBody(guard, "ValidateAndRevokeCopyHandle");
        foreach (string required in new[]
        {
            "GetFileInformationByHandle(p $SetupTxnCheckHandle",
            "GetFinalPathNameByHandleW(p $SetupTxnCheckHandle",
            "GetFileInformationByHandleEx(p $SetupTxnCheckHandle, i 18",
            "StrCmp $GuardIdentity $SetupTxnCheckIdentity",
            "StrCmp $GuardHash $SetupTxnCheckHash",
            "Call ${PREFIX}ValidatePrivateHandleAcl",
            "SetFileInformationByHandle(p $SetupTxnCheckHandle, i 4",
            "CloseHandle(p $SetupTxnCheckHandle)",
            "StrCmp $0 0 txn_revoke_close_failed"
        })
            StringAssert.Contains(validateRevoke, required, "ROLLBACK_MUST_REVERIFY_SAME_HANDLE: " + required);
        StringAssert.Contains(validateRevoke, "StrCmp $5 1 0 txn_revoke_free_refuse",
            "ROLLBACK_MUST_REFUSE_MULTIPLE_HARD_LINKS");
        StringAssert.Contains(validateRevoke,
            "IntOp $2 $1 & 0x10 ; refuse directories\n    StrCmp $2 0 0 txn_revoke_free_refuse",
            "ROLLBACK_MUST_ACCEPT_ORDINARY_FILES_AND_REJECT_DIRECTORIES");
        StringAssert.Contains(validateRevoke, "StrCmp $SetupTxnCheckHash \"${SETUP_APP_SHA256}\" txn_revoke_app_hash_ok");
        StringAssert.Contains(validateRevoke, "StrCmp $SetupTxnCheckHash \"${SETUP_NOTICE_SHA256}\" txn_revoke_notice_hash_ok");
        StringAssert.Contains(validateRevoke, "StrCmp $GuardHash $SetupTxnCheckActualHash 0 txn_revoke_free_refuse",
            "A_PARTIAL_STAGE_MUST_HAVE_A_STABLE_HANDLE_HASH_BEFORE_DISPOSITION");
        StringAssert.Contains(validateRevoke, "StrCmp $SetupTxnCheckIdentity \"\" txn_revoke_capture_created_stage_identity",
            "AN_IDENTITY_QUERY_FAILURE_MUST_BE_RETRIED_ON_THE_STILL_HELD_CREATE_NEW_HANDLE");
        StringAssert.Contains(validateRevoke, "StrCpy $SetupTxnCheckIdentity $GuardIdentity");
        StringAssert.Contains(validateRevoke, "StrCmp $SetupTxnCheckHandle $SetupCopyTargetHandle 0 txn_revoke_free_refuse",
            "IDENTITY_RECAPTURE_MUST_USE_ONLY_THE_ORIGINAL_CREATE_NEW_HANDLE");
        Assert.IsFalse(validateRevoke.Contains("DeleteFileW", StringComparison.Ordinal) ||
            validateRevoke.Contains("MoveFile", StringComparison.Ordinal) ||
            validateRevoke.Contains("RMDir", StringComparison.Ordinal),
            "ROLLBACK_MUST_NEVER_FALL_BACK_TO_PATH_MUTATION");
        StringAssert.Contains(validateRevoke, "txn_revoke_close_failed:");
        StringAssert.Contains(validateRevoke, "StrCpy $SetupCode 13");
        StringAssert.Contains(validateRevoke, "StrCpy $SetupTxnCheckDeletePending 1");

        string releaseCopy = FunctionBody(guard, "ReleaseStagedCopyHandles");
        Assert.IsFalse(releaseCopy.Contains("SetFileInformationByHandle(p $SetupCopyTargetHandle, i 4", StringComparison.Ordinal),
            "GENERIC_RELEASE_MUST_NOT_DELETE_WITHOUT_TRANSACTION_REVALIDATION");
        StringAssert.Contains(releaseCopy, "StrCmp $SetupCopyTargetHandle 0 staged_release_target_empty");
        StringAssert.Contains(releaseCopy, "StrCmp $SetupCopyRootLeasePending 1 staged_release_capture_guard_root",
            "RELEASE_MUST_RECOVER_A_ROOT_LEASE_RETAINED_IN_GUARD_HANDLE");
        StringAssert.Contains(releaseCopy, "StrCpy $SetupCopyRootHandle $GuardHandle");
        StringAssert.Contains(releaseCopy, "Call ${PREFIX}ReleaseFreshRootScratch");
        Assert.IsGreaterThan(abort.IndexOf("Call ${PREFIX}ReleaseStagedCopyHandles", StringComparison.Ordinal),
            abort.IndexOf("StrCpy $SetupCopyTransactionActive 0", StringComparison.Ordinal),
            "ACTIVE_MAY_CLEAR_ONLY_AFTER_ALL_RETRYABLE_RESOURCES_RELEASE");
        StringAssert.Contains(releaseCopy, "StrCmp $SetupCopyTxnAppCreated 0 staged_release_app_created_empty");
        StringAssert.Contains(releaseCopy, "StrCmp $SetupCopyTxnNoticeCreated 0 staged_release_notice_created_empty");

        string installer = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        AssertManagedLifecycleDelegation(installer);
        Assert.IsFalse(installer.Contains("Call CopyFreshInstallPayloadFilesToFixedRoot", StringComparison.Ordinal) ||
            installer.Contains("Call un.CopyFreshInstallPayloadFilesToFixedRoot", StringComparison.Ordinal),
            "PRODUCT_TRANSACTION_MUST_BE_DELEGATED_TO_MANAGED_LIFECYCLE");

        static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }
    }

    [TestMethod]
    public void Fresh_install_copy_and_rollback_accept_files_but_reject_directory_attributes()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        string copy = GuardFunction("CopyTrustedStageFileToFixedRoot");
        string revoke = GuardFunction("ValidateAndRevokeCopyHandle");
        var mismatches = new List<string>();
        Require(copy,
            "IntOp $2 $1 & 0x10 ; refuse directories\n    StrCmp $2 0 0 copy_failure",
            "CREATE_NEW_TARGET");
        Require(copy,
            "IntOp $2 $1 & 0x10 ; must be an ordinary file\n    StrCmp $2 0 0 copy_failure",
            "STAGED_TARGET");
        Require(copy,
            "IntOp $2 $1 & 0x10 ; must remain an ordinary file\n    StrCmp $2 0 0 copy_failure",
            "RENAMED_TARGET");
        Require(revoke,
            "IntOp $2 $1 & 0x10 ; refuse directories\n    StrCmp $2 0 0 txn_revoke_free_refuse",
            "ROLLBACK_TARGET");
        Assert.AreEqual(string.Empty, string.Join(", ", mismatches),
            "A_ZERO_DIRECTORY_ATTRIBUTE_BIT_MUST_PASS_FOR_FILES_AND_NONZERO_MUST_FAIL_FOR_DIRECTORIES");

        void Require(string body, string expected, string name)
        {
            if (!body.Contains(expected, StringComparison.Ordinal))
                mismatches.Add(name);
        }

        string GuardFunction(string name)
        {
            int begin = guard.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = guard.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return guard[begin..end];
        }
    }

    [TestMethod]
    public void Fresh_install_journal_is_created_and_verified_before_payload_copy()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        static string FunctionBody(string source, string name)
        {
            int begin = source.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return source[begin..end];
        }

        string transaction = FunctionBody(guard, "CopyFreshInstallPayloadFilesToFixedRoot");
        int prepareJournal = transaction.IndexOf("Call ${PREFIX}PrepareFreshInstallJournal", StringComparison.Ordinal);
        int copyApp = transaction.IndexOf("Call ${PREFIX}CopyTrustedStageFileToFixedRoot", StringComparison.Ordinal);
        int copyNotice = transaction.IndexOf("Call ${PREFIX}CopyTrustedStageFileToFixedRoot", copyApp + 1, StringComparison.Ordinal);
        int writeFilesPhase = transaction.IndexOf("Call ${PREFIX}WriteFreshInstallJournalFilesWritten", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, copyApp, "APP_COPY_CALL_MISSING");
        Assert.IsGreaterThanOrEqualTo(0, prepareJournal,
            "PREPARED_JOURNAL_MUST_BE_DURABLE_BEFORE_ANY_PRODUCT_FILE_CREATE");
        Assert.IsGreaterThan(prepareJournal, copyApp,
            "PREPARED_JOURNAL_MUST_PRECEDE_THE_FIRST_PRODUCT_COPY");
        Assert.IsGreaterThanOrEqualTo(0, copyNotice, "NOTICE_COPY_CALL_MISSING");
        Assert.IsGreaterThan(copyApp, copyNotice, "NOTICE_COPY_MUST_FOLLOW_APP_COPY");
        Assert.IsGreaterThan(copyNotice, writeFilesPhase,
            "FILES_WRITTEN_MUST_BE_RECORDED_ONLY_AFTER_BOTH_PAYLOAD_COPIES");

        string inspect = FunctionBody(guard, "ValidateFreshInstallJournalHandle");
        foreach (string required in new[]
        {
            "GetFileInformationByHandle", "GetFinalPathNameByHandleW",
            "GetFileInformationByHandleEx", "Call ${PREFIX}ValidatePrivateHandleAcl",
            "SetupJournalVerifyIdentity", "SetupJournalVerifyCreated"
        })
            StringAssert.Contains(inspect, required, "RETURNED_HANDLE_MUST_BE_VALIDATED: " + required);

        string root = FunctionBody(guard, "ValidateFreshInstallJournalRoot");
        foreach (string required in new[] { "SetupCopyRootHandle", "SetupCopyRootIdentity", "GuardPathPins", "GuardPathPinCount" })
            StringAssert.Contains(root, required, "JOURNAL_MUST_REMAIN_BOUND_TO_PINNED_FIXED_ROOT: " + required);

        string prepare = FunctionBody(guard, "PrepareFreshInstallJournal");
        StringAssert.Contains(prepare, "CreateDirectory2W",
            "STATE_DIRECTORY_MUST_BE_CREATED_WITH_THE_EXISTING_FIXED-DIRECTORY_PRIMITIVE");
        StringAssert.Contains(prepare, "journal.ini",
            "JOURNAL_PATH_MUST_BE_FIXED");
        StringAssert.Contains(prepare, ".GitHubBackupTool.state",
            "JOURNAL_MUST_USE_THE_FIXED_PRIVATE_STATE_DIRECTORY");
        StringAssert.Contains(prepare,
            "CreateFileW(w \"$SetupFixedRoot\\.GitHubBackupTool.state\\journal.ini\", i 0xC0010000, i 0, p $SetupJournalSecurityAttributes, i 1, i 0x00200080, p 0)",
            "JOURNAL_FILE_MUST_USE_CREATE_NEW_SHARE_NONE_AND_OPEN_REPARSE_POINT");
        StringAssert.Contains(prepare, "Call ${PREFIX}ValidateFreshInstallJournalDirectory");
        StringAssert.Contains(prepare, "Call ${PREFIX}ValidateFreshInstallJournalFile");
        StringAssert.Contains(prepare, "FlushFileBuffers",
            "PREPARED_MUST_BE_FLUSHED_BEFORE_PAYLOAD_CREATION");
        StringAssert.Contains(prepare, "Call ${PREFIX}HashHandleSha256",
            "JOURNAL_BYTES_MUST_BE_HASHED_THROUGH_THE_SAME_HANDLE");
        Assert.IsFalse(prepare.Contains("DeleteFileW", StringComparison.Ordinal) ||
            prepare.Contains("RMDir", StringComparison.Ordinal) ||
            prepare.Contains("MoveFile", StringComparison.Ordinal),
            "JOURNAL_CREATION_FAILURE_MUST_NOT_FALL_BACK_TO_PATH_MUTATION");

        string update = FunctionBody(guard, "WriteFreshInstallJournalFilesWritten");
        foreach (string required in new[]
        {
            "SetupCopyTxnAppCreated",
            "SetupCopyTxnNoticeCreated",
            "SetupJournalFileHandle",
            "SetupJournalFileHash",
            "Call ${PREFIX}ValidateFreshInstallJournalFile",
            "FILES_WRITTEN",
            "FlushFileBuffers",
            "Call ${PREFIX}HashHandleSha256"
        })
            StringAssert.Contains(update, required, "PHASE_UPDATE_MUST_RETAIN_AND_REVALIDATE_THE_CREATED_JOURNAL: " + required);
        StringAssert.Contains(update,
            "SetFilePointerEx(p $SetupJournalFileHandle, l 0, p 0, i 2)",
            "FILES_WRITTEN_MUST_APPEND_AT_EOF_NOT_OVERWRITE_PREPARED");

        string rollback = FunctionBody(guard, "RollbackFreshInstallPayloadCopies");
        StringAssert.Contains(rollback, "Call ${PREFIX}RollbackFreshInstallJournal",
            "PAIRED_ABORT_MUST_INCLUDE_JOURNAL_FILE_AND_CREATED_STATE_DIRECTORY");
        string journalRollback = FunctionBody(guard, "RollbackFreshInstallJournal");
        Assert.AreEqual(1, journalRollback.Split("journal_rollback_file_pending_empty:", StringSplitOptions.None).Length - 1,
            "JOURNAL_ROLLBACK_LABELS_MUST_BE_UNIQUE_WITHIN_THE_FUNCTION");
        Assert.AreEqual(1, journalRollback.Split("journal_rollback_directory_pending_empty:", StringSplitOptions.None).Length - 1,
            "JOURNAL_ROLLBACK_LABELS_MUST_BE_UNIQUE_WITHIN_THE_FUNCTION");
        foreach (string required in new[]
        {
            "SetupJournalFileHandle", "SetupJournalDirectoryHandle",
            "SetFileInformationByHandle", "CloseHandle",
            "SetupJournalFileDeletePending", "SetupJournalDirectoryDeletePending",
            "ValidateFreshInstallJournalFile", "ValidateFreshInstallJournalDirectory"
        })
            StringAssert.Contains(journalRollback, required,
                "ROLLBACK_MUST_RETAIN_AND_REVALIDATE_EXACT_JOURNAL_OBJECTS: " + required);
        Assert.IsFalse(journalRollback.Contains("DeleteFileW", StringComparison.Ordinal) ||
            journalRollback.Contains("RMDir", StringComparison.Ordinal) ||
            journalRollback.Contains("MoveFile", StringComparison.Ordinal),
            "ROLLBACK_MUST_NOT_USE_PATH_BASED_JOURNAL_CLEANUP");
        int fileDisposition = journalRollback.IndexOf(
            "SetFileInformationByHandle(p $SetupJournalFileHandle, i 4", StringComparison.Ordinal);
        int filePending = journalRollback.IndexOf("StrCpy $SetupJournalFileDeletePending 1", fileDisposition, StringComparison.Ordinal);
        int fileClose = journalRollback.IndexOf("CloseHandle(p $SetupJournalFileHandle)", filePending, StringComparison.Ordinal);
        int fileClear = journalRollback.IndexOf("StrCpy $SetupJournalFileHandle 0", fileClose, StringComparison.Ordinal);
        Assert.IsTrue(fileDisposition >= 0 && filePending > fileDisposition && fileClose > filePending && fileClear > fileClose,
            "FILE_HANDLE_AND_DELETE_STATE_MUST_CLEAR_ONLY_AFTER_SUCCESSFUL_CLOSE");
        int directoryDisposition = journalRollback.IndexOf(
            "SetFileInformationByHandle(p $SetupJournalDirectoryHandle, i 4", StringComparison.Ordinal);
        int directoryPending = journalRollback.IndexOf("StrCpy $SetupJournalDirectoryDeletePending 1", directoryDisposition, StringComparison.Ordinal);
        int directoryClose = journalRollback.IndexOf("CloseHandle(p $SetupJournalDirectoryHandle)", directoryPending, StringComparison.Ordinal);
        int directoryClear = journalRollback.IndexOf("StrCpy $SetupJournalDirectoryHandle 0", directoryClose, StringComparison.Ordinal);
        Assert.IsTrue(directoryDisposition >= 0 && directoryPending > directoryDisposition &&
            directoryClose > directoryPending && directoryClear > directoryClose,
            "DIRECTORY_HANDLE_AND_DELETE_STATE_MUST_CLEAR_ONLY_AFTER_SUCCESSFUL_CLOSE");
        string release = FunctionBody(guard, "ReleaseStagedCopyHandles");
        StringAssert.Contains(release, "SetupJournalFileHandle");
        StringAssert.Contains(release, "SetupJournalDirectoryHandle");
        StringAssert.Contains(release, "SetupJournalPhase");
        string installer = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        AssertManagedLifecycleDelegation(installer);
    }

    [TestMethod]
    public void Synthetic_journal_harness_is_confined_to_a_test_fixture()
    {
        string harness = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "GitHubBackup.App.Tests",
            "Fixtures", "SetupJournalHarness.nsi"));
        foreach (string required in new[]
        {
            "SETUP_JOURNAL_FIXTURE_PARENT_REQUIRED",
            "SETUP_JOURNAL_HARNESS_OUTPUT_REQUIRED",
            "CreateDirectory \"${SETUP_JOURNAL_FIXTURE_PARENT}\\temp\"",
            "SetEnvironmentVariableW(w \"TEMP\", w \"${SETUP_JOURNAL_FIXTURE_PARENT}\\temp\")",
            "SetEnvironmentVariableW(w \"TMP\", w \"${SETUP_JOURNAL_FIXTURE_PARENT}\\temp\")",
            "Call PrepareFreshInstallJournal",
            "Call CopyTrustedStageFileToFixedRoot",
            "Call WriteFreshInstallJournalFilesWritten",
            "Call AbortFreshInstallCopyTransaction",
            "${SETUP_JOURNAL_FIXTURE_PARENT}\\product",
            "GetFileAttributesW",
            "SetErrorLevel 0"
        })
            StringAssert.Contains(harness, required, "SYNTHETIC_JOURNAL_CHECK_MISSING: " + required);
        int tempCreate = harness.IndexOf("CreateDirectory \"${SETUP_JOURNAL_FIXTURE_PARENT}\\temp\"", StringComparison.Ordinal);
        int setTemp = tempCreate < 0 ? -1 : harness.IndexOf("SetEnvironmentVariableW(w \"TEMP\"", tempCreate, StringComparison.Ordinal);
        int setTmp = setTemp < 0 ? -1 : harness.IndexOf("SetEnvironmentVariableW(w \"TMP\"", setTemp, StringComparison.Ordinal);
        int pluginDirectory = setTmp < 0 ? -1 : harness.IndexOf("InitPluginsDir", setTmp, StringComparison.Ordinal);
        Assert.IsTrue(tempCreate >= 0 && setTemp > tempCreate && setTmp > setTemp && pluginDirectory > setTmp,
            "SYNTHETIC_PLUGIN_DIRECTORY_MUST_BE_REDIRECTED_TO_THE_FIXTURE_BEFORE_CREATION");
        foreach ((string path, string label) in new[]
        {
            ("${SETUP_APP_NAME}", "app"),
            ("${SETUP_NOTICE_NAME}", "notice"),
            (".GitHubBackupTool.setup-stage", "stage"),
            (".GitHubBackupTool.state", "state")
        })
        {
            StringAssert.Contains(harness,
                $"System::Call 'kernel32::GetFileAttributesW(w \"$SetupFixedRoot\\{path}\") i.r0 ?e'\n    Pop $GuardLastError\n    StrCmp $0 -1 harness_{label}_absence_error harness_fail",
                "HARNESS_MUST_CAPTURE_FAILURE_FOR_EACH_ROLLED_BACK_OBJECT: " + path);
            StringAssert.Contains(harness, $"harness_{label}_absence_error:\n    StrCmp $GuardLastError 2 ");
            StringAssert.Contains(harness, $"    StrCmp $GuardLastError 3 ");
        }

        foreach (string forbidden in new[]
        {
            "InstallDir ", "ValidateHost", "ValidateDirectoryArguments",
            "PinExistingInstallAncestors", "SHGetKnownFolderPath", "Registry::",
            "HKCU", "HKLM", "GitHubBackup.nsi", "WriteUninstaller"
        })
            Assert.IsFalse(harness.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                "SYNTHETIC_HARNESS_MUST_NOT_TOUCH_PRODUCT_OR_ACCOUNT_STATE: " + forbidden);
    }

    [TestMethod]
    public void Journal_failures_keep_nonzero_status_and_copy_requires_durable_phase()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        static string FunctionBody(string source, string name)
        {
            int begin = source.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return source[begin..end];
        }

        string prepare = FunctionBody(guard, "PrepareFreshInstallJournal");
        foreach ((string label, string nextOperation) in new[]
        {
            ("journal_prepare_root_ready:", "System::Alloc 52"),
            ("journal_prepare_file_parent_ready:", "CreateFileW(w \"$SetupFixedRoot\\.GitHubBackupTool.state\\journal.ini\""),
            ("journal_prepare_file_created:", "StrCpy $SetupJournalRecord")
        })
        {
            int labelIndex = prepare.IndexOf(label, StringComparison.Ordinal);
            int statusIndex = prepare.IndexOf("StrCpy $SetupCode 13", labelIndex, StringComparison.Ordinal);
            int operationIndex = prepare.IndexOf(nextOperation, labelIndex, StringComparison.Ordinal);
            Assert.IsTrue(labelIndex >= 0 && statusIndex > labelIndex && operationIndex > statusIndex,
                "JOURNAL_PREPARE_FAILURE_MUST_NOT_RETURN_SUCCESS: " + label);
        }

        string update = FunctionBody(guard, "WriteFreshInstallJournalFilesWritten");
        int recordHashLabel = update.IndexOf("journal_files_record_hash:", StringComparison.Ordinal);
        int mismatchStatus = update.IndexOf("StrCpy $SetupCode 13", recordHashLabel, StringComparison.Ordinal);
        int compare = update.IndexOf("StrCmp $GuardHash $SetupJournalFileHash journal_files_append", recordHashLabel, StringComparison.Ordinal);
        Assert.IsTrue(recordHashLabel >= 0 && mismatchStatus > recordHashLabel && compare > mismatchStatus,
            "JOURNAL_HASH_MISMATCH_MUST_REMAIN_A_FAILURE");
        int appendLabel = update.IndexOf("journal_files_append:", StringComparison.Ordinal);
        int appendStatus = update.IndexOf("StrCpy $SetupCode 13", appendLabel, StringComparison.Ordinal);
        int pointerWrite = update.IndexOf("SetFilePointerEx(p $SetupJournalFileHandle", appendLabel, StringComparison.Ordinal);
        Assert.IsTrue(appendLabel >= 0 && appendStatus > appendLabel && pointerWrite > appendStatus,
            "JOURNAL_WRITE_AND_FLUSH_FAILURES_MUST_REMAIN_NONZERO");

        string rollback = FunctionBody(guard, "RollbackFreshInstallJournal");
        foreach ((string label, string operation) in new[]
        {
            ("journal_rollback_file_dispose:", "System::Alloc 1"),
            ("journal_rollback_directory_dispose:", "System::Alloc 1")
        })
        {
            int labelIndex = rollback.IndexOf(label, StringComparison.Ordinal);
            int statusIndex = rollback.IndexOf("StrCpy $SetupCode 13", labelIndex, StringComparison.Ordinal);
            int allocate = rollback.IndexOf(operation, labelIndex, StringComparison.Ordinal);
            Assert.IsTrue(labelIndex >= 0 && statusIndex > labelIndex && allocate > statusIndex,
                "JOURNAL_ROLLBACK_FAILURE_MUST_NOT_RETURN_SUCCESS: " + label);
        }

        string transaction = FunctionBody(guard, "CopyFreshInstallPayloadFilesToFixedRoot");
        StringAssert.Contains(transaction,
            "Call ${PREFIX}PrepareFreshInstallJournal\n    StrCmp $SetupCode 0 copy_txn_journal_ready\n    Goto copy_txn_abort\ncopy_txn_journal_ready:\n    StrCmp $SetupJournalPhase \"PREPARED\" 0 copy_txn_abort",
            "PAYLOAD_COPY_MUST_REQUIRE_VERIFIED_PREPARED_PHASE");
        StringAssert.Contains(transaction,
            "Call ${PREFIX}WriteFreshInstallJournalFilesWritten\n    StrCmp $SetupCode 0 copy_txn_success\n    Goto copy_txn_abort\ncopy_txn_success:\n    StrCmp $SetupJournalPhase \"FILES_WRITTEN\" 0 copy_txn_abort",
            "TRANSACTION_MUST_REQUIRE_VERIFIED_FILES_WRITTEN_PHASE");
    }

    [TestMethod]
    public void V1_receipt_rejects_desktop_shortcut_outside_approved_install_scope()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        int begin = guard.IndexOf("Function ${PREFIX}ReadOwnedReceipt", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, begin, "OWNED_RECEIPT_PARSER_MISSING");
        int end = guard.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
        Assert.IsGreaterThan(begin, end, "OWNED_RECEIPT_PARSER_UNTERMINATED");
        string body = guard[begin..end];

        StringAssert.Contains(body,
            "StrCmp $SetupRecordedDesktop 0 receipt_desktop_valid\n    Goto receipt_done",
            "V1_RECEIPT_MUST_REQUIRE_DESKTOP_SHORTCUT_DISABLED");
        StringAssert.Contains(body, "StrCmp $SetupRecordedDesktopHash \"none\" 0 receipt_done",
            "V1_RECEIPT_MUST_REJECT_A_DESKTOP_SHORTCUT_HASH");
        Assert.IsFalse(body.Contains("StrCmp $SetupRecordedDesktop 1", StringComparison.Ordinal) ||
            body.Contains("$SetupRecordedDesktop == 1", StringComparison.Ordinal),
            "V1_RECEIPT_MUST_NOT_ACCEPT_DESKTOP_SHORTCUT_STATE");
    }

    [TestMethod]
    public void Fresh_install_receipt_is_create_new_flushed_and_verified_from_its_original_handle()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        int begin = guard.IndexOf("Function ${PREFIX}WriteFreshInstallReceipt", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, begin, "FRESH_INSTALL_RECEIPT_WRITER_MISSING");
        int end = guard.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
        Assert.IsGreaterThan(begin, end, "FRESH_INSTALL_RECEIPT_WRITER_UNTERMINATED");
        string body = guard[begin..end];

        foreach (string required in new[]
        {
            "StrCmp $SetupJournalPhase \"FILES_WRITTEN\"",
            "ConvertStringSecurityDescriptorToSecurityDescriptorW(w \"O:$0D:P(A;;GA;;;$0)(A;;GA;;;SY)(A;;GA;;;BA)\"",
            "*(i 12, p r8, i 0) p.r9",
            "CreateFileW(w \"$SetupFixedRoot\\${SETUP_RECEIPT_NAME}\", i 0xC0010000, i 0, p $SetupInstallReceiptSecurityAttributes, i 1",
            "System::Free $SetupInstallReceiptSecurityAttributes",
            "LocalFree(p $SetupInstallReceiptSecurityDescriptor)",
            "StrCpy $SetupInstallReceiptCreated 1",
            "System::Call '*(&i2 0xFEFF) p.r7'",
            "WriteFile(p $SetupInstallReceiptHandle",
            "FlushFileBuffers(p $SetupInstallReceiptHandle)",
            "Call ${PREFIX}ValidateFreshInstallJournalHandle",
            "Call ${PREFIX}HashHandleSha256",
            "Call ${PREFIX}ReadOwnedReceipt"
        })
            StringAssert.Contains(body, required, "FRESH_RECEIPT_CONTRACT_MISSING: " + required);

        int securityDescriptor = body.IndexOf("ConvertStringSecurityDescriptorToSecurityDescriptorW(", StringComparison.Ordinal);
        int securityAttributes = body.IndexOf("*(i 12, p r8, i 0) p.r9", securityDescriptor, StringComparison.Ordinal);
        int create = body.IndexOf("CreateFileW(w \"$SetupFixedRoot\\${SETUP_RECEIPT_NAME}\"", StringComparison.Ordinal);
        int mark = body.IndexOf("StrCpy $SetupInstallReceiptCreated 1", create, StringComparison.Ordinal);
        int write = body.IndexOf("WriteFile(p $SetupInstallReceiptHandle", mark, StringComparison.Ordinal);
        int flush = body.IndexOf("FlushFileBuffers(p $SetupInstallReceiptHandle)", write, StringComparison.Ordinal);
        int validate = body.IndexOf("Call ${PREFIX}ValidateFreshInstallJournalHandle", flush, StringComparison.Ordinal);
        int hash = body.IndexOf("Call ${PREFIX}HashHandleSha256", validate, StringComparison.Ordinal);
        int parse = body.IndexOf("Call ${PREFIX}ReadOwnedReceipt", hash, StringComparison.Ordinal);
        Assert.IsTrue(securityDescriptor >= 0 && securityAttributes > securityDescriptor && create > securityAttributes &&
            mark > create && write > mark && flush > write && validate > flush && hash > validate && parse > hash,
            "FRESH_RECEIPT_MUST_HAVE_EXPLICIT_PRIVATE_ACL_AND_BE_CREATE_NEW_FLUSHED_HASHED_AND_PARSED_IN_ORDER");
        int parsedLabel = body.IndexOf("receipt_write_receipt_parsed:", StringComparison.Ordinal);
        int failureReset = body.IndexOf("StrCpy $SetupCode 13", parsedLabel, StringComparison.Ordinal);
        int firstCrossCheck = body.IndexOf("StrCmp $SetupRecordedUninstallerHash $SetupInstallUninstallerHash receipt_write_uninstaller_matches",
            parsedLabel, StringComparison.Ordinal);
        Assert.IsTrue(parsedLabel >= 0 && failureReset > parsedLabel && firstCrossCheck > failureReset,
            "RECEIPT_READBACK_CROSS_CHECKS_MUST_RESTORE_FAILURE_BEFORE_ANY_MISMATCH_EXIT");
        foreach ((string label, string comparison) in new[]
        {
            ("receipt_write_uninstaller_hash_matches:",
                "StrCmp $GuardHash $SetupInstallUninstallerHash receipt_write_shortcut_handle"),
            ("receipt_write_shortcut_hash_matches:",
                "StrCmp $GuardHash $SetupInstallShortcutHash receipt_write_shortcut_binding")
        })
        {
            int matchedHash = body.IndexOf(label, StringComparison.Ordinal);
            int hashFailureReset = body.IndexOf("StrCpy $SetupCode 13", matchedHash, StringComparison.Ordinal);
            int hashComparison = body.IndexOf(comparison, matchedHash, StringComparison.Ordinal);
            Assert.IsTrue(matchedHash >= 0 && hashFailureReset > matchedHash && hashComparison > hashFailureReset,
                "RECEIPT_HASH_MISMATCH_MUST_REMAIN_A_FAILURE: " + label);
        }
    }

    [TestMethod]
    public void Fresh_install_receipt_writer_preserves_the_callers_last_error_scratch()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        int begin = guard.IndexOf("Function ${PREFIX}WriteFreshInstallReceipt", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, begin, "FRESH_INSTALL_RECEIPT_WRITER_MISSING");
        int end = guard.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
        Assert.IsGreaterThan(begin, end, "FRESH_INSTALL_RECEIPT_WRITER_UNTERMINATED");
        string body = guard[begin..end];

        int save = body.IndexOf("Push $GuardLastError", StringComparison.Ordinal);
        int api = body.IndexOf("CreateFileW(w \"$SetupFixedRoot\\${SETUP_RECEIPT_NAME}\"", StringComparison.Ordinal);
        int apiResult = body.IndexOf("Pop $GuardLastError", api, StringComparison.Ordinal);
        int cleanup = body.IndexOf("receipt_write_security_cleanup_done:", StringComparison.Ordinal);
        int restore = apiResult < 0 ? -1 : body.IndexOf("Pop $GuardLastError", apiResult + 1, StringComparison.Ordinal);
        int functionRestore = restore < 0 ? -1 : body.IndexOf("!insertmacro SetupRestoreRegisters", restore, StringComparison.Ordinal);

        Assert.IsTrue(save >= 0 && api > save && apiResult > api && cleanup > apiResult &&
            restore > cleanup && functionRestore > restore,
            "RECEIPT_WRITER_MUST_CAPTURE_API_ERROR_THEN_RESTORE_CALLER_GUARD_LAST_ERROR_ON_ALL_EXITS");
    }

    [TestMethod]
    public void Failed_fresh_install_rolls_back_receipt_before_payload_and_keeps_failed_cleanup_owned()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        static string FunctionBody(string source, string name)
        {
            int begin = source.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return source[begin..end];
        }
        string rollback = FunctionBody(guard, "RollbackFreshInstallReceipt");
        string abort = FunctionBody(guard, "AbortFreshInstallCopyTransaction");

        foreach (string required in new[]
        {
            "StrCmp $SetupMode \"install\"",
            "StrCmp $SetupCopyTransactionActive 1",
            "Call ${PREFIX}ValidateFreshInstallJournalRoot",
            "Call ${PREFIX}ValidateFreshInstallJournalHandle",
            "SetFileInformationByHandle(p $SetupInstallReceiptHandle, i 4",
            "StrCmp $SetupInstallReceiptCreated 1 receipt_rollback_created",
            "CloseHandle(p $SetupInstallReceiptHandle)",
            "StrCpy $SetupInstallReceiptHandle 0",
            "StrCpy $SetupInstallReceiptIdentity \"\"",
            "StrCpy $SetupInstallReceiptHash \"\""
        })
            StringAssert.Contains(rollback, required, "FRESH_RECEIPT_ROLLBACK_CONTRACT_MISSING: " + required);

        int rollbackReceipt = abort.IndexOf("Call ${PREFIX}RollbackFreshInstallReceipt", StringComparison.Ordinal);
        int rollbackPayload = abort.IndexOf("Call ${PREFIX}RollbackFreshInstallPayloadCopies", StringComparison.Ordinal);
        Assert.IsTrue(rollbackReceipt >= 0 && rollbackPayload > rollbackReceipt,
            "FRESH_RECEIPT_MUST_BE_ROLLED_BACK_BEFORE_PAYLOAD_AND_JOURNAL");

        StringAssert.Contains(rollback, "StrCpy $SetupInstallReceiptDeletePending 1",
            "FAILED_RECEIPT_CLOSE_MUST_RETAIN_DELETE_PENDING_OWNERSHIP");
        StringAssert.Contains(rollback,
            "StrCmp $SetupInstallReceiptDeletePending 1 receipt_rollback_close",
            "RETRY_MUST_CLOSE_THE_SAME_PENDING_RECEIPT_HANDLE_WITHOUT_REOPENING_BY_PATH");
        int hashRead = rollback.IndexOf("receipt_rollback_hash_read:", StringComparison.Ordinal);
        int hashFailureReset = rollback.IndexOf("StrCpy $SetupCode 13", hashRead, StringComparison.Ordinal);
        int hashCompare = rollback.IndexOf(
            "StrCmp $GuardHash $SetupInstallReceiptHash receipt_rollback_dispose", hashRead, StringComparison.Ordinal);
        Assert.IsTrue(hashRead >= 0 && hashFailureReset > hashRead && hashCompare > hashFailureReset,
            "RECEIPT_ROLLBACK_HASH_MISMATCH_MUST_NOT_AUTHORIZE_DELETION");
    }

    [TestMethod]
    public void Private_acl_parser_bounds_sid_to_current_ace_before_validation()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        int begin = guard.IndexOf("Function ${PREFIX}ValidatePrivateHandleAcl", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, begin, "PRIVATE_ACL_VALIDATOR_MISSING");
        int end = guard.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
        Assert.IsGreaterThan(begin, end, "PRIVATE_ACL_VALIDATOR_UNTERMINATED");
        string body = guard[begin..end];

        const string aceHeader = "System::Call '*$5(&i1.r6, &i1.r7, &i2.r2)'";
        const string sidCountPointer = "IntOp $GuardValue $5 + 9";
        const string sidCountRead = "System::Call '*$GuardValue(&i1.r8)'";
        const string sidBytes = "IntOp $8 $8 * 4\n    IntOp $8 $8 + 16";
        const string aceBound = "IntCmpU $2 $8 0 acl_done 0";
        const string sidValidation = "IntOp $GuardSid $5 + 8\n    Call ${PREFIX}ValidateApprovedSid";

        foreach (string required in new[] { aceHeader, sidCountPointer, sidCountRead, sidBytes, aceBound, sidValidation })
            StringAssert.Contains(body, required, "PRIVATE_ACE_SID_BOUNDS_CHECK_MISSING: " + required);
        StringAssert.Contains(body, "Push $GuardValue", "PRIVATE_ACL_TEMPORARY_POINTER_MUST_BE_PRESERVED");
        StringAssert.Contains(body, "Pop $GuardValue", "PRIVATE_ACL_TEMPORARY_POINTER_MUST_BE_PRESERVED");

        int header = body.IndexOf(aceHeader, StringComparison.Ordinal);
        int pointer = body.IndexOf(sidCountPointer, header, StringComparison.Ordinal);
        int count = body.IndexOf(sidCountRead, pointer, StringComparison.Ordinal);
        int size = body.IndexOf(sidBytes, count, StringComparison.Ordinal);
        int bound = body.IndexOf(aceBound, size, StringComparison.Ordinal);
        int validate = body.IndexOf(sidValidation, bound, StringComparison.Ordinal);
        Assert.IsTrue(header >= 0 && pointer > header && count > pointer && size > count &&
            bound > size && validate > bound,
            "SID_LENGTH_MUST_BE_BOUNDED_BY_ACE_SIZE_BEFORE_ISVALIDSID_READS_IT");
    }

    [TestMethod]
    public void Create_directory2_requests_synchronize_for_retained_directory_handles()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"));
        StringAssert.Contains(guard,
            "w \"$SetupLocalAppData\\Programs\", i 0x120081, i 1, i 1, p 0",
            "SHARED_PROGRAMS_HANDLE_MUST_REQUEST_SYNCHRONIZE_WITHOUT_DROPPING_READ_RIGHTS");
        StringAssert.Contains(guard,
            "w \"$SetupFixedRoot\", i 0x120081, i 1, i 1, p $SetupFreshRootSecurityAttributes",
            "PRODUCT_ROOT_HANDLE_MUST_REQUEST_SYNCHRONIZE_WITHOUT_DROPPING_READ_RIGHTS");
        StringAssert.Contains(guard,
            "w \"$SetupFixedRoot\\.GitHubBackupTool.state\", i 0x130081, i 1, i 1, p $SetupJournalSecurityAttributes",
            "JOURNAL_DIRECTORY_HANDLE_MUST_REQUEST_SYNCHRONIZE_WITHOUT_DROPPING_DELETE_OR_READ_RIGHTS");
    }

    [TestMethod]
    public void Fixed_root_ownership_lease_requires_native_boundary_checks_and_remains_unwired()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"));
        int begin = guard.IndexOf("Function ${PREFIX}AcquireFixedRootOwnershipLease", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, begin, "FIXED_ROOT_OWNERSHIP_LEASE_MISSING");
        int end = guard.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
        Assert.IsGreaterThan(begin, end, "FIXED_ROOT_OWNERSHIP_LEASE_UNTERMINATED");
        string body = guard[begin..end];
        foreach (string required in new[]
        {
            "Call ${PREFIX}ValidateHost", "Call ${PREFIX}ValidateDirectoryArguments",
            "Call ${PREFIX}PinExistingInstallAncestors", "StrCpy $GuardPath $SetupFixedRoot",
            "StrCpy $GuardDirectory 1", "Call ${PREFIX}OpenPathIdentityLease",
            "Call ${PREFIX}ValidatePrivateHandleAcl",
            "StrCpy $SetupCode 0"
        })
            StringAssert.Contains(body, required);
        int validateHost = body.IndexOf("Call ${PREFIX}ValidateHost", StringComparison.Ordinal);
        int validateArguments = body.IndexOf("Call ${PREFIX}ValidateDirectoryArguments", StringComparison.Ordinal);
        int pinAncestors = body.IndexOf("Call ${PREFIX}PinExistingInstallAncestors", StringComparison.Ordinal);
        int readOnlyOpen = body.IndexOf("Call ${PREFIX}OpenPathIdentityLease", StringComparison.Ordinal);
        int validateAcl = body.IndexOf("Call ${PREFIX}ValidatePrivateHandleAcl", StringComparison.Ordinal);
        int success = body.IndexOf("StrCpy $SetupCode 0", StringComparison.Ordinal);
        Assert.IsLessThan(readOnlyOpen, validateHost);
        Assert.IsLessThan(pinAncestors, validateArguments);
        Assert.IsLessThan(readOnlyOpen, pinAncestors);
        Assert.IsLessThan(validateAcl, readOnlyOpen);
        Assert.IsLessThan(success, validateAcl);
        Assert.IsFalse(body.Contains("Call ${PREFIX}OpenProductIdentityLease", StringComparison.Ordinal),
            "ROOT_DIRECTORY_MUST_NOT_REOPEN_WITH_DELETE_ACCESS");
        Assert.IsLessThan(body.IndexOf("Call ${PREFIX}ValidatePrivateHandleAcl", StringComparison.Ordinal),
            body.IndexOf("Call ${PREFIX}ValidateHost", StringComparison.Ordinal));
        StringAssert.Contains(body, "Call ${PREFIX}ReleaseFixedRootOwnershipLease");
        int releaseBegin = guard.IndexOf("Function ${PREFIX}ReleaseFixedRootOwnershipLease", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, releaseBegin, "FIXED_ROOT_OWNERSHIP_RELEASE_MISSING");
        int releaseEnd = guard.IndexOf("FunctionEnd", releaseBegin, StringComparison.Ordinal);
        Assert.IsGreaterThan(releaseBegin, releaseEnd, "FIXED_ROOT_OWNERSHIP_RELEASE_UNTERMINATED");
        string release = guard[releaseBegin..releaseEnd];
        StringAssert.Contains(release, "CloseHandle(p $GuardHandle)");
        StringAssert.Contains(release, "Call ${PREFIX}ReleasePathPins");
        int productBegin = guard.IndexOf("Function ${PREFIX}OpenProductIdentityLease", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, productBegin, "PRODUCT_IDENTITY_LEASE_MISSING");
        int productEnd = guard.IndexOf("FunctionEnd", productBegin, StringComparison.Ordinal);
        Assert.IsGreaterThan(productBegin, productEnd, "PRODUCT_IDENTITY_LEASE_UNTERMINATED");
        StringAssert.Contains(guard[productBegin..productEnd], "Call ${PREFIX}ValidatePrivateHandleAcl");
        int readBegin = guard.IndexOf("Function ${PREFIX}OpenPathIdentityLease", StringComparison.Ordinal);
        int readEnd = guard.IndexOf("FunctionEnd", readBegin, StringComparison.Ordinal);
        Assert.IsGreaterThan(readBegin, readEnd, "READ_ONLY_IDENTITY_LEASE_UNTERMINATED");
        StringAssert.Contains(guard[readBegin..readEnd],
            "StrCpy $GuardLeaseKind 0\n    Call ${PREFIX}OpenCheckedIdentityLease");
        string installer = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        Assert.IsFalse(installer.Contains("Call AcquireFixedRootOwnershipLease", StringComparison.Ordinal) ||
            installer.Contains("Call un.AcquireFixedRootOwnershipLease", StringComparison.Ordinal),
            "INCOMPLETE_ROOT_LEASE_MUST_NOT_AUTHORIZE_INSTALLER");
    }

    [TestMethod]
    public void Read_only_root_lease_remains_composable_with_empty_root_and_receipt_checks()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        static string FunctionBody(string source, string name)
        {
            int begin = source.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return source[begin..end];
        }

        string root = FunctionBody(guard, "AcquireFixedRootOwnershipLease");
        string emptyRoot = FunctionBody(guard, "ValidateEmptyFixedRootDirectory");
        string files = FunctionBody(guard, "CheckFixedInstallFilesAndReceipt");
        foreach (string required in new[]
        {
            "Call ${PREFIX}OpenPathIdentityLease", "Call ${PREFIX}ValidatePrivateHandleAcl"
        })
            StringAssert.Contains(root, required, "FIXED_ROOT_MUST_KEEP_READ_ONLY_IDENTITY_AND_ACL_CHECKS: " + required);
        Assert.IsFalse(root.Contains("OpenProductIdentityLease", StringComparison.Ordinal),
            "ROOT_CHECK_MUST_NOT_REQUEST_DELETE_ACCESS");

        StringAssert.Contains(emptyRoot, "Call ${PREFIX}AcquireFixedRootOwnershipLease",
            "EMPTY_ROOT_CHECK_MUST_KEEP_USING_THE_REVIEWED_FIXED_ROOT_LEASE");
        StringAssert.Contains(emptyRoot, "Call ${PREFIX}ReleaseFixedRootOwnershipLease",
            "EMPTY_ROOT_CHECK_MUST_KEEP_ITS_PAIRED_ROOT_RELEASE");
        int emptyAcquire = emptyRoot.IndexOf("Call ${PREFIX}AcquireFixedRootOwnershipLease", StringComparison.Ordinal);
        int emptyEnumeration = emptyRoot.IndexOf("GetFileInformationByHandleEx(p $GuardHandle", StringComparison.Ordinal);
        Assert.IsLessThan(emptyEnumeration, emptyAcquire,
            "EMPTY_ROOT_MUST_ACQUIRE_READ_ONLY_ROOT_BEFORE_ENUMERATING_ITS_RETAINED_HANDLE");

        StringAssert.Contains(files, "Call ${PREFIX}AcquireFixedRootOwnershipLease",
            "FILE_RECEIPT_CHECK_MUST_KEEP_USING_THE_REVIEWED_FIXED_ROOT_LEASE");
        StringAssert.Contains(files, "Call ${PREFIX}ReleaseFixedInstallFilesAndReceipt",
            "FILE_RECEIPT_CHECK_MUST_KEEP_ITS_PAIRED_RELEASE");
        int filesAcquire = files.IndexOf("Call ${PREFIX}AcquireFixedRootOwnershipLease", StringComparison.Ordinal);
        int firstProductOpen = files.IndexOf("Call ${PREFIX}OpenProductIdentityLease", StringComparison.Ordinal);
        Assert.IsLessThan(firstProductOpen, filesAcquire,
            "FILE_RECEIPT_CHECK_MUST_ACQUIRE_THE_READ_ONLY_ROOT_BEFORE_OPENING_FIXED_FILES");
    }

    [TestMethod]
    public void Fixed_install_files_and_receipt_are_cross_checked_and_released_without_mutation_authority()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        static string FunctionBody(string source, string name)
        {
            int begin = source.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return source[begin..end];
        }

        string check = FunctionBody(guard, "CheckFixedInstallFilesAndReceipt");
        string release = FunctionBody(guard, "ReleaseFixedInstallFilesAndReceipt");
        StringAssert.Contains(check,
            "!insertmacro SetupSaveRegisters\n    Push $GuardPath\n    Push $GuardDirectory\n    Push $GuardIdentity\n    Push $GuardHash\n    Push $GuardLastError\n    StrCpy $SetupCode 11",
            "SCRATCH_STATE_MUST_BE_BALANCED_ON_EARLY_REFUSALS");
        Assert.AreEqual(4, check.Split("Call ${PREFIX}OpenProductIdentityLease", StringSplitOptions.None).Length - 1,
            "ALL_FOUR_FIXED_FILES_MUST_BE_OPENED_AND_HASHED_ON_THE_RETAINED_HANDLE");
        foreach (string required in new[]
        {
            "StrCmp $SetupFixedFilesLeaseActive 0 fixed_files_check_idle",
            "StrCmp $GuardHandle 0 fixed_files_check_scratch_handle",
            "StrCmp $GuardPathPins 0 fixed_files_check_acquire",
            "StrCpy $SetupFixedFilesLeaseActive 1",
            "Call ${PREFIX}AcquireFixedRootOwnershipLease",
            "StrCpy $GuardPath \"$SetupFixedRoot\\${SETUP_APP_NAME}\"",
            "StrCpy $GuardPath \"$SetupFixedRoot\\${SETUP_UNINSTALLER_NAME}\"",
            "StrCpy $GuardPath \"$SetupFixedRoot\\${SETUP_NOTICE_NAME}\"",
            "StrCpy $GuardPath \"$SetupFixedRoot\\${SETUP_RECEIPT_NAME}\"",
            "StrCpy $GuardHandle $SetupFixedFilesReceiptHandle",
            "Call ${PREFIX}ReadOwnedReceipt",
            "StrCmp $SetupOwnerSid \"\"",
            "StrCmp $SetupFixedFilesAppHash \"${SETUP_APP_SHA256}\"",
            "StrCmp $SetupRecordedAppHash $SetupFixedFilesAppHash",
            "StrCmp $SetupFixedFilesNoticeHash \"${SETUP_NOTICE_SHA256}\"",
            "StrCmp $SetupRecordedNoticeHash $SetupFixedFilesNoticeHash",
            "StrCmp $SetupRecordedUninstallerHash $SetupFixedFilesUninstallerHash",
            "Call ${PREFIX}ReleaseFixedInstallFilesAndReceipt"
        })
            StringAssert.Contains(check, required, "FIXED_FILES_RECEIPT_CROSS_CHECK_MISSING: " + required);
        int beginRootLease = check.IndexOf("Call ${PREFIX}AcquireFixedRootOwnershipLease", StringComparison.Ordinal);
        int setLeaseActive = check.IndexOf("StrCpy $SetupFixedFilesLeaseActive 1", StringComparison.Ordinal);
        Assert.IsLessThan(beginRootLease, setLeaseActive,
            "LEASE_MUST_OWN_ALL_RESOURCES_ACQUIRED_BY_THE_ROOT_ATTEMPT");
        StringAssert.Contains(guard, "ownerSid=$SetupOwnerSid",
            "RECEIPT_OWNER_SID_MUST_BE_MATCHED_TO_CURRENT_VALIDATED_SID");

        string[] ownedHandles =
        [
            "$SetupFixedFilesReceiptHandle", "$SetupFixedFilesNoticeHandle",
            "$SetupFixedFilesUninstallerHandle", "$SetupFixedFilesAppHandle", "$SetupFixedFilesRootHandle"
        ];
        foreach (string handle in ownedHandles)
        {
            int close = release.IndexOf("CloseHandle(p " + handle + ")", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, close, "PAIRED_RELEASE_MISSING_CLOSE: " + handle);
            int failedCloseGuard = release.IndexOf("StrCmp $0 0 fixed_files_release_done", close, StringComparison.Ordinal);
            int clear = release.IndexOf("StrCpy " + handle + " 0", close, StringComparison.Ordinal);
            Assert.IsGreaterThan(close, failedCloseGuard, "CLOSE_FAILURE_MUST_STOP_WITH_HANDLE_RETAINED: " + handle);
            Assert.IsGreaterThan(failedCloseGuard, clear, "HANDLE_MUST_ONLY_CLEAR_AFTER_SUCCESSFUL_CLOSE: " + handle);
        }
        StringAssert.Contains(release, "Call ${PREFIX}ReleasePathPins",
            "ANCESTOR_PINS_MUST_REMAIN_HELD_UNTIL_PAIRED_RELEASE");
        Assert.IsLessThan(release.IndexOf("Call ${PREFIX}ReleasePathPins", StringComparison.Ordinal),
            release.IndexOf("CloseHandle(p $SetupFixedFilesRootHandle)", StringComparison.Ordinal));

        foreach (string forbidden in new[]
        {
            "RegWrite", "WriteReg", "CreateShortCut", "WriteUninstaller", "DeleteFile",
            "MoveFile", "ReplaceFile", "kernel32::WriteFile", "CreateDirectory"
        })
            Assert.IsFalse(check.Contains(forbidden, StringComparison.Ordinal) || release.Contains(forbidden, StringComparison.Ordinal),
                "FACT_CHECK_MUST_NOT_MUTATE_PRODUCT_STATE: " + forbidden);

        string installer = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        AssertManagedLifecycleDelegation(installer);
        Assert.IsFalse(installer.Contains("Call CheckFixedInstallFilesAndReceipt", StringComparison.Ordinal) ||
            installer.Contains("Call un.CheckFixedInstallFilesAndReceipt", StringComparison.Ordinal),
            "PRODUCT_RECEIPT_CHECK_MUST_BE_DELEGATED_TO_MANAGED_LIFECYCLE");
    }

    [TestMethod]
    public void Existing_Programs_ancestor_uses_fail_closed_shared_acl_policy_only_at_exact_path()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        static string FunctionBody(string source, string name)
        {
            int begin = source.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return source[begin..end];
        }

        string sharedAcl = FunctionBody(guard, "ValidateSharedProgramsHandleAcl");
        foreach (string required in new[]
        {
            "GetSecurityInfo(p $GuardHandle, i 1, i 5, *p .r0, p 0, *p .r1, p 0, *p .r9)",
            "IsValidSecurityDescriptor(p r9)", "Call ${PREFIX}ValidateApprovedSid",
            "GetSecurityDescriptorControl(p r9", "IntOp $2 $2 & 0x4",
            "IsValidAcl(p r1)", "GetAclInformation(p r1", "GetAce(p r1",
            "ConvertSidToStringSidW(p $GuardSid", "S-1-5-18", "S-1-5-32-544",
            "${SETUP_SHARED_NON_READ_MASK}", "IntOp $0 $2 & ${SETUP_SHARED_FORBIDDEN_WRITE_MASK}",
            "IntOp $0 $7 & 8", "IntOp $0 $7 & 3"
        })
            StringAssert.Contains(sharedAcl, required, "SHARED_PROGRAMS_ACL_CONTRACT_MISSING: " + required);
        StringAssert.Contains(guard, "!define SETUP_SHARED_FORBIDDEN_WRITE_MASK 0x510D0156");
        StringAssert.Contains(guard, "!define SETUP_SHARED_NON_READ_MASK 0x5FEDFF56");
        Assert.IsFalse(sharedAcl.Split('\n').Select(line => line.TrimStart())
            .Any(line => !line.StartsWith(";", StringComparison.Ordinal) &&
                line.Contains("SE_DACL_PROTECTED", StringComparison.Ordinal)),
            "SHARED_PARENT_MUST_ALLOW_INHERITED_DACL");
        Assert.IsFalse(new[] { "SetSecurityInfo", "SetNamedSecurityInfo", "SetFileSecurity", "CreateDirectory" }
            .Any(sharedAcl.Contains), "SHARED_ACL_VALIDATION_MUST_NOT_MUTATE_OBJECTS");

        string pins = FunctionBody(guard, "PinExistingInstallAncestors");
        const string sharedPathCheck = "StrCmp $GuardPath \"$SetupLocalAppData\\Programs\" pins_shared_programs_acl";
        const string sharedCall = "pins_shared_programs_acl:\n    Call ${PREFIX}ValidateSharedProgramsHandleAcl\n    StrCmp $SetupCode 0 0 pins_error";
        StringAssert.Contains(pins, sharedPathCheck, "SHARED_ACL_MUST_MATCH_ONLY_LOCALAPPDATA_PROGRAMS");
        StringAssert.Contains(pins, sharedCall, "SHARED_ACL_FAILURE_MUST_ABORT_ANCESTOR_PINNING");
        Assert.AreEqual(1, pins.Split("Call ${PREFIX}ValidateSharedProgramsHandleAcl", StringSplitOptions.None).Length - 1,
            "SHARED_ACL_VALIDATOR_MUST_HAVE_ONE_EXACT_ANCESTOR_PATH_CALLSITE");
        int openPath = pins.IndexOf("Call ${PREFIX}OpenPathIdentityLease", StringComparison.Ordinal);
        int checkPath = pins.IndexOf(sharedPathCheck, StringComparison.Ordinal);
        int callAcl = pins.IndexOf(sharedCall, StringComparison.Ordinal);
        Assert.IsGreaterThan(openPath, checkPath, "SHARED_ACL_MUST_VALIDATE_THE_ALREADY_OPENED_DIRECTORY");
        Assert.IsGreaterThan(checkPath, callAcl, "SHARED_ACL_CALL_MUST_BE_GUARDED_BY_EXACT_PATH_MATCH");
    }

    [TestMethod]
    public void Existing_shared_Programs_parent_is_reused_only_when_already_pinned_and_stays_unwired()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        static string FunctionBody(string source, string name)
        {
            int begin = source.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return source[begin..end];
        }
        static string WithoutCommentLines(string source) => string.Join("\n", source.Split('\n')
            .Where(line => !line.TrimStart().StartsWith(";", StringComparison.Ordinal)));

        const string function = "Function ${PREFIX}PrepareSharedProgramsParent";
        int begin = guard.IndexOf(function, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, begin, "SHARED_PROGRAMS_PARENT_PREPARATION_MISSING");
        int end = guard.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
        Assert.IsGreaterThan(begin, end, "SHARED_PROGRAMS_PARENT_PREPARATION_UNTERMINATED");
        string body = guard[begin..end];

        foreach (string required in new[]
        {
            "Call ${PREFIX}ValidateHost", "Call ${PREFIX}ValidateDirectoryArguments",
            "Call ${PREFIX}PinExistingInstallAncestors",
            "StrCpy $GuardPath \"$SetupLocalAppData\\Programs\"",
            "Call ${PREFIX}OpenPathIdentityLease",
            "StrCmp $SetupCode 0 programs_parent_existing",
            "${If} $GuardLastError == 2\n    ${OrIf} $GuardLastError == 3",
            "programs_parent_probe_no_handle:\n    StrCmp $GuardProgramsParentPinned 1 programs_parent_refuse",
            "GetModuleHandleW(w \"kernel32.dll\")",
            "GetProcAddress(p r1, m \"CreateDirectory2W\")",
            "StrCmp $1 0 programs_parent_refuse",
            "StrCmp $2 0 programs_parent_refuse",
            "System::Call '::$2(w \"$SetupLocalAppData\\Programs\", i 0x120081, i 1, i 1, p 0) p.r0 ?e'",
            "Pop $GuardLastError", "StrCmp $0 0 programs_parent_refuse",
            "StrCmp $0 -1 programs_parent_refuse", "StrCpy $GuardHandle $0",
            "GetFileInformationByHandle(p $GuardHandle",
            "GetFinalPathNameByHandleW(p $GuardHandle",
            "StrCmp $1 \"\\\\?\\$SetupLocalAppData\\Programs\" 0 programs_parent_validation_failed",
            "GetFileInformationByHandleEx(p $GuardHandle, i 18",
            "StrCpy $GuardIdentity \"$3:$4:$5:$6:$7:$8\"",
            "Call ${PREFIX}ValidateSharedProgramsHandleAcl", "StrCpy $SetupCode 0"
        })
            StringAssert.Contains(body, required, "SHARED_PROGRAMS_PARENT_CONTRACT_MISSING: " + required);

        int host = body.IndexOf("Call ${PREFIX}ValidateHost", StringComparison.Ordinal);
        int args = body.IndexOf("Call ${PREFIX}ValidateDirectoryArguments", StringComparison.Ordinal);
        int pin = body.IndexOf("Call ${PREFIX}PinExistingInstallAncestors", StringComparison.Ordinal);
        int probe = body.IndexOf("Call ${PREFIX}OpenPathIdentityLease", StringComparison.Ordinal);
        int resolve = body.IndexOf("GetProcAddress(p r1, m \"CreateDirectory2W\")", StringComparison.Ordinal);
        int create = body.IndexOf("System::Call '::$2(", StringComparison.Ordinal);
        int retain = body.IndexOf("StrCpy $GuardHandle $0", StringComparison.Ordinal);
        int identity = body.IndexOf("GetFileInformationByHandle(p $GuardHandle", StringComparison.Ordinal);
        int acl = body.LastIndexOf("Call ${PREFIX}ValidateSharedProgramsHandleAcl", StringComparison.Ordinal);
        int success = body.LastIndexOf("StrCpy $SetupCode 0", StringComparison.Ordinal);
        Assert.IsTrue(host >= 0 && args > host && pin > args && probe > pin && resolve > probe && create > resolve &&
            retain > create && identity > retain && acl > identity && success > acl,
            "HOST_ANCESTORS_MISSING_PROBE_API_HANDLE_IDENTITY_AND_ACL_MUST_BE_CHECKED_IN_ORDER");

        int existing = body.IndexOf("programs_parent_existing:", StringComparison.Ordinal);
        int missing = body.IndexOf("programs_parent_missing:", StringComparison.Ordinal);
        int refuse = body.IndexOf("programs_parent_refuse:", StringComparison.Ordinal);
        Assert.IsTrue(existing > probe && missing > existing && resolve > missing && refuse > acl,
            "EXISTING_PARENT_AND_CREATE_FAILURE_BRANCHES_MUST_BE_ORDERED_BEFORE_API_USE");
        string executableBody = WithoutCommentLines(body);
        StringAssert.Contains(executableBody,
            "programs_parent_existing_pinned:\n    StrCmp $GuardHandle 0 programs_parent_refuse\n    StrCmp $GuardIdentity \"\" programs_parent_refuse\n    Call ${PREFIX}ValidateSharedProgramsHandleAcl\n    StrCmp $SetupCode 0 0 programs_parent_refuse\n    StrCpy $SetupCode 0\n    Goto programs_parent_done",
            "VALIDATED_EXISTING_PARENT_HANDLE_AND_IDENTITY_MUST_REMAIN_HELD_ON_SUCCESS");
        StringAssert.Contains(executableBody,
            "programs_parent_existing:\n    StrCmp $GuardProgramsParentPinned 1 programs_parent_existing_pinned",
            "EXISTING_PARENT_MUST_BE_ACCEPTED_ONLY_WHEN_ALREADY_PINNED");
        StringAssert.Contains(executableBody,
            "programs_parent_existing:\n    StrCmp $GuardProgramsParentPinned 1 programs_parent_existing_pinned\n    Call ${PREFIX}ReleaseFixedRootOwnershipLease\n    StrCpy $SetupCode 11\n    Goto programs_parent_done",
            "UNPINNED_EXISTING_OR_CONCURRENT_PARENT_MUST_BE_RELEASED_AND_REFUSED");
        int acceptedExisting = body.IndexOf("programs_parent_existing_pinned:", StringComparison.Ordinal);
        Assert.IsTrue(acceptedExisting > existing && create > acceptedExisting,
            "VALIDATED_EXISTING_PARENT_MUST_BE_ACCEPTED_BEFORE_CREATE_API_BRANCH");
        string acceptedBranch = body[acceptedExisting..create];
        StringAssert.Contains(acceptedBranch, "StrCpy $SetupCode 0", "VALID_EXISTING_PARENT_MUST_RETURN_SUCCESS");
        Assert.IsFalse(acceptedBranch.Contains("ReleaseFixedRootOwnershipLease", StringComparison.Ordinal),
            "VALID_EXISTING_PARENT_MUST_KEEP_THE_HANDLE_AND_ANCESTOR_PINS_FOR_CALLER");
        string pins = FunctionBody(guard, "PinExistingInstallAncestors");
        StringAssert.Contains(pins, "StrCpy $GuardProgramsParentPinned 0",
            "ANCESTOR_SCAN_MUST_CLEAR_STALE_SHARED_PARENT_PIN_STATE");
        StringAssert.Contains(pins,
            "Call ${PREFIX}ValidateSharedProgramsHandleAcl\n    StrCmp $SetupCode 0 0 pins_error\n    StrCpy $GuardProgramsParentPinned 1",
            "SHARED_PARENT_PIN_STATE_MUST_ONLY_BE_SET_AFTER_ACL_VALIDATION");
        string releasePins = FunctionBody(guard, "ReleasePathPins");
        StringAssert.Contains(releasePins, "StrCpy $GuardProgramsParentPinned 0",
            "RELEASED_ANCESTOR_PINS_MUST_NOT_LEAVE_STALE_VALIDATION_STATE");
        StringAssert.Contains(body, "Pop $GuardLastError\n    ; The current API page says NULL on failure",
            "CREATE_API_ERROR_MUST_BE_CAPTURED_BEFORE_FAIL_CLOSED_SENTINEL_CHECKS");

        StringAssert.Contains(body,
            "StrCmp $GuardPathPins 0 programs_parent_inputs_free\n    StrCmp $GuardPathPins \"\" 0 programs_parent_done",
            "PREEXISTING_PINS_MUST_NOT_BE_REUSED_OR_OVERWRITTEN");
        foreach (string forbidden in new[]
        {
            "SetSecurityInfo", "SetNamedSecurityInfo", "WriteFile(", "DeleteFile", "RemoveDirectory",
            "RMDir", "CreateShortCut", "WriteUninstaller"
        })
            Assert.IsFalse(executableBody.Contains(forbidden, StringComparison.Ordinal),
                "SHARED_PROGRAMS_PREPARATION_MUST_NOT_MODIFY_EXISTING_ACL_OR_CONTENT: " + forbidden);
        string postCreate = body[create..];
        Assert.IsFalse(postCreate.Contains("OpenPathIdentityLease", StringComparison.Ordinal) ||
            postCreate.Contains("CreateFileW", StringComparison.Ordinal),
            "CREATED_SHARED_PARENT_MUST_NOT_BE_REOPENED_BY_PATH");
        Assert.IsFalse(body.Contains("RemoveDirectory", StringComparison.Ordinal) ||
            body.Contains("RMDir", StringComparison.Ordinal) ||
            body.Contains("DeleteFile", StringComparison.Ordinal),
            "FAILED_PREPARATION_MUST_NEVER_DELETE_SHARED_PARENT");

        string installer = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        Assert.IsFalse(installer.Contains("Call PrepareSharedProgramsParent", StringComparison.Ordinal) ||
            installer.Contains("Call un.PrepareSharedProgramsParent", StringComparison.Ordinal),
            "SHARED_PROGRAMS_PARENT_PREPARATION_MUST_REMAIN_UNWIRED");
    }

    [TestMethod]
    public void Fresh_product_root_creation_uses_returned_handle_protected_acl_and_stays_unwired()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        static string FunctionBody(string source, string name)
        {
            int begin = source.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return source[begin..end];
        }
        static string WithoutCommentLines(string source) => string.Join("\n", source.Split('\n')
            .Where(line => !line.TrimStart().StartsWith(";", StringComparison.Ordinal)));

        string body = FunctionBody(guard, "CreateFreshProductRoot");
        string code = WithoutCommentLines(body);
        foreach (string required in new[]
        {
            "StrCmp $SetupMode \"install\" fresh_root_mode_ok",
            "Call ${PREFIX}PrepareSharedProgramsParent",
            "ConvertStringSecurityDescriptorToSecurityDescriptorW(w \"O:$0D:P(A;;GA;;;$0)(A;;GA;;;SY)(A;;GA;;;BA)\"",
            "System::Call '*(i 12, p r8, i 0) p.r9'",
            "GetModuleHandleW(w \"kernel32.dll\")",
            "GetProcAddress(p r1, m \"CreateDirectory2W\")",
            "System::Call '::$2(w \"$SetupFixedRoot\", i 0x120081, i 1, i 1, p $SetupFreshRootSecurityAttributes) p.r0 ?e'",
            "StrCmp $1 0 fresh_root_create_refused", "StrCmp $2 0 fresh_root_create_refused",
            "StrCmp $0 0 fresh_root_create_refused", "StrCmp $0 -1 fresh_root_create_refused",
            "GetFileInformationByHandle(p $GuardHandle",
            "IntOp $2 $1 & 0x400", "IntOp $2 $1 & 0x10",
            "StrCmp $1 \"\\\\?\\$SetupFixedRoot\" 0 fresh_root_validation_failed",
            "GetFileInformationByHandleEx(p $GuardHandle, i 18",
            "System::Call '*$SetupFreshRootInfoBuffer(i.r3, i.r4, i.r5, i.r6, i.r7, i.r8)'",
            "StrCpy $GuardIdentity \"$3:$4:$5:$6:$7:$8\"",
            "Call ${PREFIX}ValidatePrivateHandleAcl"
        })
            StringAssert.Contains(code, required, "FRESH_PRODUCT_ROOT_CONTRACT_MISSING: " + required);

        int mode = code.IndexOf("StrCmp $SetupMode \"install\" fresh_root_mode_ok", StringComparison.Ordinal);
        int parent = code.IndexOf("Call ${PREFIX}PrepareSharedProgramsParent", StringComparison.Ordinal);
        int securityDescriptor = code.IndexOf("ConvertStringSecurityDescriptorToSecurityDescriptorW", StringComparison.Ordinal);
        int api = code.IndexOf("System::Call '::$2(", StringComparison.Ordinal);
        int retain = code.IndexOf("StrCpy $GuardHandle $1", StringComparison.Ordinal);
        int info = code.IndexOf("GetFileInformationByHandle(p $GuardHandle", StringComparison.Ordinal);
        int finalPath = code.IndexOf("GetFinalPathNameByHandleW(p $GuardHandle", StringComparison.Ordinal);
        int identity = code.IndexOf("GetFileInformationByHandleEx(p $GuardHandle, i 18", StringComparison.Ordinal);
        int identityFields = code.IndexOf("System::Call '*$SetupFreshRootInfoBuffer(i.r3, i.r4, i.r5, i.r6, i.r7, i.r8)'", StringComparison.Ordinal);
        int identityStore = code.IndexOf("StrCpy $GuardIdentity \"$3:$4:$5:$6:$7:$8\"", StringComparison.Ordinal);
        int acl = code.IndexOf("Call ${PREFIX}ValidatePrivateHandleAcl", StringComparison.Ordinal);
        Assert.IsTrue(mode >= 0 && parent > mode && securityDescriptor > parent && api > securityDescriptor && retain > api &&
            info > retain && finalPath > info && identity > finalPath && identityFields > identity &&
            identityStore > identityFields && acl > identityStore,
            "MODE_AND_PARENT_PINS_MUST_PRECEDE_CREATE_AND_SAME_HANDLE_IDENTITY_AND_ACL_CHECKS");
        string prepare = FunctionBody(guard, "PrepareSharedProgramsParent");
        Assert.IsTrue(prepare.IndexOf("Call ${PREFIX}ValidateHost", StringComparison.Ordinal) >= 0 &&
            prepare.IndexOf("Call ${PREFIX}ValidateDirectoryArguments", StringComparison.Ordinal) >
                prepare.IndexOf("Call ${PREFIX}ValidateHost", StringComparison.Ordinal),
            "PREPARED_PARENT_MUST_HAVE_KNOWN_CURRENT_USER_AND_FIXED_ROOT_ARGUMENTS");

        StringAssert.Contains(code,
            "IntOp $0 $GuardPathPinCount * ${NSIS_PTR_SIZE}\n    IntOp $0 $GuardPathPins + $0\n    System::Call '*$0(p $GuardHandle)'\n    IntOp $GuardPathPinCount $GuardPathPinCount + 1\n    StrCpy $GuardHandle $1",
            "PARENT_HANDLE_MUST_BE_TRANSFERRED_TO_PINS_BEFORE_ROOT_HANDLE_REPLACES_IT");
        StringAssert.Contains(code,
            "StrCmp $0 0 fresh_root_create_refused",
            "MISSING_CREATE_API_MUST_FAIL_CLOSED");
        StringAssert.Contains(code,
            "Call ${PREFIX}ReleaseFixedRootOwnershipLease\n    StrCpy $SetupCode 11",
            "FAILED_CREATE_OR_VALIDATION_MUST_RELEASE_HANDLES_WITHOUT_PATH_CLEANUP");
        Assert.IsFalse(code.Contains("OpenPathIdentityLease", StringComparison.Ordinal) ||
            code.Contains("SetSecurityInfo", StringComparison.Ordinal) ||
            code.Contains("SetNamedSecurityInfo", StringComparison.Ordinal) ||
            code.Contains("DeleteFile", StringComparison.Ordinal) ||
            code.Contains("RemoveDirectory", StringComparison.Ordinal) ||
            code.Contains("RMDir", StringComparison.Ordinal),
            "FRESH_ROOT_MUST_NOT_REOPEN_REPAIR_ACL_OR_DELETE_BY_PATH");
        Assert.AreEqual(1, code.Split("System::Call '::$2(", StringSplitOptions.None).Length - 1,
            "FRESH_ROOT_MUST_PERFORM_ONE_ATOMIC_DIRECTORY_CREATE");
        Assert.IsFalse(code.Contains("$SetupLocalAppData\\Programs", StringComparison.Ordinal),
            "FRESH_ROOT_MUST_CREATE_ONLY_THE_FIXED_PRODUCT_ROOT");

        string release = FunctionBody(guard, "ReleaseFixedRootOwnershipLease");
        StringAssert.Contains(release, "Call ${PREFIX}ReleasePathPins",
            "FRESH_ROOT_RELEASE_MUST_CLOSE_THE_RETAINED_ANCESTOR_PINS");
        int closeRootHandle = release.IndexOf("CloseHandle(p $GuardHandle)", StringComparison.Ordinal);
        int releaseAncestorPins = release.IndexOf("Call ${PREFIX}ReleasePathPins", StringComparison.Ordinal);
        Assert.IsTrue(closeRootHandle >= 0 && releaseAncestorPins > closeRootHandle,
            "FRESH_ROOT_HANDLE_MUST_CLOSE_BEFORE_PARENT_AND_ANCESTOR_PINS");
        StringAssert.Contains(release,
            "Call ${PREFIX}ReleaseFreshRootScratch\n    StrCmp $SetupCode 0 release_root_handles",
            "PAIRED_RELEASE_MUST_RETRY_ANY_RETAINED_SECURITY_SCRATCH_BEFORE_CLOSING_OBJECT_HANDLES");
        string scratch = FunctionBody(guard, "ReleaseFreshRootScratch");
        StringAssert.Contains(scratch,
            "System::Call 'kernel32::LocalFree(p $SetupFreshRootSecurityDescriptor) p.r0'\n    StrCmp $0 0 fresh_scratch_descriptor_freed\n    StrCpy $SetupCode 11",
            "FAILED_LOCALFREE_MUST_RETAIN_OWNERSHIP_STATE_FOR_RETRY");
        string installer = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        Assert.IsFalse(installer.Contains("Call CreateFreshProductRoot", StringComparison.Ordinal) ||
            installer.Contains("Call un.CreateFreshProductRoot", StringComparison.Ordinal),
            "PRODUCT_ROOT_CREATION_MUST_BE_DELEGATED_TO_MANAGED_LIFECYCLE");
        AssertManagedLifecycleDelegation(installer);
    }

    [TestMethod]
    public void Setup_mutex_is_sid_scoped_and_all_acquisition_and_release_errors_fail_closed()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        static string WithoutComments(string source) => string.Join("\n", source.Split('\n').Select(line =>
        {
            bool inSingleQuotedString = false;
            bool inDoubleQuotedString = false;
            for (int index = 0; index < line.Length; index++)
            {
                char character = line[index];
                if (character == '\"' && !inSingleQuotedString) inDoubleQuotedString = !inDoubleQuotedString;
                else if (character == '\'' && !inDoubleQuotedString) inSingleQuotedString = !inSingleQuotedString;
                else if (character == ';' && !inSingleQuotedString && !inDoubleQuotedString) return line[..index].TrimEnd();
            }
            return line;
        }));
        static string FunctionBody(string source, string name)
        {
            int begin = source.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return source[begin..end];
        }

        string acquire = FunctionBody(WithoutComments(guard), "AcquireSetupMutex");
        foreach (string required in new[]
        {
            "StrCpy $SetupCode 12",
            "StrCmp $SetupOwnerSid \"\" mutex_done",
            "StrCpy $0 $SetupOwnerSid",
            "StrLen $1 \"Global\\${SETUP_PRODUCT_ID}.Setup.$0\"",
            "IntCmpU $1 260 mutex_done 0 mutex_done",
            "ConvertStringSecurityDescriptorToSecurityDescriptorW(w \"O:$0D:P(A;;GA;;;$0)(A;;GA;;;SY)(A;;GA;;;BA)\", i 1, *p .r8, p 0)",
            "StrCmp $1 0 mutex_done", "StrCmp $8 0 mutex_done",
            "System::Call '*(i 12, p r8, i 0) p.r9'\n    StrCmp $9 0 mutex_done",
            "System::Call 'kernel32::SetLastError(i 0)'",
            "CreateMutexW(p r9, i 0, w \"Global\\${SETUP_PRODUCT_ID}.Setup.$0\") p.r7 ?e",
            "StrCmp $7 0 mutex_done", "StrCmp $1 183 mutex_done",
            "StrCmp $1 0 0 mutex_done", "StrCpy $SetupMutex $7",
            "StrCpy $SetupMutex $7\n    StrCpy $7 0\n    StrCpy $SetupCode 0",
            "System::Call 'kernel32::CloseHandle(p r7) i'",
            "System::Call 'kernel32::LocalFree(p r8) p'"
        })
            StringAssert.Contains(acquire, required, "SETUP_MUTEX_CONTRACT_MISSING: " + required);

        static bool HasMutexCreationSequence(string source)
        {
            int ownerCopy = source.IndexOf("StrCpy $0 $SetupOwnerSid", StringComparison.Ordinal);
            int sddl = source.IndexOf("ConvertStringSecurityDescriptorToSecurityDescriptorW(", StringComparison.Ordinal);
            if (ownerCopy < 0 || sddl <= ownerCopy) return false;
            int sddlResultCheck = source.IndexOf("StrCmp $1 0 mutex_done", sddl, StringComparison.Ordinal);
            if (sddlResultCheck < 0) return false;
            int sddlPointerCheck = source.IndexOf("StrCmp $8 0 mutex_done", sddlResultCheck, StringComparison.Ordinal);
            if (sddlPointerCheck < 0) return false;
            int securityAttributes = source.IndexOf("System::Call '*(i 12, p r8, i 0) p.r9'", StringComparison.Ordinal);
            if (securityAttributes <= sddlPointerCheck) return false;
            int attributesCheck = source.IndexOf("StrCmp $9 0 mutex_done", securityAttributes, StringComparison.Ordinal);
            if (attributesCheck < 0) return false;
            int reset = source.IndexOf("System::Call 'kernel32::SetLastError(i 0)'", StringComparison.Ordinal);
            const string adjacentResetAndCreate =
                "System::Call 'kernel32::SetLastError(i 0)'\n    System::Call 'kernel32::CreateMutexW";
            int create = source.IndexOf("System::Call 'kernel32::CreateMutexW(", StringComparison.Ordinal);
            return sddlResultCheck > sddl &&
                sddlPointerCheck > sddlResultCheck && securityAttributes > sddlPointerCheck &&
                attributesCheck > securityAttributes && reset > attributesCheck && create > reset &&
                source.Contains(adjacentResetAndCreate, StringComparison.Ordinal);
        }
        static bool HasImmediateTemporaryHandleClear(string source) => source.Contains(
            "StrCpy $SetupMutex $7\n    StrCpy $7 0\n    StrCpy $SetupCode 0", StringComparison.Ordinal);

        int initialFailure = acquire.IndexOf("StrCpy $SetupCode 12", StringComparison.Ordinal);
        int createMutex = acquire.IndexOf("CreateMutexW(", StringComparison.Ordinal);
        int lastErrorReset = acquire.IndexOf("System::Call 'kernel32::SetLastError(i 0)'", StringComparison.Ordinal);
        int nullHandle = acquire.IndexOf("StrCmp $7 0 mutex_done", createMutex, StringComparison.Ordinal);
        int alreadyExists = acquire.IndexOf("StrCmp $1 183 mutex_done", createMutex, StringComparison.Ordinal);
        int anyLastError = acquire.IndexOf("StrCmp $1 0 0 mutex_done", createMutex, StringComparison.Ordinal);
        int transfer = acquire.IndexOf("StrCpy $SetupMutex $7", StringComparison.Ordinal);
        int success = acquire.IndexOf("StrCpy $SetupCode 0", StringComparison.Ordinal);
        int cleanup = acquire.IndexOf("mutex_done:", StringComparison.Ordinal);
        int closeTemporaryHandle = acquire.IndexOf("CloseHandle(p r7)", cleanup, StringComparison.Ordinal);
        Assert.IsTrue(initialFailure >= 0 && createMutex > initialFailure && lastErrorReset >= 0 &&
            createMutex > lastErrorReset && HasMutexCreationSequence(acquire) &&
            HasImmediateTemporaryHandleClear(acquire) && nullHandle > createMutex &&
            alreadyExists > nullHandle && anyLastError > alreadyExists && transfer > anyLastError &&
            success > transfer && cleanup > success && closeTemporaryHandle > cleanup &&
            acquire.LastIndexOf("StrCpy $SetupCode 0", StringComparison.Ordinal) < cleanup,
            "MUTEX_ERRORS_MUST_REMAIN_REFUSALS_AND_UNTRANSFERRED_COMPETITION_HANDLE_MUST_BE_CLOSED");
        string withoutOwnerCopy = acquire.Replace("StrCpy $0 $SetupOwnerSid\n", "", StringComparison.Ordinal);
        Assert.IsFalse(HasMutexCreationSequence(withoutOwnerCopy),
            "MUTATION_REMOVING_MUTEX_OWNER_SID_COPY_MUST_BE_DETECTED");
        string withoutSddlResultCheck = acquire.Replace("StrCmp $1 0 mutex_done\n", "", StringComparison.Ordinal);
        Assert.IsFalse(HasMutexCreationSequence(withoutSddlResultCheck),
            "MUTATION_REMOVING_SDDL_RESULT_CHECK_MUST_BE_DETECTED");
        string reorderedSddlChecks = acquire.Replace(
            "StrCmp $1 0 mutex_done\n    StrCmp $8 0 mutex_done",
            "StrCmp $8 0 mutex_done\n    StrCmp $1 0 mutex_done", StringComparison.Ordinal);
        Assert.IsFalse(HasMutexCreationSequence(reorderedSddlChecks),
            "MUTATION_REORDERING_SDDL_FAILURE_CHECKS_MUST_BE_DETECTED");
        string movedAttributesCheck = acquire.Replace(
            "System::Call '*(i 12, p r8, i 0) p.r9'\n    StrCmp $9 0 mutex_done\n    System::Call 'kernel32::SetLastError(i 0)'",
            "System::Call '*(i 12, p r8, i 0) p.r9'\n    System::Call 'kernel32::SetLastError(i 0)'\n    StrCmp $9 0 mutex_done", StringComparison.Ordinal);
        Assert.IsFalse(HasMutexCreationSequence(movedAttributesCheck),
            "MUTATION_MOVING_SECURITY_ATTRIBUTES_FAILURE_CHECK_MUST_BE_DETECTED");
        string withoutLastErrorReset = acquire.Replace(
            "System::Call 'kernel32::SetLastError(i 0)'\n    ", "", StringComparison.Ordinal);
        Assert.IsFalse(HasMutexCreationSequence(withoutLastErrorReset),
            "MUTATION_REMOVING_LAST_ERROR_RESET_MUST_BE_DETECTED");
        string resetAfterCreate = acquire.Replace(
            "System::Call 'kernel32::SetLastError(i 0)'\n    System::Call 'kernel32::CreateMutexW",
            "System::Call 'kernel32::CreateMutexW\n    System::Call 'kernel32::SetLastError(i 0)'", StringComparison.Ordinal);
        Assert.IsFalse(HasMutexCreationSequence(resetAfterCreate),
            "MUTATION_SWAPPING_LAST_ERROR_RESET_AND_MUTEX_CREATE_MUST_BE_DETECTED");
        string withoutTemporaryClear = acquire.Replace(
            "StrCpy $SetupMutex $7\n    StrCpy $7 0\n    StrCpy $SetupCode 0",
            "StrCpy $SetupMutex $7\n    StrCpy $SetupCode 0", StringComparison.Ordinal);
        Assert.IsFalse(HasImmediateTemporaryHandleClear(withoutTemporaryClear),
            "MUTATION_REMOVING_TEMPORARY_HANDLE_CLEAR_MUST_BE_DETECTED");

        string release = FunctionBody(WithoutComments(guard), "ReleaseSetupMutex");
        StringAssert.Contains(release,
            "CloseHandle(p $SetupMutex) i.r0'\n    ${If} $0 == 0\n        StrCpy $SetupCode 12\n    ${Else}\n        StrCpy $SetupMutex 0",
            "MUTEX_RELEASE_CLOSE_FAILURE_MUST_REFUSE_AND_KEEP_HANDLE_REACHABLE");

        Assert.IsFalse(acquire.Contains("OpenMutex", StringComparison.Ordinal) ||
            acquire.Contains("SetSecurityInfo", StringComparison.Ordinal),
            "MUTEX_MUST_BE_NEW_AND_OWN_SECURITY_DESCRIPTOR_MUST_NOT_BE_REPLACED");
    }

    [TestMethod]
    public void App_idle_check_uses_only_same_fixed_file_lease_and_refuses_unknown_RM_results()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        static string WithoutComments(string source) => string.Join("\n", source.Split('\n').Select(line =>
        {
            bool inSingleQuotedString = false;
            bool inDoubleQuotedString = false;
            for (int index = 0; index < line.Length; index++)
            {
                char character = line[index];
                if (character == '\"' && !inSingleQuotedString) inDoubleQuotedString = !inDoubleQuotedString;
                else if (character == '\'' && !inDoubleQuotedString) inSingleQuotedString = !inSingleQuotedString;
                else if (character == ';' && !inSingleQuotedString && !inDoubleQuotedString) return line[..index].TrimEnd();
            }
            return line;
        }));
        static string FunctionBody(string source, string name)
        {
            int begin = source.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return source[begin..end];
        }

        string idle = FunctionBody(WithoutComments(guard), "CheckAppIdle");
        static bool TryExtractSystemCallApis(string source, out string[] apis)
        {
            var statements = System.Text.RegularExpressions.Regex.Matches(source,
                @"(?m)^\s*System::Call\b[^\r\n]*");
            var quotedCalls = System.Text.RegularExpressions.Regex.Matches(source,
                @"(?m)^\s*System::Call\s+(?:'(?<single>[^']+)'|""(?<double>[^""]+)"")");
            if (statements.Count != quotedCalls.Count)
            {
                apis = [];
                return false;
            }

            var extracted = new List<string>(quotedCalls.Count);
            foreach (System.Text.RegularExpressions.Match call in quotedCalls)
            {
                string signature = call.Groups["single"].Success
                    ? call.Groups["single"].Value : call.Groups["double"].Value;
                // CheckAppIdle has exactly two NSIS memory reads in addition to
                // native calls. Account for these explicit forms; do not treat
                // arbitrary $ptr->N() dynamic calls as an approved API.
                if (signature is "*$2(p r3)" or "*$2(i.r3)")
                {
                    extracted.Add("System::MemoryRead");
                    continue;
                }
                var api = System.Text.RegularExpressions.Regex.Match(signature,
                    @"(?<module>[A-Za-z0-9_.]+)::(?<name>[A-Za-z0-9_$]+)\s*\(");
                if (!api.Success)
                {
                    apis = [];
                    return false;
                }
                extracted.Add(api.Groups["module"].Value + "::" + api.Groups["name"].Value);
            }
            apis = extracted.ToArray();
            return true;
        }
        int start = idle.IndexOf("rstrtmgr::RmStartSession", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, "APP_IDLE_MUST_USE_RESTART_MANAGER_QUERY");
        string beforeStart = idle[..start];
        foreach (string required in new[]
        {
            "StrCmp $SetupMutex \"\" busy_done", "StrCmp $SetupMutex 0 busy_done",
            "StrCmp $GuardHandle 0 busy_done", "StrCmp $GuardHandle -1 busy_done",
            "StrCmp $GuardDirectory 0 0 busy_done", "StrCmp $GuardIdentity \"\" busy_done",
            "StrCmp $GuardHash \"${SETUP_APP_SHA256}\" 0 busy_done",
            "StrCmp $GuardPath \"$SetupFixedRoot\\${SETUP_APP_NAME}\" 0 busy_done"
        })
            StringAssert.Contains(beforeStart, required, "APP_IDLE_FILE_LEASE_PRECONDITION_MISSING: " + required);

        foreach (string required in new[]
        {
            "System::Call 'rstrtmgr::RmStartSession(*i .r0, i 0, p r9) i.r2'\n    StrCmp $2 0 0 busy_done",
            "System::Call 'rstrtmgr::RmRegisterResources(i r0, i 1, p r2, i 0, p 0, i 0, p 0) i.r3'\n    StrCmp $3 0 0 busy_done",
            "System::Call 'rstrtmgr::RmGetList(i r0, *i .r6, *i 1 .r7, p r2, *i .r8) i.r3'\n    StrCmp $3 0 0 busy_done",
            "IntOp $3 $8 & 0xFFFFFFEF\n    StrCmp $3 0 0 busy_done",
            "StrCmp $7 1 0 busy_done", "StrCmp $6 1 0 busy_done",
            "GetCurrentProcessId() i.r4", "StrCmp $3 $4 0 busy_done",
            "GetProcessTimes(p -1, p r2, p r3, p r4, p r5) i.r3'\n    StrCmp $3 0 busy_done",
            "CompareFileTime(p r2, p r3) i.r4'\n    StrCmp $4 0 busy_clear busy_done",
            "busy_empty:\n    StrCmp $6 0 0 busy_done\n    StrCmp $8 0 0 busy_done",
            "busy_clear:\n    StrCpy $SetupCode 0",
            "System::Call 'rstrtmgr::RmEndSession(i r0) i.r2'\n        ${If} $2 != 0\n            StrCpy $SetupCode 12"
        })
            StringAssert.Contains(idle, required, "APP_IDLE_RM_FAIL_CLOSED_CONTRACT_MISSING: " + required);

        string[] allowedSystemApis =
        {
            "rstrtmgr::RmStartSession", "kernel32::lstrcpynW", "System::MemoryRead",
            "rstrtmgr::RmRegisterResources", "rstrtmgr::RmGetList", "System::MemoryRead",
            "kernel32::GetCurrentProcessId", "kernel32::GetProcessTimes",
            "kernel32::CompareFileTime", "rstrtmgr::RmEndSession"
        };
        Assert.IsTrue(TryExtractSystemCallApis(idle, out string[] extractedSystemApis),
            "EVERY_RESTART_MANAGER_SYSTEM_CALL_MUST_BE_QUOTED_AND_IDENTIFIABLE");
        CollectionAssert.AreEqual(allowedSystemApis, extractedSystemApis,
            "APP_IDLE_SYSTEM_CALL_API_ALLOWLIST_CHANGED");

        int getList = idle.IndexOf("rstrtmgr::RmGetList", StringComparison.Ordinal);
        int getListError = idle.IndexOf("StrCmp $3 0 0 busy_done", getList, StringComparison.Ordinal);
        const string rebootReasonGuard = "IntOp $3 $8 & 0xFFFFFFEF\n    StrCmp $3 0 0 busy_done";
        int rebootReasons = idle.IndexOf(rebootReasonGuard, getList, StringComparison.Ordinal);
        int countEmpty = idle.IndexOf("StrCmp $7 0 busy_empty", getList, StringComparison.Ordinal);
        int countOne = idle.IndexOf("StrCmp $7 1 0 busy_done", getList, StringComparison.Ordinal);
        int pid = idle.IndexOf("GetCurrentProcessId()", getList, StringComparison.Ordinal);
        int createTime = idle.IndexOf("CompareFileTime", getList, StringComparison.Ordinal);
        Assert.IsTrue(getList >= 0 && getListError > getList && rebootReasons > getListError &&
            countEmpty > rebootReasons && countOne > countEmpty && pid > countOne && createTime > pid,
            "RM_MUST_ACCEPT_ONLY_EMPTY_LIST_OR_ONE_EXACT_SELF_MATCH");

        foreach (string forbidden in new[]
        {
            "RmShutdown", "TerminateProcess", "MoveFileEx", "MOVEFILE_DELAY_UNTIL_REBOOT", "InitiateSystemShutdown"
        })
            Assert.IsFalse(idle.Contains(forbidden, StringComparison.Ordinal),
                "APP_IDLE_MUST_NOT_SHUT_DOWN_TERMINATE_OR_SCHEDULE_REPLACEMENT: " + forbidden);

        string withoutReasonMask = idle.Replace("IntOp $3 $8 & 0xFFFFFFEF\n", "", StringComparison.Ordinal);
        Assert.IsFalse(withoutReasonMask.Contains(rebootReasonGuard, StringComparison.Ordinal),
            "MUTATION_REMOVING_REBOOT_REASON_MASK_MUST_BE_DETECTED");
        string withoutReasonCheck = idle.Replace(rebootReasonGuard, "IntOp $3 $8 & 0xFFFFFFEF", StringComparison.Ordinal);
        Assert.IsFalse(withoutReasonCheck.Contains(rebootReasonGuard, StringComparison.Ordinal),
            "MUTATION_REMOVING_REBOOT_REASON_REJECTION_MUST_BE_DETECTED");
        string withUnexpectedApi = idle.Replace("System::Alloc ${SETUP_RM_BYTES}",
            "System::Call 'kernel32::TerminateProcess(p -1, i 1) i.r4'\n    System::Alloc ${SETUP_RM_BYTES}",
            StringComparison.Ordinal);
        Assert.IsTrue(TryExtractSystemCallApis(withUnexpectedApi, out string[] withUnexpectedApiCalls));
        Assert.IsFalse(allowedSystemApis.SequenceEqual(withUnexpectedApiCalls),
            "MUTATION_ADDING_UNAPPROVED_SYSTEM_CALL_MUST_BE_DETECTED");
        string withDoubleQuotedUnexpectedApi = idle.Replace("System::Alloc ${SETUP_RM_BYTES}",
            "System::Call \"kernel32::TerminateProcess(p -1, i 1) i.r4\"\n    System::Alloc ${SETUP_RM_BYTES}",
            StringComparison.Ordinal);
        Assert.IsTrue(TryExtractSystemCallApis(withDoubleQuotedUnexpectedApi, out string[] withDoubleQuotedApiCalls));
        Assert.IsFalse(allowedSystemApis.SequenceEqual(withDoubleQuotedApiCalls),
            "MUTATION_ADDING_DOUBLE_QUOTED_UNAPPROVED_SYSTEM_CALL_MUST_BE_DETECTED");
        string withUnquotedSystemCall = idle.Replace("System::Alloc ${SETUP_RM_BYTES}",
            "System::Call kernel32::GetCurrentProcessId() i.r4\n    System::Alloc ${SETUP_RM_BYTES}",
            StringComparison.Ordinal);
        Assert.IsFalse(TryExtractSystemCallApis(withUnquotedSystemCall, out _),
            "MUTATION_ADDING_UNQUOTED_SYSTEM_CALL_MUST_NOT_BE_SILENTLY_IGNORED");
        string withUnrecognizedDynamicCall = idle.Replace("System::Alloc ${SETUP_RM_BYTES}",
            "System::Call '$1->3() i.r4'\n    System::Alloc ${SETUP_RM_BYTES}",
            StringComparison.Ordinal);
        Assert.IsFalse(TryExtractSystemCallApis(withUnrecognizedDynamicCall, out _),
            "MUTATION_ADDING_UNRECOGNIZED_DYNAMIC_SYSTEM_CALL_MUST_BE_DETECTED");
    }

    [TestMethod]
    public void Empty_fixed_root_guard_initializes_cleanup_buffer_before_any_exit()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        const string function = "Function ${PREFIX}ValidateEmptyFixedRootDirectory";
        int begin = guard.IndexOf(function, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, begin, "EMPTY_FIXED_ROOT_GUARD_MISSING");
        int end = guard.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
        Assert.IsGreaterThan(begin, end, "EMPTY_FIXED_ROOT_GUARD_UNTERMINATED");
        string body = guard[begin..end];

        StringAssert.Contains(body,
            "!insertmacro SetupSaveRegisters\n    StrCpy $9 0",
            "CLEANUP_BUFFER_REGISTER_MUST_START_UNALLOCATED_BEFORE_ANY_EXIT");
    }

    [TestMethod]
    public void Fixed_root_empty_check_enumerates_the_held_handle_and_fails_closed_on_unknown_entries()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        const string function = "Function ${PREFIX}ValidateEmptyFixedRootDirectory";
        int begin = guard.IndexOf(function, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, begin, "EMPTY_FIXED_ROOT_GUARD_MISSING");
        int end = guard.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
        Assert.IsGreaterThan(begin, end, "EMPTY_FIXED_ROOT_GUARD_UNTERMINATED");
        string body = guard[begin..end];
        foreach (string required in new[]
        {
            "StrCmp $SetupMode \"install\"",
            "Call ${PREFIX}AcquireFixedRootOwnershipLease",
            "GetFileInformationByHandleEx(p $GuardHandle, i 15, p $9, i ${SETUP_DIRECTORY_INFO_BUFFER_BYTES})",
            "GetFileInformationByHandleEx(p $GuardHandle, i 14, p $9, i ${SETUP_DIRECTORY_INFO_BUFFER_BYTES})",
            "IntOp $0 $9 + $3", "IntOp $1 $0 + ${SETUP_FULL_DIR_INFO_NAME_LENGTH_OFFSET}",
            "IntOp $4 $3 + ${SETUP_FULL_DIR_INFO_NAME_OFFSET}", "IntOp $1 $0 + ${SETUP_FULL_DIR_INFO_NAME_OFFSET}",
            "IntOp $4 $1 & 7", "StrCmp $SetupCode 0"
        })
            StringAssert.Contains(body, required);
        StringAssert.Contains(guard, "!define SETUP_DIRECTORY_INFO_BUFFER_BYTES 65536");
        StringAssert.Contains(guard, "!define SETUP_FULL_DIR_INFO_NAME_LENGTH_OFFSET 60");
        StringAssert.Contains(guard, "!define SETUP_FULL_DIR_INFO_NAME_OFFSET 68");
        StringAssert.Contains(guard, "!define SETUP_ERROR_NO_MORE_FILES 18");
        StringAssert.Contains(body, "StrCmp $2 2 directory_name_dot\n    StrCmp $2 4 directory_name_dotdot");
        StringAssert.Contains(body, "Goto directory_nonempty");
        StringAssert.Contains(body, "StrCmp $GuardLastError ${SETUP_ERROR_NO_MORE_FILES} directory_empty");
        StringAssert.Contains(body, "StrCmp $1 0 directory_query");
        StringAssert.Contains(body, "IntOp $4 $2 & 1\n    StrCmp $4 0 0 directory_reject");
        StringAssert.Contains(body, "IntCmpU $2 $6 directory_name_in_bounds directory_name_in_bounds directory_reject");
        StringAssert.Contains(body, "IntOp $4 $1 & 7\n    StrCmp $4 0 0 directory_reject");
        StringAssert.Contains(body, "IntOp $6 ${SETUP_DIRECTORY_INFO_BUFFER_BYTES} - $3\n    IntCmpU $1 $6 directory_next_offset_bounded directory_next_offset_bounded directory_reject");
        Assert.IsLessThan(body.IndexOf("IntOp $4 $3 + $1", StringComparison.Ordinal),
            body.IndexOf("IntCmpU $1 $6 directory_next_offset_bounded", StringComparison.Ordinal),
            "NEXT_OFFSET_MUST_BE_BOUNDED_BEFORE_32_BIT_ADDITION");
        StringAssert.Contains(body, "StrCmp $0 0 directory_query_failed");
        StringAssert.Contains(body, "Call ${PREFIX}ReleaseFixedRootOwnershipLease\n    StrCpy $SetupCode 11");
        StringAssert.Contains(body, "Call ${PREFIX}ReleaseFixedRootOwnershipLease");
        StringAssert.Contains(body, "StrCmp $GuardHandle 0 directory_reject");
        Assert.IsFalse(body.Contains("FindFirstFile", StringComparison.Ordinal) ||
            body.Contains("CreateFileW", StringComparison.Ordinal),
            "DIRECTORY_ENUMERATION_MUST_USE_THE_HELD_ROOT_HANDLE");
        Assert.IsLessThan(body.IndexOf("System::Call '*$1(i.r2)'", StringComparison.Ordinal),
            body.IndexOf("IntCmpU $4 ${SETUP_DIRECTORY_INFO_BUFFER_BYTES}", StringComparison.Ordinal),
            "RECORD_HEADER_MUST_BE_BOUNDS_CHECKED_BEFORE_READING_NAME_LENGTH");

        // NSIS IntCmpU jump operands are ordered equal, val1-less, val1-greater.
        var comparisons = new[]
        {
            (Left: "$4", Right: "${SETUP_DIRECTORY_INFO_BUFFER_BYTES}", Equal: "directory_header_in_bounds", Less: "directory_header_in_bounds", Greater: "directory_reject", Name: "HEADER_END"),
            (Left: "$2", Right: "$6", Equal: "directory_name_in_bounds", Less: "directory_name_in_bounds", Greater: "directory_reject", Name: "NAME_LENGTH"),
            (Left: "$7", Right: "2", Equal: "directory_dot_count_ok", Less: "directory_dot_count_ok", Greater: "directory_reject", Name: "DOT_COUNT"),
            (Left: "$1", Right: "$4", Equal: "directory_next_record_stride_ok", Less: "directory_reject", Greater: "directory_next_record_stride_ok", Name: "NEXT_OFFSET_STRIDE"),
            (Left: "$1", Right: "$6", Equal: "directory_next_offset_bounded", Less: "directory_next_offset_bounded", Greater: "directory_reject", Name: "NEXT_OFFSET_REMAINING"),
            (Left: "$4", Right: "$6", Equal: "directory_next_record_in_bounds", Less: "directory_next_record_in_bounds", Greater: "directory_reject", Name: "NEXT_RECORD_BUFFER_BOUND")
        };
        foreach (var comparison in comparisons)
        {
            string pattern = $@"(?m)^\s*IntCmpU {System.Text.RegularExpressions.Regex.Escape(comparison.Left)} {System.Text.RegularExpressions.Regex.Escape(comparison.Right)} (\S+) (\S+) (\S+)\s*$";
            var match = System.Text.RegularExpressions.Regex.Match(body, pattern);
            Assert.IsTrue(match.Success, $"INTCMPU_CASE_MISSING:{comparison.Name}");
            CollectionAssert.AreEqual(new[] { comparison.Equal, comparison.Less, comparison.Greater },
                new[] { match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value },
                $"INTCMPU_BRANCH_SEMANTICS_WRONG:{comparison.Name}");
        }

        string installer = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        Assert.IsFalse(installer.Contains("Call ValidateEmptyFixedRootDirectory", StringComparison.Ordinal) ||
            installer.Contains("Call un.ValidateEmptyFixedRootDirectory", StringComparison.Ordinal),
            "READ_ONLY_EMPTY_ROOT_GUARD_MUST_REMAIN_UNWIRED");
    }

    [TestMethod]
    public void Failed_native_close_keeps_lease_reachable_for_paired_retry()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        static string FunctionBody(string source, string name)
        {
            int begin = source.IndexOf("Function ${PREFIX}" + name, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, begin, name + "_MISSING");
            int end = source.IndexOf("FunctionEnd", begin, StringComparison.Ordinal);
            Assert.IsGreaterThan(begin, end, name + "_UNTERMINATED");
            return source[begin..end];
        }

        string product = FunctionBody(guard, "OpenProductIdentityLease");
        StringAssert.Contains(product,
            "CloseHandle(p $GuardHandle) i.r0'\n    StrCmp $0 0 product_open_done\n    StrCpy $GuardHandle 0",
            "PRODUCT_CLOSE_FAILURE_MUST_KEEP_HANDLE");
        string checkedLease = FunctionBody(guard, "OpenCheckedIdentityLease");
        StringAssert.Contains(checkedLease,
            "CloseHandle(p r0) i.r1'\n    StrCmp $1 0 0 lease_return\n    StrCpy $GuardHandle $0",
            "CHECKED_CLOSE_FAILURE_MUST_TRANSFER_HANDLE");
        string ancestors = FunctionBody(guard, "PinExistingInstallAncestors");
        StringAssert.Contains(ancestors,
            "StrCmp $GuardHandle 0 pins_failure_classify\n        IntOp $0 $GuardPathPinCount * ${NSIS_PTR_SIZE}",
            "FAILED_ANCESTOR_HANDLE_MUST_BE_SAVED");
        StringAssert.Contains(ancestors, "Call ${PREFIX}ReleasePathPins");
        string root = FunctionBody(guard, "AcquireFixedRootOwnershipLease");
        StringAssert.Contains(root,
            "Call ${PREFIX}PinExistingInstallAncestors\n    StrCmp $SetupCode 0 0 root_release",
            "FAILED_ANCESTOR_RELEASE_MUST_REMAIN_RETRYABLE");
    }

    [TestMethod]
    public void Native_guard_uses_Nsis_escape_for_literal_double_quote()
    {
        string guard = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh"));
        Assert.IsFalse(guard.Contains("'$\"'", StringComparison.Ordinal),
            "NSIS_LITERAL_QUOTE_MUST_USE_BACKSLASH_ESCAPE");
        Assert.AreEqual(4, guard.Split("'$\\\"'", StringSplitOptions.None).Length - 1,
            "NSIS_LITERAL_QUOTE_GUARD_COUNT_CHANGED");
    }

    [TestMethod]
    public async Task Contract_is_imported_and_check_only_rejects_untrusted_synthetic_payload()
    {
        using var f = new SetupFixture();
        var contract = await RunPowerShellAsync("-Command", "$c=Import-PowerShellDataFile -LiteralPath '" + f.Contract.Replace("'", "''") + "'; $c | ConvertTo-Json -Compress");
        Assert.AreEqual(0, contract.ExitCode, contract.Stderr);
        using var json = JsonDocument.Parse(contract.Stdout);
        Assert.AreEqual("GitHubBackup-setup.exe", json.RootElement.GetProperty("InstallerFile").GetString());
        Assert.AreEqual(0x8664, json.RootElement.GetProperty("NativeMachine").GetInt32());
        f.WriteMinimalPayload();
        var result = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "SETUP_INPUT_RECEIPT_INVALID");
        f.AssertNoBuildOutputs();
    }

    [TestMethod]
    public async Task PackagingInputCases_reject_extra_file_empty_payload_and_existing_output_without_side_effects()
    {
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        f.WriteFile(Path.Combine(f.Payload, "extra.dll"), [1]);
        var extra = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, extra.ExitCode);
        StringAssert.Contains(extra.Stderr, "SETUP_PAYLOAD_LAYOUT_INVALID");
        f.AssertNoBuildOutputs();

        f.DeleteFile(Path.Combine(f.Payload, "extra.dll"));
        f.WriteFile(Path.Combine(f.Payload, "extra.ps1"), [1]);
        var script = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, script.ExitCode);
        StringAssert.Contains(script.Stderr, "SETUP_PAYLOAD_LAYOUT_INVALID");
        f.AssertNoBuildOutputs();
        f.DeleteFile(Path.Combine(f.Payload, "extra.ps1"));
        f.DeleteFile(Path.Combine(f.Payload, "GitHubBackup.exe"));
        f.WriteFile(Path.Combine(f.Payload, "GitHubBackup.exe"), []);
        var empty = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, empty.ExitCode);
        StringAssert.Contains(empty.Stderr, "SETUP_PAYLOAD_LAYOUT_INVALID");
        f.AssertNoBuildOutputs();

        f.WriteDirectory(f.Output);
        var existing = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, existing.ExitCode);
        StringAssert.Contains(existing.Stderr, "SETUP_OUTPUT_PATH_INVALID");
        Assert.IsFalse(Directory.Exists(f.Work));

        var overlap = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Payload, Path.Combine(f.Root, "fresh-output"));
        Assert.AreNotEqual(0, overlap.ExitCode);
        StringAssert.Contains(overlap.Stderr, "SETUP_OUTPUT_PATH_INVALID");
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Root, "fresh-output")));
    }

    [TestMethod]
    public async Task PackagingInputCases_reject_receipt_hash_mismatch_without_side_effects()
    {
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        f.ReplaceFile(f.Receipt, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schema = 1,
            sourceCommit = new string('a', 40),
            appSourceTree = new string('A', 40),
            sourceFiles = Array.Empty<object>(),
            payloadSha256 = new string('0', 64),
            appFileVersion = "1.0.0.0",
            payloadKind = "internal-unsigned",
            buildEvidence = "docs/verification/setup-installer.md"
        })));
        var result = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "SETUP_INPUT_RECEIPT_INVALID");
        f.AssertNoBuildOutputs();
    }

    [TestMethod]
    public async Task PackagingInputCases_reject_missing_tool_provenance_before_payload_acceptance()
    {
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        f.WriteSyntheticReceipt();
        var result = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "SETUP_TOOL_RECEIPT_INVALID");
        f.AssertNoBuildOutputs();
    }

    [TestMethod]
    public async Task PackagingInputCases_require_repository_sha1_tree_length()
    {
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        f.WriteSyntheticReceipt(treeLength: 64);
        var result = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "SETUP_INPUT_RECEIPT_INVALID");
        f.AssertNoBuildOutputs();
    }

    [TestMethod]
    public async Task PackagingInputCases_reject_source_manifest_mismatch_and_missing_build_evidence()
    {
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        f.WriteSyntheticReceipt(corruptSourceHash: true);
        var source = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, source.ExitCode);
        StringAssert.Contains(source.Stderr, "SETUP_SOURCE_MANIFEST_INVALID");
        f.AssertNoBuildOutputs();

        f.WriteSyntheticReceipt();
        f.DeleteFile(f.Evidence);
        var evidence = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, evidence.ExitCode);
        StringAssert.Contains(evidence.Stderr, "SETUP_BUILD_EVIDENCE_MISSING");
        f.AssertNoBuildOutputs();
    }

    [TestMethod]
    public async Task PackagingInputCases_reject_dot_local_path_before_opening_it()
    {
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        string forbiddenWork = Path.Combine(f.Root, ".local", "work");
        var result = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, forbiddenWork, f.Output);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "SETUP_PATH_INVALID");
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Root, ".local")));
        f.AssertNoBuildOutputs();
    }

    [TestMethod]
    public async Task PackagingInputCases_reject_missing_tool_component_without_running_it()
    {
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        f.WriteSyntheticReceipt();
        f.WriteDirectory(Path.Combine(f.Nsis, "Include"));
        f.WriteFile(Path.Combine(f.Nsis, "makensis.exe"), [1]);
        f.WriteFile(Path.Combine(f.Nsis, "Include", "MUI2.nsh"), [1]);
        f.ReplaceFile(f.ToolReceipt, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            version = "3.12",
            officialReleaseUrl = "https://sourceforge.net/projects/nsis/files/NSIS%203/3.12/nsis-3.12.zip/download",
            archiveProvenance = "synthetic test value, never approved",
            archiveSha256 = "56581F90DB321581C5381193D796FFFCF2D24B2F8FED2160A6C6A3BAA67F2C4F",
            files = new[]
            {
                new { relativePath = "makensis.exe", sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1])) },
                new { relativePath = "Include/MUI2.nsh", sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1])) },
                new { relativePath = "Plugins/x86-unicode/System.dll", sha256 = new string('B', 64) }
            }
        })));
        var result = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "SETUP_TOOL_FILE_INVALID");
        f.AssertNoBuildOutputs();
    }

    [TestMethod]
    public async Task PortableNsis_accepts_versioned_Nsis_exe_when_makensis_has_no_version()
    {
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        f.WriteSyntheticReceipt();
        WriteSyntheticToolTree(f);

        Assert.IsNull(FileVersionInfo.GetVersionInfo(Path.Combine(f.Nsis, "makensis.exe")).FileVersion);
        StringAssert.StartsWith(FileVersionInfo.GetVersionInfo(Path.Combine(f.Nsis, "NSIS.exe")).FileVersion!, "3.12");

        var result = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "SETUP_PAYLOAD_VERSION_INVALID", result.Stderr);
        f.AssertNoBuildOutputs();
    }

    [TestMethod]
    public async Task PortableNsis_rejects_missing_versioned_exe_without_build_outputs()
    {
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        f.WriteSyntheticReceipt();
        WriteSyntheticToolTree(f);
        f.DeleteFile(Path.Combine(f.Nsis, "NSIS.exe"));

        var result = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "SETUP_TOOL_FILE_INVALID");
        f.AssertNoBuildOutputs();
    }

    [TestMethod]
    public async Task PortableNsis_rejects_wrong_version_without_build_outputs()
    {
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        f.WriteSyntheticReceipt();
        WriteSyntheticToolTree(f, "3.11");

        var result = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "SETUP_TOOL_VERSION_INVALID");
        f.AssertNoBuildOutputs();
    }

    [TestMethod]
    public async Task PortableNsis_rejects_tool_tree_not_covered_by_manifest_without_build_outputs()
    {
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        f.WriteSyntheticReceipt();
        WriteSyntheticToolTree(f);
        f.WriteFile(Path.Combine(f.Nsis, "unlisted.dll"), [4]);

        var result = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "SETUP_TOOL_COMPOSITION_INVALID");
        f.AssertNoBuildOutputs();
    }

    private static void WriteSyntheticToolTree(SetupFixture f, string nsisVersion = "3.12")
    {
        f.WriteDirectory(Path.Combine(f.Nsis, "Include"));
        f.WriteDirectory(Path.Combine(f.Nsis, "Plugins"));
        f.WriteDirectory(Path.Combine(f.Nsis, "Plugins", "x86-unicode"));
        f.WriteFile(Path.Combine(f.Nsis, "makensis.exe"), [1]);
        string versioned = Path.Combine(f.Nsis, "NSIS.exe");
        f.WriteFile(versioned, File.ReadAllBytes(typeof(SetupPackagingTests).Assembly.Location));
        using (var stream = new MemoryStream())
        {
            using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
            void Align4() { while (stream.Position % 4 != 0) writer.Write((byte)0); }
            void Block(string key, ushort valueLength, ushort type, Action contents)
            {
                long start = stream.Position;
                writer.Write((ushort)0); writer.Write(valueLength); writer.Write(type);
                writer.Write(Encoding.Unicode.GetBytes(key + "\0"));
                Align4();
                contents();
                Align4();
                long end = stream.Position;
                stream.Position = start;
                writer.Write(checked((ushort)(end - start)));
                stream.Position = end;
            }
            uint versionPair = 0x00030000u | uint.Parse(nsisVersion.AsSpan(2));
            Block("VS_VERSION_INFO", 52, 0, () =>
            {
                foreach (uint value in new uint[]
                {
                    0xFEEF04BD, 0x00010000, versionPair, 0, versionPair, 0,
                    0x3F, 0, 0x00040004, 1, 0, 0, 0
                }) writer.Write(value);
                Block("StringFileInfo", 0, 1, () => Block("040904B0", 0, 1, () =>
                {
                    foreach (string key in new[] { "FileVersion", "ProductVersion" })
                        Block(key, checked((ushort)(nsisVersion.Length + 1)), 1,
                            () => writer.Write(Encoding.Unicode.GetBytes(nsisVersion + "\0")));
                }));
                Block("VarFileInfo", 0, 1, () => Block("Translation", 4, 0, () => writer.Write(0x04B00409u)));
            });
            byte[] resource = stream.ToArray();
            IntPtr handle = BeginUpdateResource(versioned, true);
            Assert.AreNotEqual(IntPtr.Zero, handle, "Unable to create synthetic version resource.");
            bool updated = UpdateResource(handle, (IntPtr)16, (IntPtr)1, 0x0409, resource, (uint)resource.Length);
            bool committed = EndUpdateResource(handle, !updated);
            Assert.IsTrue(updated && committed, "Unable to commit synthetic version resource.");
        }
        f.RefreshGeneratedFileIdentity(versioned);
        f.WriteFile(Path.Combine(f.Nsis, "Include", "MUI2.nsh"), [2]);
        f.WriteFile(Path.Combine(f.Nsis, "Plugins", "x86-unicode", "System.dll"), [3]);
        string[] names = ["makensis.exe", "NSIS.exe", @"Include\MUI2.nsh", @"Plugins\x86-unicode\System.dll"];
        f.ReplaceFile(f.ToolReceipt, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            version = "3.12",
            officialReleaseUrl = "https://sourceforge.net/projects/nsis/files/NSIS%203/3.12/nsis-3.12.zip/download",
            archiveProvenance = "synthetic test tree",
            archiveSha256 = "56581F90DB321581C5381193D796FFFCF2D24B2F8FED2160A6C6A3BAA67F2C4F",
            files = names.Select(name => new
            {
                relativePath = name,
                sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(f.Nsis, name))))
            }).ToArray()
        })));
    }

    [TestMethod]
    public async Task Signed_receipt_requires_nested_evidence_and_can_reach_tool_gate()
    {
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        f.WriteSyntheticReceipt();
        var receipt = JsonNode.Parse(File.ReadAllText(f.Receipt))!.AsObject();
        receipt["payloadKind"] = "public-signed";
        f.ReplaceFile(f.Receipt, Encoding.UTF8.GetBytes(receipt.ToJsonString()));
        var missing = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, missing.ExitCode);
        StringAssert.Contains(missing.Stderr, "SETUP_SIGNED_PAYLOAD_INVALID");
        f.AssertNoBuildOutputs();

        var evidence = JsonNode.Parse(File.ReadAllText(f.Evidence))!.AsObject();
        evidence["signing"] = new JsonObject
        {
            ["schema"] = 1,
            ["preSignSha256"] = new string('A', 64),
            ["postSignSha256"] = receipt["payloadSha256"]!.GetValue<string>(),
            ["signerThumbprint"] = new string('B', 40),
            ["timestampStatus"] = "Valid",
            ["approvalReference"] = "synthetic test reference"
        };
        f.ReplaceFile(f.Evidence, Encoding.UTF8.GetBytes(evidence.ToJsonString()));
        var structured = await RunCheckOnlyAsync(f.Payload, f.Receipt, f.Nsis, f.ToolReceipt, f.Work, f.Output);
        Assert.AreNotEqual(0, structured.ExitCode);
        StringAssert.Contains(structured.Stderr, "SETUP_TOOL_RECEIPT_INVALID");
        f.AssertNoBuildOutputs();
    }

    [TestMethod]
    public async Task Path_arguments_preserve_Chinese_spaces_and_Nsis_metacharacters()
    {
        const string inputPath = @"D:\合成 测试\$NSIS!\payload";
        var result = await RunPowerShellAsync("-CommandWithArgs", "$args[0] | ConvertTo-Json -Compress", inputPath);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        string? decodedPath = JsonSerializer.Deserialize<string>(result.Stdout);
        Assert.AreEqual(inputPath, decodedPath);
    }

    [TestMethod]
    [DataRow("internal-unsigned")]
    [DataRow("public-signed")]
    public async Task Include_generator_binds_lossless_inputs_and_fixed_native_guard_contract(string payloadKind)
    {
        using var f = new SetupFixture();
        string app = Path.Combine(f.Root, "中文 路径", "GitHubBackup.exe");
        string notice = Path.Combine(f.Root, "中文 路径", "NOTICE.txt");
        string setup = Path.Combine(f.Root, "dist", "GitHubBackup-setup.exe");
        string uninstaller = Path.Combine(f.Root, "work", "Uninstall.exe");
        string exportOutput = Path.Combine(f.Root, "work", "UninstallerExport.exe");
        var result = await RunPowerShellAsync("-CommandWithArgs", """
            $ast=[Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$null,[ref]$null)
            $fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'New-SetupIncludeLines'},$true)
            if(-not $fn){throw 'FUNCTION_MISSING'}
            . ([ScriptBlock]::Create($fn.Extent.Text))
            $product=Import-PowerShellDataFile -LiteralPath $args[4]
            $lines=@(New-SetupIncludeLines $args[1] $args[2] $args[3] '1.2.3.4' ('A'*64) ('b'*40) $product ('C'*64) $args[5] $args[6] ('D'*64) $args[7])
            ConvertTo-Json -InputObject $lines -Compress
            """, f.BuildScript, app, notice, setup, f.Contract, payloadKind, uninstaller, exportOutput);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.AreEqual(19, json.RootElement.GetArrayLength());
        Assert.AreEqual("!define SETUP_APP_FILE \"" + app + "\"", json.RootElement[0].GetString());
        Assert.AreEqual("!define SETUP_NOTICE_FILE \"" + notice + "\"", json.RootElement[1].GetString());
        Assert.AreEqual("!define SETUP_OUTPUT_FILE \"" + setup + "\"", json.RootElement[2].GetString());
        Assert.AreEqual("!define SETUP_APP_VERSION \"1.2.3.4\"", json.RootElement[3].GetString());
        Assert.AreEqual("!define SETUP_APP_SHA256 \"" + new string('A', 64) + "\"", json.RootElement[4].GetString());
        Assert.AreEqual("!define SETUP_SOURCE_COMMIT \"" + new string('b', 40) + "\"", json.RootElement[5].GetString());
        Assert.AreEqual("!define SETUP_INSTALL_SUFFIX \"Programs\\GitHubBackupTool\"", json.RootElement[6].GetString());
        Assert.AreEqual("!define SETUP_SUPPORTED_BUILD 26200", json.RootElement[7].GetString());
        Assert.AreEqual("!define SETUP_NATIVE_MACHINE 34404", json.RootElement[8].GetString());
        Assert.AreEqual("!define SETUP_PRODUCT_ID \"GitHubBackupTool\"", json.RootElement[9].GetString());
        Assert.AreEqual("!define SETUP_APP_NAME \"GitHubBackup.exe\"", json.RootElement[10].GetString());
        Assert.AreEqual("!define SETUP_UNINSTALLER_NAME \"Uninstall.exe\"", json.RootElement[11].GetString());
        Assert.AreEqual("!define SETUP_UNINSTALLER_FILE \"" + uninstaller + "\"", json.RootElement[12].GetString());
        Assert.AreEqual("!define SETUP_UNINSTALLER_SHA256 \"" + new string('D', 64) + "\"", json.RootElement[13].GetString());
        Assert.AreEqual("!define SETUP_EXPORT_OUTPUT_FILE \"" + exportOutput + "\"", json.RootElement[14].GetString());
        Assert.AreEqual("!define SETUP_RECEIPT_NAME \"install.ini\"", json.RootElement[15].GetString());
        Assert.AreEqual("!define SETUP_NOTICE_NAME \"NOTICE.txt\"", json.RootElement[16].GetString());
        Assert.AreEqual("!define SETUP_NOTICE_SHA256 \"" + new string('C', 64) + "\"", json.RootElement[17].GetString());
        Assert.AreEqual("!define SETUP_PAYLOAD_KIND \"" + payloadKind + "\"", json.RootElement[18].GetString());
        Assert.IsFalse(File.ReadAllText(f.Contract).Contains("RegistryKey", StringComparison.Ordinal),
            "UNINSTALL_REGISTRY_IS_OUTSIDE_APPROVED_V1_SCOPE");
        Assert.IsFalse(File.ReadAllText(f.BuildScript).Contains("SETUP_REGISTRY_KEY", StringComparison.Ordinal),
            "BUILD_INCLUDE_MUST_NOT_EMIT_UNINSTALL_REGISTRY_KEY");
    }

    [TestMethod]
    public async Task Prebuilt_uninstaller_export_then_import_uses_only_an_isolated_synthetic_build()
    {
        string? nsisRoot = Environment.GetEnvironmentVariable("GITHUBBACKUP_NSIS_ROOT");
        if (string.IsNullOrWhiteSpace(nsisRoot) || !File.Exists(Path.Combine(nsisRoot, "makensis.exe")))
            Assert.Inconclusive("Set GITHUBBACKUP_NSIS_ROOT to an already verified portable NSIS 3.12 folder.");

        using var f = new SetupFixture();
        f.WriteDirectory(f.Work);
        string temp = Path.Combine(f.Work, "temp");
        string appData = Path.Combine(f.Work, "appdata");
        f.WriteDirectory(temp);
        f.WriteDirectory(appData);

        string harnessSource = Path.Combine(RepoRoot(), "tests", "GitHubBackup.App.Tests", "Fixtures", "SetupUninstallerExportHarness.nsi");
        string harness = Path.Combine(f.Work, "SetupUninstallerExportHarness.nsi");
        f.WriteFile(harness, File.ReadAllBytes(harnessSource));
        string input = Path.Combine(f.Work, "SetupInputs.nsh");
        f.WriteFile(input, Encoding.UTF8.GetBytes(string.Join("\n", new[]
        {
            "!define SETUP_UNINSTALLER_FILE \"" + Path.Combine(f.Work, "Uninstall.exe") + "\"",
            "!define SETUP_EXPORT_OUTPUT_FILE \"" + Path.Combine(f.Work, "UninstallerExport.exe") + "\"",
            "!define SETUP_OUTPUT_FILE \"" + Path.Combine(f.Work, "ImportedSetup.exe") + "\""
        }) + "\n"));

        var result = await RunPowerShellAsync("-CommandWithArgs", """
            $ast=[Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$null,[ref]$null)
            foreach($name in @('Fail','SafePath','UninstallerPeInfo','New-SetupCompilerArguments','Invoke-NsisCompilePass')){
                $fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
                if(-not $fn){throw "FUNCTION_MISSING_$name"}
                . ([ScriptBlock]::Create($fn.Extent.Text))
            }
            $variableNames=@('NSISDIR','NSISCONFDIR','TEMP','TMP','APPDATA')
            $before=@($variableNames | ForEach-Object { [Environment]::GetEnvironmentVariable($_) })
            $exportArgs=@(New-SetupCompilerArguments 'EXPORT_UNINST' $args[4] $args[3])
            Invoke-NsisCompilePass $args[1] $args[2] $args[5] $exportArgs 'SETUP_UNINSTALLER_EXPORT_FAILED'
            $uninstaller=Join-Path $args[5] 'Uninstall.exe'
            if(-not [IO.File]::Exists($uninstaller) -or ([IO.FileInfo]$uninstaller).Length -lt 256){throw 'SYNTHETIC_UNINSTALLER_EXPORT_MISSING'}
            UninstallerPeInfo $uninstaller
            $beforeHash=(Get-FileHash -LiteralPath $uninstaller -Algorithm SHA256).Hash
            $importArgs=@(New-SetupCompilerArguments 'IMPORT_UNINST' $args[4] $args[3])
            Invoke-NsisCompilePass $args[1] $args[2] $args[5] $importArgs 'SETUP_COMPILER_FAILED'
            if(-not [IO.File]::Exists((Join-Path $args[5] 'ImportedSetup.exe'))){throw 'SYNTHETIC_IMPORT_BUILD_MISSING'}
            if((Get-FileHash -LiteralPath $uninstaller -Algorithm SHA256).Hash -cne $beforeHash){throw 'SYNTHETIC_UNINSTALLER_CHANGED'}
            $after=@($variableNames | ForEach-Object { [Environment]::GetEnvironmentVariable($_) })
            for($i=0;$i -lt $variableNames.Count;$i++){if($before[$i] -cne $after[$i]){throw 'COMPILER_ENVIRONMENT_NOT_RESTORED'}}
            [pscustomobject]@{Status='PASS';UninstallerSha256=$beforeHash;UninstallerLength=([IO.FileInfo]$uninstaller).Length;OutputDirectory=$args[5]} | ConvertTo-Json -Compress
            """, f.BuildScript, Path.Combine(nsisRoot, "makensis.exe"), nsisRoot, harness, input, f.Work);
        foreach (string name in new[] { "Uninstall.exe", "UninstallerExport.exe", "ImportedSetup.exe" })
        {
            string generated = Path.Combine(f.Work, name);
            if (File.Exists(generated)) f.AdoptGeneratedFile(generated);
        }
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        string jsonLine = result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Last();
        using var json = JsonDocument.Parse(jsonLine);
        Assert.AreEqual("PASS", json.RootElement.GetProperty("Status").GetString());
        Assert.AreEqual(f.Work, json.RootElement.GetProperty("OutputDirectory").GetString());
    }

    [TestMethod]
    public async Task Uninstaller_validation_rejects_directory_and_non_pe_synthetic_artifacts()
    {
        using var f = new SetupFixture();
        f.WriteDirectory(f.Work);
        string directory = Path.Combine(f.Work, "UninstallDirectory.exe");
        f.WriteDirectory(directory);
        string nonPe = Path.Combine(f.Work, "UninstallNotPe.exe");
        f.WriteFile(nonPe, Encoding.UTF8.GetBytes("not an executable"));

        var result = await RunPowerShellAsync("-CommandWithArgs", """
            $ast=[Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$null,[ref]$null)
            foreach($name in @('Fail','UninstallerPeInfo')){
                $fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
                if(-not $fn){throw "FUNCTION_MISSING_$name"}
                . ([ScriptBlock]::Create($fn.Extent.Text))
            }
            function Expect-Code($path,$expected){
                try { UninstallerPeInfo $path; throw "UNINSTALLER_INPUT_ACCEPTED_$expected" }
                catch { if($_.Exception.Message -cne $expected){throw "UNEXPECTED_ERROR_$($_.Exception.Message)"} }
            }
            Expect-Code $args[1] 'SETUP_UNINSTALLER_FILE_INVALID'
            Expect-Code $args[2] 'SETUP_UNINSTALLER_PE_INVALID'
            [pscustomobject]@{Status='PASS'} | ConvertTo-Json -Compress
            """, f.BuildScript, directory, nonPe);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        StringAssert.Contains(result.Stdout, "\"Status\":\"PASS\"");

        string build = File.ReadAllText(f.BuildScript);
        int validator = build.IndexOf("function UninstallerPeInfo", StringComparison.Ordinal);
        int reparseCheck = build.IndexOf("[IO.FileAttributes]::ReparsePoint", validator, StringComparison.Ordinal);
        int peOpen = build.IndexOf("[IO.File]::OpenRead($file)", validator, StringComparison.Ordinal);
        Assert.IsTrue(validator >= 0 && reparseCheck > validator && peOpen > reparseCheck,
            "UNINSTALLER_REPARSE_POINT_MUST_BE_REJECTED_BEFORE_PE_READ");
    }

    [TestMethod]
    public void Build_script_exports_and_hashes_the_uninstaller_before_import_pass()
    {
        string build = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "Build-Setup.ps1"));
        int exportArguments = build.IndexOf("New-SetupCompilerArguments 'EXPORT_UNINST'", StringComparison.Ordinal);
        int exportPass = build.IndexOf("Invoke-NsisCompilePass", exportArguments, StringComparison.Ordinal);
        int exportCheck = build.IndexOf("UninstallerPeInfo $uninstaller", exportPass, StringComparison.Ordinal);
        int hash = build.IndexOf("$uninstallerHash = Sha $uninstaller", exportCheck, StringComparison.Ordinal);
        int importArguments = build.IndexOf("New-SetupCompilerArguments 'IMPORT_UNINST'", hash, StringComparison.Ordinal);
        int importPass = build.IndexOf("Invoke-NsisCompilePass", importArguments, StringComparison.Ordinal);
        int finalHash = build.IndexOf("Sha $uninstaller", importPass, StringComparison.Ordinal);
        Assert.IsTrue(exportArguments >= 0 && exportPass > exportArguments && exportCheck > exportPass &&
            hash > exportCheck && importArguments > hash && importPass > importArguments && finalHash > importPass,
            "EXPORT_MUST_BE_VERIFIED_AND_HASHED_BEFORE_IMPORT_PASS");
        StringAssert.Contains(build, "New-SetupCompilerArguments 'EXPORT_UNINST'");
        StringAssert.Contains(build, "New-SetupCompilerArguments 'IMPORT_UNINST'");
        StringAssert.Contains(build, "$env:TEMP = $isolatedTemp");
        StringAssert.Contains(build, "$env:TMP = $isolatedTemp");
        StringAssert.Contains(build, "SETUP_UNINSTALLER_HASH_CHANGED");
        AssertManagedLifecycleDelegation(File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi")));
    }

    [TestMethod]
    public void Wrapper_stages_only_private_inputs_and_runs_the_management_entry_points()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        StringAssert.Contains(source, "!ifdef EXPORT_UNINST");
        StringAssert.Contains(source, "!ifdef IMPORT_UNINST");
        StringAssert.Contains(source, "!uninstfinalize");
        StringAssert.Contains(source, "File /oname=${SETUP_APP_NAME} \"${SETUP_APP_FILE}\"");
        StringAssert.Contains(source, "File /oname=${SETUP_UNINSTALLER_NAME} \"${SETUP_UNINSTALLER_FILE}\"");
        StringAssert.Contains(source, "File /oname=${SETUP_NOTICE_NAME} \"${SETUP_NOTICE_FILE}\"");
        StringAssert.Contains(source, "File /oname=SetupManifest.json \"${SETUP_MANIFEST_FILE}\"");
        StringAssert.Contains(source, "SetOutPath \"$PLUGINSDIR\"");
        StringAssert.Contains(source, "Call PreparePrivatePluginDirectory");
        StringAssert.Contains(source, "Call un.PreparePrivatePluginDirectory");
        StringAssert.Contains(source, "DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        StringAssert.Contains(source, "$PLUGINSDIR\\.net");
        StringAssert.Contains(source, "--setup-install \"$PLUGINSDIR\\SetupManifest.json\" \"${SETUP_MANIFEST_SHA256}\"");
        StringAssert.Contains(source, "--setup-uninstall");
        StringAssert.Contains(source, "GetFullPathName $0 \"$EXEPATH\"");
        StringAssert.Contains(source, "SETUP_UNINSTALLER_MUST_SELF_COPY");
        int releaseStart = source.IndexOf("Function ${PREFIX}ReleasePrivatePluginDirectory", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, releaseStart);
        int releaseEnd = source.IndexOf("FunctionEnd", releaseStart, StringComparison.Ordinal);
        Assert.IsGreaterThan(releaseStart, releaseEnd);
        string releaseBody = source[releaseStart..releaseEnd];
        int leaveDirectory = releaseBody.IndexOf("SetOutPath \"$TEMP\"", StringComparison.Ordinal);
        int closeHandle = releaseBody.IndexOf("CloseHandle(p $WrapperPluginHandle)", StringComparison.Ordinal);
        Assert.IsTrue(leaveDirectory >= 0 && closeHandle > leaveDirectory,
            "Scratch cleanup must leave its current directory before releasing the held handle.");
        Assert.AreEqual(1, releaseBody.Split('\n').Count(line => line.Trim().StartsWith("SetOutPath ", StringComparison.Ordinal)));
        Assert.IsFalse(releaseBody.Split('\n').Any(line => line.Trim().StartsWith("File ", StringComparison.Ordinal)));
        foreach (string line in source.Remove(releaseStart, releaseEnd - releaseStart).Split('\n').Select(line => line.Trim()))
        {
            if (line.StartsWith("SetOutPath ", StringComparison.Ordinal))
                Assert.AreEqual("SetOutPath \"$PLUGINSDIR\"", line);
            if (line.StartsWith("ExecWait ", StringComparison.Ordinal))
            {
                Assert.AreEqual("ExecWait '${COMMAND}' $WrapperExitCode", line);
                Assert.IsFalse(line.Contains("_?=", StringComparison.Ordinal));
            }
            if (line.StartsWith("!insertmacro RunSetupEngine ", StringComparison.Ordinal))
            {
                StringAssert.Contains(line, "\"$PLUGINSDIR\\${SETUP_APP_NAME}\" --setup-");
                Assert.IsFalse(line.Contains("_?=", StringComparison.Ordinal));
            }
        }
        AssertManagedLifecycleDelegation(source);
    }

    private static void AssertManagedLifecycleDelegation(string source)
    {
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.IsFalse(source.Contains("SETUP_LIFECYCLE_NOT_IMPLEMENTED", StringComparison.Ordinal),
            "ACCEPTED_MANAGED_LIFECYCLE_MUST_NOT_RETAIN_THE_OLD_COMPILE_STOP");
        const string install = "!insertmacro RunSetupEngine \"\" '\"$PLUGINSDIR\\${SETUP_APP_NAME}\" --setup-install \"$PLUGINSDIR\\SetupManifest.json\" \"${SETUP_MANIFEST_SHA256}\"'";
        const string uninstall = "!insertmacro RunSetupEngine \"un.\" '\"$PLUGINSDIR\\${SETUP_APP_NAME}\" --setup-uninstall'";
        CollectionAssert.AreEquivalent(new[] { install, uninstall }, source.Split('\n').Select(line => line.Trim())
            .Where(line => line.StartsWith("!insertmacro RunSetupEngine ", StringComparison.Ordinal)).ToArray(),
            "PRODUCT_ENTRY_ARGUMENTS_MUST_BE_EXACT_AND_CANNOT_ACCEPT_AN_ARBITRARY_TARGET");
        StringAssert.Contains(source, "!ifndef SETUP_MANIFEST_FILE\n        !error \"SETUP_MANIFEST_REQUIRED\"\n    !endif");
        StringAssert.Contains(source, "!ifndef SETUP_MANIFEST_SHA256\n        !error \"SETUP_MANIFEST_HASH_REQUIRED\"\n    !endif");
        foreach (var entry in new[] { (Section: "安装", Prefix: "", Command: install), (Section: "Uninstall", Prefix: "un.", Command: uninstall) })
        {
            int start = source.IndexOf("Section \"" + entry.Section + "\"", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, start);
            int end = source.IndexOf("SectionEnd", start, StringComparison.Ordinal);
            Assert.IsGreaterThan(start, end);
            string body = source[start..end];
            foreach (string guard in new[] { "ValidateHost", "ValidateDirectoryArguments" })
                StringAssert.Contains(body, "Call " + entry.Prefix + guard
                    + "\n    ${If} $SetupCode != 0\n        SetErrorLevel $SetupCode\n        Abort\n    ${EndIf}",
                    "HOST_AND_ARGUMENT_FAILURES_MUST_STOP_BEFORE_EXTRACTION_OR_ENGINE_LAUNCH");
            int host = body.IndexOf("Call " + entry.Prefix + "ValidateHost", StringComparison.Ordinal);
            int arguments = body.IndexOf("Call " + entry.Prefix + "ValidateDirectoryArguments", StringComparison.Ordinal);
            int prepare = body.IndexOf("Call " + entry.Prefix + "PreparePrivatePluginDirectory", StringComparison.Ordinal);
            int extract = body.IndexOf("SetOutPath \"$PLUGINSDIR\"", StringComparison.Ordinal);
            int launch = body.IndexOf(entry.Command, StringComparison.Ordinal);
            Assert.IsTrue(host >= 0 && arguments > host && prepare > arguments && extract > prepare && launch > extract,
                "EVERY_PRODUCT_ENTRY_MUST_VALIDATE_HOST_AND_FIXED_ARGUMENTS_THEN_PREPARE_PRIVATE_INPUTS_BEFORE_LAUNCH");
        }
    }

    [TestMethod]
    public void Wrapper_verifies_the_held_scratch_directory_owner_before_changing_its_permissions()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        int start = source.IndexOf("Function ${PREFIX}PreparePrivatePluginDirectory", StringComparison.Ordinal);
        string body = source[start..source.IndexOf("FunctionEnd", start, StringComparison.Ordinal)];
        int ownerRead = body.IndexOf("GetSecurityInfo(p $WrapperPluginHandle, i 1, i 1", StringComparison.Ordinal);
        int ownerCompare = body.IndexOf("StrCmpS $6 $SetupOwnerSid 0 wrapper_directory_done", StringComparison.Ordinal);
        int permissionWrite = body.IndexOf("SetKernelObjectSecurity(p $WrapperPluginHandle", StringComparison.Ordinal);
        Assert.IsTrue(ownerRead >= 0 && ownerCompare > ownerRead && permissionWrite > ownerCompare,
            "The exact held-object owner must match the current SID before its DACL can be changed.");
    }

    [TestMethod]
    public void Wrapper_reports_actionable_management_failures_without_requests_to_remove_user_data()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        StringAssert.Contains(source, "${If} $WrapperExitCode == 10");
        StringAssert.Contains(source, "仅支持 Windows 11 25H2 x64，请以普通用户运行，不要以管理员身份运行。");
        StringAssert.Contains(source, "${ElseIf} $WrapperExitCode == 11");
        StringAssert.Contains(source, "安装参数不受支持，安装位置固定，不能更改安装目录。");
        StringAssert.Contains(source, "${ElseIf} $WrapperExitCode == 12");
        StringAssert.Contains(source, "卸载不会删除备份、设置、日志或凭据。");
        StringAssert.Contains(source, "文件被占用，或安装位置、权限存在冲突。请先关闭 GitHub 备份程序后重试，无需删除备份。");
    }

    [TestMethod]
    public async Task Manifest_generator_binds_exact_fields_and_optional_include_keeps_legacy_inputs()
    {
        using var f = new SetupFixture();
        string manifest = Path.Combine(f.Root, "中文 路径", "SetupManifest.json");
        var result = await RunPowerShellAsync("-CommandWithArgs", """
            $ast=[Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$null,[ref]$null)
            foreach($name in @('Fail','New-SetupManifestJson','New-SetupIncludeLines')){
                $fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
                if(-not $fn){throw "FUNCTION_MISSING_$name"}
                . ([ScriptBlock]::Create($fn.Extent.Text))
            }
            $product=Import-PowerShellDataFile -LiteralPath $args[1]
            $json=New-SetupManifestJson '1.2.3.4' ('b'*40) ('A'*64) ('D'*64) ('C'*64)
            $legacy=@(New-SetupIncludeLines 'app' 'notice' 'output' '1.2.3.4' ('A'*64) ('b'*40) $product ('C'*64) 'internal-unsigned' 'uninstaller' ('D'*64) 'export')
            $current=@(New-SetupIncludeLines 'app' 'notice' 'output' '1.2.3.4' ('A'*64) ('b'*40) $product ('C'*64) 'internal-unsigned' 'uninstaller' ('D'*64) 'export' $args[2] ('E'*64))
            [pscustomobject]@{Manifest=($json|ConvertFrom-Json);Legacy=$legacy;Current=$current} | ConvertTo-Json -Depth 4 -Compress
            """, f.BuildScript, f.Contract, manifest);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        var record = json.RootElement.GetProperty("Manifest");
        CollectionAssert.AreEquivalent(new[] { "Version", "SourceCommit", "AppHash", "UninstallerHash", "NoticeHash" },
            record.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual("1.2.3.4", record.GetProperty("Version").GetString());
        Assert.AreEqual(new string('b', 40), record.GetProperty("SourceCommit").GetString());
        Assert.AreEqual(new string('A', 64), record.GetProperty("AppHash").GetString());
        Assert.AreEqual(new string('D', 64), record.GetProperty("UninstallerHash").GetString());
        Assert.AreEqual(new string('C', 64), record.GetProperty("NoticeHash").GetString());
        var legacy = json.RootElement.GetProperty("Legacy");
        var current = json.RootElement.GetProperty("Current");
        Assert.AreEqual(19, legacy.GetArrayLength());
        Assert.AreEqual(21, current.GetArrayLength());
        for (int i = 0; i < 19; i++) Assert.AreEqual(legacy[i].GetString(), current[i].GetString());
        Assert.AreEqual("!define SETUP_MANIFEST_FILE \"" + manifest + "\"", current[19].GetString());
        Assert.AreEqual("!define SETUP_MANIFEST_SHA256 \"" + new string('E', 64) + "\"", current[20].GetString());
        string build = File.ReadAllText(f.BuildScript);
        int exportHash = build.IndexOf("$uninstallerHash = Sha $uninstaller", StringComparison.Ordinal);
        int manifestWrite = build.IndexOf("New-SetupManifestJson $version", StringComparison.Ordinal);
        int manifestHash = build.IndexOf("$manifestHash = Sha $manifestFile", StringComparison.Ordinal);
        int importPass = build.IndexOf("New-SetupCompilerArguments 'IMPORT_UNINST'", StringComparison.Ordinal);
        Assert.IsTrue(exportHash >= 0 && manifestWrite > exportHash && manifestHash > manifestWrite && importPass > manifestHash);
    }

    [TestMethod]
    public async Task Wrapper_export_and_import_compile_only_with_synthetic_payloads()
    {
        string? nsisRoot = Environment.GetEnvironmentVariable("GITHUBBACKUP_NSIS_ROOT");
        if (string.IsNullOrWhiteSpace(nsisRoot) || !File.Exists(Path.Combine(nsisRoot, "makensis.exe")))
            Assert.Inconclusive("Set GITHUBBACKUP_NSIS_ROOT to an already verified portable NSIS 3.12 folder.");
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        f.WriteDirectory(f.Work);
        f.WriteDirectory(f.Output);
        f.WriteDirectory(Path.Combine(f.Work, "temp"));
        f.WriteDirectory(Path.Combine(f.Work, "appdata"));
        string notice = Path.Combine(f.Work, "NOTICE.txt");
        f.WriteFile(notice, Encoding.UTF8.GetBytes("synthetic wrapper notice"));
        string wrapper = Path.Combine(f.Work, "WrapperFixture.nsi");
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        AssertManagedLifecycleDelegation(source);
        // Compile the unchanged production source with inert payloads. Never run its EXEs.
        f.WriteFile(wrapper, File.ReadAllBytes(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi")));
        Assert.AreEqual(source, File.ReadAllText(wrapper));
        f.WriteFile(Path.Combine(f.Work, "SetupGuards.nsh"),
            File.ReadAllBytes(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh")));
        var result = await RunPowerShellAsync("-CommandWithArgs", """
            $ast=[Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$null,[ref]$null)
            foreach($name in @('Fail','SafePath','UninstallerPeInfo','New-SetupCompilerArguments','Invoke-NsisCompilePass','New-SetupIncludeLines','New-SetupManifestJson')){
                $fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
                if(-not $fn){throw "FUNCTION_MISSING_$name"}
                . ([ScriptBlock]::Create($fn.Extent.Text))
            }
            $product=Import-PowerShellDataFile -LiteralPath $args[7]
            $inputFile=Join-Path $args[4] 'SetupInputs.nsh'
            $uninstaller=Join-Path $args[4] 'Uninstall.exe'
            $export=Join-Path $args[4] 'UninstallerExport.exe'
            $manifest=Join-Path $args[4] 'SetupManifest.json'
            $output=Join-Path $args[8] 'GitHubBackup-setup.exe'
            $appHash=(Get-FileHash -LiteralPath $args[5] -Algorithm SHA256).Hash
            $noticeHash=(Get-FileHash -LiteralPath $args[6] -Algorithm SHA256).Hash
            $lines=@(New-SetupIncludeLines $args[5] $args[6] $output '1.2.3.4' $appHash ('b'*40) $product $noticeHash 'internal-unsigned' $uninstaller '' $export)
            [IO.File]::WriteAllLines($inputFile,$lines,[Text.UTF8Encoding]::new($false))
            $exportArgs=@(New-SetupCompilerArguments 'EXPORT_UNINST' $inputFile $args[3])
            Invoke-NsisCompilePass $args[1] $args[2] $args[4] $exportArgs 'SETUP_UNINSTALLER_EXPORT_FAILED'
            UninstallerPeInfo $uninstaller
            $uninstallerHash=(Get-FileHash -LiteralPath $uninstaller -Algorithm SHA256).Hash
            $manifestJson=New-SetupManifestJson '1.2.3.4' ('b'*40) $appHash $uninstallerHash $noticeHash
            [IO.File]::WriteAllText($manifest,$manifestJson,[Text.UTF8Encoding]::new($false))
            $manifestHash=(Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
            $lines=@(New-SetupIncludeLines $args[5] $args[6] $output '1.2.3.4' $appHash ('b'*40) $product $noticeHash 'internal-unsigned' $uninstaller $uninstallerHash $export $manifest $manifestHash)
            [IO.File]::WriteAllLines($inputFile,$lines,[Text.UTF8Encoding]::new($false))
            $importArgs=@(New-SetupCompilerArguments 'IMPORT_UNINST' $inputFile $args[3])
            Invoke-NsisCompilePass $args[1] $args[2] $args[4] $importArgs 'SETUP_COMPILER_FAILED'
            if(-not [IO.File]::Exists($output)){throw 'SYNTHETIC_WRAPPER_OUTPUT_MISSING'}
            if((Get-FileHash -LiteralPath $uninstaller -Algorithm SHA256).Hash -cne $uninstallerHash){throw 'SYNTHETIC_UNINSTALLER_CHANGED'}
            if((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -cne $manifestHash){throw 'SYNTHETIC_MANIFEST_CHANGED'}
            [pscustomobject]@{Status='PASS';UninstallerSha256=$uninstallerHash;ManifestSha256=$manifestHash} | ConvertTo-Json -Compress
            """, f.BuildScript, Path.Combine(nsisRoot, "makensis.exe"), nsisRoot, wrapper, f.Work,
            Path.Combine(f.Payload, "GitHubBackup.exe"), notice, f.Contract, f.Output);
        foreach (string name in new[] { "SetupInputs.nsh", "Uninstall.exe", "UninstallerExport.exe", "SetupManifest.json" })
        {
            string path = Path.Combine(f.Work, name);
            if (File.Exists(path)) f.AdoptGeneratedFile(path);
        }
        string output = Path.Combine(f.Output, "GitHubBackup-setup.exe");
        if (File.Exists(output)) f.AdoptGeneratedFile(output);
        Assert.AreEqual(0, result.ExitCode, result.Stderr + result.Stdout);
        StringAssert.Contains(result.Stdout, "\"Status\":\"PASS\"");
    }

    [TestMethod]
    [DataRow(true, 0)]
    [DataRow(false, 0)]
    [DataRow(true, 10)]
    [DataRow(true, 11)]
    [DataRow(true, 13)]
    [DataRow(false, 13)]
    public async Task Shared_wrapper_functions_protect_scratch_restore_environment_and_clean_up_with_a_fake_engine(bool existingBundleVariable, int engineExit)
    {
        string? nsisRoot = Environment.GetEnvironmentVariable("GITHUBBACKUP_NSIS_ROOT");
        if (string.IsNullOrWhiteSpace(nsisRoot) || !File.Exists(Path.Combine(nsisRoot, "makensis.exe")))
            Assert.Inconclusive("Set GITHUBBACKUP_NSIS_ROOT to an already verified portable NSIS 3.12 folder.");
        using var f = new SetupFixture();
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var user = identity.User!;
        // Create a new private sandbox with explicit owner/permissions instead
        // of changing the enclosing fixture's inherited owner or permissions.
        string runtimeRoot = Path.Combine(f.Root, "runtime");
        GitHubBackup.App.AclPolicy.CreateRestrictedDirectory(runtimeRoot, user, requireNew: true);
        f.AdoptGeneratedDirectory(runtimeRoot);
        f.WriteDirectory(f.Work);
        f.WriteDirectory(Path.Combine(f.Work, "temp"));
        f.WriteDirectory(Path.Combine(f.Work, "appdata"));
        string launchTemp = Path.Combine(runtimeRoot, "launch-temp");
        string launchAppData = Path.Combine(runtimeRoot, "launch-appdata");
        f.WriteDirectory(launchTemp);
        f.WriteDirectory(launchAppData);
        string originalBundle = existingBundleVariable ? Path.Combine(runtimeRoot, "previous-bundle-base") : "";
        string wrapperSource = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        string macros = ExtractWrapperMacro(wrapperSource, "SetupWrapperFunctions") + "\n\n" + ExtractWrapperMacro(wrapperSource, "RunSetupEngine") + "\n";
        f.WriteFile(Path.Combine(f.Work, "WrapperMacros.nsh"), Encoding.UTF8.GetBytes(macros));
        Assert.AreEqual(macros, File.ReadAllText(Path.Combine(f.Work, "WrapperMacros.nsh")));
        f.WriteFile(Path.Combine(f.Work, "SetupGuards.nsh"), File.ReadAllBytes(Path.Combine(RepoRoot(), "publish", "Setup", "SetupGuards.nsh")));
        foreach (string name in new[] { "SetupWrapperHarness.nsi", "SetupWrapperFakeEngine.nsi" })
        {
            byte[] source = File.ReadAllBytes(Path.Combine(RepoRoot(), "tests", "GitHubBackup.App.Tests", "Fixtures", name));
            Assert.IsFalse(Encoding.UTF8.GetString(source).Contains("--setup-", StringComparison.Ordinal));
            Assert.IsFalse(Encoding.UTF8.GetString(source).Contains("Call ValidateHost", StringComparison.Ordinal));
            f.WriteFile(Path.Combine(f.Work, name), source);
        }
        string engine = Path.Combine(f.Work, "FakeEngine.exe");
        string harness = Path.Combine(f.Work, "WrapperHarness.exe");
        string inputFile = Path.Combine(f.Work, "SetupInputs.nsh");
        var compile = await RunPowerShellAsync("-CommandWithArgs", """
            $ast=[Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$null,[ref]$null)
            foreach($name in @('Fail','SafePath','New-SetupIncludeLines','Invoke-NsisCompilePass')){
                $fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
                if(-not $fn){throw "FUNCTION_MISSING_$name"}
                . ([ScriptBlock]::Create($fn.Extent.Text))
            }
            $product=Import-PowerShellDataFile -LiteralPath $args[3]
            $lines=@(New-SetupIncludeLines 'unused' 'unused' 'unused' '0.0.0.0' ('A'*64) ('b'*40) $product ('C'*64) 'internal-unsigned' 'unused' ('D'*64) 'unused')
            $lines+=('!define WRAPPER_FIXTURE_ROOT "{0}"' -f $args[5])
            $lines+=('!define WRAPPER_ENGINE_FILE "{0}"' -f $args[6])
            $lines+=('!define WRAPPER_HARNESS_OUTPUT "{0}"' -f $args[7])
            $lines+=('!define WRAPPER_INITIAL_BUNDLE "{0}"' -f $args[8])
            $lines+=('!define WRAPPER_ENGINE_EXIT {0}' -f $args[10])
            [IO.File]::WriteAllLines($args[4],$lines,[Text.UTF8Encoding]::new($false))
            Invoke-NsisCompilePass $args[1] $args[2] $args[9] @('/NOCONFIG','/V3',(Join-Path $args[9] 'SetupWrapperFakeEngine.nsi')) 'SETUP_FAKE_ENGINE_COMPILE_FAILED'
            Invoke-NsisCompilePass $args[1] $args[2] $args[9] @('/NOCONFIG','/V3',(Join-Path $args[9] 'SetupWrapperHarness.nsi')) 'SETUP_WRAPPER_HARNESS_COMPILE_FAILED'
            """, f.BuildScript, Path.Combine(nsisRoot, "makensis.exe"), nsisRoot, f.Contract,
            inputFile, runtimeRoot, engine, harness, originalBundle, f.Work, engineExit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (string path in new[] { inputFile, engine, harness })
            if (File.Exists(path)) f.AdoptGeneratedFile(path);
        Assert.AreEqual(0, compile.ExitCode, compile.Stderr + compile.Stdout);

        var start = new ProcessStartInfo(harness) { UseShellExecute = false, CreateNoWindow = true };
        start.Environment["TEMP"] = launchTemp;
        start.Environment["TMP"] = launchTemp;
        start.Environment["APPDATA"] = launchAppData;
        if (existingBundleVariable) start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = originalBundle;
        else start.Environment.Remove("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        string? parentBundle = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        using var process = Process.Start(start)!;
        string ready = Path.Combine(runtimeRoot, "engine-ready.flag");
        string release = Path.Combine(runtimeRoot, "release-engine.flag");
        string wrapperResult = Path.Combine(runtimeRoot, "wrapper-result.ini");
        string engineResult = Path.Combine(runtimeRoot, "engine-result.ini");
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(ready) && !process.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(25);
            Assert.IsTrue(File.Exists(ready), "The isolated fake engine did not become ready.");
            string plugin = ReadFixtureIni(wrapperResult, "PluginPath");
            Assert.IsTrue(plugin.StartsWith(launchTemp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            using (var directory = GitHubBackup.App.NativeFileSystem.Open(plugin))
            {
                GitHubBackup.App.NativeFileSystem.Inspect(directory, plugin, true);
                GitHubBackup.App.AclPolicy.VerifyRestricted(directory, user);
                Assert.AreEqual(user, GitHubBackup.App.NativeFileSystem.ReadSecurity(directory).Owner);
            }
            Assert.AreEqual(Path.Combine(plugin, ".net"), ReadFixtureIni(engineResult, "BundleBase"));
            Assert.AreEqual(launchTemp, ReadFixtureIni(engineResult, "Temp"));
            StringAssert.Contains(ReadFixtureIni(engineResult, "CommandLine"), "--wrapper-fixture");
            Assert.IsTrue(File.Exists(Path.Combine(plugin, ".net", "synthetic-runtime.bin")));
            f.WriteFile(release, "release"u8.ToArray());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(engineExit, process.ExitCode, "The wrapper must preserve the engine's nonzero failure code.");
            Assert.AreEqual(engineExit == 0 ? "PASS" : "FAIL", ReadFixtureIni(wrapperResult, "Status"));
            Assert.AreEqual("0", ReadFixtureIni(wrapperResult, "PluginHandle"));
            Assert.AreEqual(originalBundle, ReadFixtureIni(wrapperResult, "RestoredBundleBase"));
            Assert.IsFalse(Directory.Exists(plugin), "NSIS must remove its own plugin directory after the engine exits.");
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(launchTemp));
            Assert.AreEqual(parentBundle, Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"));
        }
        finally
        {
            // Release only the test handshake. Never kill a hung installer as cleanup.
            if (!File.Exists(release)) f.WriteFile(release, "release"u8.ToArray());
            if (!process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
            }
            foreach (string path in new[] { ready, wrapperResult, engineResult })
                if (File.Exists(path)) f.AdoptGeneratedFile(path);
            foreach (string directory in Directory.EnumerateDirectories(launchTemp, "ns*.tmp"))
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) f.AdoptGeneratedDirectory(directory);
        }
    }

    private static string ExtractWrapperMacro(string source, string name)
    {
        int start = source.IndexOf("!macro " + name + " ", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start);
        int end = source.IndexOf("!macroend", start, StringComparison.Ordinal);
        Assert.IsGreaterThan(start, end);
        return source[start..(end + "!macroend".Length)];
    }

    private static string ReadFixtureIni(string file, string key) =>
        File.ReadAllLines(file).FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..] ?? "";

    [TestMethod]
    public async Task Tool_stage_copies_locked_components_and_uses_only_staged_bytes()
    {
        using var f = new SetupFixture();
        f.WriteDirectory(f.Work);
        f.WriteDirectory(Path.Combine(f.Nsis, "Include"));
        f.WriteDirectory(Path.Combine(f.Nsis, "Plugins"));
        f.WriteDirectory(Path.Combine(f.Nsis, "Plugins", "x86-unicode"));
        var names = new[] { "makensis.exe", @"Include\MUI2.nsh", @"Plugins\x86-unicode\System.dll" };
        foreach (string name in names) f.WriteFile(Path.Combine(f.Nsis, name), [1, 2, 3]);
        var manifest = names.Select(name => new
        {
            relativePath = name,
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2, 3]))
        }).ToArray();
        f.ReplaceFile(f.ToolReceipt, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));
        var result = await RunPowerShellAsync("-CommandWithArgs", """
            $ast=[Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$null,[ref]$null)
            foreach($name in @('Fail','Sha','SafePath','Copy-VerifiedToolTree')){
                $fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
                if(-not $fn){throw "FUNCTION_MISSING_$name"}
                . ([ScriptBlock]::Create($fn.Extent.Text))
            }
            $entries=Get-Content -LiteralPath $args[3] -Raw | ConvertFrom-Json -AsHashtable
            Copy-VerifiedToolTree $args[1] $args[2] $entries
            """, f.BuildScript, f.Nsis, f.Work, f.ToolReceipt);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        string stagedRoot = Path.Combine(f.Work, "nsis");
        f.AdoptGeneratedDirectory(stagedRoot);
        f.AdoptGeneratedDirectory(Path.Combine(stagedRoot, "Include"));
        f.AdoptGeneratedDirectory(Path.Combine(stagedRoot, "Plugins"));
        f.AdoptGeneratedDirectory(Path.Combine(stagedRoot, "Plugins", "x86-unicode"));
        foreach (string name in names)
        {
            string staged = Path.Combine(stagedRoot, name);
            f.AdoptGeneratedFile(staged);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(staged));
        }
        f.ReplaceFile(Path.Combine(f.Nsis, "makensis.exe"), [9]);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(stagedRoot, "makensis.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(f.Work, "GitHubBackup-setup.exe")));
    }

    [TestMethod]
    public async Task FinalArtifactCases_reject_extra_file_and_missing_evidence_without_running_installer()
    {
        using var f = new SetupFixture();
        f.WriteDirectory(f.Output);
        f.WriteFile(Path.Combine(f.Output, "GitHubBackup-setup.exe"), [1]);
        f.WriteFile(Path.Combine(f.Output, "extra.txt"), [1]);
        var extra = await RunPowerShellAsync("-File", Path.Combine(RepoRoot(), "publish", "Setup", "Verify-Setup.ps1"),
            "-Directory", f.Output, "-EvidenceFile", f.Receipt);
        Assert.AreNotEqual(0, extra.ExitCode);
        StringAssert.Contains(extra.Stderr, "SETUP_VERIFY_LAYOUT_INVALID");
        f.DeleteFile(Path.Combine(f.Output, "extra.txt"));
        var malformed = await RunPowerShellAsync("-File", Path.Combine(RepoRoot(), "publish", "Setup", "Verify-Setup.ps1"),
            "-Directory", f.Output, "-EvidenceFile", f.Receipt, "-RequireSigned");
        Assert.AreNotEqual(0, malformed.ExitCode);
        StringAssert.Contains(malformed.Stderr, "SETUP_VERIFY_PE_INVALID");
    }

    [TestMethod]
    public async Task FinalArtifactCases_reject_installed_x86_app_despite_matching_hashes()
    {
        using var f = new SetupFixture();
        f.WriteMinimalPayload();
        f.WriteDirectory(f.Output);
        string installed = Path.Combine(f.Root, "installed");
        f.WriteDirectory(installed);
        byte[] setupBytes = File.ReadAllBytes(Path.Combine(f.Payload, "GitHubBackup.exe"));
        f.WriteFile(Path.Combine(f.Output, "GitHubBackup-setup.exe"), setupBytes);
        byte[] wrongApp = (byte[])setupBytes.Clone();
        wrongApp[0x84] = 0x4c; wrongApp[0x85] = 0x01;
        wrongApp[0x98] = 0x0b; wrongApp[0x99] = 0x01;
        f.WriteFile(Path.Combine(installed, "GitHubBackup.exe"), wrongApp);
        foreach (string name in new[] { "Uninstall.exe", "install.ini", "NOTICE.txt" })
            f.WriteFile(Path.Combine(installed, name), [1, 2, 3]);
        static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        string setupHash = Hash(Path.Combine(f.Output, "GitHubBackup-setup.exe"));
        string appHash = Hash(Path.Combine(installed, "GitHubBackup.exe"));
        f.ReplaceFile(f.Receipt, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schema = 1, setupSha256 = setupHash, appSha256 = appHash, signatureMode = "internal-unsigned",
            installerFile = "GitHubBackup-setup.exe", runtimeAcceptance = "PASS", runtimeSetupSha256 = setupHash,
            installedFiles = new[] { "GitHubBackup.exe", "Uninstall.exe", "install.ini", "NOTICE.txt" }
                .Select(name => new { name, sha256 = Hash(Path.Combine(installed, name)) }).ToArray()
        })));
        var result = await RunPowerShellAsync("-File", Path.Combine(RepoRoot(), "publish", "Setup", "Verify-Setup.ps1"),
            "-Directory", f.Output, "-EvidenceFile", f.Receipt, "-InstalledDirectory", installed);
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "SETUP_VERIFY_INSTALLED_APP_PE_INVALID");
    }

    [TestMethod]
    public void Fixture_rejects_replaced_file_before_overwrite_or_cleanup()
    {
        using var f = new SetupFixture();
        string path = Path.Combine(f.Payload, "owned.txt");
        f.WriteFile(path, [1]);
        File.Delete(path);
        File.WriteAllBytes(path, [2]);
        try
        {
            Assert.ThrowsExactly<IOException>(() => f.ReplaceFile(path, [3]));
            Assert.ThrowsExactly<IOException>(() => f.Dispose());
            CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(path));
        }
        finally
        {
            using var handle = GitHubBackup.App.NativeFileSystem.Open(path,
                GitHubBackup.App.NativeFileSystem.ReadControl | GitHubBackup.App.NativeFileSystem.ReadAttributes |
                GitHubBackup.App.NativeFileSystem.DeleteAccess | 1);
            GitHubBackup.App.NativeFileSystem.Inspect(handle, path, false);
            GitHubBackup.App.NativeFileSystem.DeleteByHandle(handle);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCheckOnlyAsync(string payload, string receipt, string nsis, string toolReceipt, string work, string output) =>
        await RunPowerShellAsync("-File", Path.Combine(RepoRoot(), "publish", "Setup", "Build-Setup.ps1"),
            "-PayloadDirectory", payload, "-InputReceipt", receipt, "-NsisRoot", nsis,
            "-ToolReceipt", toolReceipt, "-WorkDirectory", work, "-OutputDirectory", output, "-CheckOnly");

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPowerShellAsync(params string[] args)
    {
        Assert.IsTrue(File.Exists(PowerShell), "Confirmed PowerShell executable is unavailable.");
        var start = new ProcessStartInfo(PowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string RepoRoot([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    private sealed class SetupFixture : IDisposable
    {
        private readonly List<string> files = [];
        private readonly List<string> directories = [];
        private readonly Dictionary<string, GitHubBackup.App.NativeFileIdentity> identities = new(StringComparer.OrdinalIgnoreCase);
        public string Root { get; } = Path.Combine(RepoRoot(), "artifacts", "setup", "test-" + Guid.NewGuid().ToString("N"));
        public string Payload => Path.Combine(Root, "payload");
        public string Receipt => Path.Combine(Root, "input.json");
        public string Nsis => Path.Combine(Root, "nsis");
        public string ToolReceipt => Path.Combine(Root, "tool.json");
        public string Evidence => Path.Combine(Root, "build-evidence.json");
        public string Work => Path.Combine(Root, "work");
        public string Output => Path.Combine(Root, "dist");
        public string Contract => Path.Combine(RepoRoot(), "publish", "Setup", "SetupContract.psd1");
        public string BuildScript => Path.Combine(RepoRoot(), "publish", "Setup", "Build-Setup.ps1");

        public SetupFixture()
        {
            WriteDirectory(Root); WriteDirectory(Payload); WriteDirectory(Nsis);
            WriteFile(Receipt, Encoding.UTF8.GetBytes("{}"));
            WriteFile(ToolReceipt, Encoding.UTF8.GetBytes("{}"));
        }
        private string CheckedPath(string path)
        {
            string full = GitHubBackup.App.NativeFileSystem.CanonicalPath(path);
            string root = GitHubBackup.App.NativeFileSystem.CanonicalPath(Root);
            if (!string.Equals(full, path, StringComparison.OrdinalIgnoreCase) ||
                (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase) &&
                 !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("FIXTURE_PATH_OUTSIDE_ROOT");
            return full;
        }
        private void CheckIdentity(string path, GitHubBackup.App.NativeFileIdentity actual)
        {
            if (!identities.TryGetValue(path, out var expected) || actual != expected)
                throw new IOException("FIXTURE_IDENTITY_CHANGED");
        }
        private static GitHubBackup.App.NativeFileIdentity Inspect(string path, bool directory)
        {
            using var handle = GitHubBackup.App.NativeFileSystem.Open(path);
            return GitHubBackup.App.NativeFileSystem.Inspect(handle, path, directory);
        }
        private GitHubBackup.App.PathLease PinOwnedParents(string path)
        {
            string parent = Path.GetDirectoryName(path)!;
            var lease = GitHubBackup.App.NativeFileSystem.PinDirectories(parent);
            try
            {
                string root = GitHubBackup.App.NativeFileSystem.CanonicalPath(Root);
                foreach (string segment in GitHubBackup.App.NativeFileSystem.Segments(parent))
                    if (string.Equals(segment, root, StringComparison.OrdinalIgnoreCase) ||
                        segment.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        CheckIdentity(segment, Inspect(segment, true));
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        public void WriteDirectory(string path)
        {
            string full = CheckedPath(path);
            string setupBase = Path.Combine(RepoRoot(), "artifacts", "setup");
            foreach (string segment in GitHubBackup.App.NativeFileSystem.Segments(full))
            {
                if (Directory.Exists(segment))
                {
                    var actual = Inspect(segment, true);
                    if (identities.ContainsKey(segment)) CheckIdentity(segment, actual);
                    else if (segment.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("FIXTURE_DIRECTORY_NOT_OWNED");
                    continue;
                }
                if (!segment.StartsWith(Path.Combine(RepoRoot(), "artifacts"), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("FIXTURE_ANCESTOR_MISSING");
                using var parents = GitHubBackup.App.NativeFileSystem.PinDirectories(Path.GetDirectoryName(segment)!);
                if (File.Exists(segment) || Directory.Exists(segment)) throw new IOException("FIXTURE_PATH_EXISTS");
                Directory.CreateDirectory(segment);
                identities.Add(segment, Inspect(segment, true));
                directories.Add(segment);
            }
            if (!full.StartsWith(setupBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("FIXTURE_PATH_OUTSIDE_SETUP");
            if (!identities.ContainsKey(full)) throw new IOException("FIXTURE_DIRECTORY_NOT_OWNED");
        }
        public void WriteFile(string path, byte[] bytes)
        {
            string full = CheckedPath(path);
            using var parents = PinOwnedParents(full);
            using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            stream.Write(bytes);
            identities.Add(full, GitHubBackup.App.NativeFileSystem.Inspect(stream.SafeFileHandle, full, false));
            files.Add(full);
        }
        public void AdoptGeneratedDirectory(string path)
        {
            string full = CheckedPath(path);
            using var parents = PinOwnedParents(full);
            identities.Add(full, Inspect(full, true));
            directories.Add(full);
        }
        public void AdoptGeneratedFile(string path)
        {
            string full = CheckedPath(path);
            using var parents = PinOwnedParents(full);
            identities.Add(full, Inspect(full, false));
            files.Add(full);
        }
        public void RefreshGeneratedFileIdentity(string path)
        {
            string full = CheckedPath(path);
            using var parents = PinOwnedParents(full);
            if (!identities.ContainsKey(full)) throw new IOException("FIXTURE_FILE_NOT_OWNED");
            identities[full] = Inspect(full, false);
        }
        public void ReplaceFile(string path, byte[] bytes)
        {
            string full = CheckedPath(path);
            using var parents = PinOwnedParents(full);
            using var handle = GitHubBackup.App.NativeFileSystem.Open(full,
                GitHubBackup.App.NativeFileSystem.ReadControl | GitHubBackup.App.NativeFileSystem.ReadAttributes | 2,
                shareWrite: false);
            CheckIdentity(full, GitHubBackup.App.NativeFileSystem.Inspect(handle, full, false));
            RandomAccess.Write(handle, bytes, 0);
            RandomAccess.SetLength(handle, bytes.Length);
        }
        public void DeleteFile(string path)
        {
            string full = CheckedPath(path);
            using var parents = PinOwnedParents(full);
            using var handle = GitHubBackup.App.NativeFileSystem.Open(full,
                GitHubBackup.App.NativeFileSystem.ReadControl | GitHubBackup.App.NativeFileSystem.ReadAttributes |
                GitHubBackup.App.NativeFileSystem.DeleteAccess | 1);
            CheckIdentity(full, GitHubBackup.App.NativeFileSystem.Inspect(handle, full, false));
            GitHubBackup.App.NativeFileSystem.DeleteByHandle(handle);
            files.Remove(full);
            identities.Remove(full);
        }
        public void WriteMinimalPayload()
        {
            var data = new byte[1024];
            void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
            void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
            U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550);
            U16(0x84, 0x8664); U16(0x86, 1); U16(0x94, 0xf0); U16(0x96, 0x22);
            U16(0x98, 0x20b); U32(0xa8, 0x1000);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0xb0), 0x140000000);
            U32(0xb8, 0x1000); U32(0xbc, 0x200); U32(0xd0, 0x2000); U32(0xd4, 0x200);
            U16(0xdc, 2); U32(0x104, 16);
            Encoding.ASCII.GetBytes(".text").CopyTo(data, 0x188);
            U32(0x190, 0x100); U32(0x194, 0x1000); U32(0x198, 0x200);
            U32(0x19c, 0x200); U32(0x1ac, 0x60000020);
            WriteFile(Path.Combine(Payload, "GitHubBackup.exe"), data);
        }
        public void WriteSyntheticReceipt(int treeLength = 40, bool corruptSourceHash = false)
        {
            const string version = "1.0.0.0";
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(Payload, "GitHubBackup.exe"))));
            string evidenceRelative = Path.GetRelativePath(RepoRoot(), Evidence).Replace('\\', '/');
            var evidenceBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schema = 1, sourceCommit = new string('a', 40), appSourceTree = new string('A', treeLength),
                payloadSha256 = hash, appFileVersion = version, publishExitCode = 0,
                payloadVerificationStatus = "PASS", policyResolutionReference = "synthetic test reference"
            }));
            if (files.Contains(Evidence)) ReplaceFile(Evidence, evidenceBytes); else WriteFile(Evidence, evidenceBytes);
            ReplaceFile(Receipt, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schema = 1,
                sourceCommit = new string('a', 40),
                appSourceTree = new string('A', treeLength),
                sourceFiles = new[] { new { relativePath = "src/GitHubBackup.App/GitHubBackup.App.csproj", sha256 = corruptSourceHash ? new string('B', 64) : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(RepoRoot(), "src", "GitHubBackup.App", "GitHubBackup.App.csproj")))) } },
                payloadSha256 = hash,
                appFileVersion = version,
                payloadKind = "internal-unsigned",
                buildEvidence = evidenceRelative
            })));
        }
        public void AssertNoBuildOutputs() { Assert.IsFalse(Directory.Exists(Work)); Assert.IsFalse(Directory.Exists(Output)); }
        public void Dispose()
        {
            foreach (var file in files.ToArray().Reverse())
                if (File.Exists(file) || Directory.Exists(file)) DeleteFile(file);
            foreach (var directory in directories.AsEnumerable().Reverse())
            {
                if (!Directory.Exists(directory)) continue;
                using var parents = PinOwnedParents(directory);
                using var handle = GitHubBackup.App.NativeFileSystem.Open(directory,
                    GitHubBackup.App.NativeFileSystem.ReadControl | GitHubBackup.App.NativeFileSystem.ReadAttributes |
                    GitHubBackup.App.NativeFileSystem.DeleteAccess | 1);
                CheckIdentity(directory, GitHubBackup.App.NativeFileSystem.Inspect(handle, directory, true));
                if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("FIXTURE_DIRECTORY_NOT_EMPTY");
                GitHubBackup.App.NativeFileSystem.DeleteByHandle(handle);
                identities.Remove(directory);
            }
        }
    }
}
