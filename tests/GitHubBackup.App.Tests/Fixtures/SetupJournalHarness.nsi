; Test-only executable. It is never the product installer and only targets a
; unique fixture parent supplied by the direct compile command.
Unicode true
RequestExecutionLevel user
SetCompressor zlib

!ifndef SETUP_JOURNAL_FIXTURE_PARENT
    !error "SETUP_JOURNAL_FIXTURE_PARENT_REQUIRED"
!endif
!ifndef SETUP_JOURNAL_HARNESS_OUTPUT
    !error "SETUP_JOURNAL_HARNESS_OUTPUT_REQUIRED"
!endif

!define SETUP_INSTALL_SUFFIX "Programs\GitHubBackupTool-Synthetic"
!define SETUP_SUPPORTED_BUILD 19045
!define SETUP_NATIVE_MACHINE 34404
!define SETUP_PAYLOAD_KIND "internal-unsigned"
!define SETUP_PRODUCT_ID "GitHubBackupToolSynthetic"
!define SETUP_APP_NAME "SyntheticApp.bin"
!define SETUP_UNINSTALLER_NAME "SyntheticUninstall.exe"
!define SETUP_RECEIPT_NAME "SyntheticReceipt.ini"
!define SETUP_NOTICE_NAME "SyntheticNOTICE.txt"
!define SETUP_APP_VERSION "0.0.0.0"
!define SETUP_SOURCE_COMMIT "0000000000000000000000000000000000000000"
!define SETUP_APP_SHA256 "7DE80C706E1C4BA74764D19CF62950733D147D093161C8A556C0C6A1106027A1"
!define SETUP_NOTICE_SHA256 "761376BC0EC5390D4E5F8DDCD12B6645FFEF709557CF9DA852BEDB6004A74EFD"

!include "..\..\..\publish\Setup\SetupGuards.nsh"
Name "GitHub Backup synthetic journal test"
OutFile "${SETUP_JOURNAL_HARNESS_OUTPUT}"
SilentInstall silent
!insertmacro SetupNativeFoundation ""

Var HarnessParentHandle
Var HarnessParentOwner
Var HarnessOwnerString
Var HarnessOwnerDescriptor
Var HarnessRootSecurityDescriptor
Var HarnessRootSecurityAttributes
Var HarnessRootApi
Var HarnessRootInfo
Var HarnessFailure
Var HarnessReceiptHandle
Var HarnessReceiptIdentity

Section "Synthetic journal transaction"
    SetErrorLevel 80
    StrCpy $HarnessFailure 80
    StrCpy $HarnessFailure 81
    StrCpy $HarnessFailure 79
    ClearErrors
    CreateDirectory "${SETUP_JOURNAL_FIXTURE_PARENT}\temp"
    IfErrors harness_fail
    System::Call 'kernel32::SetEnvironmentVariableW(w "TEMP", w "${SETUP_JOURNAL_FIXTURE_PARENT}\temp") i.r0'
    StrCmp $0 0 harness_fail
    System::Call 'kernel32::SetEnvironmentVariableW(w "TMP", w "${SETUP_JOURNAL_FIXTURE_PARENT}\temp") i.r0'
    StrCmp $0 0 harness_fail
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=${SETUP_APP_NAME} "JournalSyntheticApp.bin"
    File /oname=${SETUP_NOTICE_NAME} "JournalSyntheticNotice.txt"

    StrCpy $SetupMode "install"
    StrCpy $SetupFixedRoot "${SETUP_JOURNAL_FIXTURE_PARENT}\product"
    StrCpy $SetupLocalAppData ""
    StrCpy $SetupOwnerSid ""
    StrCpy $SetupCopyTransactionActive 1
    StrCpy $SetupCopyRootLeasePending 0
    StrCpy $SetupCopyRootHandle 0
    StrCpy $SetupCopyRootIdentity ""
    StrCpy $GuardPathPins 0
    StrCpy $GuardPathPinCount 0

    ; Read the owner SID from the test fixture parent itself; do not query the
    ; token, Known Folders, account profile, or registry.
    StrCpy $HarnessFailure 82
    System::Call 'kernel32::CreateFileW(w "${SETUP_JOURNAL_FIXTURE_PARENT}", i 0x20081, i 3, p 0, i 3, i 0x02200000, p 0) p.r0'
    StrCmp $0 -1 harness_fail
    StrCpy $HarnessParentHandle $0
    StrCpy $HarnessParentOwner 0
    StrCpy $HarnessOwnerDescriptor 0
    StrCpy $HarnessFailure 83
    System::Call 'advapi32::GetSecurityInfo(p $HarnessParentHandle, i 1, i 1, *p .r1, p 0, p 0, p 0, *p .r2) i.r3'
    StrCmp $3 0 0 harness_fail
    StrCmp $1 0 harness_fail
    StrCmp $2 0 harness_fail
    StrCpy $HarnessParentOwner $1
    StrCpy $HarnessOwnerDescriptor $2
    StrCpy $HarnessFailure 84
    System::Call 'advapi32::ConvertSidToStringSidW(p $HarnessParentOwner, *p .r4) i.r3'
    StrCmp $3 0 harness_fail
    StrCmp $4 0 harness_fail
    StrCpy $HarnessOwnerString $4
    System::Call 'kernel32::lstrlenW(p $HarnessOwnerString) i.r3'
    IntCmpU $3 ${NSIS_MAX_STRLEN} harness_fail 0 harness_fail
    System::Call 'kernel32::lstrcpynW(w .r5, p $HarnessOwnerString, i ${NSIS_MAX_STRLEN}) p'
    StrCpy $SetupOwnerSid $5
    System::Call 'kernel32::LocalFree(p $HarnessOwnerString) p'
    StrCpy $HarnessOwnerString 0
    System::Call 'kernel32::LocalFree(p $HarnessOwnerDescriptor) p'
    StrCpy $HarnessOwnerDescriptor 0
    System::Call 'kernel32::CloseHandle(p $HarnessParentHandle) i.r3'
    StrCmp $3 0 harness_fail
    StrCpy $HarnessParentHandle 0

    ; Create the private synthetic product root with the same SID/SYSTEM/Admin
    ; DACL shape used by the production fresh-root primitive.
    StrCpy $HarnessFailure 85
    System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "O:$SetupOwnerSidD:P(A;;GA;;;$SetupOwnerSid)(A;;GA;;;SY)(A;;GA;;;BA)", i 1, *p .r8, p 0) i.r7'
    StrCmp $7 0 harness_fail
    StrCmp $8 0 harness_fail
    StrCpy $HarnessRootSecurityDescriptor $8
    System::Call '*(i 12, p r8, i 0) p.r9'
    StrCmp $9 0 harness_fail
    StrCpy $HarnessRootSecurityAttributes $9
    System::Call 'kernel32::GetModuleHandleW(w "kernel32.dll") p.r1'
    StrCmp $1 0 harness_fail
    System::Call 'kernel32::GetProcAddress(p r1, m "CreateDirectory2W") p.r2'
    StrCmp $2 0 harness_fail
    StrCpy $HarnessRootApi $2
    StrCpy $2 $HarnessRootApi
    StrCpy $HarnessFailure 86
    System::Call '::$2(w "$SetupFixedRoot", i 0x120081, i 1, i 1, p $HarnessRootSecurityAttributes) p.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 0 harness_root_api_failed
    StrCmp $0 -1 harness_root_api_failed
    StrCpy $SetupCopyRootHandle $0
    StrCpy $GuardLastError 0
    System::Call 'kernel32::LocalFree(p $HarnessRootSecurityDescriptor) p.r1'
    StrCmp $1 0 0 harness_fail
    StrCpy $HarnessRootSecurityDescriptor 0
    System::Free $HarnessRootSecurityAttributes
    StrCpy $HarnessRootSecurityAttributes 0

    StrCpy $HarnessFailure 170
    System::Alloc 52
    Pop $HarnessRootInfo
    StrCpy $HarnessFailure 171
    StrCmp $HarnessRootInfo 0 harness_fail
    StrCpy $HarnessFailure 172
    System::Call 'kernel32::GetFileInformationByHandle(p $SetupCopyRootHandle, p $HarnessRootInfo) i.r1 ?e'
    Pop $GuardLastError
    StrCpy $HarnessFailure 173
    StrCmp $1 0 harness_fail
    StrCpy $GuardLastError 0
    System::Call '*$HarnessRootInfo(i.r1, i, i, i, i, i, i, i, i, i, i, i, i)'
    IntOp $2 $1 & 0x400
    StrCpy $HarnessFailure 174
    StrCmp $2 0 0 harness_fail
    IntOp $2 $1 & 0x10
    StrCpy $HarnessFailure 175
    StrCmp $2 0 harness_fail
    StrCpy $HarnessFailure 176
    System::Call 'kernel32::GetFileInformationByHandleEx(p $SetupCopyRootHandle, i 18, p $HarnessRootInfo, i 24) i.r1 ?e'
    Pop $GuardLastError
    StrCpy $HarnessFailure 177
    StrCmp $1 0 harness_fail
    StrCpy $GuardLastError 0
    System::Call '*$HarnessRootInfo(i.r3, i.r4, i.r5, i.r6, i.r7, i.r8)'
    StrCpy $SetupCopyRootIdentity "$3:$4:$5:$6:$7:$8"
    System::Free $HarnessRootInfo
    StrCpy $HarnessRootInfo 0

    ; Distinguish an unreadable security descriptor from ACL-policy parsing.
    StrCpy $4 0
    StrCpy $5 0
    StrCpy $6 0
    StrCpy $HarnessFailure 178
    System::Call 'advapi32::GetSecurityInfo(p $SetupCopyRootHandle, i 1, i 5, *p .r4, p 0, *p .r5, p 0, *p .r6) i.r7'
    StrCmp $7 0 harness_root_acl_query_ok
    IntOp $HarnessFailure 1000 + $7
    Goto harness_fail
harness_root_acl_query_ok:
    StrCpy $HarnessFailure 179
    StrCmp $4 0 harness_fail
    StrCmp $5 0 harness_fail
    StrCmp $6 0 harness_fail
    System::Call 'advapi32::IsValidSecurityDescriptor(p r6) i.r7'
    StrCpy $HarnessFailure 180
    StrCmp $7 0 harness_fail
    System::Call 'kernel32::LocalFree(p r6) p.r7'
    StrCpy $HarnessFailure 181
    StrCmp $7 0 0 harness_fail

    StrCpy $HarnessFailure 88
    StrCpy $GuardHandle $SetupCopyRootHandle
    StrCpy $GuardDirectory 1
    Call ValidatePrivateHandleAcl
    StrCmp $SetupCode 0 0 harness_fail
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""

    ; Pin the existing fixture parent as the one synthetic ancestor. This is
    ; inside artifacts/setup only and is not derived from any user profile.
    StrCpy $HarnessFailure 89
    StrCpy $GuardPath "${SETUP_JOURNAL_FIXTURE_PARENT}"
    StrCpy $GuardDirectory 1
    Call OpenPathIdentityLease
    StrCmp $SetupCode 0 0 harness_fail
    StrCmp $GuardHandle 0 harness_fail
    System::Alloc ${SETUP_PIN_BYTES}
    Pop $GuardPathPins
    StrCmp $GuardPathPins 0 harness_fail
    System::Call '*$GuardPathPins(p $GuardHandle)'
    StrCpy $GuardPathPinCount 1
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""

    StrCpy $HarnessFailure 90
    Call PrepareFreshInstallJournal
    StrCmp $SetupCode 0 0 harness_fail
    StrCmp $SetupJournalPhase "PREPARED" 0 harness_abort_fail

    StrCpy $HarnessFailure 91
    StrCpy $SetupCopyTargetName "${SETUP_APP_NAME}"
    Call CopyTrustedStageFileToFixedRoot
    StrCmp $SetupCode 0 0 harness_abort_fail
    StrCpy $HarnessFailure 92
    StrCpy $SetupCopyTargetName "${SETUP_NOTICE_NAME}"
    Call CopyTrustedStageFileToFixedRoot
    StrCmp $SetupCode 0 0 harness_abort_fail

    StrCpy $HarnessFailure 93
    Call WriteFreshInstallJournalFilesWritten
    StrCmp $SetupCode 0 0 harness_abort_fail
    StrCmp $SetupJournalPhase "FILES_WRITTEN" 0 harness_abort_fail
    StrLen $1 $SetupJournalFileHash
    StrCmp $1 64 0 harness_abort_fail

    ; Exercise receipt rollback with an empty CREATE_NEW file. The first call
    ; supplies a valid-looking but incorrect expected hash and must preserve the
    ; exact handle, identity and file; only this fixture then clears that hash
    ; before asking the paired transaction abort to delete all owned objects.
    StrCpy $HarnessFailure 182
    StrCpy $SetupInstallReceiptHandle 0
    StrCpy $SetupInstallReceiptIdentity ""
    StrCpy $SetupInstallReceiptHash ""
    StrCpy $SetupInstallReceiptCreated 0
    StrCpy $SetupInstallReceiptDeletePending 0
    System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "O:$SetupOwnerSidD:P(A;;GA;;;$SetupOwnerSid)(A;;GA;;;SY)(A;;GA;;;BA)", i 1, *p .r8, p 0) i.r7'
    StrCmp $7 0 harness_abort_fail
    StrCmp $8 0 harness_abort_fail
    StrCpy $HarnessRootSecurityDescriptor $8
    System::Call '*(i 12, p r8, i 0) p.r9'
    StrCmp $9 0 harness_abort_fail
    StrCpy $HarnessRootSecurityAttributes $9
    System::Call 'kernel32::CreateFileW(w "$SetupFixedRoot\${SETUP_RECEIPT_NAME}", i 0xC0010000, i 0, p $HarnessRootSecurityAttributes, i 1, i 0x80, p 0) p.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 -1 harness_receipt_create_failed
    StrCmp $0 0 harness_receipt_create_failed
    StrCpy $SetupInstallReceiptHandle $0
    StrCpy $SetupInstallReceiptCreated 1
    StrCpy $SetupInstallReceiptDeletePending 0
    StrCpy $SetupInstallReceiptIdentity ""
    StrCpy $SetupInstallReceiptHash "0000000000000000000000000000000000000000000000000000000000000000"
    Goto harness_receipt_security_cleanup
harness_receipt_create_failed:
    StrCpy $HarnessFailure 182
harness_receipt_security_cleanup:
    System::Free $HarnessRootSecurityAttributes
    StrCpy $HarnessRootSecurityAttributes 0
    System::Call 'kernel32::LocalFree(p $HarnessRootSecurityDescriptor) p.r1'
    StrCmp $1 0 harness_receipt_security_released
    StrCpy $HarnessFailure 187
    Goto harness_abort_fail
harness_receipt_security_released:
    StrCpy $HarnessRootSecurityDescriptor 0
    StrCmp $SetupInstallReceiptHandle 0 harness_abort_fail
    StrCpy $SetupJournalVerifyObjectHandle $SetupInstallReceiptHandle
    StrCpy $SetupJournalVerifyPath "\\?\$SetupFixedRoot\${SETUP_RECEIPT_NAME}"
    StrCpy $SetupJournalVerifyIdentity ""
    StrCpy $SetupJournalVerifyDirectory 0
    StrCpy $SetupJournalVerifyCreated 1
    StrCpy $HarnessFailure 183
    Call ValidateFreshInstallJournalHandle
    StrCmp $SetupCode 0 harness_receipt_identity_captured
    Goto harness_abort_fail
harness_receipt_identity_captured:
    StrCmp $SetupJournalVerifyIdentity "" harness_abort_fail
    StrCpy $SetupInstallReceiptIdentity $SetupJournalVerifyIdentity
    StrCpy $HarnessReceiptHandle $SetupInstallReceiptHandle
    StrCpy $HarnessReceiptIdentity $SetupInstallReceiptIdentity

    StrCpy $HarnessFailure 184
    Call RollbackFreshInstallReceipt
    StrCmp $SetupCode 0 harness_receipt_rollback_mismatch_fail
    StrCmp $SetupInstallReceiptHandle $HarnessReceiptHandle 0 harness_abort_fail
    StrCmp $SetupInstallReceiptIdentity $HarnessReceiptIdentity 0 harness_abort_fail
    StrCmp $SetupInstallReceiptCreated 1 0 harness_abort_fail
    StrCmp $SetupInstallReceiptDeletePending 0 0 harness_abort_fail
    StrCmp $SetupInstallReceiptHash "0000000000000000000000000000000000000000000000000000000000000000" 0 harness_abort_fail
    StrCpy $SetupJournalVerifyObjectHandle $SetupInstallReceiptHandle
    StrCpy $SetupJournalVerifyPath "\\?\$SetupFixedRoot\${SETUP_RECEIPT_NAME}"
    StrCpy $SetupJournalVerifyIdentity $HarnessReceiptIdentity
    StrCpy $SetupJournalVerifyDirectory 0
    StrCpy $SetupJournalVerifyCreated 1
    StrCpy $HarnessFailure 185
    Call ValidateFreshInstallJournalHandle
    StrCmp $SetupCode 0 harness_receipt_handle_still_valid
    Goto harness_abort_fail
harness_receipt_handle_still_valid:
    StrCmp $SetupJournalVerifyIdentity $HarnessReceiptIdentity 0 harness_abort_fail
    System::Call 'kernel32::GetFileAttributesW(w "$SetupFixedRoot\${SETUP_RECEIPT_NAME}") i.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 -1 harness_abort_fail
    Goto harness_receipt_preserved
harness_receipt_rollback_mismatch_fail:
    Goto harness_abort_fail
harness_receipt_preserved:

    ; This changes only the fixture-owned expected hash; the original receipt
    ; handle and identity remain the rollback authority for the final abort.
    StrCpy $SetupInstallReceiptHash ""
    ; Exercise the same paired abort used when this in-process copy transaction
    ; fails; this does not claim crash/restart recovery.
    StrCpy $HarnessFailure 186
    Call AbortFreshInstallCopyTransaction
    StrCmp $SetupCode 0 0 harness_fail
    StrCmp $SetupCopyTransactionActive 0 0 harness_fail
    StrCmp $SetupInstallReceiptHandle 0 0 harness_fail
    StrCmp $SetupInstallReceiptIdentity "" 0 harness_fail
    StrCmp $SetupInstallReceiptHash "" 0 harness_fail
    StrCmp $SetupInstallReceiptCreated 0 0 harness_fail
    StrCmp $SetupInstallReceiptDeletePending 0 0 harness_fail
    StrCmp $SetupJournalFileHandle 0 0 harness_fail
    StrCmp $SetupJournalDirectoryHandle 0 0 harness_fail
    StrCmp $SetupJournalPhase "" 0 harness_fail

    StrCpy $HarnessFailure 95
    System::Call 'kernel32::GetFileAttributesW(w "$SetupFixedRoot\${SETUP_APP_NAME}") i.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 -1 harness_app_absence_error harness_fail
harness_app_absence_error:
    StrCmp $GuardLastError 2 harness_notice_check
    StrCmp $GuardLastError 3 harness_notice_check harness_fail
harness_notice_check:
    System::Call 'kernel32::GetFileAttributesW(w "$SetupFixedRoot\${SETUP_NOTICE_NAME}") i.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 -1 harness_notice_absence_error harness_fail
harness_notice_absence_error:
    StrCmp $GuardLastError 2 harness_receipt_check
    StrCmp $GuardLastError 3 harness_receipt_check harness_fail
harness_receipt_check:
    System::Call 'kernel32::GetFileAttributesW(w "$SetupFixedRoot\${SETUP_RECEIPT_NAME}") i.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 -1 harness_receipt_absence_error harness_fail
harness_receipt_absence_error:
    StrCmp $GuardLastError 2 harness_stage_check
    StrCmp $GuardLastError 3 harness_stage_check harness_fail
harness_stage_check:
    System::Call 'kernel32::GetFileAttributesW(w "$SetupFixedRoot\.GitHubBackupTool.setup-stage") i.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 -1 harness_stage_absence_error harness_fail
harness_stage_absence_error:
    StrCmp $GuardLastError 2 harness_state_check
    StrCmp $GuardLastError 3 harness_state_check harness_fail
harness_state_check:
    System::Call 'kernel32::GetFileAttributesW(w "$SetupFixedRoot\.GitHubBackupTool.state") i.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 -1 harness_state_absence_error harness_fail
harness_state_absence_error:
    StrCmp $GuardLastError 2 harness_cleanup_verified
    StrCmp $GuardLastError 3 harness_cleanup_verified harness_fail
harness_cleanup_verified:

    SetErrorLevel 0
    Goto harness_done

harness_root_api_failed:
    StrCpy $HarnessFailure $GuardLastError
    Goto harness_fail
harness_abort_fail:
    StrCpy $HarnessFailure 1
    Call AbortFreshInstallCopyTransaction
    Goto harness_fail
harness_fail:
    StrCmp $SetupCopyRootHandle 0 harness_report_fail
    Call AbortFreshInstallCopyTransaction
harness_report_fail:
    SetErrorLevel $HarnessFailure
    Abort "SYNTHETIC_JOURNAL_HARNESS_FAILED"
harness_done:
SectionEnd
