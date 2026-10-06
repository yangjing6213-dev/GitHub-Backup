; Synthetic parser-only harness. It creates and removes only receipt files in
; the unique parent supplied by the direct compile command; it is not a product
; installer and never reads machine/user identity, profile paths or the registry.
Unicode true
RequestExecutionLevel user
SetCompressor zlib

!ifndef SETUP_RECEIPT_FIXTURE_PARENT
    !error "SETUP_RECEIPT_FIXTURE_PARENT_REQUIRED"
!endif

!define SETUP_INSTALL_SUFFIX "GitHubBackupReceiptSynthetic"
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
!define SETUP_APP_SHA256 "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
!define SETUP_NOTICE_SHA256 "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"

!include "..\..\..\publish\Setup\SetupGuards.nsh"
Name "Synthetic receipt parser checks"
OutFile "${SETUP_RECEIPT_FIXTURE_PARENT}\SetupReceiptHarness.exe"
SilentInstall silent
!insertmacro SetupNativeFoundation ""
!insertmacro SetupNativeFoundation "un."

Var HarnessReceiptPath
Var HarnessReceiptText
Var HarnessReceiptKind
Var HarnessReceiptCreated
Var HarnessExpectedCode
Var HarnessResultTag
Var HarnessStatus
Var HarnessResults
Var HarnessResultsPath
Var HarnessFailures
Var HarnessWritePath
Var HarnessWriteText
Var HarnessWriteHandle
Var HarnessWriteCreated
Var HarnessWriteBytes
Var HarnessWriteStatus
Var HarnessDesktop
Var HarnessDesktopHash

; Each invocation uses CREATE_NEW and a different filename. The result labels
; are desktop-zero=PASS, desktop-one=FAIL, desktop-zero-hash=FAIL,
; missing-desktop-hash=FAIL, and extra-field=FAIL when the parser is correct.
!macro CheckReceipt TAG PREFIX FILE EXPECTED KIND CASEID RESULT
    StrCpy $HarnessReceiptPath "${SETUP_RECEIPT_FIXTURE_PARENT}\${FILE}"
    StrCpy $HarnessExpectedCode "${EXPECTED}"
    StrCpy $HarnessReceiptKind "${KIND}"
    StrCpy $HarnessResultTag "${RESULT}"
    StrCpy $HarnessReceiptCreated 0
    StrCpy $GuardHandle 0
    StrCpy $GuardDirectory 0
    StrCpy $SetupCode 90
    Call ${PREFIX}HarnessWriteReceipt
    StrCmp $SetupCode 0 ${CASEID}_write_ok
    StrCpy $HarnessStatus "WRITE_FAILED"
    Goto ${CASEID}_case_failed
${CASEID}_write_ok:
    Call ${PREFIX}HarnessOpenReceipt
    StrCmp $SetupCode 0 ${CASEID}_open_ok
    StrCpy $HarnessStatus "OPEN_FAILED"
    Goto ${CASEID}_case_failed
${CASEID}_open_ok:
    Call ${PREFIX}ReadOwnedReceipt
    StrCmp $SetupCode $HarnessExpectedCode ${CASEID}_code_matches
    StrCpy $HarnessStatus "UNEXPECTED_CODE_$SetupCode"
    Goto ${CASEID}_case_failed
${CASEID}_code_matches:
    StrCmp $HarnessExpectedCode 0 ${CASEID}_accepted
    StrCmp $SetupRecordedDesktop "" 0 ${CASEID}_fields_not_cleared
    StrCmp $SetupRecordedDesktopHash "" 0 ${CASEID}_fields_not_cleared
    StrCpy $HarnessStatus "PASS"
    Goto ${CASEID}_case_complete
${CASEID}_accepted:
    StrCmp $SetupRecordedDesktop "0" 0 ${CASEID}_invalid_values
    StrCmp $SetupRecordedDesktopHash "none" 0 ${CASEID}_invalid_values
    StrCpy $HarnessStatus "PASS"
    Goto ${CASEID}_case_complete
${CASEID}_fields_not_cleared:
    StrCpy $HarnessStatus "REJECTED_FIELDS_NOT_CLEARED"
    Goto ${CASEID}_case_failed
${CASEID}_invalid_values:
    StrCpy $HarnessStatus "ACCEPTED_INVALID_VALUES"
    Goto ${CASEID}_case_failed
${CASEID}_case_failed:
    StrCpy $HarnessFailures 1
${CASEID}_case_complete:
    Call ${PREFIX}HarnessCloseReceipt
    StrCmp $SetupCode 0 ${CASEID}_delete_receipt
    StrCpy $HarnessFailures 1
    StrCpy $HarnessStatus "CLOSE_FAILED"
    Goto ${CASEID}_record_result
${CASEID}_delete_receipt:
    StrCmp $HarnessReceiptCreated 1 0 ${CASEID}_record_result
    Call ${PREFIX}HarnessDeleteReceipt
    StrCmp $SetupCode 0 ${CASEID}_record_result
    StrCpy $HarnessFailures 1
    StrCpy $HarnessStatus "DELETE_FAILED"
${CASEID}_record_result:
    StrCpy $HarnessResults "$HarnessResults$HarnessResultTag=$HarnessStatus$\r$\n"
!macroend

!macro DefineReceiptHarnessHelpers PREFIX
Function ${PREFIX}HarnessWriteReceipt
    StrCpy $HarnessDesktop "0"
    StrCpy $HarnessDesktopHash "none"
    StrCmp $HarnessReceiptKind "desktop-one" 0 +2
        StrCpy $HarnessDesktop "1"
    StrCmp $HarnessReceiptKind "desktop-zero-hash" 0 +2
        StrCpy $HarnessDesktopHash "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE"

    StrCpy $HarnessReceiptText "[Installation]$\r$\nschema=1$\r$\nproductId=${SETUP_PRODUCT_ID}$\r$\nownerSid=$SetupOwnerSid$\r$\nappVersion=${SETUP_APP_VERSION}$\r$\nsourceCommit=${SETUP_SOURCE_COMMIT}$\r$\npayloadSha256=${SETUP_APP_SHA256}$\r$\n${SETUP_APP_NAME}=${SETUP_APP_SHA256}$\r$\n${SETUP_UNINSTALLER_NAME}=BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB$\r$\n${SETUP_NOTICE_NAME}=${SETUP_NOTICE_SHA256}$\r$\nstartMenu=1$\r$\ndesktop=$HarnessDesktop$\r$\nstartMenuSha256=DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD$\r$\n"
    StrCmp $HarnessReceiptKind "missing-desktop-hash" receipt_missing_hash
    StrCpy $HarnessReceiptText "$HarnessReceiptTextdesktopSha256=$HarnessDesktopHash$\r$\n"
    StrCmp $HarnessReceiptKind "extra-field" 0 receipt_text_ready
    StrCpy $HarnessReceiptText "$HarnessReceiptTextunexpected=value$\r$\n"
    Goto receipt_text_ready
receipt_missing_hash:
    ; Keep the final CRLF but omit desktopSha256 entirely.
receipt_text_ready:
    StrCpy $HarnessWritePath $HarnessReceiptPath
    StrCpy $HarnessWriteText $HarnessReceiptText
    Call ${PREFIX}HarnessWriteUtf16File
    StrCpy $HarnessReceiptCreated $HarnessWriteCreated
FunctionEnd

Function ${PREFIX}HarnessOpenReceipt
    StrCpy $SetupCode 91
    StrCpy $GuardHandle 0
    StrCpy $GuardDirectory 0
    System::Call 'kernel32::CreateFileW(w "$HarnessReceiptPath", i 0x80000000, i 1, p 0, i 3, i 0x80, p 0) p.r0'
    StrCmp $0 -1 open_receipt_done
    StrCpy $GuardHandle $0
    StrCpy $SetupCode 0
open_receipt_done:
FunctionEnd

Function ${PREFIX}HarnessCloseReceipt
    StrCpy $SetupCode 0
    StrCmp $GuardHandle 0 close_receipt_done
    System::Call 'kernel32::CloseHandle(p $GuardHandle) i.r1'
    StrCmp $1 0 close_receipt_failed
    StrCpy $GuardHandle 0
    Goto close_receipt_done
close_receipt_failed:
    StrCpy $SetupCode 92
close_receipt_done:
FunctionEnd

Function ${PREFIX}HarnessDeleteReceipt
    StrCpy $SetupCode 93
    System::Call 'kernel32::DeleteFileW(w "$HarnessReceiptPath") i.r1'
    StrCmp $1 0 delete_receipt_done
    StrCpy $HarnessReceiptCreated 0
    StrCpy $SetupCode 0
delete_receipt_done:
FunctionEnd

Function ${PREFIX}HarnessWriteUtf16File
    StrCpy $SetupCode 94
    StrCpy $HarnessWriteCreated 0
    StrCpy $HarnessWriteStatus 1
    StrCpy $HarnessWriteHandle 0
    System::Call 'kernel32::CreateFileW(w "$HarnessWritePath", i 0x40000000, i 0, p 0, i 1, i 0x80, p 0) p.r0'
    StrCmp $0 -1 write_utf16_done
    StrCpy $HarnessWriteHandle $0
    StrCpy $HarnessWriteCreated 1
    System::Call '*(&i2 0xFEFF) p.r1'
    StrCmp $1 0 write_utf16_close
    System::Call 'kernel32::WriteFile(p $HarnessWriteHandle, p r1, i 2, *i .r2, p 0) i.r3'
    StrCmp $3 0 write_utf16_free_bom
    StrCmp $2 2 0 write_utf16_free_bom
    StrLen $HarnessWriteBytes $HarnessWriteText
    IntOp $HarnessWriteBytes $HarnessWriteBytes * 2
    System::Call 'kernel32::WriteFile(p $HarnessWriteHandle, w "$HarnessWriteText", i $HarnessWriteBytes, *i .r2, p 0) i.r3'
    StrCmp $3 0 write_utf16_free_bom
    StrCmp $2 $HarnessWriteBytes 0 write_utf16_free_bom
    StrCpy $HarnessWriteStatus 0
write_utf16_free_bom:
    System::Free $1
write_utf16_close:
    System::Call 'kernel32::CloseHandle(p $HarnessWriteHandle) i.r4'
    StrCmp $4 0 write_utf16_done
    StrCpy $HarnessWriteHandle 0
    StrCmp $HarnessWriteStatus 0 0 write_utf16_done
    StrCpy $SetupCode 0
write_utf16_done:
FunctionEnd

Function ${PREFIX}HarnessWriteResults
    StrCpy $HarnessWritePath $HarnessResultsPath
    StrCpy $HarnessWriteText $HarnessResults
    Call ${PREFIX}HarnessWriteUtf16File
FunctionEnd
!macroend

!insertmacro DefineReceiptHarnessHelpers ""
!insertmacro DefineReceiptHarnessHelpers "un."

Section "Synthetic receipt parser checks"
    SetErrorLevel 90
    WriteUninstaller "${SETUP_RECEIPT_FIXTURE_PARENT}\SetupReceiptHarness-un.exe"
    StrCpy $SetupOwnerSid "S-1-5-21-100-200-300-400"
    StrCpy $HarnessFailures 0
    StrCpy $HarnessResults ""
    StrCpy $HarnessResultsPath "${SETUP_RECEIPT_FIXTURE_PARENT}\receipt-results.txt"

    !insertmacro CheckReceipt "desktop-zero" "" "desktop-zero-install.receipt" 0 "valid" receipt_zero_install "desktop-zero"
    !insertmacro CheckReceipt "desktop-one" "" "desktop-one-install.receipt" 11 "desktop-one" receipt_one_install "desktop-one"
    !insertmacro CheckReceipt "desktop-zero-hash" "" "desktop-zero-hash-install.receipt" 11 "desktop-zero-hash" receipt_hash_install "desktop-zero-hash"
    !insertmacro CheckReceipt "missing-desktop-hash" "" "missing-desktop-hash-install.receipt" 11 "missing-desktop-hash" receipt_missing_install "missing-desktop-hash"
    !insertmacro CheckReceipt "extra-field" "" "extra-field-install.receipt" 11 "extra-field" receipt_extra_install "extra-field"

    Call HarnessWriteResults
    StrCmp $SetupCode 0 0 receipt_harness_failed
    StrCmp $HarnessFailures 0 receipt_harness_passed
receipt_harness_failed:
    SetErrorLevel 90
    Goto receipt_harness_done
receipt_harness_passed:
    SetErrorLevel 0
receipt_harness_done:
SectionEnd

Section "Uninstall"
    SetErrorLevel 90
    StrCpy $SetupOwnerSid "S-1-5-21-100-200-300-400"
    StrCpy $HarnessFailures 0
    StrCpy $HarnessResults ""
    StrCpy $HarnessResultsPath "${SETUP_RECEIPT_FIXTURE_PARENT}\receipt-uninstall-results.txt"

    !insertmacro CheckReceipt "desktop-zero" "un." "desktop-zero-uninstall.receipt" 0 "valid" receipt_zero_uninstall "desktop-zero-uninstall"
    !insertmacro CheckReceipt "desktop-one" "un." "desktop-one-uninstall.receipt" 11 "desktop-one" receipt_one_uninstall "desktop-one-uninstall"
    !insertmacro CheckReceipt "desktop-zero-hash" "un." "desktop-zero-hash-uninstall.receipt" 11 "desktop-zero-hash" receipt_hash_uninstall "desktop-zero-hash-uninstall"
    !insertmacro CheckReceipt "missing-desktop-hash" "un." "missing-desktop-hash-uninstall.receipt" 11 "missing-desktop-hash" receipt_missing_uninstall "missing-desktop-hash-uninstall"
    !insertmacro CheckReceipt "extra-field" "un." "extra-field-uninstall.receipt" 11 "extra-field" receipt_extra_uninstall "extra-field-uninstall"

    Call un.HarnessWriteResults
    StrCmp $SetupCode 0 0 receipt_uninstall_harness_failed
    StrCmp $HarnessFailures 0 receipt_uninstall_harness_passed
receipt_uninstall_harness_failed:
    SetErrorLevel 90
    Goto receipt_uninstall_harness_done
receipt_uninstall_harness_passed:
    SetErrorLevel 0
receipt_uninstall_harness_done:
SectionEnd
