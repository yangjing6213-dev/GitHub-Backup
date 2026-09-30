; -*- coding: utf-8 -*-
; Read-only product-boundary harness. Does not install, uninstall, extract a payload,
; create a product directory/shortcut/registry record, or supply a runtime path override.
; The ownership/hash checks create only explicit files in NSIS's private temp dir.
; NSIS may create its normal private temporary directory for the System plugin.
; Compilation requires verified G1/G2 inputs. Execution additionally requires G3.
Unicode true
RequestExecutionLevel user
SetCompressor zlib
!ifndef SETUP_INPUTS
    !error "SETUP_INPUTS_REQUIRED"
!endif
!ifndef SETUP_HARNESS_OUTPUT
    !error "SETUP_HARNESS_OUTPUT_REQUIRED"
!endif
!include "${SETUP_INPUTS}"
!include "..\..\..\publish\Setup\SetupGuards.nsh"
Name "GitHub Backup native guard checks"
OutFile "${SETUP_HARNESS_OUTPUT}"
InstallDir "$LOCALAPPDATA\${SETUP_INSTALL_SUFFIX}"
SilentInstall silent
!insertmacro SetupNativeFoundation ""

Var HarnessFixtureName
Var HarnessFixtureSddl
Var HarnessExpectedAcl
Var HarnessReceiptText
Var HarnessFailureStage

; Diagnostic exit codes identify only the first failed top-level phase. They are
; not guard result codes and contain no path, account, or fixture contents.
!define HARNESS_FAIL_ENTRY 39
!define HARNESS_FAIL_HOST 40
!define HARNESS_FAIL_ARGUMENTS 41
!define HARNESS_FAIL_CAPACITY 42
!define HARNESS_FAIL_MUTEX 43
!define HARNESS_FAIL_PLUGIN_DIR 44
!define HARNESS_FAIL_PRIVATE_FILE 45
!define HARNESS_FAIL_RECEIPT 46
!define HARNESS_FAIL_PUBLIC_FILE 47
!define HARNESS_FAIL_UNPROTECTED_FILE 48
!define HARNESS_FAIL_ANCESTORS 49
!define HARNESS_FAIL_FILE_ID 50

; A stub failure before .onInit may still return NSIS's generic exit code.
Function .onInit
    SetErrorLevel ${HARNESS_FAIL_ENTRY}
FunctionEnd

; Pure native capacity decisions, no product/shortcut creation. Preserve the
; authoritative root and assert it is not changed by each refusal.
Function CheckShortcutCapacity
    StrCpy $R2 $SetupFixedRoot
    StrCpy $R3 258
capacity_case:
    StrCpy $SetupFixedRoot "C:"
    StrLen $R4 "\${SETUP_APP_NAME}"
    IntOp $R4 $R3 - $R4
capacity_fill:
    StrLen $R5 $SetupFixedRoot
    IntCmp $R5 $R4 capacity_check
    StrCpy $SetupFixedRoot "$SetupFixedRoot\a"
    ; A one-character remainder must not accidentally cross the intended bound.
    StrLen $R5 $SetupFixedRoot
    IntCmp $R5 $R4 capacity_check capacity_fill
    StrCpy $SetupFixedRoot $SetupFixedRoot $R4
capacity_check:
    StrCpy $R5 $SetupFixedRoot
    Call ValidateShortcutTargetCapacity
    StrCmp $SetupFixedRoot $R5 0 capacity_failed
    StrCmp $R3 258 0 capacity_refusal
    StrCmp $SetupCode 0 0 capacity_failed
    StrLen $R5 $SetupAppPath
    StrCmp $R5 258 0 capacity_failed
    Goto capacity_next
capacity_refusal:
    StrCmp $SetupCode 11 0 capacity_failed
    StrCmp $SetupAppPath "" 0 capacity_failed
capacity_next:
    IntOp $R3 $R3 + 1
    IntCmp $R3 262 capacity_passed capacity_case capacity_failed
capacity_passed:
    StrCpy $SetupCode 0
    Goto capacity_done
capacity_failed:
    StrCpy $SetupCode 11
capacity_done:
    StrCpy $SetupFixedRoot $R2
    StrCpy $SetupAppPath ""
FunctionEnd

; Test-owned fixtures, not an alternate production path or authorization result.
; A wrong SID/DACL decision or a hash that skips/duplicates bytes must fail here.
Function CheckOwnedFileFixture
    StrCpy $SetupCode 11
    StrCpy $0 -1
    StrCpy $7 0
    StrCpy $8 0
    StrCpy $9 0
    System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "$HarnessFixtureSddl", i 1, *p .r8, p 0) i.r1'
    StrCmp $1 0 fixture_done
    System::Call '*(i 12, p r8, i 0) p.r9' ; x86 SECURITY_ATTRIBUTES
    StrCmp $9 0 fixture_done
    System::Call 'kernel32::CreateFileW(w "$PLUGINSDIR\$HarnessFixtureName", i 0xC0000000, i 0, p r9, i 1, i 0x80, p 0) p.r0'
    StrCmp $0 -1 fixture_done
    StrCpy $GuardHandle $0
    StrCpy $GuardDirectory 0
    Call ValidatePrivateHandleAcl
    StrCmp $SetupCode $HarnessExpectedAcl 0 fixture_failed
    StrCmp $HarnessExpectedAcl 0 0 fixture_passed
    StrCmp $HarnessFixtureName "receipt.dat" fixture_receipt
    Call HashHandleSha256
    StrCmp $SetupCode 0 0 fixture_done
    StrCmp $GuardHash "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855" 0 fixture_failed
    System::Call '*(&i1 97, &i1 98, &i1 99) p.r7'
    StrCmp $7 0 fixture_failed
    System::Call 'kernel32::WriteFile(p r0, p r7, i 3, *i .r2, p 0) i.r1'
    StrCmp $1 0 fixture_failed
    StrCmp $2 3 0 fixture_failed
    ; Hash from offset zero despite the caller's current position being three;
    ; preserve that position so subsequent operations retain their exact state.
    Call HashHandleSha256
    StrCmp $SetupCode 0 0 fixture_done
    StrCmp $GuardHash "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD" 0 fixture_failed
    System::Call 'kernel32::SetFilePointerEx(p r0, l 0, *l .r2, i 1) i.r1'
    StrCmp $1 0 fixture_failed
    StrCmp $2 3 0 fixture_failed
    ; The production opener must acquire DELETE before inspecting/hashing. The
    ; fixture writer closes here, not an already-validated production read lease.
    System::Call 'kernel32::CloseHandle(p r0) i.r1'
    StrCmp $1 0 fixture_failed
    StrCpy $0 -1
    StrCpy $GuardPath "$PLUGINSDIR\$HarnessFixtureName"
    Call OpenProductIdentityLease
    StrCmp $SetupCode 0 0 fixture_done
    StrCpy $0 $GuardHandle
    StrCmp $GuardHash "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD" 0 fixture_failed
    Goto fixture_passed
fixture_receipt:
    ; Fixed schema values, no arbitrary target path or simulated authorization.
    StrCpy $HarnessReceiptText "[Installation]$\r$\nschema=1$\r$\nproductId=${SETUP_PRODUCT_ID}$\r$\nownerSid=$SetupOwnerSid$\r$\nappVersion=${SETUP_APP_VERSION}$\r$\nsourceCommit=${SETUP_SOURCE_COMMIT}$\r$\npayloadSha256=${SETUP_APP_SHA256}$\r$\n${SETUP_APP_NAME}=${SETUP_APP_SHA256}$\r$\n${SETUP_UNINSTALLER_NAME}=BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB$\r$\n${SETUP_NOTICE_NAME}=${SETUP_NOTICE_SHA256}$\r$\nstartMenu=1$\r$\ndesktop=0$\r$\nstartMenuSha256=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA$\r$\ndesktopSha256=none$\r$\n"
    StrLen $3 $HarnessReceiptText
    IntCmpU $3 1000 fixture_failed 0 fixture_failed ; bounded test fixture
    System::Call '*(&i2 0xFEFF) p.r7'
    StrCmp $7 0 fixture_failed
    System::Call 'kernel32::WriteFile(p r0, p r7, i 2, *i .r2, p 0) i.r1'
    StrCmp $1 0 fixture_failed
    StrCmp $2 2 0 fixture_failed
    IntOp $3 $3 * 2
    System::Call 'kernel32::WriteFile(p r0, w "$HarnessReceiptText", i r3, *i .r2, p 0) i.r1'
    StrCmp $1 0 fixture_failed
    StrCmp $2 $3 0 fixture_failed
    Call ReadOwnedReceipt
    StrCmp $SetupCode 0 0 fixture_done
    StrCmp $SetupRecordedDesktop 0 0 fixture_failed
    StrCmp $SetupRecordedDesktopHash "none" 0 fixture_failed
    ; Corrupt the first schema character while retaining the very same handle.
    System::Call '*$7(&i2 63)'
    System::Call 'kernel32::SetFilePointerEx(p r0, l 2, p 0, i 0) i.r1'
    StrCmp $1 0 fixture_failed
    System::Call 'kernel32::WriteFile(p r0, p r7, i 2, *i .r2, p 0) i.r1'
    StrCmp $1 0 fixture_failed
    StrCmp $2 2 0 fixture_failed
    Call ReadOwnedReceipt
    StrCmp $SetupCode 11 0 fixture_failed
    StrCmp $SetupRecordedAppHash "" 0 fixture_failed
fixture_passed:
    StrCpy $SetupCode 0
    Goto fixture_done
fixture_failed:
    StrCpy $SetupCode 11
fixture_done:
    StrCmp $7 0 +2
        System::Free $7
    StrCmp $9 0 +2
        System::Free $9
    StrCmp $8 0 +2
        System::Call 'kernel32::LocalFree(p r8) p'
    ${If} $0 != -1
        System::Call 'kernel32::CloseHandle(p r0) i.r1'
        ${If} $1 == 0
            StrCpy $SetupCode 11
        ${EndIf}
    ${EndIf}
    StrCpy $GuardHandle 0
    StrCpy $GuardHash ""
FunctionEnd

Section
    SetShellVarContext current
    SetRegView 64
    StrCpy $SetupMode "install"
    StrCpy $HarnessFailureStage ${HARNESS_FAIL_HOST}
    SetErrorLevel $HarnessFailureStage
    Call ValidateHost
    StrCmp $SetupCode 0 0 done
    StrCpy $HarnessFailureStage ${HARNESS_FAIL_ARGUMENTS}
    SetErrorLevel $HarnessFailureStage
    Call ValidateDirectoryArguments
    StrCmp $SetupCode 0 0 done
    StrCpy $HarnessFailureStage ${HARNESS_FAIL_CAPACITY}
    SetErrorLevel $HarnessFailureStage
    Call CheckShortcutCapacity
    StrCmp $SetupCode 0 0 done
    StrCpy $HarnessFailureStage ${HARNESS_FAIL_MUTEX}
    SetErrorLevel $HarnessFailureStage
    Call AcquireSetupMutex
    StrCmp $SetupCode 0 0 done
    ; Model a second opener of the named object, not only the helper's local
    ; double-acquisition guard. Keep the first native handle alive throughout.
    StrCpy $R1 $SetupMutex
    StrCpy $SetupMutex 0
    Call AcquireSetupMutex
    StrCmp $SetupCode 12 mutex_expected
    Call ReleaseSetupMutex
    StrCpy $SetupMutex $R1
    Call ReleaseSetupMutex
    StrCpy $SetupCode 12
    Goto done
mutex_expected:
    StrCpy $SetupMutex $R1
    Call ReleaseSetupMutex
    StrCmp $SetupCode 0 0 done
    StrCpy $HarnessFailureStage ${HARNESS_FAIL_PLUGIN_DIR}
    SetErrorLevel $HarnessFailureStage
    ClearErrors
    InitPluginsDir
    IfErrors plugin_dir_failed
    Goto plugin_dir_ready
plugin_dir_failed:
    StrCpy $SetupCode 11
    Goto done
plugin_dir_ready:
    StrCpy $R0 $SetupOwnerSid
    StrCpy $HarnessFailureStage ${HARNESS_FAIL_PRIVATE_FILE}
    SetErrorLevel $HarnessFailureStage
    StrCpy $HarnessFixtureName "private.dat"
    StrCpy $HarnessFixtureSddl "O:$R0D:P(A;;FA;;;$R0)(A;;FA;;;SY)(A;;FA;;;BA)"
    StrCpy $HarnessExpectedAcl 0
    Call CheckOwnedFileFixture
    StrCmp $SetupCode 0 0 done
    StrCpy $HarnessFailureStage ${HARNESS_FAIL_RECEIPT}
    SetErrorLevel $HarnessFailureStage
    StrCpy $HarnessFixtureName "receipt.dat"
    Call CheckOwnedFileFixture
    StrCmp $SetupCode 0 0 done
    StrCpy $HarnessFailureStage ${HARNESS_FAIL_PUBLIC_FILE}
    SetErrorLevel $HarnessFailureStage
    StrCpy $HarnessFixtureName "public.dat"
    StrCpy $HarnessFixtureSddl "O:$R0D:P(A;;FA;;;$R0)(A;;FR;;;WD)"
    StrCpy $HarnessExpectedAcl 11
    Call CheckOwnedFileFixture
    StrCmp $SetupCode 0 0 done
    StrCpy $HarnessFailureStage ${HARNESS_FAIL_UNPROTECTED_FILE}
    SetErrorLevel $HarnessFailureStage
    StrCpy $HarnessFixtureName "unprotected.dat"
    StrCpy $HarnessFixtureSddl "O:$R0D:(A;;FA;;;$R0)"
    Call CheckOwnedFileFixture
    StrCmp $SetupCode 0 0 done
    StrCpy $HarnessFailureStage ${HARNESS_FAIL_ANCESTORS}
    SetErrorLevel $HarnessFailureStage
    Call PinExistingInstallAncestors
    StrCmp $SetupCode 0 0 done
    ; Native regression oracle: compare the retained object's identity with all
    ; 24 bytes returned independently by FileIdInfo, not the legacy 64-bit index.
    ; This remains read-only and cannot run before the external execution gates.
    StrCpy $HarnessFailureStage ${HARNESS_FAIL_FILE_ID}
    SetErrorLevel $HarnessFailureStage
    StrCpy $GuardPath $SetupLocalAppData
    StrCpy $GuardDirectory 1
    Call OpenPathIdentityLease
    StrCmp $SetupCode 0 0 release_pins
    StrCpy $0 $GuardHandle
    StrCpy $SetupCode 11
    System::Alloc 24
    Pop $9
    StrCmp $9 0 identity_close
    System::Call 'kernel32::GetFileInformationByHandleEx(p r0, i 18, p r9, i 24) i.r1'
    StrCmp $1 0 identity_free
    System::Call '*$9(i.r1, i.r2, i.r3, i.r4, i.r5, i.r6)'
    StrCmp $GuardIdentity "$1:$2:$3:$4:$5:$6" 0 identity_free
    StrCpy $SetupCode 0
identity_free:
    System::Free $9
identity_close:
    System::Call 'kernel32::CloseHandle(p r0) i.r1'
    StrCmp $1 0 0 +2
        StrCpy $SetupCode 11
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""
release_pins:
    StrCpy $8 $SetupCode
    Call ReleasePathPins
    StrCmp $SetupCode 0 0 done
    StrCpy $SetupCode $8
done:
    StrCmp $SetupCode 0 harness_success
    SetErrorLevel $HarnessFailureStage
    Goto harness_done
harness_success:
    SetErrorLevel 0
harness_done:
SectionEnd
