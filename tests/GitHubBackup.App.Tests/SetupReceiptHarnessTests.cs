using System.Runtime.CompilerServices;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SetupReceiptHarnessTests
{
    [TestMethod]
    public void Receipt_harness_uses_the_real_parser_for_both_prefixes_and_only_synthetic_cases()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "GitHubBackup.App.Tests",
            "Fixtures", "SetupReceiptHarness.nsi")).Replace("\r\n", "\n", StringComparison.Ordinal);

        foreach (string required in new[]
        {
            "!include \"..\\..\\..\\publish\\Setup\\SetupGuards.nsh\"",
            "!insertmacro SetupNativeFoundation \"\"",
            "!insertmacro SetupNativeFoundation \"un.\"",
            "Call ${PREFIX}ReadOwnedReceipt",
            "Call ${PREFIX}HarnessWriteReceipt",
            "Call ${PREFIX}HarnessOpenReceipt",
            "Call ${PREFIX}HarnessCloseReceipt",
            "Call ${PREFIX}HarnessDeleteReceipt",
            "CreateFileW(w \"$HarnessWritePath\", i 0x40000000, i 0, p 0, i 1, i 0x80, p 0)",
            "StrCpy $HarnessReceiptPath \"${SETUP_RECEIPT_FIXTURE_PARENT}\\${FILE}\"",
            "Call un.HarnessWriteResults",
            "OutFile \"${SETUP_RECEIPT_FIXTURE_PARENT}\\SetupReceiptHarness.exe\"",
            "${SETUP_RECEIPT_FIXTURE_PARENT}\\receipt-results.txt",
            "Section \"Uninstall\"",
            "WriteUninstaller \"${SETUP_RECEIPT_FIXTURE_PARENT}\\SetupReceiptHarness-un.exe\"",
            "StrCmp $SetupCode $HarnessExpectedCode",
            "StrCmp $SetupRecordedDesktop \"\"",
            "StrCmp $SetupRecordedDesktopHash \"\"",
            "StrCmp $SetupRecordedDesktop \"0\"",
            "StrCmp $SetupRecordedDesktopHash \"none\""
        })
            StringAssert.Contains(source, required, "SYNTHETIC_RECEIPT_HARNESS_CONTRACT_MISSING: " + required);

        foreach (string prefix in new[] { "\"\"", "\"un.\"" })
            foreach (string scenario in new[]
            {
                "desktop-zero", "desktop-one", "desktop-zero-hash", "missing-desktop-hash", "extra-field"
            })
                StringAssert.Contains(source, $"!insertmacro CheckReceipt \"{scenario}\" {prefix}",
                    $"PARSER_CASE_MISSING_FOR_PREFIX:{prefix}:{scenario}");

        foreach (string forbidden in new[]
        {
            "InstallDir ", "ValidateHost", "ValidateDirectoryArguments", "CheckFixedInstallFilesAndReceipt",
            "PinExistingInstallAncestors", "SHGetKnownFolderPath", "GetKnownFolderPath", "SetRegView",
            "WriteReg", "Registry::", "HKCU", "HKLM", "LOCALAPPDATA", "GitHubBackup.nsi",
            "CreateShortCut", "Exec ", "ExecWait "
        })
            Assert.IsFalse(source.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                "SYNTHETIC_RECEIPT_HARNESS_MUST_NOT_TOUCH_PRODUCT_OR_ACCOUNT_STATE: " + forbidden);
    }

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
}
