; Read-only host-check regression harness. Only the explicitly supplied result
; file under artifacts/setup is written; derived profile paths are never logged.
Unicode true
RequestExecutionLevel user
SetCompressor zlib
!ifndef SETUP_HOST_HARNESS_OUTPUT
    !error "SETUP_HOST_HARNESS_OUTPUT_REQUIRED"
!endif
!ifndef SETUP_HOST_RESULT_FILE
    !error "SETUP_HOST_RESULT_FILE_REQUIRED"
!endif
!ifndef SETUP_GUARD_SOURCE
    !error "SETUP_GUARD_SOURCE_REQUIRED"
!endif
!ifndef SETUP_SUPPORTED_BUILD
    !error "SETUP_SUPPORTED_BUILD_REQUIRED"
!endif
!ifndef SETUP_NATIVE_MACHINE
    !error "SETUP_NATIVE_MACHINE_REQUIRED"
!endif
!define SETUP_INSTALL_SUFFIX "Programs\GitHubBackupTool"
!define SETUP_PAYLOAD_KIND "internal-unsigned"
!define SETUP_PRODUCT_ID "GitHubBackupTool"
!define SETUP_APP_NAME "SyntheticApp.bin"
!define SETUP_UNINSTALLER_NAME "SyntheticUninstall.exe"
!define SETUP_RECEIPT_NAME "SyntheticReceipt.ini"
!define SETUP_NOTICE_NAME "SyntheticNOTICE.txt"
!define SETUP_APP_VERSION "0.0.0.0"
!define SETUP_SOURCE_COMMIT "0000000000000000000000000000000000000000"
!define SETUP_APP_SHA256 "7DE80C706E1C4BA74764D19CF62950733D147D093161C8A556C0C6A1106027A1"
!define SETUP_NOTICE_SHA256 "761376BC0EC5390D4E5F8DDCD12B6645FFEF709557CF9DA852BEDB6004A74EFD"
!include "${SETUP_GUARD_SOURCE}"
Name "Read-only host check test"
OutFile "${SETUP_HOST_HARNESS_OUTPUT}"
SilentInstall silent
!insertmacro SetupNativeFoundation ""

Var HarnessResult

Section
    FileOpen $HarnessResult "${SETUP_HOST_RESULT_FILE}" w
    IfErrors harness_failed

    ; Compare the original and explicit source/destination parameter grammar.
    StrCpy $0 123456
    StrCpy $1 123456
    System::Call 'kernel32::IsWow64Process2(p -1, *i 0 .r0, *i 0 .r1) i.r2'
    FileWrite $HarnessResult "legacyArchitecture=$2,$0,$1$\r$\n"
    System::Call 'kernel32::IsWow64Process2(p -1, *i 0 r0, *i 0 r1) i.r2'
    FileWrite $HarnessResult "architecture=$2,$0,$1$\r$\n"

    System::Call '*(i 284, i 0, i 0, i 0, i 0, &w128 "", &i2 0, &i2 0, &i2 0, &i1 0, &i1 0) p.r9'
    StrCmp $9 0 harness_failed_open
    System::Call 'kernel32::GetVersionExW(p r9) i.r5'
    System::Call '*$9(i, i.r0, i.r1, i.r2, i.r4)'
    IntOp $6 $9 + 282
    System::Call '*$6(&i1 .r6)'
    FileWrite $HarnessResult "version=$5,$0,$1,$2,$4,$6$\r$\n"
    System::Free $9

    StrCpy $3 0
    System::Call 'advapi32::OpenProcessToken(p -1, i 8, *p .r3) i.r2'
    FileWrite $HarnessResult "openToken=$2$\r$\n"
    StrCmp $2 0 harness_failed_open
    StrCpy $0 123456
    System::Call 'advapi32::GetTokenInformation(p r3, i 20, *i 0 .r0, i 4, *i .r4) i.r2'
    FileWrite $HarnessResult "legacyElevation=$2,$0,$4$\r$\n"
    System::Call 'advapi32::GetTokenInformation(p r3, i 20, *i 0 r0, i 4, *i .r4) i.r2'
    FileWrite $HarnessResult "elevation=$2,$0,$4$\r$\n"
    System::Call 'kernel32::CloseHandle(p r3) i'

    Call ValidateHost
    FileWrite $HarnessResult "hostCode=$SetupCode$\r$\n"
    StrCpy $0 0
    StrCmp $SetupFixedRoot "" +2
        StrCpy $0 1
    FileWrite $HarnessResult "hostHasRoot=$0$\r$\n"
    StrCpy $0 0
    StrCmp $SetupOwnerSid "" +2
        StrCpy $0 1
    FileWrite $HarnessResult "hostHasOwner=$0$\r$\n"
    StrCmp $SetupCode 0 0 harness_done
    StrCpy $SetupMode "install"
    StrCpy $INSTDIR $SetupFixedRoot
    Call ValidateDirectoryArguments
    FileWrite $HarnessResult "argumentCode=$SetupCode$\r$\n"
    StrCpy $SetupMode "uninstall"
    StrCpy $INSTDIR $SetupFixedRoot
    Call ValidateDirectoryArguments
    FileWrite $HarnessResult "uninstallArgumentCode=$SetupCode$\r$\n"
harness_done:
    FileClose $HarnessResult
    SetErrorLevel 0
    Quit
harness_failed_open:
    FileClose $HarnessResult
harness_failed:
    SetErrorLevel 90
    Quit
SectionEnd
