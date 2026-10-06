using System.Runtime.CompilerServices;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SetupJournalReceiptRollbackHarnessTests
{
    [TestMethod]
    public void Synthetic_harness_keeps_receipt_on_hash_mismatch_then_aborts_the_paired_transaction()
    {
        string harness = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "GitHubBackup.App.Tests",
            "Fixtures", "SetupJournalHarness.nsi")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string zeroHash = new('0', 64);

        foreach (string required in new[]
        {
            "ConvertStringSecurityDescriptorToSecurityDescriptorW(w \"O:$SetupOwnerSidD:P(A;;GA;;;$SetupOwnerSid)(A;;GA;;;SY)(A;;GA;;;BA)\"",
            "*(i 12, p r8, i 0) p.r9",
            "CreateFileW(w \"$SetupFixedRoot\\${SETUP_RECEIPT_NAME}\", i 0xC0010000, i 0, p $HarnessRootSecurityAttributes, i 1, i 0x80, p 0)",
            "System::Free $HarnessRootSecurityAttributes",
            "LocalFree(p $HarnessRootSecurityDescriptor)",
            "StrCpy $SetupInstallReceiptHandle $0",
            "StrCpy $SetupInstallReceiptCreated 1",
            "StrCpy $SetupInstallReceiptDeletePending 0",
            $"StrCpy $SetupInstallReceiptHash \"{zeroHash}\"",
            "StrCpy $SetupJournalVerifyObjectHandle $SetupInstallReceiptHandle",
            "Call ValidateFreshInstallJournalHandle",
            "StrCpy $HarnessReceiptHandle $SetupInstallReceiptHandle",
            "StrCpy $HarnessReceiptIdentity $SetupInstallReceiptIdentity",
            "Call RollbackFreshInstallReceipt",
            "StrCmp $SetupCode 0 harness_receipt_rollback_mismatch_fail",
            "StrCmp $SetupInstallReceiptHandle $HarnessReceiptHandle 0 harness_abort_fail",
            "StrCmp $SetupInstallReceiptIdentity $HarnessReceiptIdentity 0 harness_abort_fail",
            "StrCmp $SetupInstallReceiptCreated 1 0 harness_abort_fail",
            "StrCmp $SetupInstallReceiptDeletePending 0 0 harness_abort_fail",
            $"StrCmp $SetupInstallReceiptHash \"{zeroHash}\" 0 harness_abort_fail",
            "StrCpy $SetupJournalVerifyObjectHandle $SetupInstallReceiptHandle",
            "StrCpy $SetupJournalVerifyIdentity $HarnessReceiptIdentity",
            "StrCpy $SetupInstallReceiptHash \"\"",
            "Call AbortFreshInstallCopyTransaction",
            "StrCmp $SetupInstallReceiptHandle 0 0 harness_fail",
            "StrCmp $SetupInstallReceiptIdentity \"\" 0 harness_fail",
            "StrCmp $SetupInstallReceiptHash \"\" 0 harness_fail",
            "StrCmp $SetupInstallReceiptCreated 0 0 harness_fail",
            "StrCmp $SetupInstallReceiptDeletePending 0 0 harness_fail",
            "StrCmp $SetupCopyTransactionActive 0 0 harness_fail"
        })
            StringAssert.Contains(harness, required, "SYNTHETIC_RECEIPT_ROLLBACK_CHECK_MISSING: " + required);

        const string receiptAttributes = "GetFileAttributesW(w \"$SetupFixedRoot\\${SETUP_RECEIPT_NAME}\")";
        int receiptDescriptor = harness.IndexOf(
            "ConvertStringSecurityDescriptorToSecurityDescriptorW(w \"O:$SetupOwnerSidD:P(A;;GA;;;$SetupOwnerSid)(A;;GA;;;SY)(A;;GA;;;BA)\"",
            StringComparison.Ordinal);
        int receiptSecurityAttributes = harness.IndexOf("*(i 12, p r8, i 0) p.r9", receiptDescriptor, StringComparison.Ordinal);
        int receiptCreate = harness.IndexOf("CreateFileW(w \"$SetupFixedRoot\\${SETUP_RECEIPT_NAME}\"", StringComparison.Ordinal);
        int receiptSecurityCleanup = harness.IndexOf("System::Free $HarnessRootSecurityAttributes", receiptCreate, StringComparison.Ordinal);
        int receiptIdentity = harness.IndexOf("StrCpy $HarnessReceiptIdentity $SetupInstallReceiptIdentity", receiptCreate, StringComparison.Ordinal);
        int rollbackAttempt = harness.IndexOf("Call RollbackFreshInstallReceipt", receiptIdentity, StringComparison.Ordinal);
        int nonzeroAssertion = harness.IndexOf("StrCmp $SetupCode 0 harness_receipt_rollback_mismatch_fail", rollbackAttempt, StringComparison.Ordinal);
        int handleRevalidation = harness.IndexOf("Call ValidateFreshInstallJournalHandle", nonzeroAssertion, StringComparison.Ordinal);
        int receiptStillExists = harness.IndexOf(receiptAttributes, handleRevalidation, StringComparison.Ordinal);
        int clearExpectedHash = harness.IndexOf("StrCpy $SetupInstallReceiptHash \"\"", receiptStillExists, StringComparison.Ordinal);
        int pairedAbort = harness.IndexOf("Call AbortFreshInstallCopyTransaction", clearExpectedHash, StringComparison.Ordinal);
        int finalReceiptCheck = harness.IndexOf(receiptAttributes, pairedAbort, StringComparison.Ordinal);

        Assert.IsTrue(receiptDescriptor >= 0 && receiptSecurityAttributes > receiptDescriptor &&
            receiptCreate > receiptSecurityAttributes && receiptSecurityCleanup > receiptCreate &&
            receiptIdentity > receiptSecurityCleanup && rollbackAttempt > receiptIdentity &&
            nonzeroAssertion > rollbackAttempt && receiptStillExists > nonzeroAssertion &&
            handleRevalidation > nonzeroAssertion && receiptStillExists > handleRevalidation &&
            clearExpectedHash > receiptStillExists && pairedAbort > clearExpectedHash && finalReceiptCheck > pairedAbort,
            "RECEIPT_MISMATCH_MUST_PRESERVE_THE_ORIGINAL_FILE_UNTIL_THE_FIXTURE_EXPLICITLY_RETRIES_ABORT");

        string receiptBeforeMismatch = harness[receiptCreate..rollbackAttempt];
        Assert.IsFalse(receiptBeforeMismatch.Contains("WriteFile(", StringComparison.Ordinal),
            "RECEIPT_MISMATCH_FIXTURE_MUST_BEGIN_WITH_AN_EMPTY_CREATE_NEW_FILE");
    }

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
}
