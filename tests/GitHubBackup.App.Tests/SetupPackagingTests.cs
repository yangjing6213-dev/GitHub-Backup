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
        const string header = "GitHubBackup setup - internal candidate\n"
            + "Copyright (c) 2026 yangjing6213-dev. All rights reserved.\n"
            + "This notice does not grant a license to the application's source code.\n"
            + "The current-user installer is intended to retain settings, logs, credentials, and backups on uninstall.\n"
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
    public void Formal_installer_source_is_fixed_and_fails_closed_until_lifecycle_guards_exist()
    {
        string setupPath = Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi");
        Assert.IsTrue(File.Exists(setupPath), "SETUP_INSTALLER_SOURCE_MISSING");
        string source = File.ReadAllText(setupPath);
        foreach (string required in new[]
        {
            "Unicode true", "RequestExecutionLevel user", "!include \"MUI2.nsh\"",
            "!include \"SetupGuards.nsh\"", "!insertmacro SetupNativeFoundation \"\"",
            "!insertmacro SetupNativeFoundation \"un.\"", "SetShellVarContext current",
            "SetRegView 64", "Call ValidateHost", "Call ValidateDirectoryArguments",
            "!error \"SETUP_LIFECYCLE_NOT_IMPLEMENTED\""
        })
            StringAssert.Contains(source, required);
        Assert.IsTrue(HasActiveFailStopBeforeOutput(source), "INCOMPLETE_INSTALLER_MUST_STOP_BEFORE_OUTPUT");
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
            Assert.IsFalse(new[] { "File ", "Delete ", "RMDir ", "WriteReg", "CreateShortCut ",
                "Exec ", "ExecWait ", "WriteUninstaller " }.Any(line.StartsWith),
                "INCOMPLETE_INSTALLER_MUST_NOT_MUTATE_PRODUCT");

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
    public void Commented_or_conditional_fail_stop_cannot_satisfy_formal_installer_contract()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        const string stop = "!error \"SETUP_LIFECYCLE_NOT_IMPLEMENTED\"";
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
    public void Continued_comment_and_tab_macro_cannot_hide_formal_fail_stop()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        const string stop = "!error \"SETUP_LIFECYCLE_NOT_IMPLEMENTED\"";
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
        StringAssert.Contains(installer, "!error \"SETUP_LIFECYCLE_NOT_IMPLEMENTED\"");
        Assert.IsFalse(installer.Contains("Call CopyTrustedStageFileToFixedRoot", StringComparison.Ordinal) ||
            installer.Contains("Call un.CopyTrustedStageFileToFixedRoot", StringComparison.Ordinal),
            "STAGED_COPY_PRIMITIVE_MUST_REMAIN_UNWIRED_UNTIL_TRANSACTION_REVIEW");
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
        StringAssert.Contains(installer, "!error \"SETUP_LIFECYCLE_NOT_IMPLEMENTED\"");
        Assert.IsFalse(installer.Contains("Call CopyFreshInstallPayloadFilesToFixedRoot", StringComparison.Ordinal) ||
            installer.Contains("Call un.CopyFreshInstallPayloadFilesToFixedRoot", StringComparison.Ordinal),
            "FILE_TRANSACTION_MUST_REMAIN_UNWIRED_UNTIL_THE_FULL_INSTALL_LIFECYCLE_IS_REVIEWED");

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
        int writeFilesPhase = transaction.IndexOf("Call ${PREFIX}WriteFreshInstallJournalFilesWritten", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, prepareJournal,
            "PREPARED_JOURNAL_MUST_BE_DURABLE_BEFORE_ANY_PRODUCT_FILE_CREATE");
        Assert.IsGreaterThan(prepareJournal, copyApp,
            "PREPARED_JOURNAL_MUST_PRECEDE_THE_FIRST_PRODUCT_COPY");
        Assert.IsGreaterThan(copyApp, writeFilesPhase,
            "FILES_WRITTEN_MUST_BE_RECORDED_ONLY_AFTER_BOTH_PAYLOAD_COPIES");

        string prepare = FunctionBody(guard, "PrepareFreshInstallJournal");
        StringAssert.Contains(prepare, "CreateDirectory2W",
            "STATE_DIRECTORY_MUST_BE_CREATED_WITH_THE_EXISTING_FIXED-DIRECTORY_PRIMITIVE");
        StringAssert.Contains(prepare, "journal.ini",
            "JOURNAL_PATH_MUST_BE_FIXED");
        StringAssert.Contains(prepare, "i 1",
            "JOURNAL_FILE_MUST_USE_CREATE_NEW_NOT_OPEN_ALWAYS_OR_TRUNCATE");
        StringAssert.Contains(prepare, "GetFileInformationByHandleEx",
            "CREATED_JOURNAL_IDENTITY_MUST_COME_FROM_ITS_RETURNED_HANDLE");
        StringAssert.Contains(prepare, "Call ${PREFIX}ValidatePrivateHandleAcl",
            "JOURNAL_ACL_MUST_BE_CHECKED_ON_THE_RETURNED_HANDLE");
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
            "SetupCopyRootHandle",
            "GuardPathPins",
            "SetupJournalFileHandle",
            "SetupJournalFileIdentity",
            "FILES_WRITTEN",
            "FlushFileBuffers",
            "Call ${PREFIX}HashHandleSha256",
            "Call ${PREFIX}ValidatePrivateHandleAcl"
        })
            StringAssert.Contains(update, required, "PHASE_UPDATE_MUST_RETAIN_AND_REVALIDATE_THE_CREATED_JOURNAL: " + required);

        string rollback = FunctionBody(guard, "RollbackFreshInstallPayloadCopies");
        Assert.IsTrue(rollback.Contains("SetupJournalFileHandle", StringComparison.Ordinal) &&
            rollback.Contains("SetupJournalDirectoryHandle", StringComparison.Ordinal),
            "PAIRED_ABORT_MUST_INCLUDE_JOURNAL_FILE_AND_CREATED_STATE_DIRECTORY");
        string installer = File.ReadAllText(Path.Combine(RepoRoot(), "publish", "Setup", "GitHubBackup.nsi"));
        StringAssert.Contains(installer, "!error \"SETUP_LIFECYCLE_NOT_IMPLEMENTED\"");
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
        Assert.IsTrue(HasActiveFailStopBeforeOutput(installer), "INCOMPLETE_INSTALLER_MUST_STOP_BEFORE_OUTPUT");
        Assert.IsFalse(installer.Contains("Call CheckFixedInstallFilesAndReceipt", StringComparison.Ordinal) ||
            installer.Contains("Call un.CheckFixedInstallFilesAndReceipt", StringComparison.Ordinal),
            "FACT_CHECK_MUST_REMAIN_UNCONNECTED_TO_FORMAL_INSTALLER");
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
            "System::Call '::$2(w \"$SetupLocalAppData\\Programs\", i 0x20081, i 1, i 1, p 0) p.r0 ?e'",
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
            "System::Call '::$2(w \"$SetupFixedRoot\", i 0x20081, i 1, i 1, p $SetupFreshRootSecurityAttributes) p.r0 ?e'",
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
            "FRESH_ROOT_PRIMITIVE_MUST_REMAIN_UNWIRED");
        Assert.IsTrue(HasActiveFailStopBeforeOutput(installer),
            "FORMAL_INSTALLER_MUST_REMAIN_FAIL_STOP");
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
        var result = await RunPowerShellAsync("-CommandWithArgs", """
            $ast=[Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$null,[ref]$null)
            $fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'New-SetupIncludeLines'},$true)
            if(-not $fn){throw 'FUNCTION_MISSING'}
            . ([ScriptBlock]::Create($fn.Extent.Text))
            $product=Import-PowerShellDataFile -LiteralPath $args[4]
            $lines=@(New-SetupIncludeLines $args[1] $args[2] $args[3] '1.2.3.4' ('A'*64) ('b'*40) $product ('C'*64) $args[5])
            ConvertTo-Json -InputObject $lines -Compress
            """, f.BuildScript, app, notice, setup, f.Contract, payloadKind);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.AreEqual(16, json.RootElement.GetArrayLength());
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
        Assert.AreEqual("!define SETUP_RECEIPT_NAME \"install.ini\"", json.RootElement[12].GetString());
        Assert.AreEqual("!define SETUP_NOTICE_NAME \"NOTICE.txt\"", json.RootElement[13].GetString());
        Assert.AreEqual("!define SETUP_NOTICE_SHA256 \"" + new string('C', 64) + "\"", json.RootElement[14].GetString());
        Assert.AreEqual("!define SETUP_PAYLOAD_KIND \"" + payloadKind + "\"", json.RootElement[15].GetString());
        Assert.IsFalse(File.ReadAllText(f.Contract).Contains("RegistryKey", StringComparison.Ordinal),
            "UNINSTALL_REGISTRY_IS_OUTSIDE_APPROVED_V1_SCOPE");
        Assert.IsFalse(File.ReadAllText(f.BuildScript).Contains("SETUP_REGISTRY_KEY", StringComparison.Ordinal),
            "BUILD_INCLUDE_MUST_NOT_EMIT_UNINSTALL_REGISTRY_KEY");
    }

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
