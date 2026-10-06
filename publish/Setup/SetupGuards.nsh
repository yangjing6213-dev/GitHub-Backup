; -*- coding: utf-8 -*-
; Task 2 ownership primitive checkpoint, NOT a complete installer or write authorization.
; SID/DACL and hash primitives are not a complete product-ownership decision.
; Receipt, mutex, occupancy and shortcut helpers remain uncomposed: HKCU binding,
; expected uninstaller content and shortcut/commit/recovery are NOT implemented.
; The durable PREPARED/FILES_WRITTEN journal is wired only to the two-payload
; copy/rollback slice; this still does NOT compose or authorize product install.
; AcquireFixedRootOwnershipLease proves only the existing fixed root and retains
; its handle; success is NOT ValidateOwnedLayout or permission to change files.
; A held path identity or an isolated primitive must NEVER authorize
; changing a product file. No product entry point includes this file until those checks exist.
;
; Bindings target the locked x86-Unicode NSIS/System combination, even on x64 Windows.
; Win32 structure references and limitations are recorded in task-2-report.md.
!ifndef GITHUB_BACKUP_SETUP_GUARDS
!define GITHUB_BACKUP_SETUP_GUARDS
!include "LogicLib.nsh"
!if ${NSIS_CHAR_SIZE} != 2
    !error "SETUP_REQUIRES_UNICODE"
!endif
!if ${NSIS_PTR_SIZE} != 4
    !error "SETUP_REQUIRES_LOCKED_X86_SYSTEM_PLUGIN"
!endif
!ifndef SETUP_INSTALL_SUFFIX
    !error "SETUP_INPUTS_REQUIRED"
!endif
!ifndef SETUP_SUPPORTED_BUILD
    !error "SETUP_INPUTS_REQUIRED"
!endif
!ifndef SETUP_NATIVE_MACHINE
    !error "SETUP_INPUTS_REQUIRED"
!endif
!ifndef SETUP_PAYLOAD_KIND
    !error "SETUP_PAYLOAD_KIND_REQUIRED"
!endif
; Task 1's public-signed mode must never silently become an internal build.
; Runtime signature policy is not implemented at this preparation checkpoint.
!if "${SETUP_PAYLOAD_KIND}" != "internal-unsigned"
    !error "SETUP_RUNTIME_SIGNATURE_POLICY_NOT_IMPLEMENTED"
!endif

; Required for GetVersionExW to report the actual supported build.
ManifestSupportedOS Win10

Var SetupCode
Var SetupMode
Var SetupFixedRoot
Var SetupLocalAppData
Var SetupOwnerSid
Var SetupMutex
Var SetupAppPath

; Internal helper inputs/outputs. Helpers preserve $0..$9 and do not use $R0..$R9.
; GuardHandle belongs to its caller. OpenPathIdentityLease grants read access;
; OpenProductIdentityLease obtains read+DELETE up front, before validation. Neither
; alone authorizes a mutation without the complete product ownership chain.
Var GuardPath
Var GuardDirectory
Var GuardHandle
Var GuardIdentity
Var GuardHash
Var GuardSid
Var GuardLastError
Var GuardPathPins
Var GuardPathPinCount
Var GuardProgramsParentPinned
Var GuardLeaseKind
Var GuardRecordBuffer
Var GuardRecordSize
Var GuardRecordOffset
Var GuardLine
Var GuardValue
Var GuardValueLength
Var SetupRecordedVersion
Var SetupRecordedSource
Var SetupRecordedAppHash
Var SetupRecordedUninstallerHash
Var SetupRecordedNoticeHash
Var SetupRecordedStartMenuHash
Var SetupRecordedDesktopHash
Var SetupRecordedDesktop
; Generated-file handles owned by the in-progress fresh install. These are
; retained from creation through receipt verification or paired rollback.
Var SetupInstallUninstallerHandle
Var SetupInstallUninstallerIdentity
Var SetupInstallUninstallerHash
Var SetupInstallShortcutHandle
Var SetupInstallShortcutIdentity
Var SetupInstallShortcutHash
Var SetupInstallShortcutPath
Var SetupInstallReceiptHandle
Var SetupInstallReceiptIdentity
Var SetupInstallReceiptHash
Var SetupInstallReceiptCreated
Var SetupInstallReceiptDeletePending
Var SetupInstallReceiptRecord
Var SetupInstallReceiptSecurityDescriptor
Var SetupInstallReceiptSecurityAttributes
; Read-only fixed-file fact check state. These slots own only handles opened by
; CheckFixedInstallFilesAndReceipt; GuardPathPins/GuardPathPinCount stay attached
; to this lease until ReleaseFixedInstallFilesAndReceipt completes.
Var SetupFixedFilesLeaseActive
Var SetupFixedFilesRootHandle
Var SetupFixedFilesRootIdentity
Var SetupFixedFilesAppHandle
Var SetupFixedFilesAppIdentity
Var SetupFixedFilesAppHash
Var SetupFixedFilesUninstallerHandle
Var SetupFixedFilesUninstallerIdentity
Var SetupFixedFilesUninstallerHash
Var SetupFixedFilesNoticeHandle
Var SetupFixedFilesNoticeIdentity
Var SetupFixedFilesNoticeHash
Var SetupFixedFilesReceiptHandle
Var SetupFixedFilesReceiptIdentity
Var SetupFixedFilesReceiptHash
; Unwired first-install copy primitive. Handles stay attached until the paired
; release call; only the fixed app/NOTICE inputs have compiled trusted hashes.
Var SetupCopyTargetName
Var SetupCopyStagingName
Var SetupCopySourcePath
Var SetupCopyExpectedHash
Var SetupCopyResultHash
Var SetupCopySourceHandle
Var SetupCopyTargetHandle
Var SetupCopyRootHandle
Var SetupCopyRootIdentity
Var SetupCopyRootLeasePending
Var SetupCopyTargetIdentity
Var SetupCopyTargetDelete
Var SetupCopyTargetDeletePending
; Two-file Task 2.5 transaction ledger. Created slots retain their exact handles
; and immutable identity/hash evidence until commit exists or rollback completes.
Var SetupCopyTransactionActive
Var SetupCopyTxnAppHandle
Var SetupCopyTxnAppIdentity
Var SetupCopyTxnAppHash
Var SetupCopyTxnAppCreated
Var SetupCopyTxnAppDeletePending
Var SetupCopyTxnNoticeHandle
Var SetupCopyTxnNoticeIdentity
Var SetupCopyTxnNoticeHash
Var SetupCopyTxnNoticeCreated
Var SetupCopyTxnNoticeDeletePending
; Fresh-install journal slots are transaction-owned. The state directory and
; journal file remain open through commit or paired rollback; a cleanup failure
; must retain the exact handle, identity and delete-pending state for retry.
Var SetupJournalDirectoryHandle
Var SetupJournalDirectoryIdentity
Var SetupJournalDirectoryCreated
Var SetupJournalDirectoryDeletePending
Var SetupJournalFileHandle
Var SetupJournalFileIdentity
Var SetupJournalFileHash
Var SetupJournalFileCreated
Var SetupJournalFileDeletePending
Var SetupJournalPhase
Var SetupJournalSecurityDescriptor
Var SetupJournalSecurityAttributes
Var SetupJournalInfoBuffer
Var SetupJournalRecord
Var SetupJournalVerifyObjectHandle
Var SetupJournalVerifyPath
Var SetupJournalVerifyIdentity
Var SetupJournalVerifyDirectory
Var SetupJournalVerifyCreated
; Read-only cross-process journal classification. These handles are opened with
; path-identity leases and are never used as mutation or rollback authority.
Var SetupJournalReadRootHandle
Var SetupJournalReadRootIdentity
Var SetupJournalReadDirectoryHandle
Var SetupJournalReadDirectoryIdentity
Var SetupJournalReadFileHandle
Var SetupJournalReadFileIdentity
Var SetupJournalReadStatus
Var SetupJournalReadPhase
Var SetupJournalReadBuffer
Var SetupJournalReadSizeBuffer
Var SetupJournalReadSize
Var SetupJournalReadOffset
Var SetupJournalReadLine
Var SetupJournalReadPositionSaved
Var SetupTxnCheckHandle
Var SetupTxnCheckName
Var SetupTxnCheckIdentity
Var SetupTxnCheckHash
Var SetupTxnCheckDeletePending
Var SetupTxnCheckAllowStage
Var SetupTxnCheckIsStage
Var SetupTxnCheckActualHash
Var SetupTxnCheckInfoBuffer
Var SetupCopyBuffer
Var SetupCopyInfoBuffer
Var SetupCopySecurityDescriptor
Var SetupCopySecurityAttributes
Var SetupCopyRenameBuffer
Var SetupCopyRenameBufferBytes
Var SetupCopyChunkBytes
Var SetupCopyWriteOffset
; Scratch resources for the unwired first-install product-root creator.
Var SetupFreshRootSecurityDescriptor
Var SetupFreshRootSecurityAttributes
Var SetupFreshRootInfoBuffer
!define /math SETUP_PIN_BYTES ${NSIS_MAX_STRLEN} * ${NSIS_PTR_SIZE}
!define /math SETUP_RM_PATH_BYTES ${NSIS_MAX_STRLEN} * 2
!define /math SETUP_RM_BYTES 772 + ${SETUP_RM_PATH_BYTES}
!define SETUP_DIRECTORY_INFO_BUFFER_BYTES 65536
!define SETUP_FULL_DIR_INFO_NAME_LENGTH_OFFSET 60
!define SETUP_FULL_DIR_INFO_NAME_OFFSET 68
!define SETUP_ERROR_NO_MORE_FILES 18
!define SETUP_SHARED_FORBIDDEN_WRITE_MASK 0x510D0156
!define SETUP_SHARED_NON_READ_MASK 0x5FEDFF56

!macro SetupSaveRegisters
    Push $0
    Push $1
    Push $2
    Push $3
    Push $4
    Push $5
    Push $6
    Push $7
    Push $8
    Push $9
!macroend
!macro SetupRestoreRegisters
    Pop $9
    Pop $8
    Pop $7
    Pop $6
    Pop $5
    Pop $4
    Pop $3
    Pop $2
    Pop $1
    Pop $0
!macroend

; The receipt is a bounded, canonical UTF-16LE INI written by this product only.
; It never supplies paths. A changed order/extra key/embedded NUL is not silently
; interpreted by Windows INI mappings or accepted as an alternate product schema.
!macro SetupReceiptLiteral PREFIX TEXT
    Call ${PREFIX}ReadReceiptLine
    StrCmp $SetupCode 0 0 receipt_done
    StrCpy $SetupCode 11
    StrCmp $GuardLine "${TEXT}" 0 receipt_done
!macroend
!macro SetupReceiptField PREFIX KEY OUTPUT
    Call ${PREFIX}ReadReceiptLine
    StrCmp $SetupCode 0 0 receipt_done
    StrCpy $SetupCode 11
    StrLen $2 "${KEY}="
    StrCpy $1 $GuardLine $2
    StrCmp $1 "${KEY}=" 0 receipt_done
    StrCpy ${OUTPUT} $GuardLine "" $2
!macroend
!macro SetupReceiptHash PREFIX VALUE LENGTH
    StrCpy $GuardValue ${VALUE}
    StrCpy $GuardValueLength ${LENGTH}
    Call ${PREFIX}ValidateHexText
    StrCmp $SetupCode 0 0 receipt_done
    StrCpy $SetupCode 11
!macroend
!macro SetupClearReceipt
    StrCpy $SetupRecordedVersion ""
    StrCpy $SetupRecordedSource ""
    StrCpy $SetupRecordedAppHash ""
    StrCpy $SetupRecordedUninstallerHash ""
    StrCpy $SetupRecordedNoticeHash ""
    StrCpy $SetupRecordedStartMenuHash ""
    StrCpy $SetupRecordedDesktopHash ""
    StrCpy $SetupRecordedDesktop ""
!macroend

; Instantiate once with "" and once with "un." in the eventual entry point.
; This macro deliberately does not define unimplemented lifecycle interfaces.
!macro SetupNativeFoundation PREFIX
Function ${PREFIX}ValidateHost
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 10
    StrCpy $SetupFixedRoot ""
    StrCpy $SetupLocalAppData ""
    StrCpy $SetupOwnerSid ""
    StrCpy $3 0 ; process token
    StrCpy $5 0 ; TOKEN_USER allocation
    StrCpy $7 0 ; string SID allocation
    StrCpy $8 0 ; Known Folder allocation
    StrCpy $9 0 ; version/GUID allocation

    ; NativeMachine is the OS architecture, not the x86 installer's architecture.
    ; With an explicit zero source, rN is the destination; . would discard it.
    ; Zero-initialize DWORD slots before the API writes its USHORT outputs.
    System::Call 'kernel32::IsWow64Process2(p -1, *i 0 r0, *i 0 r1) i.r2'
    StrCmp $2 0 host_done
    StrCmp $1 ${SETUP_NATIVE_MACHINE} 0 host_done

    ; OSVERSIONINFOEXW: five DWORDs, WCHAR[128], three WORDs, two BYTEs = 284.
    System::Call '*(i 284, i 0, i 0, i 0, i 0, &w128 "", &i2 0, &i2 0, &i2 0, &i1 0, &i1 0) p.r9'
    StrCmp $9 0 host_done
    System::Call 'kernel32::GetVersionExW(p r9) i.r2'
    StrCmp $2 0 host_done
    System::Call '*$9(i, i.r0, i.r1, i.r2, i.r4)'
    StrCmp $0 10 0 host_done
    StrCmp $1 0 0 host_done
    StrCmp $2 ${SETUP_SUPPORTED_BUILD} 0 host_done
    StrCmp $4 2 0 host_done
    IntOp $0 $9 + 282 ; wProductType: VER_NT_WORKSTATION, not a server
    System::Call '*$0(&i1 .r1)'
    StrCmp $1 1 0 host_done
    System::Free $9
    StrCpy $9 0

    System::Call 'advapi32::OpenProcessToken(p -1, i 8, *p .r3) i.r2'
    StrCmp $2 0 host_done
    ; TokenElevation (20) returns one DWORD. Query failure also rejects the host.
    System::Call 'advapi32::GetTokenInformation(p r3, i 20, *i 0 r0, i 4, *i .r4) i.r2'
    StrCmp $2 0 host_done
    StrCmp $4 4 0 host_done
    StrCmp $0 0 0 host_done
    ; TokenUser (1), dynamically sized; no token data is persisted.
    System::Call 'advapi32::GetTokenInformation(p r3, i 1, p 0, i 0, *i .r4) i.r2'
    IntCmpU $4 8 0 host_done
    IntCmpU $4 65536 0 0 host_done
    System::Alloc $4
    Pop $5
    StrCmp $5 0 host_done
    System::Call 'advapi32::GetTokenInformation(p r3, i 1, p r5, i r4, *i .r0) i.r2'
    StrCmp $2 0 host_done
    System::Call '*$5(p.r6)'
    System::Call 'advapi32::ConvertSidToStringSidW(p r6, *p .r7) i.r2'
    StrCmp $2 0 host_done
    System::Call 'kernel32::lstrlenW(p r7) i.r0'
    IntCmpU $0 ${NSIS_MAX_STRLEN} host_done 0 host_done
    StrCmp $0 0 host_done
    System::Call 'kernel32::lstrcpynW(w .r1, p r7, i ${NSIS_MAX_STRLEN}) p'
    StrCpy $SetupOwnerSid $1

    StrCpy $SetupCode 11
    ; FOLDERID_LocalAppData. No environment-variable or caller-supplied root.
    System::Alloc 16
    Pop $9
    StrCmp $9 0 host_done
    System::Call 'ole32::CLSIDFromString(w "{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}", p r9) i.r2'
    StrCmp $2 0 0 host_done
    System::Call 'shell32::SHGetKnownFolderPath(p r9, i 0, p 0, *p .r8) i.r2'
    StrCmp $2 0 0 host_done
    StrCmp $8 0 host_done
    System::Call 'kernel32::lstrlenW(p r8) i.r0'
    StrLen $1 "\${SETUP_INSTALL_SUFFIX}"
    IntOp $1 $1 + $0
    ; Check the native length BEFORE copying into a bounded NSIS string.
    IntCmpU $1 ${NSIS_MAX_STRLEN} host_done 0 host_done
    StrCmp $0 0 host_done
    System::Call 'kernel32::lstrcpynW(w .r1, p r8, i ${NSIS_MAX_STRLEN}) p'
    StrCpy $SetupLocalAppData $1
    StrCpy $SetupFixedRoot "$1\${SETUP_INSTALL_SUFFIX}"
    Push $GuardPath
    StrCpy $GuardPath $SetupFixedRoot
    Call ${PREFIX}ValidateLocalPathText
    Pop $GuardPath
host_done:
    StrCmp $9 0 +2
        System::Free $9
    StrCmp $8 0 +2
        System::Call 'ole32::CoTaskMemFree(p r8)'
    StrCmp $7 0 +2
        System::Call 'kernel32::LocalFree(p r7) p'
    StrCmp $5 0 +2
        System::Free $5
    StrCmp $3 0 +2
        System::Call 'kernel32::CloseHandle(p r3) i'
    ${If} $SetupCode != 0
        StrCpy $SetupFixedRoot ""
        StrCpy $SetupLocalAppData ""
        StrCpy $SetupOwnerSid ""
    ${EndIf}
    !insertmacro SetupRestoreRegisters
FunctionEnd

Function ${PREFIX}ValidateDirectoryArguments
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 11
    StrCmp $SetupFixedRoot "" args_done
    StrCmp $SetupMode 'install' args_mode_ok
    StrCmp $SetupMode 'uninstall' 0 args_done
args_mode_ok:
    ; Both state and raw current-process arguments must agree. NSIS $CMDLINE
    ; omits /D=; checking that variable alone loses conflicts (NSIS v312 Main.c).
    StrCmp $INSTDIR $SetupFixedRoot args_state_ok
    StrCmp $INSTDIR "$SetupFixedRoot\" 0 args_done
args_state_ok:
    System::Call 'kernel32::GetCommandLineW() p.r0'
    StrCmp $0 0 args_done
    System::Call 'kernel32::lstrlenW(p r0) i.r1'
    StrCmp $1 0 args_done
    IntCmpU $1 ${NSIS_MAX_STRLEN} args_done 0 args_done
    System::Call 'kernel32::lstrcpynW(w .r2, p r0, i ${NSIS_MAX_STRLEN}) p'
    StrCpy $3 0
    StrCpy $4 $2 1
    StrCmp $4 " " args_done
    StrCmp $4 '$\"' args_quoted
args_unquoted:
    StrCpy $4 $2 1 $3
    StrCmp $4 "" args_accept
    StrCmp $4 " " args_skip_spaces
    StrCmp $4 '$\"' args_done
    StrCmp $4 '$\t' args_done
    StrCmp $4 '$\r' args_done
    StrCmp $4 '$\n' args_done
    IntOp $3 $3 + 1
    Goto args_unquoted
args_quoted:
    StrCpy $3 1
args_quote_loop:
    StrCpy $4 $2 1 $3
    StrCmp $4 "" args_done
    StrCmp $4 '$\"' args_quote_end
    IntOp $3 $3 + 1
    Goto args_quote_loop
args_quote_end:
    IntCmp $3 1 args_done ; empty executable token is ambiguous
    IntOp $3 $3 + 1
    StrCpy $4 $2 1 $3
    StrCmp $4 "" args_accept
    StrCmp $4 " " 0 args_done
args_skip_spaces:
    StrCpy $4 $2 1 $3
    StrCmp $4 "" args_accept
    StrCmp $4 " " 0 args_option
    IntOp $3 $3 + 1
    Goto args_skip_spaces
args_option:
    ; Only the documented final, unquoted directory option is accepted.
    ; Extra switches/quotes/multiple directory options cannot equal the fixed root.
    StrCpy $4 $2 3 $3
    StrCmpS $4 '/D=' args_directory
    StrCmp $SetupMode 'uninstall' 0 args_done
    StrCmpS $4 '_?=' 0 args_done
args_directory:
    IntOp $3 $3 + 3
    StrCpy $4 $2 "" $3
    StrCmp $4 $SetupFixedRoot args_accept
    StrCmp $4 "$SetupFixedRoot\" 0 args_done
args_accept:
    StrCpy $SetupCode 0
args_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Lexical validation only, NOT ownership. No path is silently normalized.
Function ${PREFIX}ValidateLocalPathText
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 11
    StrLen $1 $GuardPath
    IntCmpU $1 3 0 path_done
    IntCmpU $1 ${NSIS_MAX_STRLEN} path_done 0 path_done
    StrCpy $0 $GuardPath 2 1
    StrCmp $0 ':\' 0 path_done
    StrCpy $2 3
    StrCpy $3 ""
path_character:
    IntCmpU $2 $1 path_end
    StrCpy $0 $GuardPath 1 $2
    StrCmp $0 ':' path_done
    StrCmp $0 '/' path_done
    StrCmp $0 '*' path_done
    StrCmp $0 '?' path_done
    StrCmp $0 '$\"' path_done
    StrCmp $0 '<' path_done
    StrCmp $0 '>' path_done
    StrCmp $0 '|' path_done
    StrCmp $0 '$\t' path_done
    StrCmp $0 '$\r' path_done
    StrCmp $0 '$\n' path_done
    ${If} $0 == '\'
        StrCmp $3 "" path_done
        StrCmp $3 '.' path_done
        StrCmp $3 ' ' path_done
        StrCmp $3 '\' path_done
    ${EndIf}
    StrCpy $3 $0
    IntOp $2 $2 + 1
    Goto path_character
path_end:
    StrCmp $3 '.' path_done
    StrCmp $3 ' ' path_done
    StrCmp $3 '\' path_done ; only the drive root may end in a slash
    System::Call 'kernel32::GetFullPathNameW(w "$GuardPath", i ${NSIS_MAX_STRLEN}, w .r0, p 0) i.r2'
    StrCmp $2 0 path_done
    IntCmpU $2 ${NSIS_MAX_STRLEN} path_done 0 path_done
    StrCmp $0 $GuardPath 0 path_done
    StrCpy $SetupCode 0
path_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Opens an existing object without following a reparse. On success the caller
; retains GuardHandle, plus all 24 FILE_ID_INFO bytes in GuardIdentity as six
; colon-separated DWORDs (volume low/high, then the four file-ID chunks).
; The legacy 64-bit file index is not unique on ReFS and is never used here.
; FILE_READ_DATA/LIST_DIRECTORY is intentional: metadata-only opens do not pin
; rename/delete. Files deny write and delete sharing; directories deny deletion.
; This does NOT perform SID/DACL/product/hash authorization.
Function ${PREFIX}OpenPathIdentityLease
    Push $GuardLeaseKind
    StrCpy $GuardLeaseKind 0
    Call ${PREFIX}OpenCheckedIdentityLease
    Pop $GuardLeaseKind
FunctionEnd

; Do not upgrade a read lease by closing it and reopening a pathname. Acquire the
; final access at the first open, inspect/authorize that object, and retain it.
; Files deny all sharing, including new read opens. Mapped-image and startup-race
; behavior still require the authorized native matrix; DELETE alone is not a lock.
Function ${PREFIX}OpenProductIdentityLease
    !insertmacro SetupSaveRegisters
    Push $GuardLeaseKind
    StrCpy $GuardLeaseKind 1
    Call ${PREFIX}OpenCheckedIdentityLease
    Pop $GuardLeaseKind
    StrCmp $SetupCode 0 0 product_open_done
    Call ${PREFIX}ValidatePrivateHandleAcl
    StrCmp $SetupCode 0 0 product_open_refuse
    StrCmp $GuardDirectory 1 product_open_done
    Call ${PREFIX}HashHandleSha256
    StrCmp $SetupCode 0 product_open_done
product_open_refuse:
    System::Call 'kernel32::CloseHandle(p $GuardHandle) i.r0'
    StrCmp $0 0 product_open_done
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
product_open_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Internal common opener; GuardLeaseKind is set only by the two fixed wrappers.
Function ${PREFIX}OpenCheckedIdentityLease
    !insertmacro SetupSaveRegisters
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
    StrCpy $GuardLastError 0
    StrCpy $0 -1
    StrCpy $9 0
    Call ${PREFIX}ValidateLocalPathText
    StrCmp $SetupCode 0 0 lease_done
    StrCpy $SetupCode 11
    StrCmp $GuardDirectory 0 lease_file
    StrCmp $GuardDirectory 1 0 lease_done
    StrCpy $1 3
    Goto lease_open
lease_file:
    StrCpy $1 1
lease_open:
    StrCpy $2 0x20081
    ${If} $GuardLeaseKind == 1
        StrCpy $2 0x80010000 ; GENERIC_READ | DELETE; no WRITE_DAC/WRITE_OWNER
        ${If} $GuardDirectory == 0
            StrCpy $1 0
        ${EndIf}
    ${ElseIf} $GuardLeaseKind != 0
        Goto lease_done
    ${EndIf}
    ; READ_CONTROL | FILE_READ_ATTRIBUTES | FILE_READ_DATA; OPEN_EXISTING;
    ; FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS.
    System::Call 'kernel32::CreateFileW(w "$GuardPath", i r2, i r1, p 0, i 3, i 0x02200000, p 0) p.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 -1 lease_done
    ; LastError is undefined after a successful OPEN_EXISTING. A later validation
    ; failure must not accidentally be classified as a missing Programs/root.
    StrCpy $GuardLastError 0
    System::Alloc 52 ; BY_HANDLE_FILE_INFORMATION, thirteen DWORDs
    Pop $9
    StrCmp $9 0 lease_done
    System::Call 'kernel32::GetFileInformationByHandle(p r0, p r9) i.r1'
    StrCmp $1 0 lease_done
    System::Call '*$9(i.r1, i, i, i, i, i, i, i, i, i, i.r5, i, i)'
    IntOp $2 $1 & 0x400 ; reparse
    StrCmp $2 0 0 lease_done
    IntOp $2 $1 & 0x10 ; directory
    ${If} $GuardDirectory == 1
        StrCmp $2 0 lease_done
    ${Else}
        StrCmp $2 0 0 lease_done
        StrCmp $5 1 0 lease_done ; exactly one hard link for every ordinary file
    ${EndIf}
    System::Call 'kernel32::GetFinalPathNameByHandleW(p r0, w .r1, i ${NSIS_MAX_STRLEN}, i 0) i.r2'
    StrCmp $2 0 lease_done
    IntCmpU $2 ${NSIS_MAX_STRLEN} lease_done 0 lease_done
    StrCmp $1 '\\?\$GuardPath' 0 lease_done
    ; FILE_ID_INFO: ULONGLONG volume at offset 0, BYTE FileId[16] at offset 8.
    ; Reuse the 52-byte allocation, passing the exact 24-byte result size. Read
    ; six DWORDs to preserve every bit without NSIS's 32-bit integer truncation.
    ; Unsupported/failed FileIdInfo is a refusal, never a legacy-ID fallback.
    System::Call 'kernel32::GetFileInformationByHandleEx(p r0, i 18, p r9, i 24) i.r1'
    StrCmp $1 0 lease_done
    System::Call '*$9(i.r3, i.r4, i.r5, i.r6, i.r7, i.r8)'
    StrCpy $GuardIdentity "$3:$4:$5:$6:$7:$8"
    StrCpy $GuardHandle $0
    StrCpy $0 -1 ; transfer ownership to caller; do not close the verified object
    StrCpy $SetupCode 0
lease_done:
    StrCmp $9 0 +2
        System::Free $9
    StrCmp $0 -1 lease_return
    System::Call 'kernel32::CloseHandle(p r0) i.r1'
    StrCmp $1 0 0 lease_return
    StrCpy $GuardHandle $0
    StrCpy $GuardLastError 0 ; a failed close is never a missing-path result
lease_return:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; GuardSid is a pointer into a live, validated native security descriptor.
; Match the application's private-boundary policy: current user, SYSTEM or BA.
Function ${PREFIX}ValidateApprovedSid
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 11
    StrCpy $0 0
    StrCmp $SetupOwnerSid "" sid_done
    StrCmp $GuardSid 0 sid_done
    System::Call 'advapi32::IsValidSid(p $GuardSid) i.r1'
    StrCmp $1 0 sid_done
    System::Call 'advapi32::ConvertSidToStringSidW(p $GuardSid, *p .r0) i.r1'
    StrCmp $1 0 sid_done
    StrCmp $0 0 sid_done
    System::Call 'kernel32::lstrlenW(p r0) i.r1'
    StrCmp $1 0 sid_done
    IntCmpU $1 ${NSIS_MAX_STRLEN} sid_done 0 sid_done
    System::Call 'kernel32::lstrcpynW(w .r2, p r0, i ${NSIS_MAX_STRLEN}) p'
    StrCmp $2 $SetupOwnerSid sid_approved
    StrCmp $2 "S-1-5-18" sid_approved
    StrCmp $2 "S-1-5-32-544" 0 sid_done
sid_approved:
    StrCpy $SetupCode 0
sid_done:
    StrCmp $0 0 +2
        System::Call 'kernel32::LocalFree(p r0) p'
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Product objects only, NOT shared Windows/profile ancestors. Read the retained
; file/directory handle; never reopen by path and never repair an unknown ACL.
; Require a protected, present, non-null DACL and an approved owner. As in the
; app's AclPolicy, effective allow entries may grant rights only to approved SIDs;
; unsupported effective ACE forms refuse rather than guessing their semantics.
Function ${PREFIX}ValidatePrivateHandleAcl
    !insertmacro SetupSaveRegisters
    Push $GuardSid
    Push $GuardValue
    StrCpy $SetupCode 11
    StrCpy $0 0
    StrCpy $9 0
    StrCmp $GuardHandle 0 acl_done
    StrCmp $GuardHandle -1 acl_done
    ; SE_FILE_OBJECT=1, OWNER_SECURITY_INFORMATION|DACL_SECURITY_INFORMATION=5.
    System::Call 'advapi32::GetSecurityInfo(p $GuardHandle, i 1, i 5, *p .r0, p 0, *p .r1, p 0, *p .r9) i.r2'
    StrCmp $2 0 0 acl_clear_owner
    StrCmp $9 0 acl_clear_owner
    StrCmp $1 0 acl_clear_owner
    System::Call 'advapi32::IsValidSecurityDescriptor(p r9) i.r2'
    StrCmp $2 0 acl_clear_owner
    StrCpy $GuardSid $0
    StrCpy $0 0 ; owner is part of r9, not a separate allocation
    Call ${PREFIX}ValidateApprovedSid
    StrCmp $SetupCode 0 0 acl_done
    StrCpy $SetupCode 11
    ; Control is a WORD; initialize the DWORD output so high bits are zero.
    ; System syntax is input 0 then output r2, without an extra ignored field.
    System::Call 'advapi32::GetSecurityDescriptorControl(p r9, *i 0 r2, *i .r3) i.r4'
    StrCmp $4 0 acl_done
    IntOp $2 $2 & 0x1004 ; SE_DACL_PROTECTED | SE_DACL_PRESENT
    IntCmp $2 0x1004 0 acl_done acl_done
    System::Call 'advapi32::IsValidAcl(p r1) i.r2'
    StrCmp $2 0 acl_done
    System::Alloc 12 ; ACL_SIZE_INFORMATION: three DWORDs
    Pop $0
    StrCmp $0 0 acl_done
    System::Call 'advapi32::GetAclInformation(p r1, p r0, i 12, i 2) i.r2'
    StrCmp $2 0 acl_done
    System::Call '*$0(i.r3, i, i)'
    System::Free $0
    StrCpy $0 0
    StrCpy $4 0
acl_next:
    IntCmpU $4 $3 acl_approved 0 acl_done
    System::Call 'advapi32::GetAce(p r1, i r4, *p .r5) i.r2'
    StrCmp $2 0 acl_done
    StrCmp $5 0 acl_done
    ; ACE_HEADER byte/byte/WORD, then ACCESS_MASK. SID starts at byte 8.
    System::Call '*$5(&i1.r6, &i1.r7, &i2.r2)'
    IntOp $7 $7 & 8 ; INHERIT_ONLY_ACE does not grant access to this object
    StrCmp $7 0 0 acl_advance
    StrCmp $6 0 acl_allow
    StrCmp $6 1 acl_advance acl_done ; common deny is safe; other forms unknown
acl_allow:
    IntCmpU $2 16 0 acl_done 0 ; header+mask+minimum SID, before reading SID
    System::Call '*$5(i, i.r8)'
    StrCmp $8 0 acl_advance
    ; Bound the SID's subauthority array to this ACE before IsValidSid reads it.
    IntOp $GuardValue $5 + 9 ; SID SubAuthorityCount is ACE+8+1
    System::Call '*$GuardValue(&i1.r8)'
    IntOp $8 $8 * 4
    IntOp $8 $8 + 16 ; ACE header+mask (8) plus SID header (8)
    IntCmpU $2 $8 0 acl_done 0
    IntOp $GuardSid $5 + 8
    Call ${PREFIX}ValidateApprovedSid
    StrCmp $SetupCode 0 0 acl_done
    StrCpy $SetupCode 11
acl_advance:
    IntOp $4 $4 + 1
    Goto acl_next
acl_approved:
    StrCpy $SetupCode 0
    Goto acl_done
acl_clear_owner:
    StrCpy $0 0
acl_done:
    StrCmp $0 0 +2
        System::Free $0
    StrCmp $9 0 +2
        System::Call 'kernel32::LocalFree(p r9) p'
    Pop $GuardValue
    Pop $GuardSid
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Existing %LocalAppData%\Programs is a shared container, not a product-private
; object. Accept inherited DACLs, but require a valid owner and a present,
; non-null, valid DACL. No ACL is repaired or rewritten. Only basic allow/deny
; ACEs are interpreted; an unknown ACE is accepted only when INHERIT_ONLY is
; set and neither object nor container inheritance can carry it to descendants.
; For non-approved allow SIDs, only known read-only file/directory rights pass.
; This rejects GENERIC_WRITE/ALL, FILE_ADD_FILE (2), FILE_ADD_SUBDIRECTORY (4),
; FILE_WRITE_EA (16), FILE_WRITE_ATTRIBUTES (256), FILE_DELETE_CHILD (64),
; DELETE (0x10000), WRITE_DAC (0x40000), WRITE_OWNER (0x80000), and
; ACCESS_SYSTEM_SECURITY (0x01000000), as well as other non-read rights.
Function ${PREFIX}ValidateSharedProgramsHandleAcl
    !insertmacro SetupSaveRegisters
    Push $GuardSid
    StrCpy $SetupCode 11
    StrCpy $0 0
    StrCpy $8 0 ; ConvertSidToStringSidW allocation
    StrCpy $9 0 ; GetSecurityInfo allocation
    StrCmp $GuardHandle 0 shared_acl_done
    StrCmp $GuardHandle -1 shared_acl_done
    ; SE_FILE_OBJECT=1, OWNER_SECURITY_INFORMATION|DACL_SECURITY_INFORMATION=5.
    System::Call 'advapi32::GetSecurityInfo(p $GuardHandle, i 1, i 5, *p .r0, p 0, *p .r1, p 0, *p .r9) i.r2'
    StrCmp $2 0 0 shared_acl_done
    StrCmp $9 0 shared_acl_done
    StrCmp $0 0 shared_acl_done
    StrCmp $1 0 shared_acl_done ; rejects both absent and null DACLs
    System::Call 'advapi32::IsValidSecurityDescriptor(p r9) i.r2'
    StrCmp $2 0 shared_acl_done
    StrCpy $GuardSid $0
    Call ${PREFIX}ValidateApprovedSid
    StrCmp $SetupCode 0 0 shared_acl_done
    StrCpy $SetupCode 11
    ; SE_DACL_PRESENT is required. Do not require SE_DACL_PROTECTED here.
    ; System syntax is input 0 then output r2, without an extra ignored field.
    System::Call 'advapi32::GetSecurityDescriptorControl(p r9, *i 0 r2, *i .r3) i.r4'
    StrCmp $4 0 shared_acl_done
    IntOp $2 $2 & 0x4
    StrCmp $2 0 shared_acl_done
    System::Call 'advapi32::IsValidAcl(p r1) i.r2'
    StrCmp $2 0 shared_acl_done
    System::Alloc 12 ; ACL_SIZE_INFORMATION: three DWORDs
    Pop $0
    StrCmp $0 0 shared_acl_done
    System::Call 'advapi32::GetAclInformation(p r1, p r0, i 12, i 2) i.r2'
    StrCmp $2 0 shared_acl_info_failed
    System::Call '*$0(i.r3, i, i)'
    System::Free $0
    StrCpy $0 0
    StrCpy $4 0
shared_acl_next:
    IntCmpU $4 $3 shared_acl_approved 0 shared_acl_done
    System::Call 'advapi32::GetAce(p r1, i r4, *p .r5) i.r2'
    StrCmp $2 0 shared_acl_done
    StrCmp $5 0 shared_acl_done
    ; ACE_HEADER byte/byte/WORD, then ACCESS_MASK. Basic ACE SID starts at byte 8.
    System::Call '*$5(&i1.r6, &i1.r7, &i2.r2)'
    StrCmp $6 0 shared_acl_allow
    StrCmp $6 1 shared_acl_deny
    ; Unknown ACEs must not apply to this directory or any child.
    IntOp $0 $7 & 8 ; INHERIT_ONLY_ACE
    StrCmp $0 0 shared_acl_done
    IntOp $0 $7 & 3 ; OBJECT_INHERIT_ACE | CONTAINER_INHERIT_ACE
    StrCmp $0 0 shared_acl_advance
    Goto shared_acl_done
shared_acl_allow:
    ; Basic allow/deny ACEs use only the five defined inheritance flag bits.
    IntOp $0 $7 & 0xE0
    StrCmp $0 0 0 shared_acl_done
    IntCmpU $2 16 shared_acl_allow_size_ok shared_acl_done shared_acl_allow_size_ok
shared_acl_deny:
    IntOp $0 $7 & 0xE0
    StrCmp $0 0 0 shared_acl_done
    IntCmpU $2 16 shared_acl_deny_size_ok shared_acl_done shared_acl_deny_size_ok
shared_acl_allow_size_ok:
    StrCpy $7 0 ; remember an allow ACE after its flags have been checked
    IntOp $GuardSid $5 + 8
    Goto shared_acl_validate_sid
shared_acl_deny_size_ok:
    StrCpy $7 1 ; remember a deny ACE after its flags have been checked
    IntOp $GuardSid $5 + 8
shared_acl_validate_sid:
    ; Bound the SID using its subauthority count before IsValidSid reads it.
    IntOp $0 $GuardSid + 1
    System::Call '*$0(&i1.r6)'
    IntCmpU $6 15 shared_acl_sid_count_ok shared_acl_sid_count_ok shared_acl_done
shared_acl_sid_count_ok:
    IntOp $6 $6 * 4
    IntOp $6 $6 + 8
    IntOp $0 $2 - 8 ; bytes available after the ACE header and access mask
    IntCmpU $6 $0 shared_acl_sid_size_ok shared_acl_done shared_acl_done
shared_acl_sid_size_ok:
    System::Call 'advapi32::IsValidSid(p $GuardSid) i.r0'
    StrCmp $0 0 shared_acl_done
    StrCmp $7 1 shared_acl_advance ; deny ACE can only restrict access
    Goto shared_acl_allow_sid
shared_acl_allow_sid:
    System::Call 'advapi32::ConvertSidToStringSidW(p $GuardSid, *p .r8) i.r0'
    StrCmp $0 0 shared_acl_done
    StrCmp $8 0 shared_acl_done
    System::Call 'kernel32::lstrlenW(p r8) i.r0'
    StrCmp $0 0 shared_acl_done
    IntCmpU $0 ${NSIS_MAX_STRLEN} shared_acl_done 0 shared_acl_done
    System::Call 'kernel32::lstrcpynW(w .r6, p r8, i ${NSIS_MAX_STRLEN}) p'
    System::Call 'kernel32::LocalFree(p r8) p'
    StrCpy $8 0
    StrCmp $6 $SetupOwnerSid shared_acl_advance
    StrCmp $6 "S-1-5-18" shared_acl_advance
    StrCmp $6 "S-1-5-32-544" shared_acl_advance
    System::Call '*$5(i, i.r2)'
    IntOp $0 $2 & ${SETUP_SHARED_FORBIDDEN_WRITE_MASK}
    StrCmp $0 0 shared_acl_check_read_mask
    Goto shared_acl_done
shared_acl_check_read_mask:
    ; FILE_GENERIC_READ/EXECUTE plus GENERIC_READ/EXECUTE and SYNCHRONIZE.
    ; Reject MAXIMUM_ALLOWED, reserved bits and every right outside this set.
    IntOp $0 $2 & ${SETUP_SHARED_NON_READ_MASK}
    StrCmp $0 0 shared_acl_advance
    Goto shared_acl_done
shared_acl_advance:
    StrCpy $8 0
    IntOp $4 $4 + 1
    Goto shared_acl_next
shared_acl_approved:
    StrCpy $SetupCode 0
    Goto shared_acl_done
shared_acl_info_failed:
    System::Free $0
    StrCpy $0 0
shared_acl_done:
    StrCmp $8 0 +2
        System::Call 'kernel32::LocalFree(p r8) p'
    StrCmp $9 0 +2
        System::Call 'kernel32::LocalFree(p r9) p'
    Pop $GuardSid
    !insertmacro SetupRestoreRegisters
FunctionEnd

; SHA-256 over the same synchronous disk-file handle. The caller retains its
; no-write/no-delete lease, and its original file position is restored even on
; failure. No pathname reopening, shell, external hashing utility, or fallback.
Function ${PREFIX}HashHandleSha256
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 13
    StrCpy $GuardHash ""
    StrCpy $0 0 ; CNG provider
    StrCpy $1 0 ; CNG hash, owns its own object allocation (Windows 7+)
    StrCpy $2 0 ; 64 KiB read buffer, reused for the 32-byte digest
    StrCpy $9 0 ; original file position has been obtained
    StrCmp $GuardDirectory 0 0 hash_done
    StrCmp $GuardHandle 0 hash_done
    StrCmp $GuardHandle -1 hash_done
    System::Call 'kernel32::GetFileType(p $GuardHandle) i.r5'
    StrCmp $5 1 0 hash_done ; FILE_TYPE_DISK
    System::Call 'kernel32::SetFilePointerEx(p $GuardHandle, l 0, *l .r3, i 1) i.r5'
    StrCmp $5 0 hash_done
    StrCpy $9 1
    System::Call 'kernel32::SetFilePointerEx(p $GuardHandle, l 0, p 0, i 0) i.r5'
    StrCmp $5 0 hash_done
    System::Call 'bcrypt::BCryptOpenAlgorithmProvider(*p .r0, w "SHA256", p 0, i 0) i.r5'
    StrCmp $5 0 0 hash_done
    System::Call 'bcrypt::BCryptCreateHash(p r0, *p .r1, p 0, i 0, p 0, i 0, i 0) i.r5'
    StrCmp $5 0 0 hash_done
    System::Alloc 65536
    Pop $2
    StrCmp $2 0 hash_done
hash_read:
    System::Call 'kernel32::ReadFile(p $GuardHandle, p r2, i 65536, *i .r4, p 0) i.r5'
    StrCmp $5 0 hash_done
    StrCmp $4 0 hash_finish
    IntCmpU $4 65536 0 0 hash_done
    System::Call 'bcrypt::BCryptHashData(p r1, p r2, i r4, i 0) i.r5'
    StrCmp $5 0 hash_read hash_done
hash_finish:
    System::Call 'bcrypt::BCryptFinishHash(p r1, p r2, i 32, i 0) i.r5'
    StrCmp $5 0 0 hash_done
    StrCpy $6 0
hash_hex:
    IntOp $8 $2 + $6
    System::Call '*$8(&i1.r4)'
    IntOp $4 $4 & 255
    IntFmt $7 "%02X" $4
    StrCpy $GuardHash "$GuardHash$7"
    IntOp $6 $6 + 1
    IntCmp $6 32 0 hash_hex 0
    StrCpy $SetupCode 0
hash_done:
    ${If} $1 != 0
        System::Call 'bcrypt::BCryptDestroyHash(p r1) i.r5'
        ${If} $5 != 0
            StrCpy $SetupCode 13
        ${EndIf}
    ${EndIf}
    ${If} $0 != 0
        System::Call 'bcrypt::BCryptCloseAlgorithmProvider(p r0, i 0) i.r5'
        ${If} $5 != 0
            StrCpy $SetupCode 13
        ${EndIf}
    ${EndIf}
    StrCmp $2 0 +2
        System::Free $2
    ${If} $9 == 1
        System::Call 'kernel32::SetFilePointerEx(p $GuardHandle, l r3, p 0, i 0) i.r5'
        ${If} $5 == 0
            StrCpy $SetupCode 13
        ${EndIf}
    ${EndIf}
    ${If} $SetupCode != 0
        StrCpy $GuardHash ""
    ${EndIf}
    !insertmacro SetupRestoreRegisters
FunctionEnd

Function ${PREFIX}ValidateHexText
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 11
    StrLen $0 $GuardValue
    StrCmp $0 $GuardValueLength 0 hex_done
    StrCmp $0 40 hex_start
    StrCmp $0 64 0 hex_done
hex_start:
    StrCpy $1 0
hex_next:
    StrCpy $2 $GuardValue 1 $1
    StrCpy $3 0
hex_digit:
    StrCpy $4 "0123456789ABCDEF" 1 $3
    StrCmp $2 $4 hex_advance
    IntOp $3 $3 + 1
    IntCmp $3 16 hex_done hex_digit hex_done
hex_advance:
    IntOp $1 $1 + 1
    IntCmp $1 $0 0 hex_next hex_done
    StrCpy $SetupCode 0
hex_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Reads ASCII schema/value characters from the held UTF-16LE receipt buffer.
; All receipt values are IDs/hashes/flags, never Unicode filesystem paths.
Function ${PREFIX}ReadReceiptLine
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 11
    StrCpy $GuardLine ""
    StrCmp $GuardRecordBuffer 0 line_done
    StrCpy $3 0
line_next:
    IntCmpU $GuardRecordOffset $GuardRecordSize line_done 0 line_done
    IntOp $0 $GuardRecordBuffer + $GuardRecordOffset
    System::Call '*$0(&i2.r1)'
    IntOp $GuardRecordOffset $GuardRecordOffset + 2
    StrCmp $1 13 line_cr
    IntCmpU $1 32 0 line_done 0
    IntCmpU $1 126 0 0 line_done
    IntOp $3 $3 + 1
    IntCmpU $3 ${NSIS_MAX_STRLEN} line_done 0 line_done
    IntFmt $2 "%c" $1
    StrCpy $GuardLine "$GuardLine$2"
    Goto line_next
line_cr:
    IntCmpU $GuardRecordOffset $GuardRecordSize line_done 0 line_done
    IntOp $0 $GuardRecordBuffer + $GuardRecordOffset
    System::Call '*$0(&i2.r1)'
    StrCmp $1 10 0 line_done
    IntOp $GuardRecordOffset $GuardRecordOffset + 2
    StrCpy $SetupCode 0
line_done:
    ${If} $SetupCode != 0
        StrCpy $GuardLine ""
    ${EndIf}
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Parse only the retained, already identity/ACL-checked receipt handle. Outputs
; are untrusted claims to cross-check against embedded input, actual files and
; HKCU; successful parsing alone is NOT product ownership or deletion authority.
Function ${PREFIX}ReadOwnedReceipt
    !insertmacro SetupSaveRegisters
    Push $GuardRecordBuffer
    Push $GuardRecordSize
    Push $GuardRecordOffset
    Push $GuardLine
    Push $GuardValue
    Push $GuardValueLength
    !insertmacro SetupClearReceipt
    StrCpy $SetupCode 11
    StrCpy $GuardRecordBuffer 0
    StrCpy $9 0 ; saved file position is valid
    StrCmp $GuardHandle 0 receipt_done
    StrCmp $GuardHandle -1 receipt_done
    StrCmp $GuardDirectory 0 0 receipt_done
    System::Alloc 32768
    Pop $GuardRecordBuffer
    StrCmp $GuardRecordBuffer 0 receipt_done
    System::Call 'kernel32::GetFileSizeEx(p $GuardHandle, p $GuardRecordBuffer) i.r2'
    StrCmp $2 0 receipt_done
    System::Call '*$GuardRecordBuffer(i.r0, i.r1)'
    StrCmp $1 0 0 receipt_done
    IntCmpU $0 4 0 receipt_done 0
    IntCmpU $0 32768 0 0 receipt_done
    IntOp $1 $0 & 1
    StrCmp $1 0 0 receipt_done
    StrCpy $GuardRecordSize $0
    System::Call 'kernel32::SetFilePointerEx(p $GuardHandle, l 0, *l .r8, i 1) i.r2'
    StrCmp $2 0 receipt_done
    StrCpy $9 1
    System::Call 'kernel32::SetFilePointerEx(p $GuardHandle, l 0, p 0, i 0) i.r2'
    StrCmp $2 0 receipt_done
    System::Call 'kernel32::ReadFile(p $GuardHandle, p $GuardRecordBuffer, i $GuardRecordSize, *i .r1, p 0) i.r2'
    StrCmp $2 0 receipt_done
    StrCmp $1 $GuardRecordSize 0 receipt_done
    System::Call '*$GuardRecordBuffer(&i2.r1)'
    IntCmp $1 0xFEFF 0 receipt_done receipt_done
    StrCpy $GuardRecordOffset 2
    !insertmacro SetupReceiptLiteral "${PREFIX}" "[Installation]"
    !insertmacro SetupReceiptLiteral "${PREFIX}" "schema=1"
    !insertmacro SetupReceiptLiteral "${PREFIX}" "productId=${SETUP_PRODUCT_ID}"
    !insertmacro SetupReceiptLiteral "${PREFIX}" "ownerSid=$SetupOwnerSid"
    !insertmacro SetupReceiptField "${PREFIX}" "appVersion" $SetupRecordedVersion
    !insertmacro SetupReceiptField "${PREFIX}" "sourceCommit" $SetupRecordedSource
    !insertmacro SetupReceiptHash "${PREFIX}" $SetupRecordedSource 40
    !insertmacro SetupReceiptField "${PREFIX}" "payloadSha256" $SetupRecordedAppHash
    !insertmacro SetupReceiptHash "${PREFIX}" $SetupRecordedAppHash 64
    !insertmacro SetupReceiptLiteral "${PREFIX}" "${SETUP_APP_NAME}=$SetupRecordedAppHash"
    !insertmacro SetupReceiptField "${PREFIX}" "${SETUP_UNINSTALLER_NAME}" $SetupRecordedUninstallerHash
    !insertmacro SetupReceiptHash "${PREFIX}" $SetupRecordedUninstallerHash 64
    !insertmacro SetupReceiptField "${PREFIX}" "${SETUP_NOTICE_NAME}" $SetupRecordedNoticeHash
    !insertmacro SetupReceiptHash "${PREFIX}" $SetupRecordedNoticeHash 64
    !insertmacro SetupReceiptLiteral "${PREFIX}" "startMenu=1"
    !insertmacro SetupReceiptField "${PREFIX}" "desktop" $SetupRecordedDesktop
    StrCmp $SetupRecordedDesktop 0 receipt_desktop_valid
    Goto receipt_done
receipt_desktop_valid:
    !insertmacro SetupReceiptField "${PREFIX}" "startMenuSha256" $SetupRecordedStartMenuHash
    !insertmacro SetupReceiptHash "${PREFIX}" $SetupRecordedStartMenuHash 64
    !insertmacro SetupReceiptField "${PREFIX}" "desktopSha256" $SetupRecordedDesktopHash
    StrCmp $SetupRecordedDesktopHash "none" 0 receipt_done
    StrCmp $GuardRecordOffset $GuardRecordSize 0 receipt_done
    ; First product release has no approved previous installations. A future
    ; reviewed predecessor table must be embedded, never learned from this file.
    StrCpy $SetupCode 13
    StrCmp $SetupRecordedVersion "${SETUP_APP_VERSION}" 0 receipt_done
    StrCmp $SetupRecordedSource "${SETUP_SOURCE_COMMIT}" 0 receipt_done
    StrCmp $SetupRecordedAppHash "${SETUP_APP_SHA256}" 0 receipt_done
    StrCmp $SetupRecordedNoticeHash "${SETUP_NOTICE_SHA256}" 0 receipt_done
    StrCpy $SetupCode 0
receipt_done:
    StrCmp $GuardRecordBuffer 0 +2
        System::Free $GuardRecordBuffer
    ${If} $9 == 1
        System::Call 'kernel32::SetFilePointerEx(p $GuardHandle, l r8, p 0, i 0) i.r2'
        ${If} $2 == 0
            StrCpy $SetupCode 11
        ${EndIf}
    ${EndIf}
    ${If} $SetupCode != 0
        !insertmacro SetupClearReceipt
    ${EndIf}
    Pop $GuardValueLength
    Pop $GuardValue
    Pop $GuardLine
    Pop $GuardRecordOffset
    Pop $GuardRecordSize
    Pop $GuardRecordBuffer
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Presence of a newly created named object is the lease. No wait/abandoned-owner
; shortcut: ANY existing object is a conflict because its security descriptor is
; not replaced by CreateMutex. Global covers every session of this same SID.
Function ${PREFIX}AcquireSetupMutex
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 12
    StrCpy $7 0
    StrCpy $8 0
    StrCpy $9 0
    StrCmp $SetupMutex "" mutex_new
    StrCmp $SetupMutex 0 0 mutex_done
mutex_new:
    StrCmp $SetupOwnerSid "" mutex_done
    StrCpy $0 $SetupOwnerSid
    StrLen $1 "Global\${SETUP_PRODUCT_ID}.Setup.$0"
    IntCmpU $1 260 mutex_done 0 mutex_done
    System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "O:$0D:P(A;;GA;;;$0)(A;;GA;;;SY)(A;;GA;;;BA)", i 1, *p .r8, p 0) i.r1'
    StrCmp $1 0 mutex_done
    StrCmp $8 0 mutex_done
    System::Call '*(i 12, p r8, i 0) p.r9'
    StrCmp $9 0 mutex_done
    System::Call 'kernel32::SetLastError(i 0)'
    System::Call 'kernel32::CreateMutexW(p r9, i 0, w "Global\${SETUP_PRODUCT_ID}.Setup.$0") p.r7 ?e'
    Pop $1
    StrCmp $7 0 mutex_done
    StrCmp $1 183 mutex_done ; ERROR_ALREADY_EXISTS, including same-user contention
    StrCmp $1 0 0 mutex_done
    StrCpy $SetupMutex $7
    StrCpy $7 0
    StrCpy $SetupCode 0
mutex_done:
    StrCmp $7 0 +2
        System::Call 'kernel32::CloseHandle(p r7) i'
    StrCmp $9 0 +2
        System::Free $9
    StrCmp $8 0 +2
        System::Call 'kernel32::LocalFree(p r8) p'
    !insertmacro SetupRestoreRegisters
FunctionEnd

Function ${PREFIX}ReleaseSetupMutex
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 0
    StrCmp $SetupMutex "" mutex_release_done
    StrCmp $SetupMutex 0 mutex_release_done
    System::Call 'kernel32::CloseHandle(p $SetupMutex) i.r0'
    ${If} $0 == 0
        StrCpy $SetupCode 12
    ${Else}
        StrCpy $SetupMutex 0
    ${EndIf}
mutex_release_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Requires the final-access app-file lease to remain held across this query AND
; the eventual operation. RM is supplementary, never permission to reopen a path,
; close somebody's handle, terminate a process, or schedule a reboot replacement.
; RM itself maintains OS session/registry bookkeeping; do not run outside G3.
Function ${PREFIX}CheckAppIdle
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 12
    StrCpy $9 0
    StrCpy $1 0 ; session started
    StrCmp $SetupMutex "" busy_done
    StrCmp $SetupMutex 0 busy_done
    StrCmp $GuardHandle 0 busy_done
    StrCmp $GuardHandle -1 busy_done
    StrCmp $GuardDirectory 0 0 busy_done
    StrCmp $GuardIdentity "" busy_done
    StrCmp $GuardHash "${SETUP_APP_SHA256}" 0 busy_done
    StrLen $2 $SetupFixedRoot
    StrLen $3 "\${SETUP_APP_NAME}"
    IntOp $2 $2 + $3
    IntCmpU $2 ${NSIS_MAX_STRLEN} busy_done 0 busy_done
    StrCmp $GuardPath "$SetupFixedRoot\${SETUP_APP_NAME}" 0 busy_done
    ; x86 layout: key[33] WCHAR at 0; path pointer at 68; one RM_PROCESS_INFO
    ; (12+512+128+16 = 668 bytes) at 72; four FILETIMEs at 740; path at 772.
    ; One slot is sufficient: any result other than solely this exact querying
    ; process means occupied. ERROR_MORE_DATA is a refusal, not an empty result.
    System::Alloc ${SETUP_RM_BYTES}
    Pop $9
    StrCmp $9 0 busy_done
    System::Call 'rstrtmgr::RmStartSession(*i .r0, i 0, p r9) i.r2'
    StrCmp $2 0 0 busy_done
    StrCpy $1 1
    IntOp $3 $9 + 772
    System::Call 'kernel32::lstrcpynW(p r3, w "$GuardPath", i ${NSIS_MAX_STRLEN}) p'
    IntOp $2 $9 + 68
    System::Call '*$2(p r3)'
    System::Call 'rstrtmgr::RmRegisterResources(i r0, i 1, p r2, i 0, p 0, i 0, p 0) i.r3'
    StrCmp $3 0 0 busy_done
    IntOp $2 $9 + 72
    System::Call 'rstrtmgr::RmGetList(i r0, *i .r6, *i 1 .r7, p r2, *i .r8) i.r3'
    StrCmp $3 0 0 busy_done
    IntOp $3 $8 & 0xFFFFFFEF ; only DetectedSelf (0x10) may be explained below
    StrCmp $3 0 0 busy_done
    StrCmp $7 0 busy_empty
    StrCmp $7 1 0 busy_done
    StrCmp $6 1 0 busy_done
    System::Call '*$2(i.r3)'
    System::Call 'kernel32::GetCurrentProcessId() i.r4'
    StrCmp $3 $4 0 busy_done
    IntOp $2 $9 + 740
    IntOp $3 $9 + 748
    IntOp $4 $9 + 756
    IntOp $5 $9 + 764
    System::Call 'kernel32::GetProcessTimes(p -1, p r2, p r3, p r4, p r5) i.r3'
    StrCmp $3 0 busy_done
    IntOp $3 $9 + 76 ; RM_UNIQUE_PROCESS.ProcessStartTime
    System::Call 'kernel32::CompareFileTime(p r2, p r3) i.r4'
    StrCmp $4 0 busy_clear busy_done
busy_empty:
    StrCmp $6 0 0 busy_done
    StrCmp $8 0 0 busy_done
busy_clear:
    StrCpy $SetupCode 0
busy_done:
    ${If} $1 == 1
        System::Call 'rstrtmgr::RmEndSession(i r0) i.r2'
        ${If} $2 != 0
            StrCpy $SetupCode 12
        ${EndIf}
    ${EndIf}
    StrCmp $9 0 +2
        System::Free $9
    !insertmacro SetupRestoreRegisters
FunctionEnd

; GetPath can return at most MAX_PATH WCHARs including NUL. Keep both the full
; expected target and the returned string strictly below 259 UTF-16 code units,
; so a possibly truncated 259-character result can never be accepted as equal.
; This capability check MUST precede any fresh-install product write. It does
; not limit source, payload, settings or backup paths.
Function ${PREFIX}ValidateShortcutTargetCapacity
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 11
    StrCpy $SetupAppPath ""
    StrCmp $SetupFixedRoot "" shortcut_capacity_done
    StrLen $0 $SetupFixedRoot
    StrLen $1 "\${SETUP_APP_NAME}"
    IntOp $0 $0 + $1
    IntCmpU $0 259 shortcut_capacity_done 0 shortcut_capacity_done
    StrCpy $SetupAppPath "$SetupFixedRoot\${SETUP_APP_NAME}"
    StrCpy $SetupCode 0
shortcut_capacity_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; The caller holds the shortcut's verified no-write/no-delete lease and compares
; its hash with the creation record. Deserialize only bytes read from that same
; handle: IPersistFile::Load would require reopening an exclusively held path.
; No Resolve, target existence query, FindData, environment expansion or launch.
Function ${PREFIX}ValidateShortcutBinding
    !insertmacro SetupSaveRegisters
    StrCpy $0 0 ; successful CoInitializeEx to balance
    StrCpy $1 0 ; IShellLinkW
    StrCpy $2 0 ; IPersistStream
    StrCpy $3 0 ; IStream
    StrCpy $4 0 ; bytes read from the retained handle
    StrCpy $9 0 ; saved file position
    Call ${PREFIX}ValidateShortcutTargetCapacity
    StrCmp $SetupCode 0 0 shortcut_done
    StrCpy $SetupCode 11
    StrCmp $GuardHandle 0 shortcut_done
    StrCmp $GuardHandle -1 shortcut_done
    StrCmp $GuardDirectory 0 0 shortcut_done
    System::Alloc 65536
    Pop $4
    StrCmp $4 0 shortcut_done
    System::Call 'kernel32::GetFileSizeEx(p $GuardHandle, p r4) i.r5'
    StrCmp $5 0 shortcut_done
    System::Call '*$4(i.r6, i.r5)'
    StrCmp $5 0 0 shortcut_done
    IntCmpU $6 76 0 shortcut_done 0 ; minimum ShellLinkHeader
    IntCmpU $6 65536 0 0 shortcut_done ; larger unknown links are preserved
    System::Call 'kernel32::SetFilePointerEx(p $GuardHandle, l 0, *l .r8, i 1) i.r5'
    StrCmp $5 0 shortcut_done
    StrCpy $9 1
    System::Call 'kernel32::SetFilePointerEx(p $GuardHandle, l 0, p 0, i 0) i.r5'
    StrCmp $5 0 shortcut_done
    System::Call 'kernel32::ReadFile(p $GuardHandle, p r4, i r6, *i .r7, p 0) i.r5'
    StrCmp $5 0 shortcut_done
    StrCmp $7 $6 0 shortcut_done
    System::Call 'shlwapi::SHCreateMemStream(p r4, i r6) p.r3'
    StrCmp $3 0 shortcut_done
    System::Call 'ole32::CoInitializeEx(p 0, i 2) i.r5'
    StrCmp $5 0 shortcut_com_initialized
    StrCmp $5 1 0 shortcut_done ; S_FALSE still requires CoUninitialize
shortcut_com_initialized:
    StrCpy $0 1
    System::Call 'ole32::CoCreateInstance(g "{00021401-0000-0000-C000-000000000046}", p 0, i 1, g "{000214F9-0000-0000-C000-000000000046}", *p .r1) i.r5'
    StrCmp $5 0 0 shortcut_done
    StrCmp $1 0 shortcut_done
    System::Call '$1->0(g "{00000109-0000-0000-C000-000000000046}", *p .r2) i.r5'
    StrCmp $5 0 0 shortcut_done
    StrCmp $2 0 shortcut_done
    ; IPersistStream::Load takes ONE IStream* argument. Do not use v312's
    ; COM.nsh Load macro, whose (p,i) declaration differs from the SDK interface.
    System::Call '$2->5(p r3) i.r5'
    StrCmp $5 0 0 shortcut_done
    System::Call '$1->3(w .r7, i 260, p 0, i 4) i.r5' ; SLGP_RAWPATH, pfd=NULL
    StrCmp $5 0 0 shortcut_done
    StrLen $6 $7
    IntCmpU $6 259 shortcut_done 0 shortcut_done
    StrCmp $7 $SetupAppPath 0 shortcut_done
    System::Call '$1->10(w .r7, i 260) i.r5'
    StrCmp $5 0 0 shortcut_done
    StrCmp $7 "" 0 shortcut_done
    System::Call '$1->8(w .r7, i 260) i.r5'
    StrCmp $5 0 0 shortcut_done
    StrLen $6 $7
    IntCmpU $6 259 shortcut_done 0 shortcut_done
    StrCmp $7 $SetupFixedRoot 0 shortcut_done
    StrCpy $SetupCode 0
shortcut_done:
    StrCmp $2 0 +2
        System::Call '$2->2() i'
    StrCmp $1 0 +2
        System::Call '$1->2() i'
    StrCmp $3 0 +2
        System::Call '$3->2() i'
    StrCmp $4 0 +2
        System::Free $4
    StrCmp $0 0 +2
        System::Call 'ole32::CoUninitialize()'
    ${If} $9 == 1
        System::Call 'kernel32::SetFilePointerEx(p $GuardHandle, l r8, p 0, i 0) i.r5'
        ${If} $5 == 0
            StrCpy $SetupCode 11
        ${EndIf}
    ${EndIf}
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Hold all existing ancestors of the independently derived fixed root. A missing
; Programs/product directory is reported by stopping at its already-held parent;
; this function does not create it or authorize accepting an existing directory.
Function ${PREFIX}PinExistingInstallAncestors
    !insertmacro SetupSaveRegisters
    Push $GuardPath
    Push $GuardDirectory
    Push $GuardHandle
    Push $GuardIdentity
    Push $GuardHash
    Push $GuardLastError
    Push $GuardValue
    Push $GuardValueLength
    StrCpy $SetupCode 11
    StrCpy $GuardProgramsParentPinned 0
    StrCmp $GuardPathPins 0 pins_allocate
    StrCmp $GuardPathPins "" 0 pins_done ; reject accidental double acquisition
pins_allocate:
    StrCpy $GuardPathPinCount 0
    StrCmp $SetupFixedRoot "" pins_done
    StrCpy $GuardPath $SetupFixedRoot
    Call ${PREFIX}ValidateLocalPathText
    StrCmp $SetupCode 0 0 pins_done
    System::Alloc ${SETUP_PIN_BYTES}
    Pop $GuardPathPins
    StrCmp $GuardPathPins 0 pins_error
    StrCpy $GuardDirectory 1
    StrCpy $2 3
    StrLen $3 $SetupFixedRoot
pins_next:
    StrCpy $GuardPath $SetupFixedRoot $2
    Call ${PREFIX}OpenPathIdentityLease
    ${If} $SetupCode != 0
        ; The opener retains an unclosed handle after CloseHandle failure.
        ; Transfer it to the existing pin table before local state is restored.
        StrCmp $GuardHandle 0 pins_failure_classify
        IntOp $0 $GuardPathPinCount * ${NSIS_PTR_SIZE}
        IntOp $0 $GuardPathPins + $0
        System::Call '*$0(p $GuardHandle)'
        IntOp $GuardPathPinCount $GuardPathPinCount + 1
        StrCpy $GuardHandle 0
pins_failure_classify:
        ${If} $GuardLastError == 2
        ${OrIf} $GuardLastError == 3
            StrCmp $GuardPath "$SetupLocalAppData\Programs" pins_success
            StrCmp $GuardPath $SetupFixedRoot pins_success
        ${EndIf}
        Goto pins_error
    ${EndIf}
    IntOp $0 $GuardPathPinCount * ${NSIS_PTR_SIZE}
    IntOp $0 $GuardPathPins + $0
    System::Call '*$0(p $GuardHandle)'
    IntOp $GuardPathPinCount $GuardPathPinCount + 1
    StrCmp $GuardPath "$SetupLocalAppData\Programs" pins_shared_programs_acl
    Goto pins_continue
pins_shared_programs_acl:
    Call ${PREFIX}ValidateSharedProgramsHandleAcl
    StrCmp $SetupCode 0 0 pins_error
    StrCpy $GuardProgramsParentPinned 1
pins_continue:
    IntCmpU $2 $3 pins_success
    ; At the drive root $2 already points at the first component, otherwise it
    ; points at its separating backslash. Include exactly the next component.
    StrCpy $0 $SetupFixedRoot 1 $2
    StrCmp $0 '\' 0 pins_scan
    IntOp $2 $2 + 1
pins_scan:
    IntCmpU $2 $3 pins_next
    StrCpy $0 $SetupFixedRoot 1 $2
    StrCmp $0 '\' pins_next
    IntOp $2 $2 + 1
    Goto pins_scan
pins_success:
    StrCpy $SetupCode 0
    Goto pins_done
pins_error:
    StrCpy $GuardProgramsParentPinned 0
    Call ${PREFIX}ReleasePathPins
    StrCpy $SetupCode 11
pins_done:
    Pop $GuardValueLength
    Pop $GuardValue
    Pop $GuardLastError
    Pop $GuardHash
    Pop $GuardIdentity
    Pop $GuardHandle
    Pop $GuardDirectory
    Pop $GuardPath
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Releases only handles allocated above. Does not touch product state or paths.
; Release failure is propagated; the table is kept for diagnosis/retry.
Function ${PREFIX}ReleasePathPins
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 0
    StrCpy $GuardProgramsParentPinned 0
    StrCmp $GuardPathPins "" release_done
    StrCmp $GuardPathPins 0 release_done
release_next:
    StrCmp $GuardPathPinCount 0 release_table
    IntOp $1 $GuardPathPinCount - 1
    IntOp $0 $1 * ${NSIS_PTR_SIZE}
    IntOp $0 $GuardPathPins + $0
    System::Call '*$0(p.r2)'
    System::Call 'kernel32::CloseHandle(p r2) i.r3'
    ${If} $3 == 0
        StrCpy $SetupCode 11
        Goto release_done
    ${EndIf}
    StrCpy $GuardPathPinCount $1
    Goto release_next
release_table:
    System::Free $GuardPathPins
    StrCpy $GuardPathPins 0
release_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Read-only prerequisite for an EXISTING product root. ValidateHost independently
; derives current SID and Known Folder; directory arguments must still match it.
; On success GuardHandle and GuardPathPins remain live until the paired release.
; This does not validate receipt, files, shortcuts or HKCU product metadata and
; must never be used alone to authorize an install/uninstall mutation.
Function ${PREFIX}AcquireFixedRootOwnershipLease
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 11
    StrCmp $GuardHandle "" root_handle_free
    StrCmp $GuardHandle 0 root_handle_free
    Goto root_done
root_handle_free:
    StrCmp $GuardPathPins "" root_pins_free
    StrCmp $GuardPathPins 0 root_pins_free
    Goto root_done
root_pins_free:
    Call ${PREFIX}ValidateHost
    StrCmp $SetupCode 0 0 root_done
    StrCmp $SetupMode "install" root_mode_ok
    StrCmp $SetupMode "uninstall" 0 root_refuse
root_mode_ok:
    Call ${PREFIX}ValidateDirectoryArguments
    StrCmp $SetupCode 0 0 root_done
    Call ${PREFIX}PinExistingInstallAncestors
    StrCmp $SetupCode 0 0 root_release
    StrCpy $GuardPath $SetupFixedRoot
    StrCpy $GuardDirectory 1
    ; PinExistingInstallAncestors already holds this exact root with read/write
    ; sharing. Reopen it without DELETE access, then validate ACL and identity on
    ; the retained same-object handle; a product-file lease conflicts here.
    Call ${PREFIX}OpenPathIdentityLease
    StrCmp $SetupCode 0 0 root_release
    StrCmp $GuardHandle 0 root_release
    StrCmp $GuardIdentity "" root_release
    Call ${PREFIX}ValidatePrivateHandleAcl
    StrCmp $SetupCode 0 0 root_release
    StrCpy $SetupCode 0
    Goto root_done
root_release:
    Call ${PREFIX}ReleaseFixedRootOwnershipLease
root_refuse:
    StrCpy $SetupCode 11
root_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Release only temporary allocations owned by CreateFreshProductRoot. A failed
; LocalFree retains its pointer for a later paired-release retry.
Function ${PREFIX}ReleaseFreshRootScratch
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 0
    StrCmp $SetupFreshRootInfoBuffer "" fresh_scratch_attributes
    StrCmp $SetupFreshRootInfoBuffer 0 fresh_scratch_attributes
    System::Free $SetupFreshRootInfoBuffer
    StrCpy $SetupFreshRootInfoBuffer 0
fresh_scratch_attributes:
    StrCmp $SetupFreshRootSecurityAttributes "" fresh_scratch_descriptor
    StrCmp $SetupFreshRootSecurityAttributes 0 fresh_scratch_descriptor
    System::Free $SetupFreshRootSecurityAttributes
    StrCpy $SetupFreshRootSecurityAttributes 0
fresh_scratch_descriptor:
    StrCmp $SetupFreshRootSecurityDescriptor "" fresh_scratch_done
    StrCmp $SetupFreshRootSecurityDescriptor 0 fresh_scratch_done
    System::Call 'kernel32::LocalFree(p $SetupFreshRootSecurityDescriptor) p.r0'
    StrCmp $0 0 fresh_scratch_descriptor_freed
    StrCpy $SetupCode 11
    Goto fresh_scratch_done
fresh_scratch_descriptor_freed:
    StrCpy $SetupFreshRootSecurityDescriptor 0
fresh_scratch_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Retry only local allocations left behind by a failed journal preparation.
; A failed LocalFree keeps its pointer so a later paired-release attempt can retry.
Function ${PREFIX}ReleaseFreshInstallJournalScratch
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 0
    StrCpy $SetupJournalRecord ""
    StrCmp $SetupJournalInfoBuffer "" journal_scratch_attributes
    StrCmp $SetupJournalInfoBuffer 0 journal_scratch_attributes
    System::Free $SetupJournalInfoBuffer
    StrCpy $SetupJournalInfoBuffer 0
journal_scratch_attributes:
    StrCmp $SetupJournalSecurityAttributes "" journal_scratch_descriptor
    StrCmp $SetupJournalSecurityAttributes 0 journal_scratch_descriptor
    System::Free $SetupJournalSecurityAttributes
    StrCpy $SetupJournalSecurityAttributes 0
journal_scratch_descriptor:
    StrCmp $SetupJournalSecurityDescriptor "" journal_scratch_done
    StrCmp $SetupJournalSecurityDescriptor 0 journal_scratch_done
    System::Call 'kernel32::LocalFree(p $SetupJournalSecurityDescriptor) p.r0'
    StrCmp $0 0 journal_scratch_descriptor_freed
    StrCpy $SetupCode 13
    Goto journal_scratch_done
journal_scratch_descriptor_freed:
    StrCpy $SetupJournalSecurityDescriptor 0
journal_scratch_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

Function ${PREFIX}ReleaseFixedRootOwnershipLease
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 11
    Call ${PREFIX}ReleaseFreshRootScratch
    StrCmp $SetupCode 0 release_root_handles
    Goto release_root_done
release_root_handles:
    StrCpy $SetupCode 11
    StrCmp $GuardHandle "" release_root_pins
    StrCmp $GuardHandle 0 release_root_pins
    StrCmp $GuardHandle -1 release_root_done
    System::Call 'kernel32::CloseHandle(p $GuardHandle) i.r0'
    StrCmp $0 0 release_root_done
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
    StrCpy $GuardPath ""
    StrCpy $GuardDirectory 0
release_root_pins:
    Call ${PREFIX}ReleasePathPins
    StrCmp $SetupCode 0 0 release_root_done
    StrCpy $SetupCode 0
release_root_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Read-only consistency check for the four fixed product files and install.ini.
; Every path is derived only from the Known Folder root and the build contract.
; The receipt is untrusted input: it is compared with the current SID, embedded
; app/NOTICE hashes and actual hashes from the retained same-object handles. A
; successful result is a fact snapshot only, never overwrite/delete authority.
; GuardPathPins/Count and all successfully opened handles remain owned until the
; paired Release... call; do not compose this helper into the formal installer yet.
Function ${PREFIX}CheckFixedInstallFilesAndReceipt
    !insertmacro SetupSaveRegisters
    Push $GuardPath
    Push $GuardDirectory
    Push $GuardIdentity
    Push $GuardHash
    Push $GuardLastError
    StrCpy $SetupCode 11
    StrCmp $SetupFixedFilesLeaseActive "" fixed_files_check_idle
    StrCmp $SetupFixedFilesLeaseActive 0 fixed_files_check_idle
    Goto fixed_files_check_done
fixed_files_check_idle:
    StrCmp $SetupFixedFilesRootHandle "" fixed_files_check_root_slot
    StrCmp $SetupFixedFilesRootHandle 0 fixed_files_check_root_slot
    Goto fixed_files_check_done
fixed_files_check_root_slot:
    StrCmp $SetupFixedFilesAppHandle "" fixed_files_check_app_slot
    StrCmp $SetupFixedFilesAppHandle 0 fixed_files_check_app_slot
    Goto fixed_files_check_done
fixed_files_check_app_slot:
    StrCmp $SetupFixedFilesUninstallerHandle "" fixed_files_check_uninstaller_slot
    StrCmp $SetupFixedFilesUninstallerHandle 0 fixed_files_check_uninstaller_slot
    Goto fixed_files_check_done
fixed_files_check_uninstaller_slot:
    StrCmp $SetupFixedFilesNoticeHandle "" fixed_files_check_notice_slot
    StrCmp $SetupFixedFilesNoticeHandle 0 fixed_files_check_notice_slot
    Goto fixed_files_check_done
fixed_files_check_notice_slot:
    StrCmp $SetupFixedFilesReceiptHandle "" fixed_files_check_receipt_slot
    StrCmp $SetupFixedFilesReceiptHandle 0 fixed_files_check_receipt_slot
    Goto fixed_files_check_done
fixed_files_check_receipt_slot:
    StrCmp $GuardHandle "" fixed_files_check_scratch_handle
    StrCmp $GuardHandle 0 fixed_files_check_scratch_handle
    Goto fixed_files_check_done
fixed_files_check_scratch_handle:
    StrCmp $GuardPathPins "" fixed_files_check_acquire
    StrCmp $GuardPathPins 0 fixed_files_check_acquire
    Goto fixed_files_check_done

fixed_files_check_acquire:
    ; From this point every residual root/pin/file resource was acquired by this
    ; check, including a handle retained after a failed CloseHandle.
    StrCpy $SetupFixedFilesLeaseActive 1
    Call ${PREFIX}AcquireFixedRootOwnershipLease
    StrCmp $GuardHandle "" fixed_files_root_capture_done
    StrCmp $GuardHandle 0 fixed_files_root_capture_done
    StrCpy $SetupFixedFilesRootHandle $GuardHandle
    StrCpy $GuardHandle 0
    StrCmp $SetupCode 0 fixed_files_root_capture_identity
    StrCpy $SetupFixedFilesRootIdentity ""
    Goto fixed_files_root_capture_done
fixed_files_root_capture_identity:
    StrCpy $SetupFixedFilesRootIdentity $GuardIdentity
fixed_files_root_capture_done:
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
    StrCmp $SetupCode 0 0 fixed_files_check_failed
    StrCmp $SetupOwnerSid "" fixed_files_check_failed
    StrCmp $SetupFixedRoot "" fixed_files_check_failed
    StrCmp $SetupFixedFilesRootHandle 0 fixed_files_check_failed
    StrCmp $SetupFixedFilesRootIdentity "" fixed_files_check_failed
    StrCmp $GuardPathPins "" fixed_files_check_failed
    StrCmp $GuardPathPins 0 fixed_files_check_failed
    StrCmp $GuardPathPinCount 0 fixed_files_check_failed

    StrCpy $GuardPath "$SetupFixedRoot\${SETUP_APP_NAME}"
    StrCpy $GuardDirectory 0
    Call ${PREFIX}OpenProductIdentityLease
    StrCmp $GuardHandle "" fixed_files_app_capture_done
    StrCmp $GuardHandle 0 fixed_files_app_capture_done
    StrCpy $SetupFixedFilesAppHandle $GuardHandle
    StrCpy $GuardHandle 0
    StrCmp $SetupCode 0 fixed_files_app_capture_identity
    StrCpy $SetupFixedFilesAppIdentity ""
    StrCpy $SetupFixedFilesAppHash ""
    Goto fixed_files_app_capture_done
fixed_files_app_capture_identity:
    StrCpy $SetupFixedFilesAppIdentity $GuardIdentity
    StrCpy $SetupFixedFilesAppHash $GuardHash
fixed_files_app_capture_done:
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
    StrCmp $SetupCode 0 0 fixed_files_check_failed
    StrCmp $SetupFixedFilesAppHandle 0 fixed_files_check_failed
    StrCmp $SetupFixedFilesAppIdentity "" fixed_files_check_failed
    StrCmp $SetupFixedFilesAppHash "" fixed_files_check_failed

    StrCpy $GuardPath "$SetupFixedRoot\${SETUP_UNINSTALLER_NAME}"
    StrCpy $GuardDirectory 0
    Call ${PREFIX}OpenProductIdentityLease
    StrCmp $GuardHandle "" fixed_files_uninstaller_capture_done
    StrCmp $GuardHandle 0 fixed_files_uninstaller_capture_done
    StrCpy $SetupFixedFilesUninstallerHandle $GuardHandle
    StrCpy $GuardHandle 0
    StrCmp $SetupCode 0 fixed_files_uninstaller_capture_identity
    StrCpy $SetupFixedFilesUninstallerIdentity ""
    StrCpy $SetupFixedFilesUninstallerHash ""
    Goto fixed_files_uninstaller_capture_done
fixed_files_uninstaller_capture_identity:
    StrCpy $SetupFixedFilesUninstallerIdentity $GuardIdentity
    StrCpy $SetupFixedFilesUninstallerHash $GuardHash
fixed_files_uninstaller_capture_done:
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
    StrCmp $SetupCode 0 0 fixed_files_check_failed
    StrCmp $SetupFixedFilesUninstallerHandle 0 fixed_files_check_failed
    StrCmp $SetupFixedFilesUninstallerIdentity "" fixed_files_check_failed
    StrCmp $SetupFixedFilesUninstallerHash "" fixed_files_check_failed

    StrCpy $GuardPath "$SetupFixedRoot\${SETUP_NOTICE_NAME}"
    StrCpy $GuardDirectory 0
    Call ${PREFIX}OpenProductIdentityLease
    StrCmp $GuardHandle "" fixed_files_notice_capture_done
    StrCmp $GuardHandle 0 fixed_files_notice_capture_done
    StrCpy $SetupFixedFilesNoticeHandle $GuardHandle
    StrCpy $GuardHandle 0
    StrCmp $SetupCode 0 fixed_files_notice_capture_identity
    StrCpy $SetupFixedFilesNoticeIdentity ""
    StrCpy $SetupFixedFilesNoticeHash ""
    Goto fixed_files_notice_capture_done
fixed_files_notice_capture_identity:
    StrCpy $SetupFixedFilesNoticeIdentity $GuardIdentity
    StrCpy $SetupFixedFilesNoticeHash $GuardHash
fixed_files_notice_capture_done:
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
    StrCmp $SetupCode 0 0 fixed_files_check_failed
    StrCmp $SetupFixedFilesNoticeHandle 0 fixed_files_check_failed
    StrCmp $SetupFixedFilesNoticeIdentity "" fixed_files_check_failed
    StrCmp $SetupFixedFilesNoticeHash "" fixed_files_check_failed

    StrCpy $GuardPath "$SetupFixedRoot\${SETUP_RECEIPT_NAME}"
    StrCpy $GuardDirectory 0
    Call ${PREFIX}OpenProductIdentityLease
    StrCmp $GuardHandle "" fixed_files_receipt_capture_done
    StrCmp $GuardHandle 0 fixed_files_receipt_capture_done
    StrCpy $SetupFixedFilesReceiptHandle $GuardHandle
    StrCpy $GuardHandle 0
    StrCmp $SetupCode 0 fixed_files_receipt_capture_identity
    StrCpy $SetupFixedFilesReceiptIdentity ""
    StrCpy $SetupFixedFilesReceiptHash ""
    Goto fixed_files_receipt_capture_done
fixed_files_receipt_capture_identity:
    StrCpy $SetupFixedFilesReceiptIdentity $GuardIdentity
    StrCpy $SetupFixedFilesReceiptHash $GuardHash
fixed_files_receipt_capture_done:
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
    StrCmp $SetupCode 0 0 fixed_files_check_failed
    StrCmp $SetupFixedFilesReceiptHandle 0 fixed_files_check_failed
    StrCmp $SetupFixedFilesReceiptIdentity "" fixed_files_check_failed
    StrCmp $SetupFixedFilesReceiptHash "" fixed_files_check_failed

    ; ReadOwnedReceipt consumes only the fixed retained receipt handle. Its exact
    ; ownerSid line is compared with the SID freshly derived by ValidateHost.
    StrCpy $GuardHandle $SetupFixedFilesReceiptHandle
    StrCpy $GuardIdentity $SetupFixedFilesReceiptIdentity
    StrCpy $GuardDirectory 0
    Call ${PREFIX}ReadOwnedReceipt
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
    StrCmp $SetupCode 0 0 fixed_files_check_failed

    ; Actual app == embedded app == receipt; the parser independently checks the
    ; receipt claim against the embedded hash before returning success.
    StrCmp $SetupFixedFilesAppHash "${SETUP_APP_SHA256}" 0 fixed_files_check_failed
    StrCmp $SetupRecordedAppHash $SetupFixedFilesAppHash 0 fixed_files_check_failed
    ; Actual NOTICE == embedded NOTICE == receipt.
    StrCmp $SetupFixedFilesNoticeHash "${SETUP_NOTICE_SHA256}" 0 fixed_files_check_failed
    StrCmp $SetupRecordedNoticeHash $SetupFixedFilesNoticeHash 0 fixed_files_check_failed
    ; There is no embedded expected uninstaller hash in this contract; only the
    ; actual held-object hash and parsed receipt claim can be compared here.
    StrCmp $SetupRecordedUninstallerHash $SetupFixedFilesUninstallerHash 0 fixed_files_check_failed
    StrCpy $SetupCode 0
    Goto fixed_files_check_done

fixed_files_check_failed:
    StrCpy $8 $SetupCode
    !insertmacro SetupClearReceipt
    Call ${PREFIX}ReleaseFixedInstallFilesAndReceipt
    StrCmp $SetupCode 0 fixed_files_check_restore_failure
    StrCpy $SetupCode 11
    Goto fixed_files_check_done
fixed_files_check_restore_failure:
    StrCpy $SetupCode $8
    Goto fixed_files_check_done
fixed_files_check_done:
    Pop $GuardLastError
    Pop $GuardHash
    Pop $GuardIdentity
    Pop $GuardDirectory
    Pop $GuardPath
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Close only handles retained by CheckFixedInstallFilesAndReceipt. A failed
; CloseHandle exits without clearing that handle or releasing later resources,
; so a caller can retry; ancestor pins are freed only after the root handle closes.
Function ${PREFIX}ReleaseFixedInstallFilesAndReceipt
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 11
    StrCmp $SetupFixedFilesLeaseActive 1 fixed_files_release_active
    StrCpy $SetupCode 0
    Goto fixed_files_release_done
fixed_files_release_active:
    StrCmp $SetupFixedFilesReceiptHandle "" fixed_files_release_notice
    StrCmp $SetupFixedFilesReceiptHandle 0 fixed_files_release_notice
    StrCmp $SetupFixedFilesReceiptHandle -1 fixed_files_release_done
    System::Call 'kernel32::CloseHandle(p $SetupFixedFilesReceiptHandle) i.r0'
    StrCmp $0 0 fixed_files_release_done
    StrCpy $SetupFixedFilesReceiptHandle 0
    StrCpy $SetupFixedFilesReceiptIdentity ""
    StrCpy $SetupFixedFilesReceiptHash ""
fixed_files_release_notice:
    StrCmp $SetupFixedFilesNoticeHandle "" fixed_files_release_uninstaller
    StrCmp $SetupFixedFilesNoticeHandle 0 fixed_files_release_uninstaller
    StrCmp $SetupFixedFilesNoticeHandle -1 fixed_files_release_done
    System::Call 'kernel32::CloseHandle(p $SetupFixedFilesNoticeHandle) i.r0'
    StrCmp $0 0 fixed_files_release_done
    StrCpy $SetupFixedFilesNoticeHandle 0
    StrCpy $SetupFixedFilesNoticeIdentity ""
    StrCpy $SetupFixedFilesNoticeHash ""
fixed_files_release_uninstaller:
    StrCmp $SetupFixedFilesUninstallerHandle "" fixed_files_release_app
    StrCmp $SetupFixedFilesUninstallerHandle 0 fixed_files_release_app
    StrCmp $SetupFixedFilesUninstallerHandle -1 fixed_files_release_done
    System::Call 'kernel32::CloseHandle(p $SetupFixedFilesUninstallerHandle) i.r0'
    StrCmp $0 0 fixed_files_release_done
    StrCpy $SetupFixedFilesUninstallerHandle 0
    StrCpy $SetupFixedFilesUninstallerIdentity ""
    StrCpy $SetupFixedFilesUninstallerHash ""
fixed_files_release_app:
    StrCmp $SetupFixedFilesAppHandle "" fixed_files_release_root
    StrCmp $SetupFixedFilesAppHandle 0 fixed_files_release_root
    StrCmp $SetupFixedFilesAppHandle -1 fixed_files_release_done
    System::Call 'kernel32::CloseHandle(p $SetupFixedFilesAppHandle) i.r0'
    StrCmp $0 0 fixed_files_release_done
    StrCpy $SetupFixedFilesAppHandle 0
    StrCpy $SetupFixedFilesAppIdentity ""
    StrCpy $SetupFixedFilesAppHash ""
fixed_files_release_root:
    StrCmp $SetupFixedFilesRootHandle "" fixed_files_release_pins
    StrCmp $SetupFixedFilesRootHandle 0 fixed_files_release_pins
    StrCmp $SetupFixedFilesRootHandle -1 fixed_files_release_done
    System::Call 'kernel32::CloseHandle(p $SetupFixedFilesRootHandle) i.r0'
    StrCmp $0 0 fixed_files_release_done
    StrCpy $SetupFixedFilesRootHandle 0
    StrCpy $SetupFixedFilesRootIdentity ""
fixed_files_release_pins:
    Call ${PREFIX}ReleasePathPins
    StrCmp $SetupCode 0 0 fixed_files_release_done
    StrCpy $SetupFixedFilesReceiptIdentity ""
    StrCpy $SetupFixedFilesReceiptHash ""
    StrCpy $SetupFixedFilesNoticeIdentity ""
    StrCpy $SetupFixedFilesNoticeHash ""
    StrCpy $SetupFixedFilesUninstallerIdentity ""
    StrCpy $SetupFixedFilesUninstallerHash ""
    StrCpy $SetupFixedFilesAppIdentity ""
    StrCpy $SetupFixedFilesAppHash ""
    StrCpy $SetupFixedFilesRootIdentity ""
    StrCpy $SetupFixedFilesLeaseActive 0
fixed_files_release_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Read-only first-install guard for an EXISTING empty fixed root. It acquires
; the root and its already-existing ancestors through the same current-user,
; Known Folder, identity, final-path and ACL checks as the paired root lease.
; Enumerate through that retained directory handle only; no path reopen, writes,
; deletes, receipt interpretation or product ownership inference occurs here.
; Any child, malformed/oversized record or unexpected API error refuses. On
; success the root and ancestor handles remain held until Release... is called.
; This snapshot is NOT permission to mutate: the future write transaction must
; repeat its complete same-object checks immediately before acting.
Function ${PREFIX}ValidateEmptyFixedRootDirectory
    !insertmacro SetupSaveRegisters
    StrCpy $9 0 ; cleanup may run before allocation on any early refusal
    StrCpy $SetupCode 11
    StrCmp $SetupMode "install" directory_mode_ok
    Goto directory_done
directory_mode_ok:
    Call ${PREFIX}AcquireFixedRootOwnershipLease
    StrCmp $SetupCode 0 0 directory_done
    StrCmp $GuardHandle 0 directory_reject
    StrCmp $GuardIdentity "" directory_reject
    StrCmp $GuardDirectory 1 0 directory_reject
    System::Alloc ${SETUP_DIRECTORY_INFO_BUFFER_BYTES}
    Pop $9
    StrCmp $9 0 directory_reject
    ; FILE_FULL_DIR_INFO requires an 8-byte-aligned base and each non-final
    ; NextEntryOffset to be 8-byte aligned. Refuse if the allocator cannot prove it.
    IntOp $0 $9 & 7
    StrCmp $0 0 0 directory_reject
    StrCpy $8 15 ; FileFullDirectoryRestartInfo for the first batch
    StrCpy $7 0 ; At most the synthetic "." and ".." entries may be skipped.
directory_query:
    StrCmp $8 15 directory_restart_query
    System::Call 'kernel32::GetFileInformationByHandleEx(p $GuardHandle, i 14, p $9, i ${SETUP_DIRECTORY_INFO_BUFFER_BYTES}) i.r0 ?e'
    Goto directory_query_result
directory_restart_query:
    System::Call 'kernel32::GetFileInformationByHandleEx(p $GuardHandle, i 15, p $9, i ${SETUP_DIRECTORY_INFO_BUFFER_BYTES}) i.r0 ?e'
directory_query_result:
    Pop $GuardLastError
    StrCmp $0 0 directory_query_failed
    StrCpy $GuardLastError 0
    StrCpy $8 14 ; FileFullDirectoryInfo resumes after the prior batch.
    StrCpy $3 0 ; byte offset of the current FILE_FULL_DIR_INFO record
directory_record:
    ; Verify the fixed header is within the supplied buffer before reading it.
    IntOp $4 $3 + ${SETUP_FULL_DIR_INFO_NAME_OFFSET}
    IntCmpU $4 ${SETUP_DIRECTORY_INFO_BUFFER_BYTES} directory_header_in_bounds directory_header_in_bounds directory_reject
directory_header_in_bounds:
    IntOp $0 $9 + $3
    IntOp $1 $0 + ${SETUP_FULL_DIR_INFO_NAME_LENGTH_OFFSET}
    System::Call '*$1(i.r2)'
    StrCmp $2 0 directory_reject
    IntOp $4 $2 & 1
    StrCmp $4 0 0 directory_reject ; UTF-16 byte length must be even.
    IntOp $4 $3 + ${SETUP_FULL_DIR_INFO_NAME_OFFSET}
    IntOp $6 ${SETUP_DIRECTORY_INFO_BUFFER_BYTES} - $4
    IntCmpU $2 $6 directory_name_in_bounds directory_name_in_bounds directory_reject
directory_name_in_bounds:
    ; Empty-directory enumeration may report only the two synthetic names.
    ; Every other name, including hidden/system files and child directories,
    ; makes the existing root foreign to this narrow first-install check.
    StrCmp $2 2 directory_name_dot
    StrCmp $2 4 directory_name_dotdot
    Goto directory_nonempty
directory_name_dot:
    IntOp $1 $0 + ${SETUP_FULL_DIR_INFO_NAME_OFFSET}
    System::Call '*$1(&i2.r5)'
    StrCmp $5 46 directory_dot_entry directory_nonempty
directory_name_dotdot:
    IntOp $1 $0 + ${SETUP_FULL_DIR_INFO_NAME_OFFSET}
    System::Call '*$1(&i2.r5)'
    StrCmp $5 46 0 directory_nonempty
    IntOp $1 $1 + 2
    System::Call '*$1(&i2.r6)'
    StrCmp $6 46 directory_dot_entry directory_nonempty
directory_dot_entry:
    IntOp $7 $7 + 1
    IntCmpU $7 2 directory_dot_count_ok directory_dot_count_ok directory_reject
directory_dot_count_ok:
    System::Call '*$0(i.r1)' ; NextEntryOffset, zero means this batch is done.
    StrCmp $1 0 directory_query
    IntOp $4 $1 & 7
    StrCmp $4 0 0 directory_reject
    ; The next record must follow this complete filename and remain inside the
    ; fixed buffer. Its own header/name bounds are checked on the next pass.
    IntOp $4 ${SETUP_FULL_DIR_INFO_NAME_OFFSET} + $2
    IntCmpU $1 $4 directory_next_record_stride_ok directory_reject directory_next_record_stride_ok
directory_next_record_stride_ok:
    ; Bound the unsigned offset before addition so a malicious/corrupt DWORD
    ; cannot wrap the NSIS 32-bit arithmetic into a small in-range pointer.
    IntOp $6 ${SETUP_DIRECTORY_INFO_BUFFER_BYTES} - $3
    IntCmpU $1 $6 directory_next_offset_bounded directory_next_offset_bounded directory_reject
directory_next_offset_bounded:
    IntOp $4 $3 + $1
    IntOp $6 ${SETUP_DIRECTORY_INFO_BUFFER_BYTES} - ${SETUP_FULL_DIR_INFO_NAME_OFFSET}
    IntCmpU $4 $6 directory_next_record_in_bounds directory_next_record_in_bounds directory_reject
directory_next_record_in_bounds:
    StrCpy $3 $4
    Goto directory_record
directory_query_failed:
    StrCmp $GuardLastError ${SETUP_ERROR_NO_MORE_FILES} directory_empty
    Goto directory_reject
directory_nonempty:
    Goto directory_reject
directory_empty:
    StrCpy $GuardLastError 0
    StrCpy $SetupCode 0
    System::Free $9
    StrCpy $9 0
    Goto directory_done
directory_reject:
    StrCpy $SetupCode 11
    StrCmp $9 0 directory_release_root
    System::Free $9
    StrCpy $9 0
directory_release_root:
    Call ${PREFIX}ReleaseFixedRootOwnershipLease
    StrCpy $SetupCode 11
directory_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Create only the fixed shared %LocalAppData%\Programs container. The caller
; receives the exact handle returned by CreateDirectory2W plus the existing
; ancestor pins; it must release them with ReleaseFixedRootOwnershipLease.
; This primitive is deliberately not connected to installer/uninstaller entry
; points. An existing Programs is reusable only when the ancestor scan already
; pinned and validated it; an object appearing after a missing scan is refused.
Function ${PREFIX}PrepareSharedProgramsParent
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 11
    StrCpy $GuardLastError 0
    StrCpy $9 0 ; GetFileInformationByHandle allocation
    StrCmp $SetupMode "install" programs_parent_mode_ok
    Goto programs_parent_done
programs_parent_mode_ok:
    StrCmp $GuardHandle "" programs_parent_handle_free
    StrCmp $GuardHandle 0 programs_parent_handle_free
    Goto programs_parent_done
programs_parent_handle_free:
    StrCmp $GuardPathPins 0 programs_parent_inputs_free
    StrCmp $GuardPathPins "" 0 programs_parent_done
programs_parent_inputs_free:
    StrCmp $GuardIdentity "" programs_parent_identity_free
    Goto programs_parent_done
programs_parent_identity_free:
    StrCmp $GuardHash "" 0 programs_parent_done
    Call ${PREFIX}ValidateHost
    StrCmp $SetupCode 0 0 programs_parent_done
    Call ${PREFIX}ValidateDirectoryArguments
    StrCmp $SetupCode 0 0 programs_parent_done
    StrCpy $GuardPath $SetupFixedRoot
    StrCpy $GuardDirectory 1
    Call ${PREFIX}PinExistingInstallAncestors
    StrCmp $SetupCode 0 0 programs_parent_done

    ; PinExistingInstallAncestors retains every existing ancestor of the
    ; validated root and marks Programs only if its handle passed shared ACL
    ; validation during that scan. Probe while those pins are held: a result
    ; found without that marker is a concurrently-created race object.
    StrCpy $GuardPath "$SetupLocalAppData\Programs"
    StrCpy $GuardDirectory 1
    Call ${PREFIX}OpenPathIdentityLease
    StrCmp $SetupCode 0 programs_parent_existing
    StrCmp $GuardHandle 0 programs_parent_probe_no_handle
    ; A failed close leaves a live handle; release it and refuse.
    Call ${PREFIX}ReleaseFixedRootOwnershipLease
    StrCpy $SetupCode 11
    Goto programs_parent_done
programs_parent_probe_no_handle:
    StrCmp $GuardProgramsParentPinned 1 programs_parent_refuse
    ${If} $GuardLastError == 2
    ${OrIf} $GuardLastError == 3
        Goto programs_parent_missing
    ${EndIf}
    Call ${PREFIX}ReleaseFixedRootOwnershipLease
    StrCpy $SetupCode 11
    Goto programs_parent_done
programs_parent_existing:
    ; The pin marker proves Programs existed during the ancestor scan and its
    ; pinned handle passed shared-ACL validation. A later appearance is a race.
    StrCmp $GuardProgramsParentPinned 1 programs_parent_existing_pinned
    Call ${PREFIX}ReleaseFixedRootOwnershipLease
    StrCpy $SetupCode 11
    Goto programs_parent_done
programs_parent_existing_pinned:
    ; OpenPathIdentityLease validated this returned same-object handle while
    ; the original Programs pin and all higher ancestors remained held.
    StrCmp $GuardHandle 0 programs_parent_refuse
    StrCmp $GuardIdentity "" programs_parent_refuse
    Call ${PREFIX}ValidateSharedProgramsHandleAcl
    StrCmp $SetupCode 0 0 programs_parent_refuse
    StrCpy $SetupCode 0
    Goto programs_parent_done
programs_parent_missing:
    System::Call 'kernel32::GetModuleHandleW(w "kernel32.dll") p.r1'
    StrCmp $1 0 programs_parent_refuse
    System::Call 'kernel32::GetProcAddress(p r1, m "CreateDirectory2W") p.r2'
    StrCmp $2 0 programs_parent_refuse
    ; FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | READ_CONTROL | SYNCHRONIZE; share read
    ; only (no write/delete opens); DISALLOW_PATH_REDIRECTS; inherit normal ACL.
    System::Call '::$2(w "$SetupLocalAppData\Programs", i 0x120081, i 1, i 1, p 0) p.r0 ?e'
    Pop $GuardLastError
    ; The current API page says NULL on failure but its example uses
    ; INVALID_HANDLE_VALUE. Treat either sentinel as failure; never adopt a
    ; path reopened after creation.
    StrCmp $0 0 programs_parent_refuse
    StrCmp $0 -1 programs_parent_refuse
    StrCpy $GuardHandle $0
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
    StrCpy $GuardLastError 0 ; LastError is undefined after a successful call.

    System::Alloc 52 ; BY_HANDLE_FILE_INFORMATION
    Pop $9
    StrCmp $9 0 programs_parent_validation_failed
    System::Call 'kernel32::GetFileInformationByHandle(p $GuardHandle, p r9) i.r1'
    StrCmp $1 0 programs_parent_validation_failed
    System::Call '*$9(i.r1, i, i, i, i, i, i, i, i, i, i.r5, i, i)'
    IntOp $2 $1 & 0x400 ; reject a reparse-point result
    StrCmp $2 0 0 programs_parent_validation_failed
    IntOp $2 $1 & 0x10 ; require a directory
    StrCmp $2 0 programs_parent_validation_failed
    System::Call 'kernel32::GetFinalPathNameByHandleW(p $GuardHandle, w .r1, i ${NSIS_MAX_STRLEN}, i 0) i.r2'
    StrCmp $2 0 programs_parent_validation_failed
    IntCmpU $2 ${NSIS_MAX_STRLEN} programs_parent_validation_failed 0 programs_parent_validation_failed
    StrCmp $1 "\\?\$SetupLocalAppData\Programs" 0 programs_parent_validation_failed
    ; Preserve the complete FILE_ID_INFO value; never reopen the path for ID.
    System::Call 'kernel32::GetFileInformationByHandleEx(p $GuardHandle, i 18, p r9, i 24) i.r1'
    StrCmp $1 0 programs_parent_validation_failed
    System::Call '*$9(i.r3, i.r4, i.r5, i.r6, i.r7, i.r8)'
    StrCpy $GuardIdentity "$3:$4:$5:$6:$7:$8"
    System::Free $9
    StrCpy $9 0
    Call ${PREFIX}ValidateSharedProgramsHandleAcl
    StrCmp $SetupCode 0 0 programs_parent_validation_failed
    StrCpy $SetupCode 0
    Goto programs_parent_done

programs_parent_validation_failed:
    StrCmp $9 0 +2
        System::Free $9
    StrCpy $9 0
programs_parent_refuse:
    ; Close handles only; the approved shared parent is never removed on failure.
    Call ${PREFIX}ReleaseFixedRootOwnershipLease
    StrCpy $SetupCode 11
programs_parent_done:
    StrCmp $9 0 +2
        System::Free $9
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Atomically create only the fixed product-root leaf under the parent lease
; returned by PrepareSharedProgramsParent. The returned root handle replaces
; GuardHandle; the parent handle is transferred into the retained pin table.
; The creator deliberately leaves a failed/unverified directory in place rather
; than deleting by path. This primitive is not connected to setup entry points.
Function ${PREFIX}CreateFreshProductRoot
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 11
    StrCmp $SetupMode "install" fresh_root_mode_ok
    Goto fresh_root_done
fresh_root_mode_ok:
    StrCmp $GuardHandle "" fresh_root_input_handle_empty
    StrCmp $GuardHandle 0 fresh_root_input_handle_empty
    Goto fresh_root_done
fresh_root_input_handle_empty:
    StrCmp $GuardPathPins "" fresh_root_input_pins_empty
    StrCmp $GuardPathPins 0 fresh_root_input_pins_empty
    Goto fresh_root_done
fresh_root_input_pins_empty:
    StrCmp $GuardIdentity "" fresh_root_input_identity_empty
    Goto fresh_root_done
fresh_root_input_identity_empty:
    StrCmp $GuardHash "" fresh_root_descriptor_empty
    Goto fresh_root_done
fresh_root_descriptor_empty:
    StrCmp $SetupFreshRootSecurityDescriptor "" fresh_root_descriptor_zero
    StrCmp $SetupFreshRootSecurityDescriptor 0 fresh_root_descriptor_zero
    Goto fresh_root_done
fresh_root_descriptor_zero:
    StrCmp $SetupFreshRootSecurityAttributes "" fresh_root_attributes_zero
    StrCmp $SetupFreshRootSecurityAttributes 0 fresh_root_attributes_zero
    Goto fresh_root_done
fresh_root_attributes_zero:
    StrCmp $SetupFreshRootInfoBuffer "" fresh_root_info_zero
    StrCmp $SetupFreshRootInfoBuffer 0 fresh_root_info_zero
    Goto fresh_root_done
fresh_root_info_zero:
    Call ${PREFIX}PrepareSharedProgramsParent
    StrCmp $SetupCode 0 fresh_root_parent_ready
    Goto fresh_root_failure
fresh_root_parent_ready:
    StrCmp $GuardHandle 0 fresh_root_failure
    StrCmp $GuardIdentity "" fresh_root_failure
    StrCmp $GuardPathPins 0 fresh_root_failure
    StrCmp $GuardPathPins "" fresh_root_failure
    StrCmp $GuardPathPinCount 0 fresh_root_failure
    ; Reserve one more pointer slot before the irreversible directory create.
    IntCmpU $GuardPathPinCount ${NSIS_MAX_STRLEN} fresh_root_failure fresh_root_pin_capacity_ok fresh_root_failure
fresh_root_pin_capacity_ok:
    ; Create a protected DACL for only the current SID, SYSTEM and administrators.
    StrCpy $0 $SetupOwnerSid
    StrCpy $8 0
    System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "O:$0D:P(A;;GA;;;$0)(A;;GA;;;SY)(A;;GA;;;BA)", i 1, *p .r8, p 0) i.r1'
    StrCmp $8 0 fresh_root_failure
    StrCpy $SetupFreshRootSecurityDescriptor $8
    StrCmp $1 0 fresh_root_failure
    StrCpy $9 0
    System::Call '*(i 12, p r8, i 0) p.r9' ; x86 SECURITY_ATTRIBUTES, non-inheritable
    StrCmp $9 0 fresh_root_failure
    StrCpy $SetupFreshRootSecurityAttributes $9
    System::Alloc 52 ; BY_HANDLE_FILE_INFORMATION, allocate before creating the leaf
    Pop $SetupFreshRootInfoBuffer
    StrCmp $SetupFreshRootInfoBuffer 0 fresh_root_failure

    System::Call 'kernel32::GetModuleHandleW(w "kernel32.dll") p.r1'
    StrCmp $1 0 fresh_root_create_refused
    System::Call 'kernel32::GetProcAddress(p r1, m "CreateDirectory2W") p.r2'
    StrCmp $2 0 fresh_root_create_refused
    ; FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | READ_CONTROL | SYNCHRONIZE; share read;
    ; DISALLOW_PATH_REDIRECTS; apply the protected current-user DACL at creation.
    System::Call '::$2(w "$SetupFixedRoot", i 0x120081, i 1, i 1, p $SetupFreshRootSecurityAttributes) p.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 0 fresh_root_create_refused
    StrCmp $0 -1 fresh_root_create_refused
    StrCpy $1 $0 ; retain this same returned product-root handle
    ; Do not overwrite the Programs handle: transfer its ownership to the path
    ; pin array first, so the paired release closes both handles exactly once.
    IntOp $0 $GuardPathPinCount * ${NSIS_PTR_SIZE}
    IntOp $0 $GuardPathPins + $0
    System::Call '*$0(p $GuardHandle)'
    IntOp $GuardPathPinCount $GuardPathPinCount + 1
    StrCpy $GuardHandle $1
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
    StrCpy $GuardPath $SetupFixedRoot
    StrCpy $GuardDirectory 1
    StrCpy $GuardLastError 0 ; LastError is undefined after success.
    StrCpy $1 0

    ; Validate directory type, final path, identity and ACL on the returned
    ; handle only. Never reopen the created path to repair or verify it.
    System::Call 'kernel32::GetFileInformationByHandle(p $GuardHandle, p $SetupFreshRootInfoBuffer) i.r1'
    StrCmp $1 0 fresh_root_validation_failed
    System::Call '*$SetupFreshRootInfoBuffer(i.r1, i, i, i, i, i, i, i, i, i, i, i, i)'
    IntOp $2 $1 & 0x400 ; reject a reparse point
    StrCmp $2 0 0 fresh_root_validation_failed
    IntOp $2 $1 & 0x10 ; require a directory
    StrCmp $2 0 fresh_root_validation_failed
    System::Call 'kernel32::GetFinalPathNameByHandleW(p $GuardHandle, w .r1, i ${NSIS_MAX_STRLEN}, i 0) i.r2'
    StrCmp $2 0 fresh_root_validation_failed
    IntCmpU $2 ${NSIS_MAX_STRLEN} fresh_root_validation_failed 0 fresh_root_validation_failed
    StrCmp $1 "\\?\$SetupFixedRoot" 0 fresh_root_validation_failed
    ; FILE_ID_INFO is captured from this same handle; there is no earlier root
    ; identity to reopen/compare because CreateDirectory2W created the object.
    System::Call 'kernel32::GetFileInformationByHandleEx(p $GuardHandle, i 18, p $SetupFreshRootInfoBuffer, i 24) i.r1'
    StrCmp $1 0 fresh_root_validation_failed
    System::Call '*$SetupFreshRootInfoBuffer(i.r3, i.r4, i.r5, i.r6, i.r7, i.r8)'
    StrCpy $GuardIdentity "$3:$4:$5:$6:$7:$8"
    Call ${PREFIX}ValidatePrivateHandleAcl
    StrCmp $SetupCode 0 fresh_root_verified
    Goto fresh_root_failure
fresh_root_verified:
    Call ${PREFIX}ReleaseFreshRootScratch
    StrCmp $SetupCode 0 fresh_root_success
    Goto fresh_root_failure
fresh_root_success:
    StrCpy $SetupCode 0
    Goto fresh_root_done

fresh_root_create_refused:
    StrCpy $SetupCode 11 ; missing API, existing target, or any create error
    Goto fresh_root_failure
fresh_root_validation_failed:
    StrCpy $SetupCode 11
fresh_root_failure:
    ; Scratch release is retried by ReleaseFixedRootOwnershipLease if needed.
    ; A created or foreign directory is never removed or modified by path.
    Call ${PREFIX}ReleaseFreshRootScratch
    Call ${PREFIX}ReleaseFixedRootOwnershipLease
    StrCpy $SetupCode 11
fresh_root_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Resolve only source files with build-contract hashes. The self-generated
; uninstaller and receipt are known destination names, not trusted staged input.
Function ${PREFIX}ValidateCopyManifestName
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 11
    StrCpy $SetupCopySourcePath ""
    StrCpy $SetupCopyExpectedHash ""
    StrCmpS $SetupCopyTargetName "${SETUP_APP_NAME}" copy_manifest_app
    StrCmpS $SetupCopyTargetName "${SETUP_NOTICE_NAME}" copy_manifest_notice
    StrCmpS $SetupCopyTargetName "${SETUP_UNINSTALLER_NAME}" copy_manifest_refuse
    StrCmpS $SetupCopyTargetName "${SETUP_RECEIPT_NAME}" copy_manifest_refuse
    Goto copy_manifest_refuse
copy_manifest_app:
    StrCpy $SetupCopySourcePath "$PLUGINSDIR\${SETUP_APP_NAME}"
    StrCpy $SetupCopyExpectedHash "${SETUP_APP_SHA256}"
    Goto copy_manifest_validate_hash
copy_manifest_notice:
    StrCpy $SetupCopySourcePath "$PLUGINSDIR\${SETUP_NOTICE_NAME}"
    StrCpy $SetupCopyExpectedHash "${SETUP_NOTICE_SHA256}"
copy_manifest_validate_hash:
    StrCpy $GuardValue $SetupCopyExpectedHash
    StrCpy $GuardValueLength 64
    Call ${PREFIX}ValidateHexText
    StrCmp $SetupCode 0 copy_manifest_approved
copy_manifest_refuse:
    StrCpy $SetupCode 11
    Goto copy_manifest_done
copy_manifest_approved:
    StrCpy $SetupCode 0
copy_manifest_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Copy one trusted staged payload to a missing file below the single validated
; root lease of an active two-file transaction. CREATE_NEW is the sole create
; attempt; final validation moves the still-open target handle into its own
; rollback ledger slot. No production entry point invokes this primitive yet.
Function ${PREFIX}CopyTrustedStageFileToFixedRoot
    !insertmacro SetupSaveRegisters
    Push $GuardHandle
    Push $GuardPath
    Push $GuardDirectory
    Push $GuardIdentity
    Push $GuardHash
    Push $GuardLastError
    StrCpy $SetupCode 13
    StrCpy $SetupCopyResultHash ""
    StrCmp $SetupMode "install" copy_mode_ok
    Goto copy_done
copy_mode_ok:
    StrCmp $SetupCopyTransactionActive 1 copy_transaction_active
    Goto copy_done
copy_transaction_active:
    ; Refuse stale resources rather than replacing their ownership state.
    StrCmp $SetupCopySourceHandle "" copy_source_slot_empty
    StrCmp $SetupCopySourceHandle 0 copy_source_slot_empty
    Goto copy_done
copy_source_slot_empty:
    StrCmp $SetupCopyTargetHandle "" copy_target_slot_empty
    StrCmp $SetupCopyTargetHandle 0 copy_target_slot_empty
    Goto copy_done
copy_target_slot_empty:
    StrCmp $SetupCopyBuffer "" copy_buffer_slot_empty
    StrCmp $SetupCopyBuffer 0 copy_buffer_slot_empty
    Goto copy_done
copy_buffer_slot_empty:
    StrCmp $SetupCopyInfoBuffer "" copy_info_slot_empty
    StrCmp $SetupCopyInfoBuffer 0 copy_info_slot_empty
    Goto copy_done
copy_info_slot_empty:
    StrCmp $SetupCopySecurityDescriptor "" copy_descriptor_slot_empty
    StrCmp $SetupCopySecurityDescriptor 0 copy_descriptor_slot_empty
    Goto copy_done
copy_descriptor_slot_empty:
    StrCmp $SetupCopySecurityAttributes "" copy_attributes_slot_empty
    StrCmp $SetupCopySecurityAttributes 0 copy_attributes_slot_empty
    Goto copy_done
copy_attributes_slot_empty:
    StrCmp $SetupCopyRenameBuffer "" copy_rename_buffer_slot_empty
    StrCmp $SetupCopyRenameBuffer 0 copy_rename_buffer_slot_empty
    Goto copy_done
copy_rename_buffer_slot_empty:
    StrCmp $SetupCopyStagingName "" copy_stage_name_slot_empty
    Goto copy_done
copy_stage_name_slot_empty:
    StrCmp $SetupCopyTargetDelete "" copy_delete_slot_empty
    StrCmp $SetupCopyTargetDelete 0 copy_delete_slot_empty
    Goto copy_done
copy_delete_slot_empty:
    StrCmp $SetupCopyTargetDeletePending "" copy_pending_slot_empty
    StrCmp $SetupCopyTargetDeletePending 0 copy_pending_slot_empty
    Goto copy_done
copy_pending_slot_empty:
    StrCmp $GuardHandle "" copy_guard_slot_empty
    StrCmp $GuardHandle 0 copy_guard_slot_empty
    Goto copy_done
copy_guard_slot_empty:
    ; This is the one retained ancestor-pin array returned by the transaction's
    ; empty-root scan; every per-file call must reuse it without reacquiring.
    StrCmp $GuardPathPins "" copy_done
    StrCmp $GuardPathPins 0 copy_done
    StrCmp $GuardPathPinCount 0 copy_done
    StrCpy $SetupCopyTargetIdentity ""
    StrCpy $SetupCopyTargetDelete 0
    StrCpy $SetupCopyTargetDeletePending 0
    StrCpy $SetupCopyRenameBufferBytes 0
    StrCmpS $SetupCopyTargetName "${SETUP_APP_NAME}" copy_name_app
    StrCmpS $SetupCopyTargetName "${SETUP_NOTICE_NAME}" copy_name_notice
    Goto copy_done
copy_name_app:
    StrCmp $SetupCopyTxnAppCreated "" copy_name_app_uncreated
    StrCmp $SetupCopyTxnAppCreated 0 copy_name_app_uncreated
    Goto copy_done
copy_name_app_uncreated:
    StrCmp $SetupCopyTxnAppHandle "" copy_name_app_handle_empty
    StrCmp $SetupCopyTxnAppHandle 0 copy_name_app_handle_empty
    Goto copy_done
copy_name_app_handle_empty:
    StrCmp $SetupCopyTxnAppIdentity "" copy_name_app_identity_empty
    Goto copy_done
copy_name_app_identity_empty:
    StrCmp $SetupCopyTxnAppHash "" copy_name_app_hash_empty
    Goto copy_done
copy_name_app_hash_empty:
    StrCmp $SetupCopyTxnAppDeletePending "" copy_name_app_pending_empty
    StrCmp $SetupCopyTxnAppDeletePending 0 copy_name_app_pending_empty
    Goto copy_done
copy_name_app_pending_empty:
    StrCmp $SetupCopyTxnNoticeCreated "" copy_name_app_notice_empty
    StrCmp $SetupCopyTxnNoticeCreated 0 copy_name_app_notice_empty
    Goto copy_done
copy_name_app_notice_empty:
    StrCmp $SetupCopyTxnNoticeHandle "" copy_name_app_notice_handle_empty
    StrCmp $SetupCopyTxnNoticeHandle 0 copy_name_app_notice_handle_empty
    Goto copy_done
copy_name_app_notice_handle_empty:
    StrCmp $SetupCopyTxnNoticeIdentity "" copy_name_app_notice_identity_empty
    Goto copy_done
copy_name_app_notice_identity_empty:
    StrCmp $SetupCopyTxnNoticeHash "" copy_name_app_notice_hash_empty
    Goto copy_done
copy_name_app_notice_hash_empty:
    StrCmp $SetupCopyTxnNoticeDeletePending "" copy_name_app_notice_pending_empty
    StrCmp $SetupCopyTxnNoticeDeletePending 0 copy_name_app_notice_pending_empty
    Goto copy_done
copy_name_app_notice_pending_empty:
    Goto copy_target_order_ok
copy_name_notice:
    StrCmp $SetupCopyTxnAppCreated 1 copy_name_notice_app_present
    Goto copy_done
copy_name_notice_app_present:
    StrCmp $SetupCopyTxnAppHandle "" copy_done
    StrCmp $SetupCopyTxnAppHandle 0 copy_done
    StrCmp $SetupCopyTxnAppIdentity "" copy_done
    StrCmp $SetupCopyTxnAppHash "${SETUP_APP_SHA256}" 0 copy_done
    StrCmp $SetupCopyTxnNoticeCreated "" copy_name_notice_uncreated
    StrCmp $SetupCopyTxnNoticeCreated 0 copy_name_notice_uncreated
    Goto copy_done
copy_name_notice_uncreated:
    StrCmp $SetupCopyTxnNoticeHandle "" copy_name_notice_handle_empty
    StrCmp $SetupCopyTxnNoticeHandle 0 copy_name_notice_handle_empty
    Goto copy_done
copy_name_notice_handle_empty:
    StrCmp $SetupCopyTxnNoticeIdentity "" copy_name_notice_identity_empty
    Goto copy_done
copy_name_notice_identity_empty:
    StrCmp $SetupCopyTxnNoticeHash "" copy_name_notice_hash_empty
    Goto copy_done
copy_name_notice_hash_empty:
    StrCmp $SetupCopyTxnNoticeDeletePending "" copy_target_order_ok
    StrCmp $SetupCopyTxnNoticeDeletePending 0 copy_target_order_ok
    Goto copy_done
copy_target_order_ok:
    Call ${PREFIX}ValidateCopyManifestName
    StrCmp $SetupCode 0 0 copy_done
    StrCmp $SetupCopyRootHandle 0 copy_failure copy_root_reuse
copy_root_reuse:
    StrCmp $SetupCopyRootIdentity "" copy_failure

    ; Revalidate the retained root handle immediately before creating a child.
    StrCpy $GuardHandle $SetupCopyRootHandle
    StrCpy $GuardIdentity ""
    StrCpy $GuardDirectory 1
    System::Alloc 52 ; BY_HANDLE_FILE_INFORMATION
    Pop $SetupCopyInfoBuffer
    StrCmp $SetupCopyInfoBuffer 0 copy_failure
    System::Call 'kernel32::GetFileInformationByHandle(p $GuardHandle, p $SetupCopyInfoBuffer) i.r1'
    StrCmp $1 0 copy_failure
    System::Call '*$SetupCopyInfoBuffer(i.r1, i, i, i, i, i, i, i, i, i, i, i, i)'
    IntOp $2 $1 & 0x400 ; reparse point
    StrCmp $2 0 0 copy_failure
    IntOp $2 $1 & 0x10 ; directory
    StrCmp $2 0 copy_failure
    System::Call 'kernel32::GetFinalPathNameByHandleW(p $GuardHandle, w .r1, i ${NSIS_MAX_STRLEN}, i 0) i.r2'
    StrCmp $2 0 copy_failure
    IntCmpU $2 ${NSIS_MAX_STRLEN} copy_failure 0 copy_failure
    StrCmp $1 "\\?\$SetupFixedRoot" 0 copy_failure
    System::Call 'kernel32::GetFileInformationByHandleEx(p $GuardHandle, i 18, p $SetupCopyInfoBuffer, i 24) i.r1'
    StrCmp $1 0 copy_failure
    System::Call '*$SetupCopyInfoBuffer(i.r3, i.r4, i.r5, i.r6, i.r7, i.r8)'
    StrCpy $GuardIdentity "$3:$4:$5:$6:$7:$8"
    StrCmp $GuardIdentity $SetupCopyRootIdentity 0 copy_failure
    Call ${PREFIX}ValidatePrivateHandleAcl
    StrCmp $SetupCode 0 0 copy_failure
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""
    System::Free $SetupCopyInfoBuffer
    StrCpy $SetupCopyInfoBuffer 0

    StrCpy $GuardPath $SetupCopySourcePath
    StrCpy $GuardDirectory 0
    Call ${PREFIX}OpenPathIdentityLease
    StrCmp $SetupCode 0 copy_source_opened
    StrCmp $GuardHandle 0 copy_failure
    StrCpy $SetupCopySourceHandle $GuardHandle
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""
    Goto copy_failure
copy_source_opened:
    StrCmp $GuardHandle 0 copy_failure
    StrCpy $SetupCopySourceHandle $GuardHandle
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""
    StrCpy $GuardHandle $SetupCopySourceHandle
    Call ${PREFIX}HashHandleSha256
    StrCmp $SetupCode 0 0 copy_failure
    StrCmp $GuardHash $SetupCopyExpectedHash 0 copy_failure
    System::Call 'kernel32::SetFilePointerEx(p $SetupCopySourceHandle, l 0, p 0, i 0) i.r1'
    StrCmp $1 0 copy_failure
    System::Alloc 65536
    Pop $SetupCopyBuffer
    StrCmp $SetupCopyBuffer 0 copy_failure

    ; Create a protected DACL for only the current SID, SYSTEM and administrators.
    StrCpy $0 $SetupOwnerSid
    System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "O:$0D:P(A;;GA;;;$0)(A;;GA;;;SY)(A;;GA;;;BA)", i 1, *p .r8, p 0) i.r1'
    StrCmp $1 0 copy_failure
    StrCmp $8 0 copy_failure
    StrCpy $SetupCopySecurityDescriptor $8
    System::Call '*(i 12, p r8, i 0) p.r9' ; x86 SECURITY_ATTRIBUTES, non-inheritable
    StrCmp $9 0 copy_failure
    StrCpy $SetupCopySecurityAttributes $9
    ; One fixed, private, non-overwritable name; a crash here cannot publish a
    ; partially copied product filename. A stale stage file causes refusal.
    StrCpy $SetupCopyStagingName ".GitHubBackupTool.setup-stage"
    ; Share none; CREATE_NEW; OPEN_REPARSE_POINT; no path-based cleanup fallback.
    System::Call 'kernel32::CreateFileW(w "$SetupFixedRoot\$SetupCopyStagingName", i 0xC0030000, i 0, p $SetupCopySecurityAttributes, i 1, i 0x00200080, p 0) p.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 -1 copy_create_failed
    StrCmp $0 0 copy_create_failed
    StrCpy $SetupCopyTargetHandle $0
    StrCpy $SetupCopyTargetDelete 1
    StrCpy $SetupCopyTargetDeletePending 0
    ; Capture the returned CREATE_NEW object's identity before freeing the
    ; security descriptor or doing any other operation that can fail. If this
    ; first query fails, the paired abort may retry only on this exact handle.
    System::Alloc 52 ; BY_HANDLE_FILE_INFORMATION / FILE_ID_INFO
    Pop $SetupCopyInfoBuffer
    StrCmp $SetupCopyInfoBuffer 0 copy_failure
    System::Call 'kernel32::GetFileInformationByHandleEx(p $SetupCopyTargetHandle, i 18, p $SetupCopyInfoBuffer, i 24) i.r1'
    StrCmp $1 0 copy_failure
    System::Call '*$SetupCopyInfoBuffer(i.r3, i.r4, i.r5, i.r6, i.r7, i.r8)'
    StrCpy $SetupCopyTargetIdentity "$3:$4:$5:$6:$7:$8"
    System::Call 'kernel32::GetFileInformationByHandle(p $SetupCopyTargetHandle, p $SetupCopyInfoBuffer) i.r1'
    StrCmp $1 0 copy_failure
    System::Call '*$SetupCopyInfoBuffer(i.r1, i, i, i, i, i, i, i, i, i, i.r5, i, i)'
    IntOp $2 $1 & 0x400 ; refuse reparse points
    StrCmp $2 0 0 copy_failure
    IntOp $2 $1 & 0x10 ; refuse directories
    StrCmp $2 0 0 copy_failure
    StrCmp $5 1 0 copy_failure ; exactly one hard link
    System::Call 'kernel32::GetFinalPathNameByHandleW(p $SetupCopyTargetHandle, w .r1, i ${NSIS_MAX_STRLEN}, i 0) i.r2'
    StrCmp $2 0 copy_failure
    IntCmpU $2 ${NSIS_MAX_STRLEN} copy_failure 0 copy_failure
    StrCmp $1 "\\?\$SetupFixedRoot\$SetupCopyStagingName" 0 copy_failure
    StrCpy $GuardHandle $SetupCopyTargetHandle
    Call ${PREFIX}ValidatePrivateHandleAcl
    StrCmp $SetupCode 0 0 copy_failure
    Goto copy_free_security
copy_create_failed:
    StrCpy $SetupCode 13
    Goto copy_free_security
copy_free_security:
    StrCmp $SetupCopySecurityAttributes 0 copy_free_descriptor
    System::Free $SetupCopySecurityAttributes
    StrCpy $SetupCopySecurityAttributes 0
copy_free_descriptor:
    StrCmp $SetupCopySecurityDescriptor 0 copy_security_freed
    System::Call 'kernel32::LocalFree(p $SetupCopySecurityDescriptor) p.r1'
    StrCmp $1 0 0 copy_failure
    StrCpy $SetupCopySecurityDescriptor 0
copy_security_freed:
    StrCmp $SetupCopyTargetHandle 0 copy_failure
    StrCmp $SetupCode 13 copy_failure

    ; Verify type, link count, final path, identity and DACL on the returned handle.
    StrCpy $GuardHandle $SetupCopyTargetHandle
    StrCpy $GuardDirectory 0
    StrCmp $SetupCopyInfoBuffer 0 copy_target_info_allocate
    Goto copy_target_info_ready
copy_target_info_allocate:
    System::Alloc 52 ; BY_HANDLE_FILE_INFORMATION
    Pop $SetupCopyInfoBuffer
    StrCmp $SetupCopyInfoBuffer 0 copy_failure
copy_target_info_ready:
    System::Call 'kernel32::GetFileInformationByHandle(p $SetupCopyTargetHandle, p $SetupCopyInfoBuffer) i.r1'
    StrCmp $1 0 copy_failure
    System::Call '*$SetupCopyInfoBuffer(i.r1, i, i, i, i, i, i, i, i, i, i.r5, i, i)'
    IntOp $2 $1 & 0x400 ; reparse point
    StrCmp $2 0 0 copy_failure
    IntOp $2 $1 & 0x10 ; must be an ordinary file
    StrCmp $2 0 0 copy_failure
    StrCmp $5 1 0 copy_failure ; exactly one hard link
    System::Call 'kernel32::GetFinalPathNameByHandleW(p $SetupCopyTargetHandle, w .r1, i ${NSIS_MAX_STRLEN}, i 0) i.r2'
    StrCmp $2 0 copy_failure
    IntCmpU $2 ${NSIS_MAX_STRLEN} copy_failure 0 copy_failure
    StrCmp $1 "\\?\$SetupFixedRoot\$SetupCopyStagingName" 0 copy_failure
    System::Call 'kernel32::GetFileInformationByHandleEx(p $SetupCopyTargetHandle, i 18, p $SetupCopyInfoBuffer, i 24) i.r1'
    StrCmp $1 0 copy_failure
    System::Call '*$SetupCopyInfoBuffer(i.r3, i.r4, i.r5, i.r6, i.r7, i.r8)'
    StrCpy $SetupCopyTargetIdentity "$3:$4:$5:$6:$7:$8"
    Call ${PREFIX}ValidatePrivateHandleAcl
    StrCmp $SetupCode 0 0 copy_failure
    System::Free $SetupCopyInfoBuffer
    StrCpy $SetupCopyInfoBuffer 0

copy_read_source:
    System::Call 'kernel32::ReadFile(p $SetupCopySourceHandle, p $SetupCopyBuffer, i 65536, *i .r4, p 0) i.r5'
    StrCmp $5 0 copy_failure
    StrCmp $4 0 copy_flush_target
    IntCmpU $4 65536 0 0 copy_failure
    StrCpy $SetupCopyChunkBytes $4
    StrCpy $SetupCopyWriteOffset 0
copy_write_chunk:
    IntCmpU $SetupCopyWriteOffset $SetupCopyChunkBytes copy_read_source copy_write_more copy_failure
copy_write_more:
    IntOp $0 $SetupCopyBuffer + $SetupCopyWriteOffset
    IntOp $5 $SetupCopyChunkBytes - $SetupCopyWriteOffset
    System::Call 'kernel32::WriteFile(p $SetupCopyTargetHandle, p r0, i r5, *i .r6, p 0) i.r7'
    StrCmp $7 0 copy_failure
    StrCmp $6 0 copy_failure ; reject a zero-byte short write (no infinite loop)
    IntCmpU $6 $5 0 0 copy_failure
    IntOp $SetupCopyWriteOffset $SetupCopyWriteOffset + $6
    Goto copy_write_chunk
copy_flush_target:
    System::Call 'kernel32::FlushFileBuffers(p $SetupCopyTargetHandle) i.r1'
    StrCmp $1 0 copy_failure
    StrCpy $GuardHandle $SetupCopyTargetHandle
    Call ${PREFIX}HashHandleSha256
    StrCmp $SetupCode 0 0 copy_failure
    StrCmp $GuardHash $SetupCopyExpectedHash 0 copy_failure
    StrCpy $SetupCopyResultHash $GuardHash

    ; FILE_RENAME_INFO x86 layout: DWORD flags at 0, HANDLE root at 4,
    ; DWORD byte-length at 8 and WCHAR name at 12. Extra WCHAR stores NUL.
    StrLen $3 $SetupCopyTargetName
    IntOp $5 $3 * 2 ; FileNameLength is bytes, not WCHAR count
    IntOp $6 $5 + 12 ; API buffer size: FIELD_OFFSET(FileName) + FileNameLength
    IntOp $7 $6 + 2 ; allocation includes a trailing NUL for lstrcpyW
    System::Alloc $7
    Pop $SetupCopyRenameBuffer
    StrCmp $SetupCopyRenameBuffer 0 copy_failure
    StrCpy $SetupCopyRenameBufferBytes $6
    ; ReplaceIfExists/Flags=0 refuses an existing final filename. RootDirectory
    ; is the still-pinned root handle; the new name is one manifest leaf only.
    System::Call '*$SetupCopyRenameBuffer(i 0, p $SetupCopyRootHandle, i r5)'
    IntOp $8 $SetupCopyRenameBuffer + 12
    System::Call 'kernel32::lstrcpyW(p r8, w "$SetupCopyTargetName") p.r9'
    StrCmp $9 0 copy_failure
    System::Call 'kernel32::SetFileInformationByHandle(p $SetupCopyTargetHandle, i 3, p $SetupCopyRenameBuffer, i $SetupCopyRenameBufferBytes) i.r1'
    StrCmp $1 0 copy_failure
    System::Free $SetupCopyRenameBuffer
    StrCpy $SetupCopyRenameBuffer 0
    StrCpy $SetupCopyRenameBufferBytes 0

    ; Confirm the same file handle now names the final fixed path and retains
    ; its pre-rename file identity and private DACL.
    StrCpy $GuardHandle $SetupCopyTargetHandle
    StrCpy $GuardIdentity ""
    System::Alloc 52 ; BY_HANDLE_FILE_INFORMATION
    Pop $SetupCopyInfoBuffer
    StrCmp $SetupCopyInfoBuffer 0 copy_failure
    System::Call 'kernel32::GetFileInformationByHandle(p $SetupCopyTargetHandle, p $SetupCopyInfoBuffer) i.r1'
    StrCmp $1 0 copy_failure
    System::Call '*$SetupCopyInfoBuffer(i.r1, i, i, i, i, i, i, i, i, i, i.r5, i, i)'
    IntOp $2 $1 & 0x400 ; reparse point
    StrCmp $2 0 0 copy_failure
    IntOp $2 $1 & 0x10 ; must remain an ordinary file
    StrCmp $2 0 0 copy_failure
    StrCmp $5 1 0 copy_failure ; exactly one hard link
    System::Call 'kernel32::GetFinalPathNameByHandleW(p $SetupCopyTargetHandle, w .r1, i ${NSIS_MAX_STRLEN}, i 0) i.r2'
    StrCmp $2 0 copy_failure
    IntCmpU $2 ${NSIS_MAX_STRLEN} copy_failure 0 copy_failure
    StrCmp $1 "\\?\$SetupFixedRoot\$SetupCopyTargetName" 0 copy_failure
    System::Call 'kernel32::GetFileInformationByHandleEx(p $SetupCopyTargetHandle, i 18, p $SetupCopyInfoBuffer, i 24) i.r1'
    StrCmp $1 0 copy_failure
    System::Call '*$SetupCopyInfoBuffer(i.r3, i.r4, i.r5, i.r6, i.r7, i.r8)'
    StrCpy $GuardIdentity "$3:$4:$5:$6:$7:$8"
    StrCmp $GuardIdentity $SetupCopyTargetIdentity 0 copy_failure
    Call ${PREFIX}ValidatePrivateHandleAcl
    StrCmp $SetupCode 0 0 copy_failure
    System::Free $SetupCopyInfoBuffer
    StrCpy $SetupCopyInfoBuffer 0

    System::Free $SetupCopyBuffer
    StrCpy $SetupCopyBuffer 0

    ; Move each fully verified final target into a distinct rollback ledger slot
    ; before releasing any file handle or source lease. This is the point at
    ; which the transaction records durable ownership of that created object.
    StrCmpS $SetupCopyTargetName "${SETUP_APP_NAME}" copy_register_app
    StrCmpS $SetupCopyTargetName "${SETUP_NOTICE_NAME}" copy_register_notice
    Goto copy_failure
copy_register_app:
    StrCpy $SetupCopyTxnAppHandle $SetupCopyTargetHandle
    StrCpy $SetupCopyTxnAppIdentity $SetupCopyTargetIdentity
    StrCpy $SetupCopyTxnAppHash $SetupCopyResultHash
    StrCpy $SetupCopyTxnAppDeletePending 0
    StrCpy $SetupCopyTxnAppCreated 1
    Goto copy_registered
copy_register_notice:
    StrCpy $SetupCopyTxnNoticeHandle $SetupCopyTargetHandle
    StrCpy $SetupCopyTxnNoticeIdentity $SetupCopyTargetIdentity
    StrCpy $SetupCopyTxnNoticeHash $SetupCopyResultHash
    StrCpy $SetupCopyTxnNoticeDeletePending 0
    StrCpy $SetupCopyTxnNoticeCreated 1
copy_registered:
    StrCpy $SetupCopyTargetHandle 0
    StrCpy $SetupCopyTargetIdentity ""
    StrCpy $SetupCopyTargetDelete 0
    StrCpy $SetupCopyTargetDeletePending 0
    StrCpy $SetupCopyStagingName ""
    System::Call 'kernel32::CloseHandle(p $SetupCopySourceHandle) i.r1'
    StrCmp $1 0 copy_failure
    StrCpy $SetupCopySourceHandle 0
    StrCpy $SetupCode 0
    Goto copy_done

copy_failure:
    StrCpy $SetupCode 13
    ; The actual source/target/root handles remain in their owning slots for the
    ; transaction rollback/release helpers. These are only scratch aliases.
    StrCpy $GuardHandle 0
    StrCpy $GuardPath ""
    StrCpy $GuardDirectory 0
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
copy_done:
    Pop $GuardLastError
    Pop $GuardHash
    Pop $GuardIdentity
    Pop $GuardDirectory
    Pop $GuardPath
    Pop $GuardHandle
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Inspect exactly the retained handle named by SetupJournalVerifyObjectHandle.
; An empty identity may be captured only for a slot whose CREATE_NEW creator
; returned this same still-open handle and recorded Created=1.
Function ${PREFIX}ValidateFreshInstallJournalHandle
    !insertmacro SetupSaveRegisters
    Push $GuardHandle
    Push $GuardPath
    Push $GuardDirectory
    Push $GuardIdentity
    Push $GuardHash
    Push $GuardLastError
    StrCpy $SetupCode 13
    StrCpy $9 0 ; locally allocated file-information buffer
    StrCmp $SetupJournalVerifyObjectHandle "" journal_handle_done
    StrCmp $SetupJournalVerifyObjectHandle 0 journal_handle_done
    StrCmp $SetupJournalVerifyObjectHandle -1 journal_handle_done
    StrCmp $SetupJournalVerifyPath "" journal_handle_done
    StrCmp $SetupJournalVerifyDirectory 0 journal_handle_kind_ok
    StrCmp $SetupJournalVerifyDirectory 1 0 journal_handle_done
journal_handle_kind_ok:
    StrCmp $SetupJournalInfoBuffer "" journal_handle_allocate
    StrCmp $SetupJournalInfoBuffer 0 journal_handle_allocate
    StrCpy $0 $SetupJournalInfoBuffer
    Goto journal_handle_info_ready
journal_handle_allocate:
    System::Alloc 52 ; BY_HANDLE_FILE_INFORMATION / FILE_ID_INFO
    Pop $0
    StrCmp $0 0 journal_handle_done
    StrCpy $9 1
journal_handle_info_ready:
    System::Call 'kernel32::GetFileInformationByHandle(p $SetupJournalVerifyObjectHandle, p r0) i.r1'
    StrCmp $1 0 journal_handle_done
    System::Call '*$0(i.r1, i, i, i, i, i, i, i, i, i, i.r5, i, i)'
    IntOp $2 $1 & 0x400 ; reject reparse points on the exact returned handle
    StrCmp $2 0 0 journal_handle_done
    IntOp $2 $1 & 0x10
    ${If} $SetupJournalVerifyDirectory == 1
        StrCmp $2 0 journal_handle_done
    ${Else}
        StrCmp $2 0 0 journal_handle_done
        StrCmp $5 1 0 journal_handle_done ; journal file has exactly one hard link
    ${EndIf}
    System::Call 'kernel32::GetFinalPathNameByHandleW(p $SetupJournalVerifyObjectHandle, w .r1, i ${NSIS_MAX_STRLEN}, i 0) i.r2'
    StrCmp $2 0 journal_handle_done
    IntCmpU $2 ${NSIS_MAX_STRLEN} journal_handle_done 0 journal_handle_done
    StrCmp $1 $SetupJournalVerifyPath 0 journal_handle_done
    ; FILE_ID_INFO: preserve all 24 bytes from this same handle.
    System::Call 'kernel32::GetFileInformationByHandleEx(p $SetupJournalVerifyObjectHandle, i 18, p r0, i 24) i.r1'
    StrCmp $1 0 journal_handle_done
    System::Call '*$0(i.r3, i.r4, i.r5, i.r6, i.r7, i.r8)'
    StrCpy $GuardIdentity "$3:$4:$5:$6:$7:$8"
    StrCmp $SetupJournalVerifyIdentity "" journal_handle_capture_identity
    StrCmp $GuardIdentity $SetupJournalVerifyIdentity 0 journal_handle_done
    Goto journal_handle_acl
journal_handle_capture_identity:
    StrCmp $SetupJournalVerifyCreated 1 0 journal_handle_done
    StrCpy $SetupJournalVerifyIdentity $GuardIdentity
journal_handle_acl:
    StrCpy $GuardHandle $SetupJournalVerifyObjectHandle
    StrCpy $GuardDirectory $SetupJournalVerifyDirectory
    Call ${PREFIX}ValidatePrivateHandleAcl
    StrCmp $SetupCode 0 0 journal_handle_done
    StrCpy $SetupCode 0
journal_handle_done:
    StrCmp $9 1 0 journal_handle_restore
    System::Free $0
journal_handle_restore:
    Pop $GuardLastError
    Pop $GuardHash
    Pop $GuardIdentity
    Pop $GuardDirectory
    Pop $GuardPath
    Pop $GuardHandle
    !insertmacro SetupRestoreRegisters
FunctionEnd

Function ${PREFIX}ValidateFreshInstallJournalRoot
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 13
    StrCmp $SetupCopyTransactionActive 1 journal_root_active
    Goto journal_root_done
journal_root_active:
    StrCmp $SetupCopyRootHandle "" journal_root_done
    StrCmp $SetupCopyRootHandle 0 journal_root_done
    StrCmp $SetupCopyRootIdentity "" journal_root_done
    StrCmp $SetupCopyRootLeasePending 0 journal_root_lease_ready
    Goto journal_root_done
journal_root_lease_ready:
    StrCmp $GuardPathPins "" journal_root_done
    StrCmp $GuardPathPins 0 journal_root_done
    StrCmp $GuardPathPinCount 0 journal_root_done
    StrCpy $SetupJournalVerifyObjectHandle $SetupCopyRootHandle
    StrCpy $SetupJournalVerifyPath "\\?\$SetupFixedRoot"
    StrCpy $SetupJournalVerifyIdentity $SetupCopyRootIdentity
    StrCpy $SetupJournalVerifyDirectory 1
    StrCpy $SetupJournalVerifyCreated 0
    Call ${PREFIX}ValidateFreshInstallJournalHandle
    StrCmp $SetupCode 0 journal_root_done
journal_root_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

Function ${PREFIX}ValidateFreshInstallJournalDirectory
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 13
    StrCmp $SetupJournalDirectoryCreated 1 journal_dir_created
    Goto journal_dir_done
journal_dir_created:
    StrCmp $SetupJournalDirectoryHandle "" journal_dir_done
    StrCmp $SetupJournalDirectoryHandle 0 journal_dir_done
    Call ${PREFIX}ValidateFreshInstallJournalRoot
    StrCmp $SetupCode 0 journal_dir_root_ready
    Goto journal_dir_done
journal_dir_root_ready:
    StrCpy $SetupJournalVerifyObjectHandle $SetupJournalDirectoryHandle
    StrCpy $SetupJournalVerifyPath "\\?\$SetupFixedRoot\.GitHubBackupTool.state"
    StrCpy $SetupJournalVerifyIdentity $SetupJournalDirectoryIdentity
    StrCpy $SetupJournalVerifyDirectory 1
    StrCpy $SetupJournalVerifyCreated $SetupJournalDirectoryCreated
    Call ${PREFIX}ValidateFreshInstallJournalHandle
    StrCmp $SetupCode 0 journal_dir_capture
    Goto journal_dir_done
journal_dir_capture:
    StrCpy $SetupJournalDirectoryIdentity $SetupJournalVerifyIdentity
    StrCpy $SetupCode 0
journal_dir_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

Function ${PREFIX}ValidateFreshInstallJournalFile
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 13
    StrCmp $SetupJournalFileCreated 1 journal_file_created
    Goto journal_file_done
journal_file_created:
    StrCmp $SetupJournalFileHandle "" journal_file_done
    StrCmp $SetupJournalFileHandle 0 journal_file_done
    Call ${PREFIX}ValidateFreshInstallJournalDirectory
    StrCmp $SetupCode 0 journal_file_parent_ready
    Goto journal_file_done
journal_file_parent_ready:
    StrCpy $SetupJournalVerifyObjectHandle $SetupJournalFileHandle
    StrCpy $SetupJournalVerifyPath "\\?\$SetupFixedRoot\.GitHubBackupTool.state\journal.ini"
    StrCpy $SetupJournalVerifyIdentity $SetupJournalFileIdentity
    StrCpy $SetupJournalVerifyDirectory 0
    StrCpy $SetupJournalVerifyCreated $SetupJournalFileCreated
    Call ${PREFIX}ValidateFreshInstallJournalHandle
    StrCmp $SetupCode 0 journal_file_capture
    Goto journal_file_done
journal_file_capture:
    StrCpy $SetupJournalFileIdentity $SetupJournalVerifyIdentity
    StrCpy $SetupCode 0
journal_file_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Read one bounded ASCII line from the same retained UTF-16LE journal handle.
; Lines must be nonempty, printable ASCII and terminated by canonical CRLF.
Function ${PREFIX}ReadFreshInstallJournalLine
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 13
    StrCpy $SetupJournalReadLine ""
    StrCmp $SetupJournalReadBuffer 0 journal_read_line_done
    StrCpy $3 0
journal_read_line_next:
    IntCmpU $SetupJournalReadOffset $SetupJournalReadSize journal_read_line_done journal_read_line_has_data journal_read_line_done
journal_read_line_has_data:
    IntOp $0 $SetupJournalReadBuffer + $SetupJournalReadOffset
    System::Call '*$0(&i2.r1)'
    IntOp $SetupJournalReadOffset $SetupJournalReadOffset + 2
    StrCmp $1 13 journal_read_line_cr
    IntCmpU $1 32 journal_read_line_ascii_min journal_read_line_done journal_read_line_ascii_min
journal_read_line_ascii_min:
    IntCmpU $1 126 journal_read_line_append journal_read_line_append journal_read_line_done
journal_read_line_append:
    IntOp $3 $3 + 1
    IntCmpU $3 ${NSIS_MAX_STRLEN} journal_read_line_done 0 journal_read_line_done
    IntFmt $2 "%c" $1
    StrCpy $SetupJournalReadLine "$SetupJournalReadLine$2"
    Goto journal_read_line_next
journal_read_line_cr:
    IntCmpU $3 0 journal_read_line_done journal_read_line_done journal_read_line_has_lf
journal_read_line_has_lf:
    IntCmpU $SetupJournalReadOffset $SetupJournalReadSize journal_read_line_done journal_read_line_lf_available journal_read_line_done
journal_read_line_lf_available:
    IntOp $0 $SetupJournalReadBuffer + $SetupJournalReadOffset
    System::Call '*$0(&i2.r1)'
    StrCmp $1 10 0 journal_read_line_done
    IntOp $SetupJournalReadOffset $SetupJournalReadOffset + 2
    StrCpy $SetupCode 0
journal_read_line_done:
    ${If} $SetupCode != 0
        StrCpy $SetupJournalReadLine ""
    ${EndIf}
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Compare the next canonical journal line with GuardValue. A mismatch leaves a
; nonzero status; no INI APIs or path fields are interpreted.
Function ${PREFIX}RequireFreshInstallJournalLine
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 13
    Call ${PREFIX}ReadFreshInstallJournalLine
    StrCmp $SetupCode 0 journal_require_line_read
    Goto journal_require_line_done
journal_require_line_read:
    StrCpy $SetupCode 13
    StrCmp $SetupJournalReadLine $GuardValue 0 journal_require_line_done
    StrCpy $SetupCode 0
journal_require_line_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Parse only the already opened journal handle. Exact ordered fields reject
; duplicates, omissions, extra data and unknown phases. The payload hashes and
; SID are claims checked against the caller's independently derived contract;
; PARSED is syntax/classification only, never install ownership or recovery authority.
Function ${PREFIX}ReadFreshInstallJournalRecord
    !insertmacro SetupSaveRegisters
    Push $GuardValue
    Push $SetupJournalReadSizeBuffer
    Push $SetupJournalReadBuffer
    Push $SetupJournalReadSize
    Push $SetupJournalReadOffset
    Push $SetupJournalReadLine
    Push $SetupJournalReadPositionSaved
    Push $R8
    StrCpy $SetupCode 13
    StrCpy $SetupJournalReadPhase ""
    StrCmp $SetupJournalReadSizeBuffer "" journal_record_size_slot_empty
    StrCmp $SetupJournalReadSizeBuffer 0 journal_record_size_slot_empty
    Goto journal_record_done
journal_record_size_slot_empty:
    StrCmp $SetupJournalReadBuffer "" journal_record_buffer_slot_empty
    StrCmp $SetupJournalReadBuffer 0 journal_record_buffer_slot_empty
    Goto journal_record_done
journal_record_buffer_slot_empty:
    StrCpy $SetupJournalReadSizeBuffer 0
    StrCpy $SetupJournalReadBuffer 0
    StrCpy $SetupJournalReadPositionSaved 0
    StrCmp $SetupJournalReadFileHandle 0 journal_record_done
    StrCmp $SetupJournalReadFileHandle -1 journal_record_done
    System::Alloc 8
    Pop $SetupJournalReadSizeBuffer
    StrCmp $SetupJournalReadSizeBuffer 0 journal_record_done
    System::Call 'kernel32::GetFileSizeEx(p $SetupJournalReadFileHandle, p $SetupJournalReadSizeBuffer) i.r2'
    StrCmp $2 0 journal_record_done
    System::Call '*$SetupJournalReadSizeBuffer(i.r0, i.r1)'
    StrCmp $1 0 0 journal_record_done
    IntCmpU $0 4 journal_record_size_minimum_ok journal_record_done journal_record_size_minimum_ok
journal_record_size_minimum_ok:
    IntCmpU $0 4096 journal_record_size_bounded journal_record_size_bounded journal_record_done
journal_record_size_bounded:
    IntOp $1 $0 & 1
    StrCmp $1 0 0 journal_record_done
    StrCpy $SetupJournalReadSize $0
    System::Alloc 4096
    Pop $SetupJournalReadBuffer
    StrCmp $SetupJournalReadBuffer 0 journal_record_done
    System::Call 'kernel32::SetFilePointerEx(p $SetupJournalReadFileHandle, l 0, *l .r8, i 1) i.r2'
    StrCmp $2 0 journal_record_done
    StrCpy $SetupJournalReadPositionSaved 1
    System::Call 'kernel32::SetFilePointerEx(p $SetupJournalReadFileHandle, l 0, p 0, i 0) i.r2'
    StrCmp $2 0 journal_record_done
    System::Call 'kernel32::ReadFile(p $SetupJournalReadFileHandle, p $SetupJournalReadBuffer, i $SetupJournalReadSize, *i .r1, p 0) i.r2'
    StrCmp $2 0 journal_record_done
    StrCmp $1 $SetupJournalReadSize 0 journal_record_done
    System::Call '*$SetupJournalReadBuffer(&i2.r1)'
    IntCmpU $1 0xFEFF journal_record_bom_valid journal_record_done journal_record_done
journal_record_bom_valid:
    StrCpy $SetupJournalReadOffset 2

    StrCpy $GuardValue "[GitHubBackupSetupJournal]"
    Call ${PREFIX}RequireFreshInstallJournalLine
    StrCmp $SetupCode 0 journal_record_schema
    Goto journal_record_done
journal_record_schema:
    StrCpy $GuardValue "schema=1"
    Call ${PREFIX}RequireFreshInstallJournalLine
    StrCmp $SetupCode 0 journal_record_product
    Goto journal_record_done
journal_record_product:
    StrCpy $GuardValue "productId=${SETUP_PRODUCT_ID}"
    Call ${PREFIX}RequireFreshInstallJournalLine
    StrCmp $SetupCode 0 journal_record_sid
    Goto journal_record_done
journal_record_sid:
    StrCpy $GuardValue "ownerSid=$SetupOwnerSid"
    Call ${PREFIX}RequireFreshInstallJournalLine
    StrCmp $SetupCode 0 journal_record_version
    Goto journal_record_done
journal_record_version:
    StrCpy $GuardValue "appVersion=${SETUP_APP_VERSION}"
    Call ${PREFIX}RequireFreshInstallJournalLine
    StrCmp $SetupCode 0 journal_record_source
    Goto journal_record_done
journal_record_source:
    StrCpy $GuardValue "sourceCommit=${SETUP_SOURCE_COMMIT}"
    Call ${PREFIX}RequireFreshInstallJournalLine
    StrCmp $SetupCode 0 journal_record_payload_hash
    Goto journal_record_done
journal_record_payload_hash:
    StrCpy $GuardValue "payloadSha256=${SETUP_APP_SHA256}"
    Call ${PREFIX}RequireFreshInstallJournalLine
    StrCmp $SetupCode 0 journal_record_app_hash
    Goto journal_record_done
journal_record_app_hash:
    StrCpy $GuardValue "${SETUP_APP_NAME}=${SETUP_APP_SHA256}"
    Call ${PREFIX}RequireFreshInstallJournalLine
    StrCmp $SetupCode 0 journal_record_notice_hash
    Goto journal_record_done
journal_record_notice_hash:
    StrCpy $GuardValue "${SETUP_NOTICE_NAME}=${SETUP_NOTICE_SHA256}"
    Call ${PREFIX}RequireFreshInstallJournalLine
    StrCmp $SetupCode 0 journal_record_prepared_phase
    Goto journal_record_done
journal_record_prepared_phase:
    StrCpy $GuardValue "phase=PREPARED"
    Call ${PREFIX}RequireFreshInstallJournalLine
    StrCmp $SetupCode 0 journal_record_phase_choice
    Goto journal_record_done
journal_record_phase_choice:
    StrCmp $SetupJournalReadOffset $SetupJournalReadSize journal_record_prepared
    StrCpy $GuardValue "phase=FILES_WRITTEN"
    Call ${PREFIX}RequireFreshInstallJournalLine
    StrCmp $SetupCode 0 journal_record_files_written
    Goto journal_record_done
journal_record_files_written:
    StrCmp $SetupJournalReadOffset $SetupJournalReadSize journal_record_files_written_valid
    Goto journal_record_done
journal_record_files_written_valid:
    StrCpy $SetupJournalReadPhase "FILES_WRITTEN"
    StrCpy $SetupCode 0
    Goto journal_record_done
journal_record_prepared:
    StrCpy $SetupJournalReadPhase "PREPARED"
    StrCpy $SetupCode 0
journal_record_done:
    StrCmp $SetupJournalReadPositionSaved 1 journal_record_restore_position
    Goto journal_record_free_buffers
journal_record_restore_position:
    System::Call 'kernel32::SetFilePointerEx(p $SetupJournalReadFileHandle, l r8, p 0, i 0) i.r2'
    StrCmp $2 0 journal_record_restore_failed
    Goto journal_record_free_buffers
journal_record_restore_failed:
    StrCpy $SetupCode 13
journal_record_free_buffers:
    StrCmp $SetupJournalReadBuffer 0 journal_record_free_size
    System::Free $SetupJournalReadBuffer
    StrCpy $SetupJournalReadBuffer 0
journal_record_free_size:
    StrCmp $SetupJournalReadSizeBuffer 0 journal_record_clear_phase
    System::Free $SetupJournalReadSizeBuffer
    StrCpy $SetupJournalReadSizeBuffer 0
journal_record_clear_phase:
    StrCmp $SetupCode 0 journal_record_restore_scratch
    StrCpy $SetupJournalReadPhase ""
journal_record_restore_scratch:
    Pop $R8
    Pop $SetupJournalReadPositionSaved
    Pop $SetupJournalReadLine
    Pop $SetupJournalReadOffset
    Pop $SetupJournalReadSize
    Pop $SetupJournalReadBuffer
    Pop $SetupJournalReadSizeBuffer
    Pop $GuardValue
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Read-only classification of an existing journal. The caller must first bind
; SetupFixedRoot/SetupLocalAppData from ValidateHost and retain the ancestor
; pins from PinExistingInstallAncestors; this helper independently checks the
; expected Programs relationship, opens each exact fixed path with no-delete
; sharing, validates private ACLs and reads only the held single-link file.
; A PARSED result never authorizes cleanup, rollback, resume, overwrite or uninstall.
Function ${PREFIX}InspectFreshInstallJournal
    !insertmacro SetupSaveRegisters
    Push $GuardPath
    Push $GuardDirectory
    Push $GuardHandle
    Push $GuardIdentity
    Push $GuardHash
    Push $GuardLastError
    Push $GuardSid
    Push $GuardValue
    Push $GuardValueLength
    StrCpy $SetupCode 13
    StrCpy $SetupJournalReadStatus "REJECTED"
    StrCpy $SetupJournalReadPhase ""
    StrCmp $SetupMode "install" journal_inspect_mode_valid
    StrCmp $SetupMode "uninstall" 0 journal_inspect_done
journal_inspect_mode_valid:
    StrCmp $SetupOwnerSid "" journal_inspect_done
    StrCmp $SetupLocalAppData "" journal_inspect_done
    StrCmp $SetupFixedRoot "$SetupLocalAppData\${SETUP_INSTALL_SUFFIX}" 0 journal_inspect_done
    StrCmp $GuardProgramsParentPinned 1 0 journal_inspect_done
    StrCmp $GuardPathPins "" journal_inspect_done
    StrCmp $GuardPathPins 0 journal_inspect_done
    StrCmp $GuardPathPinCount 0 journal_inspect_done
    StrCmp $SetupJournalReadRootHandle "" journal_inspect_slots_root_empty
    StrCmp $SetupJournalReadRootHandle 0 journal_inspect_slots_root_empty
    Goto journal_inspect_done
journal_inspect_slots_root_empty:
    StrCmp $SetupJournalReadDirectoryHandle "" journal_inspect_slots_directory_empty
    StrCmp $SetupJournalReadDirectoryHandle 0 journal_inspect_slots_directory_empty
    Goto journal_inspect_done
journal_inspect_slots_directory_empty:
    StrCmp $SetupJournalReadFileHandle "" journal_inspect_slots_file_empty
    StrCmp $SetupJournalReadFileHandle 0 journal_inspect_slots_file_empty
    Goto journal_inspect_done
journal_inspect_slots_file_empty:
    StrCpy $GuardPath $SetupFixedRoot
    StrCpy $GuardDirectory 1
    Call ${PREFIX}OpenPathIdentityLease
    StrCmp $GuardHandle 0 journal_inspect_root_opened
    StrCpy $SetupJournalReadRootHandle $GuardHandle
    StrCpy $SetupJournalReadRootIdentity $GuardIdentity
    StrCmp $SetupCode 0 journal_inspect_root_opened
    Goto journal_inspect_close
journal_inspect_root_opened:
    StrCmp $SetupCode 0 journal_inspect_root_open
    Goto journal_inspect_close
journal_inspect_root_open:
    StrCpy $SetupJournalReadRootHandle $GuardHandle
    StrCpy $SetupJournalReadRootIdentity $GuardIdentity
    StrCpy $SetupCode 13
    StrCpy $GuardHandle $SetupJournalReadRootHandle
    StrCpy $GuardDirectory 1
    Call ${PREFIX}ValidatePrivateHandleAcl
    StrCmp $SetupCode 0 journal_inspect_root_acl
    Goto journal_inspect_close
journal_inspect_root_acl:
    StrCpy $SetupCode 13
    StrCpy $GuardPath "$SetupFixedRoot\.GitHubBackupTool.state"
    StrCpy $GuardDirectory 1
    Call ${PREFIX}OpenPathIdentityLease
    StrCmp $GuardHandle 0 journal_inspect_directory_opened
    StrCpy $SetupJournalReadDirectoryHandle $GuardHandle
    StrCpy $SetupJournalReadDirectoryIdentity $GuardIdentity
    StrCmp $SetupCode 0 journal_inspect_directory_opened
    Goto journal_inspect_close
journal_inspect_directory_opened:
    StrCmp $SetupCode 0 journal_inspect_directory_open
    Goto journal_inspect_close
journal_inspect_directory_open:
    StrCpy $SetupJournalReadDirectoryHandle $GuardHandle
    StrCpy $SetupJournalReadDirectoryIdentity $GuardIdentity
    StrCpy $SetupCode 13
    StrCpy $GuardHandle $SetupJournalReadDirectoryHandle
    StrCpy $GuardDirectory 1
    Call ${PREFIX}ValidatePrivateHandleAcl
    StrCmp $SetupCode 0 journal_inspect_directory_acl
    Goto journal_inspect_close
journal_inspect_directory_acl:
    StrCpy $SetupCode 13
    StrCpy $GuardPath "$SetupFixedRoot\.GitHubBackupTool.state\journal.ini"
    StrCpy $GuardDirectory 0
    Call ${PREFIX}OpenPathIdentityLease
    StrCmp $GuardHandle 0 journal_inspect_file_opened
    StrCpy $SetupJournalReadFileHandle $GuardHandle
    StrCpy $SetupJournalReadFileIdentity $GuardIdentity
    StrCmp $SetupCode 0 journal_inspect_file_opened
    Goto journal_inspect_close
journal_inspect_file_opened:
    StrCmp $SetupCode 0 journal_inspect_file_open
    Goto journal_inspect_close
journal_inspect_file_open:
    StrCpy $SetupJournalReadFileHandle $GuardHandle
    StrCpy $SetupJournalReadFileIdentity $GuardIdentity
    StrCpy $SetupCode 13
    StrCpy $GuardHandle $SetupJournalReadFileHandle
    StrCpy $GuardDirectory 0
    Call ${PREFIX}ValidatePrivateHandleAcl
    StrCmp $SetupCode 0 journal_inspect_file_acl
    Goto journal_inspect_close
journal_inspect_file_acl:
    StrCpy $SetupCode 13
    Call ${PREFIX}ReadFreshInstallJournalRecord
    StrCmp $SetupCode 0 journal_inspect_parse_ok
    Goto journal_inspect_close
journal_inspect_parse_ok:
journal_inspect_close:
    StrCmp $SetupJournalReadFileHandle 0 journal_inspect_close_directory
    System::Call 'kernel32::CloseHandle(p $SetupJournalReadFileHandle) i.r0'
    StrCmp $0 0 journal_inspect_close_file_failed
    StrCpy $SetupJournalReadFileHandle 0
    StrCpy $SetupJournalReadFileIdentity ""
    Goto journal_inspect_close_directory
journal_inspect_close_file_failed:
    StrCpy $SetupCode 13
journal_inspect_close_directory:
    StrCmp $SetupJournalReadDirectoryHandle 0 journal_inspect_close_root
    System::Call 'kernel32::CloseHandle(p $SetupJournalReadDirectoryHandle) i.r0'
    StrCmp $0 0 journal_inspect_close_directory_failed
    StrCpy $SetupJournalReadDirectoryHandle 0
    StrCpy $SetupJournalReadDirectoryIdentity ""
    Goto journal_inspect_close_root
journal_inspect_close_directory_failed:
    StrCpy $SetupCode 13
journal_inspect_close_root:
    StrCmp $SetupJournalReadRootHandle 0 journal_inspect_result
    System::Call 'kernel32::CloseHandle(p $SetupJournalReadRootHandle) i.r0'
    StrCmp $0 0 journal_inspect_close_root_failed
    StrCpy $SetupJournalReadRootHandle 0
    StrCpy $SetupJournalReadRootIdentity ""
    Goto journal_inspect_result
journal_inspect_close_root_failed:
    StrCpy $SetupCode 13
journal_inspect_result:
    StrCmp $SetupCode 0 journal_inspect_parsed
    StrCpy $SetupJournalReadStatus "REJECTED"
    StrCpy $SetupJournalReadPhase ""
    Goto journal_inspect_done
journal_inspect_parsed:
    StrCpy $SetupJournalReadStatus "PARSED"
journal_inspect_done:
    Pop $GuardValueLength
    Pop $GuardValue
    Pop $GuardSid
    Pop $GuardLastError
    Pop $GuardHash
    Pop $GuardIdentity
    Pop $GuardHandle
    Pop $GuardDirectory
    Pop $GuardPath
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Prepare the durable PREPARED record before either payload file is created.
; The state folder and journal are both CREATE_NEW objects with protected ACLs;
; only their original returned handles are retained or ever used for cleanup.
Function ${PREFIX}PrepareFreshInstallJournal
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 13
    StrCmp $SetupMode "install" journal_prepare_mode_ok
    Goto journal_prepare_done
journal_prepare_mode_ok:
    StrCmp $SetupJournalDirectoryCreated "" journal_prepare_dir_created_empty
    StrCmp $SetupJournalDirectoryCreated 0 journal_prepare_dir_created_empty
    Goto journal_prepare_done
journal_prepare_dir_created_empty:
    StrCmp $SetupJournalFileCreated "" journal_prepare_file_created_empty
    StrCmp $SetupJournalFileCreated 0 journal_prepare_file_created_empty
    Goto journal_prepare_done
journal_prepare_file_created_empty:
    StrCmp $SetupJournalDirectoryHandle "" journal_prepare_dir_handle_empty
    StrCmp $SetupJournalDirectoryHandle 0 journal_prepare_dir_handle_empty
    Goto journal_prepare_done
journal_prepare_dir_handle_empty:
    StrCmp $SetupJournalFileHandle "" journal_prepare_file_handle_empty
    StrCmp $SetupJournalFileHandle 0 journal_prepare_file_handle_empty
    Goto journal_prepare_done
journal_prepare_file_handle_empty:
    StrCmp $SetupJournalDirectoryIdentity "" journal_prepare_dir_identity_empty
    Goto journal_prepare_done
journal_prepare_dir_identity_empty:
    StrCmp $SetupJournalFileIdentity "" journal_prepare_file_identity_empty
    Goto journal_prepare_done
journal_prepare_file_identity_empty:
    StrCmp $SetupJournalDirectoryDeletePending "" journal_prepare_dir_pending_empty
    StrCmp $SetupJournalDirectoryDeletePending 0 journal_prepare_dir_pending_empty
    Goto journal_prepare_done
journal_prepare_dir_pending_empty:
    StrCmp $SetupJournalFileDeletePending "" journal_prepare_file_pending_empty
    StrCmp $SetupJournalFileDeletePending 0 journal_prepare_file_pending_empty
    Goto journal_prepare_done
journal_prepare_file_pending_empty:
    StrCmp $SetupJournalFileHash "" journal_prepare_hash_empty
    Goto journal_prepare_done
journal_prepare_hash_empty:
    StrCmp $SetupJournalPhase "" journal_prepare_phase_empty
    Goto journal_prepare_done
journal_prepare_phase_empty:
    StrCmp $SetupJournalSecurityDescriptor "" journal_prepare_descriptor_empty
    StrCmp $SetupJournalSecurityDescriptor 0 journal_prepare_descriptor_empty
    Goto journal_prepare_done
journal_prepare_descriptor_empty:
    StrCmp $SetupJournalSecurityAttributes "" journal_prepare_attributes_empty
    StrCmp $SetupJournalSecurityAttributes 0 journal_prepare_attributes_empty
    Goto journal_prepare_done
journal_prepare_attributes_empty:
    StrCmp $SetupJournalInfoBuffer "" journal_prepare_info_empty
    StrCmp $SetupJournalInfoBuffer 0 journal_prepare_info_empty
    Goto journal_prepare_done
journal_prepare_info_empty:
    StrCmp $SetupJournalRecord "" journal_prepare_record_empty
    Goto journal_prepare_done
journal_prepare_record_empty:
    Call ${PREFIX}ValidateFreshInstallJournalRoot
    StrCmp $SetupCode 0 journal_prepare_root_ready
    Goto journal_prepare_cleanup
journal_prepare_root_ready:
    ; The validator returns SetupCode=0 on success. Restore the prepare
    ; transaction's fail-closed status before any later fallible operation.
    StrCpy $SetupCode 13
    ; Preallocate before the directory create so validation need not allocate
    ; after an irreversible CREATE_NEW operation.
    System::Alloc 52
    Pop $SetupJournalInfoBuffer
    StrCmp $SetupJournalInfoBuffer 0 journal_prepare_cleanup
    StrCpy $0 $SetupOwnerSid
    StrCpy $8 0
    System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "O:$0D:P(A;;GA;;;$0)(A;;GA;;;SY)(A;;GA;;;BA)", i 1, *p .r8, p 0) i.r1'
    StrCmp $1 0 journal_prepare_cleanup
    StrCmp $8 0 journal_prepare_cleanup
    StrCpy $SetupJournalSecurityDescriptor $8
    System::Call '*(i 12, p r8, i 0) p.r9' ; x86 SECURITY_ATTRIBUTES, non-inheritable
    StrCmp $9 0 journal_prepare_cleanup
    StrCpy $SetupJournalSecurityAttributes $9
    System::Call 'kernel32::GetModuleHandleW(w "kernel32.dll") p.r1'
    StrCmp $1 0 journal_prepare_cleanup
    System::Call 'kernel32::GetProcAddress(p r1, m "CreateDirectory2W") p.r2'
    StrCmp $2 0 journal_prepare_cleanup
    ; Create the fixed state directory relative to the pinned empty product root.
    ; FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | READ_CONTROL | DELETE | SYNCHRONIZE;
    ; share-read only; DISALLOW_PATH_REDIRECTS; apply protected current-user ACL.
    System::Call '::$2(w "$SetupFixedRoot\.GitHubBackupTool.state", i 0x130081, i 1, i 1, p $SetupJournalSecurityAttributes) p.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 0 journal_prepare_cleanup
    StrCmp $0 -1 journal_prepare_cleanup
    StrCpy $SetupJournalDirectoryHandle $0
    StrCpy $SetupJournalDirectoryCreated 1
    StrCpy $SetupJournalDirectoryDeletePending 0
    StrCpy $SetupJournalDirectoryIdentity ""
    StrCpy $GuardLastError 0
    Call ${PREFIX}ValidateFreshInstallJournalDirectory
    StrCmp $SetupCode 0 journal_prepare_directory_ready
    Goto journal_prepare_cleanup
journal_prepare_directory_ready:
    Call ${PREFIX}ValidateFreshInstallJournalDirectory
    StrCmp $SetupCode 0 journal_prepare_file_parent_ready
    Goto journal_prepare_cleanup
journal_prepare_file_parent_ready:
    StrCpy $SetupCode 13
    ; CREATE_NEW and share-none make collisions and pre-existing journals a hard
    ; refusal. The handle and ownership flag are stored before any validation.
    System::Call 'kernel32::CreateFileW(w "$SetupFixedRoot\.GitHubBackupTool.state\journal.ini", i 0xC0010000, i 0, p $SetupJournalSecurityAttributes, i 1, i 0x00200080, p 0) p.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 -1 journal_prepare_cleanup
    StrCmp $0 0 journal_prepare_cleanup
    StrCpy $SetupJournalFileHandle $0
    StrCpy $SetupJournalFileCreated 1
    StrCpy $SetupJournalFileDeletePending 0
    StrCpy $SetupJournalFileIdentity ""
    StrCpy $SetupJournalFileHash ""
    StrCpy $GuardLastError 0
    Call ${PREFIX}ValidateFreshInstallJournalFile
    StrCmp $SetupCode 0 journal_prepare_file_created
    Goto journal_prepare_cleanup
journal_prepare_file_created:
    StrCpy $SetupCode 13
    StrCpy $SetupJournalRecord "[GitHubBackupSetupJournal]$\r$\nschema=1$\r$\nproductId=${SETUP_PRODUCT_ID}$\r$\nownerSid=$SetupOwnerSid$\r$\nappVersion=${SETUP_APP_VERSION}$\r$\nsourceCommit=${SETUP_SOURCE_COMMIT}$\r$\npayloadSha256=${SETUP_APP_SHA256}$\r$\n${SETUP_APP_NAME}=${SETUP_APP_SHA256}$\r$\n${SETUP_NOTICE_NAME}=${SETUP_NOTICE_SHA256}$\r$\nphase=PREPARED$\r$\n"
    StrLen $3 $SetupJournalRecord
    IntCmpU $3 1024 journal_prepare_cleanup journal_prepare_record_bounded journal_prepare_cleanup
journal_prepare_record_bounded:
    IntOp $3 $3 * 2 ; UTF-16LE byte count, excluding the BOM
    System::Call '*(&i2 0xFEFF) p.r7'
    StrCmp $7 0 journal_prepare_cleanup
    System::Call 'kernel32::WriteFile(p $SetupJournalFileHandle, p r7, i 2, *i .r4, p 0) i.r5'
    System::Free $7
    StrCmp $5 0 journal_prepare_cleanup
    StrCmp $4 2 0 journal_prepare_cleanup
    System::Call 'kernel32::WriteFile(p $SetupJournalFileHandle, w "$SetupJournalRecord", i r3, *i .r4, p 0) i.r5'
    StrCmp $5 0 journal_prepare_cleanup
    StrCmp $4 $3 0 journal_prepare_cleanup
    System::Call 'kernel32::FlushFileBuffers(p $SetupJournalFileHandle) i.r1'
    StrCmp $1 0 journal_prepare_cleanup
    Call ${PREFIX}ValidateFreshInstallJournalFile
    StrCmp $SetupCode 0 journal_prepare_hash
    Goto journal_prepare_cleanup
journal_prepare_hash:
    Push $GuardHandle
    Push $GuardDirectory
    Push $GuardHash
    StrCpy $GuardHandle $SetupJournalFileHandle
    StrCpy $GuardDirectory 0
    Call ${PREFIX}HashHandleSha256
    StrCmp $SetupCode 0 journal_prepare_hash_ok
    Pop $GuardHash
    Pop $GuardDirectory
    Pop $GuardHandle
    Goto journal_prepare_cleanup
journal_prepare_hash_ok:
    StrCpy $SetupJournalFileHash $GuardHash
    Pop $GuardHash
    Pop $GuardDirectory
    Pop $GuardHandle
    StrCpy $SetupJournalPhase "PREPARED"
    StrCpy $SetupCode 0
journal_prepare_cleanup:
    StrCmp $SetupJournalRecord "" journal_prepare_free_attributes
    StrCpy $SetupJournalRecord ""
journal_prepare_free_attributes:
    StrCmp $SetupJournalSecurityAttributes "" journal_prepare_free_descriptor
    StrCmp $SetupJournalSecurityAttributes 0 journal_prepare_free_descriptor
    System::Free $SetupJournalSecurityAttributes
    StrCpy $SetupJournalSecurityAttributes 0
journal_prepare_free_descriptor:
    StrCmp $SetupJournalSecurityDescriptor "" journal_prepare_free_info
    StrCmp $SetupJournalSecurityDescriptor 0 journal_prepare_free_info
    System::Call 'kernel32::LocalFree(p $SetupJournalSecurityDescriptor) p.r1'
    StrCmp $1 0 journal_prepare_descriptor_freed
    StrCpy $SetupCode 13
    Goto journal_prepare_free_info
journal_prepare_descriptor_freed:
    StrCpy $SetupJournalSecurityDescriptor 0
journal_prepare_free_info:
    StrCmp $SetupJournalInfoBuffer "" journal_prepare_done
    StrCmp $SetupJournalInfoBuffer 0 journal_prepare_done
    System::Free $SetupJournalInfoBuffer
    StrCpy $SetupJournalInfoBuffer 0
journal_prepare_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Append and flush FILES_WRITTEN only after both expected file copies have
; returned success and their exact handles remain registered in the ledger.
Function ${PREFIX}WriteFreshInstallJournalFilesWritten
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 13
    StrCmp $SetupJournalPhase "PREPARED" journal_files_prepared
    Goto journal_files_done
journal_files_prepared:
    StrCmp $SetupCopyTxnAppCreated 1 journal_files_app_created
    Goto journal_files_done
journal_files_app_created:
    StrCmp $SetupCopyTxnAppHandle "" journal_files_done
    StrCmp $SetupCopyTxnAppHandle 0 journal_files_done
    StrCmp $SetupCopyTxnAppIdentity "" journal_files_done
    StrCmp $SetupCopyTxnAppHash "${SETUP_APP_SHA256}" journal_files_app_valid
    Goto journal_files_done
journal_files_app_valid:
    StrCmp $SetupCopyTxnNoticeCreated 1 journal_files_notice_created
    Goto journal_files_done
journal_files_notice_created:
    StrCmp $SetupCopyTxnNoticeHandle "" journal_files_done
    StrCmp $SetupCopyTxnNoticeHandle 0 journal_files_done
    StrCmp $SetupCopyTxnNoticeIdentity "" journal_files_done
    StrCmp $SetupCopyTxnNoticeHash "${SETUP_NOTICE_SHA256}" journal_files_ledger_valid
    Goto journal_files_done
journal_files_ledger_valid:
    Call ${PREFIX}ValidateFreshInstallJournalFile
    StrCmp $SetupCode 0 journal_files_record_check
    Goto journal_files_done
journal_files_record_check:
    ; Detect any unexpected journal change before appending the second phase.
    Push $GuardHandle
    Push $GuardDirectory
    Push $GuardHash
    StrCpy $GuardHandle $SetupJournalFileHandle
    StrCpy $GuardDirectory 0
    Call ${PREFIX}HashHandleSha256
    StrCmp $SetupCode 0 journal_files_record_hash
    Pop $GuardHash
    Pop $GuardDirectory
    Pop $GuardHandle
    Goto journal_files_done
journal_files_record_hash:
    ; HashHandleSha256 clears SetupCode on success; mismatch is still failure.
    StrCpy $SetupCode 13
    StrCmp $GuardHash $SetupJournalFileHash journal_files_append
    Pop $GuardHash
    Pop $GuardDirectory
    Pop $GuardHandle
    Goto journal_files_done
journal_files_append:
    Pop $GuardHash
    Pop $GuardDirectory
    Pop $GuardHandle
    ; From here until the post-flush hash succeeds, every failure must stay nonzero.
    StrCpy $SetupCode 13
    StrCpy $SetupJournalRecord "phase=FILES_WRITTEN$\r$\n"
    StrLen $3 $SetupJournalRecord
    IntOp $3 $3 * 2
    System::Call 'kernel32::SetFilePointerEx(p $SetupJournalFileHandle, l 0, p 0, i 2) i.r1'
    StrCmp $1 0 journal_files_cleanup
    System::Call 'kernel32::WriteFile(p $SetupJournalFileHandle, w "$SetupJournalRecord", i r3, *i .r4, p 0) i.r5'
    StrCmp $5 0 journal_files_cleanup
    StrCmp $4 $3 0 journal_files_cleanup
    System::Call 'kernel32::FlushFileBuffers(p $SetupJournalFileHandle) i.r1'
    StrCmp $1 0 journal_files_cleanup
    Call ${PREFIX}ValidateFreshInstallJournalFile
    StrCmp $SetupCode 0 journal_files_hash_updated
    Goto journal_files_cleanup
journal_files_hash_updated:
    Push $GuardHandle
    Push $GuardDirectory
    Push $GuardHash
    StrCpy $GuardHandle $SetupJournalFileHandle
    StrCpy $GuardDirectory 0
    Call ${PREFIX}HashHandleSha256
    StrCmp $SetupCode 0 journal_files_hash_ok
    Pop $GuardHash
    Pop $GuardDirectory
    Pop $GuardHandle
    Goto journal_files_cleanup
journal_files_hash_ok:
    StrCpy $SetupJournalFileHash $GuardHash
    Pop $GuardHash
    Pop $GuardDirectory
    Pop $GuardHandle
    StrCpy $SetupJournalPhase "FILES_WRITTEN"
    StrCpy $SetupCode 0
journal_files_cleanup:
    StrCpy $SetupJournalRecord ""
journal_files_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Delete only the journal file and state directory created by this transaction.
; A pending disposition keeps the exact handle/identity until CloseHandle works;
; errors never clear ownership slots or fall back to pathname deletion.
Function ${PREFIX}RollbackFreshInstallJournal
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 13
    StrCmp $SetupJournalFileCreated "" journal_rollback_file_created_empty
    StrCmp $SetupJournalFileCreated 0 journal_rollback_file_created_empty
    StrCmp $SetupJournalFileCreated 1 journal_rollback_file_created
    Goto journal_rollback_done
journal_rollback_file_created:
    StrCmp $SetupJournalFileHandle "" journal_rollback_done
    StrCmp $SetupJournalFileHandle 0 journal_rollback_done
    StrCmp $SetupJournalFileDeletePending 1 journal_rollback_file_close
    StrCmp $SetupJournalFileDeletePending "" journal_rollback_file_pending_empty
    StrCmp $SetupJournalFileDeletePending 0 journal_rollback_file_pending_empty
    Goto journal_rollback_done
journal_rollback_file_pending_empty:
    Call ${PREFIX}ValidateFreshInstallJournalFile
    StrCmp $SetupCode 0 journal_rollback_file_dispose
    Goto journal_rollback_done
journal_rollback_file_dispose:
    ; The validator succeeded with SetupCode=0; cleanup failures must stay nonzero.
    StrCpy $SetupCode 13
    System::Alloc 1
    Pop $0
    StrCmp $0 0 journal_rollback_done
    System::Call '*$0(&i1 1)'
    System::Call 'kernel32::SetFileInformationByHandle(p $SetupJournalFileHandle, i 4, p r0, i 1) i.r1'
    System::Free $0
    StrCmp $1 0 journal_rollback_done
    StrCpy $SetupJournalFileDeletePending 1
journal_rollback_file_close:
    System::Call 'kernel32::CloseHandle(p $SetupJournalFileHandle) i.r0'
    StrCmp $0 0 journal_rollback_done
    StrCpy $SetupJournalFileHandle 0
    StrCpy $SetupJournalFileIdentity ""
    StrCpy $SetupJournalFileHash ""
    StrCpy $SetupJournalFileCreated 0
    StrCpy $SetupJournalFileDeletePending 0
    Goto journal_rollback_directory
journal_rollback_file_created_empty:
    StrCmp $SetupJournalFileHandle "" journal_rollback_file_identity_empty
    StrCmp $SetupJournalFileHandle 0 journal_rollback_file_identity_empty
    Goto journal_rollback_done
journal_rollback_file_identity_empty:
    StrCmp $SetupJournalFileIdentity "" journal_rollback_file_hash_empty
    Goto journal_rollback_done
journal_rollback_file_hash_empty:
    StrCmp $SetupJournalFileHash "" journal_rollback_file_pending_state_empty
    Goto journal_rollback_done
journal_rollback_file_pending_state_empty:
    StrCmp $SetupJournalFileDeletePending "" journal_rollback_directory
    StrCmp $SetupJournalFileDeletePending 0 journal_rollback_directory
    Goto journal_rollback_done

journal_rollback_directory:
    StrCmp $SetupJournalDirectoryCreated "" journal_rollback_directory_created_empty
    StrCmp $SetupJournalDirectoryCreated 0 journal_rollback_directory_created_empty
    StrCmp $SetupJournalDirectoryCreated 1 journal_rollback_directory_created
    Goto journal_rollback_done
journal_rollback_directory_created:
    StrCmp $SetupJournalDirectoryHandle "" journal_rollback_done
    StrCmp $SetupJournalDirectoryHandle 0 journal_rollback_done
    StrCmp $SetupJournalDirectoryDeletePending 1 journal_rollback_directory_close
    StrCmp $SetupJournalDirectoryDeletePending "" journal_rollback_directory_pending_empty
    StrCmp $SetupJournalDirectoryDeletePending 0 journal_rollback_directory_pending_empty
    Goto journal_rollback_done
journal_rollback_directory_pending_empty:
    Call ${PREFIX}ValidateFreshInstallJournalDirectory
    StrCmp $SetupCode 0 journal_rollback_directory_dispose
    Goto journal_rollback_done
journal_rollback_directory_dispose:
    ; The validator succeeded with SetupCode=0; cleanup failures must stay nonzero.
    StrCpy $SetupCode 13
    System::Alloc 1
    Pop $0
    StrCmp $0 0 journal_rollback_done
    System::Call '*$0(&i1 1)'
    System::Call 'kernel32::SetFileInformationByHandle(p $SetupJournalDirectoryHandle, i 4, p r0, i 1) i.r1'
    System::Free $0
    StrCmp $1 0 journal_rollback_done
    StrCpy $SetupJournalDirectoryDeletePending 1
journal_rollback_directory_close:
    System::Call 'kernel32::CloseHandle(p $SetupJournalDirectoryHandle) i.r0'
    StrCmp $0 0 journal_rollback_done
    StrCpy $SetupJournalDirectoryHandle 0
    StrCpy $SetupJournalDirectoryIdentity ""
    StrCpy $SetupJournalDirectoryCreated 0
    StrCpy $SetupJournalDirectoryDeletePending 0
    StrCpy $SetupJournalPhase ""
    StrCpy $SetupCode 0
    Goto journal_rollback_done
journal_rollback_directory_created_empty:
    StrCmp $SetupJournalDirectoryHandle "" journal_rollback_directory_identity_empty
    StrCmp $SetupJournalDirectoryHandle 0 journal_rollback_directory_identity_empty
    Goto journal_rollback_done
journal_rollback_directory_identity_empty:
    StrCmp $SetupJournalDirectoryIdentity "" journal_rollback_directory_pending_state_empty
    Goto journal_rollback_done
journal_rollback_directory_pending_state_empty:
    StrCmp $SetupJournalDirectoryDeletePending "" journal_rollback_empty_success
    StrCmp $SetupJournalDirectoryDeletePending 0 journal_rollback_empty_success
    Goto journal_rollback_done
journal_rollback_empty_success:
    StrCpy $SetupJournalPhase ""
    StrCpy $SetupCode 0
journal_rollback_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Copy the only two payload files permitted by the v1 fresh-install contract.
; The empty-root scan returns a retained root/ancestor lease; both file copies
; reuse it. Successful file handles stay in separate transaction slots until a
; later whole-install commit or this paired abort completes.
Function ${PREFIX}CopyFreshInstallPayloadFilesToFixedRoot
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 13
    StrCmp $SetupMode "install" copy_txn_mode_ok
    Goto copy_txn_done
copy_txn_mode_ok:
    StrCmp $SetupCopyTransactionActive "" copy_txn_active_empty
    StrCmp $SetupCopyTransactionActive 0 copy_txn_active_empty
    Goto copy_txn_done
copy_txn_active_empty:
    StrCmp $SetupCopyTxnAppCreated "" copy_txn_app_created_empty
    StrCmp $SetupCopyTxnAppCreated 0 copy_txn_app_created_empty
    Goto copy_txn_done
copy_txn_app_created_empty:
    StrCmp $SetupCopyTxnNoticeCreated "" copy_txn_notice_created_empty
    StrCmp $SetupCopyTxnNoticeCreated 0 copy_txn_notice_created_empty
    Goto copy_txn_done
copy_txn_notice_created_empty:
    StrCmp $SetupCopyTxnAppHandle "" copy_txn_app_handle_empty
    StrCmp $SetupCopyTxnAppHandle 0 copy_txn_app_handle_empty
    Goto copy_txn_done
copy_txn_app_handle_empty:
    StrCmp $SetupCopyTxnNoticeHandle "" copy_txn_notice_handle_empty
    StrCmp $SetupCopyTxnNoticeHandle 0 copy_txn_notice_handle_empty
    Goto copy_txn_done
copy_txn_notice_handle_empty:
    StrCmp $SetupCopyRootHandle "" copy_txn_root_empty
    StrCmp $SetupCopyRootHandle 0 copy_txn_root_empty
    Goto copy_txn_done
copy_txn_root_empty:
    StrCmp $SetupCopyRootLeasePending "" copy_txn_root_pending_empty
    StrCmp $SetupCopyRootLeasePending 0 copy_txn_root_pending_empty
    Goto copy_txn_done
copy_txn_root_pending_empty:
    StrCmp $SetupCopySourceHandle "" copy_txn_source_empty
    StrCmp $SetupCopySourceHandle 0 copy_txn_source_empty
    Goto copy_txn_done
copy_txn_source_empty:
    StrCmp $SetupCopyTargetHandle "" copy_txn_target_empty
    StrCmp $SetupCopyTargetHandle 0 copy_txn_target_empty
    Goto copy_txn_done
copy_txn_target_empty:
    StrCmp $GuardHandle "" copy_txn_guard_empty
    StrCmp $GuardHandle 0 copy_txn_guard_empty
    Goto copy_txn_done
copy_txn_guard_empty:
    StrCmp $GuardPathPins "" copy_txn_pins_empty
    StrCmp $GuardPathPins 0 copy_txn_pins_empty
    Goto copy_txn_done
copy_txn_pins_empty:
    StrCmp $GuardPathPinCount 0 copy_txn_begin
    Goto copy_txn_done
copy_txn_begin:
    StrCpy $SetupCopyTransactionActive 1
    StrCpy $SetupCopyRootLeasePending 1
    Call ${PREFIX}ValidateEmptyFixedRootDirectory
    StrCmp $SetupCode 0 copy_txn_root_valid
    ; Abort retries any root handle/pins retained by a failed read-only lease
    ; release. The active marker stays set if any resource remains.
    Call ${PREFIX}AbortFreshInstallCopyTransaction
    StrCpy $SetupCode 13
    Goto copy_txn_done
copy_txn_root_valid:
    StrCmp $GuardHandle 0 copy_txn_root_bad
    StrCmp $GuardIdentity "" copy_txn_root_bad
    StrCpy $SetupCopyRootHandle $GuardHandle
    StrCpy $SetupCopyRootIdentity $GuardIdentity
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""
    StrCpy $SetupCopyRootLeasePending 0
    Call ${PREFIX}PrepareFreshInstallJournal
    StrCmp $SetupCode 0 copy_txn_journal_ready
    Goto copy_txn_abort
copy_txn_journal_ready:
    StrCmp $SetupJournalPhase "PREPARED" 0 copy_txn_abort
    StrCpy $SetupCopyTargetName "${SETUP_APP_NAME}"
    Call ${PREFIX}CopyTrustedStageFileToFixedRoot
    StrCmp $SetupCode 0 copy_txn_copy_notice
    Goto copy_txn_abort
copy_txn_copy_notice:
    StrCpy $SetupCopyTargetName "${SETUP_NOTICE_NAME}"
    Call ${PREFIX}CopyTrustedStageFileToFixedRoot
    StrCmp $SetupCode 0 copy_txn_files_written
    Goto copy_txn_abort
copy_txn_files_written:
    Call ${PREFIX}WriteFreshInstallJournalFilesWritten
    StrCmp $SetupCode 0 copy_txn_success
    Goto copy_txn_abort
copy_txn_success:
    StrCmp $SetupJournalPhase "FILES_WRITTEN" 0 copy_txn_abort
    StrCpy $SetupCode 0
    Goto copy_txn_done
copy_txn_root_bad:
    Call ${PREFIX}AbortFreshInstallCopyTransaction
    StrCpy $SetupCode 13
    Goto copy_txn_done
copy_txn_abort:
    Call ${PREFIX}AbortFreshInstallCopyTransaction
    StrCpy $SetupCode 13
copy_txn_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Write the canonical ownership receipt only after the payload journal is in
; FILES_WRITTEN and both generated companion files have retained, verified
; handles. The receipt itself is CREATE_NEW, flushed, revalidated, hashed and
; parsed from its original handle before the caller may commit the install.
Function ${PREFIX}WriteFreshInstallReceipt
    !insertmacro SetupSaveRegisters
    Push $GuardHandle
    Push $GuardDirectory
    Push $GuardIdentity
    Push $GuardHash
    Push $GuardLastError
    StrCpy $SetupCode 13
    StrCmp $SetupMode "install" receipt_write_mode_ok
    Goto receipt_write_done
receipt_write_mode_ok:
    StrCmp $SetupCopyTransactionActive 1 receipt_write_txn_active
    Goto receipt_write_done
receipt_write_txn_active:
    StrCmp $SetupJournalPhase "FILES_WRITTEN" receipt_write_phase_ok
    Goto receipt_write_done
receipt_write_phase_ok:
    StrCmp $SetupCopyTxnAppCreated 1 receipt_write_app_created
    Goto receipt_write_done
receipt_write_app_created:
    StrCmp $SetupCopyTxnAppHandle 0 receipt_write_done
    StrCmp $SetupCopyTxnAppHash "${SETUP_APP_SHA256}" receipt_write_app_valid
    Goto receipt_write_done
receipt_write_app_valid:
    StrCmp $SetupCopyTxnNoticeCreated 1 receipt_write_notice_created
    Goto receipt_write_done
receipt_write_notice_created:
    StrCmp $SetupCopyTxnNoticeHandle 0 receipt_write_done
    StrCmp $SetupCopyTxnNoticeHash "${SETUP_NOTICE_SHA256}" receipt_write_notice_valid
    Goto receipt_write_done
receipt_write_notice_valid:
    StrCmp $SetupInstallUninstallerHandle 0 receipt_write_uninstaller_handle
    Goto receipt_write_done
receipt_write_uninstaller_handle:
    StrCmp $SetupInstallUninstallerIdentity "" receipt_write_done
    StrCpy $GuardValue $SetupInstallUninstallerHash
    StrCpy $GuardValueLength 64
    Call ${PREFIX}ValidateHexText
    StrCmp $SetupCode 0 receipt_write_uninstaller_hash
    Goto receipt_write_done
receipt_write_uninstaller_hash:
    StrCpy $SetupCode 13
    StrCpy $SetupJournalVerifyObjectHandle $SetupInstallUninstallerHandle
    StrCpy $SetupJournalVerifyPath "\\?\$SetupFixedRoot\${SETUP_UNINSTALLER_NAME}"
    StrCpy $SetupJournalVerifyIdentity $SetupInstallUninstallerIdentity
    StrCpy $SetupJournalVerifyDirectory 0
    StrCpy $SetupJournalVerifyCreated 1
    Call ${PREFIX}ValidateFreshInstallJournalHandle
    StrCmp $SetupCode 0 receipt_write_uninstaller_verified
    Goto receipt_write_done
receipt_write_uninstaller_verified:
    StrCpy $SetupInstallUninstallerIdentity $SetupJournalVerifyIdentity
    StrCpy $GuardHandle $SetupInstallUninstallerHandle
    StrCpy $GuardDirectory 0
    Call ${PREFIX}HashHandleSha256
    StrCmp $SetupCode 0 receipt_write_uninstaller_hash_matches
    Goto receipt_write_done
receipt_write_uninstaller_hash_matches:
    ; HashHandleSha256 clears SetupCode on success; keep mismatch fail-closed.
    StrCpy $SetupCode 13
    StrCmp $GuardHash $SetupInstallUninstallerHash receipt_write_shortcut_handle
    Goto receipt_write_done
receipt_write_shortcut_handle:
    StrCmp $SetupInstallShortcutHandle 0 receipt_write_shortcut_identity
    Goto receipt_write_done
receipt_write_shortcut_identity:
    StrCmp $SetupInstallShortcutIdentity "" receipt_write_done
    StrCmp $SetupInstallShortcutPath "" receipt_write_done
    StrCpy $GuardPath $SetupInstallShortcutPath
    Call ${PREFIX}ValidateLocalPathText
    StrCmp $SetupCode 0 receipt_write_shortcut_hash
    Goto receipt_write_done
receipt_write_shortcut_hash:
    StrCpy $SetupCode 13
    StrCpy $GuardValue $SetupInstallShortcutHash
    StrCpy $GuardValueLength 64
    Call ${PREFIX}ValidateHexText
    StrCmp $SetupCode 0 receipt_write_shortcut_verify
    Goto receipt_write_done
receipt_write_shortcut_verify:
    StrCpy $SetupCode 13
    StrCpy $SetupJournalVerifyObjectHandle $SetupInstallShortcutHandle
    StrCpy $SetupJournalVerifyPath "\\?\$SetupInstallShortcutPath"
    StrCpy $SetupJournalVerifyIdentity $SetupInstallShortcutIdentity
    StrCpy $SetupJournalVerifyDirectory 0
    StrCpy $SetupJournalVerifyCreated 1
    Call ${PREFIX}ValidateFreshInstallJournalHandle
    StrCmp $SetupCode 0 receipt_write_shortcut_verified
    Goto receipt_write_done
receipt_write_shortcut_verified:
    StrCpy $SetupInstallShortcutIdentity $SetupJournalVerifyIdentity
    StrCpy $GuardHandle $SetupInstallShortcutHandle
    StrCpy $GuardDirectory 0
    Call ${PREFIX}HashHandleSha256
    StrCmp $SetupCode 0 receipt_write_shortcut_hash_matches
    Goto receipt_write_done
receipt_write_shortcut_hash_matches:
    ; HashHandleSha256 clears SetupCode on success; keep mismatch fail-closed.
    StrCpy $SetupCode 13
    StrCmp $GuardHash $SetupInstallShortcutHash receipt_write_shortcut_binding
    Goto receipt_write_done
receipt_write_shortcut_binding:
    Call ${PREFIX}ValidateShortcutTargetCapacity
    StrCmp $SetupCode 0 receipt_write_shortcut_binding_ready
    Goto receipt_write_done
receipt_write_shortcut_binding_ready:
    StrCpy $GuardHandle $SetupInstallShortcutHandle
    StrCpy $GuardDirectory 0
    Call ${PREFIX}ValidateShortcutBinding
    StrCmp $SetupCode 0 receipt_write_shortcut_bound
    Goto receipt_write_done
receipt_write_shortcut_bound:
    StrCpy $SetupCode 13
    StrCmp $SetupInstallReceiptCreated "" receipt_write_created_empty
    StrCmp $SetupInstallReceiptCreated 0 receipt_write_created_empty
    Goto receipt_write_done
receipt_write_created_empty:
    StrCmp $SetupInstallReceiptHandle "" receipt_write_handle_empty
    StrCmp $SetupInstallReceiptHandle 0 receipt_write_handle_empty
    Goto receipt_write_done
receipt_write_handle_empty:
    StrCmp $SetupInstallReceiptIdentity "" receipt_write_identity_empty
    Goto receipt_write_done
receipt_write_identity_empty:
    StrCmp $SetupInstallReceiptHash "" receipt_write_hash_empty
    Goto receipt_write_done
receipt_write_hash_empty:
    StrCmp $SetupInstallReceiptSecurityDescriptor "" receipt_write_descriptor_empty
    StrCmp $SetupInstallReceiptSecurityDescriptor 0 receipt_write_descriptor_empty
    Goto receipt_write_done
receipt_write_descriptor_empty:
    StrCmp $SetupInstallReceiptSecurityAttributes "" receipt_write_attributes_empty
    StrCmp $SetupInstallReceiptSecurityAttributes 0 receipt_write_attributes_empty
    Goto receipt_write_done
receipt_write_attributes_empty:
    StrCpy $SetupInstallReceiptRecord "[Installation]$\r$\nschema=1$\r$\nproductId=${SETUP_PRODUCT_ID}$\r$\nownerSid=$SetupOwnerSid$\r$\nappVersion=${SETUP_APP_VERSION}$\r$\nsourceCommit=${SETUP_SOURCE_COMMIT}$\r$\npayloadSha256=${SETUP_APP_SHA256}$\r$\n${SETUP_APP_NAME}=${SETUP_APP_SHA256}$\r$\n${SETUP_UNINSTALLER_NAME}=$SetupInstallUninstallerHash$\r$\n${SETUP_NOTICE_NAME}=${SETUP_NOTICE_SHA256}$\r$\nstartMenu=1$\r$\ndesktop=0$\r$\nstartMenuSha256=$SetupInstallShortcutHash$\r$\ndesktopSha256=none$\r$\n"
    StrLen $3 $SetupInstallReceiptRecord
    IntCmpU $3 ${NSIS_MAX_STRLEN} receipt_write_record_ready receipt_write_done receipt_write_done
receipt_write_record_ready:
    ; Apply the same explicit protected current-SID/SYSTEM/Admin DACL as the
    ; journal and payload files; the private root's own DACL is not inheritable.
    StrCpy $SetupCode 13
    StrCpy $0 $SetupOwnerSid
    StrCpy $8 0
    System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "O:$0D:P(A;;GA;;;$0)(A;;GA;;;SY)(A;;GA;;;BA)", i 1, *p .r8, p 0) i.r1'
    StrCmp $8 0 receipt_write_done
    StrCpy $SetupInstallReceiptSecurityDescriptor $8
    StrCmp $1 0 receipt_write_done
    System::Call '*(i 12, p r8, i 0) p.r9' ; x86 SECURITY_ATTRIBUTES, non-inheritable
    StrCmp $9 0 receipt_write_done
    StrCpy $SetupInstallReceiptSecurityAttributes $9
    StrCpy $SetupCode 13
    System::Call 'kernel32::CreateFileW(w "$SetupFixedRoot\${SETUP_RECEIPT_NAME}", i 0xC0010000, i 0, p $SetupInstallReceiptSecurityAttributes, i 1, i 0x80, p 0) p.r0 ?e'
    Pop $GuardLastError
    StrCmp $0 -1 receipt_write_done
    StrCmp $0 0 receipt_write_done
    StrCpy $SetupInstallReceiptHandle $0
    StrCpy $SetupInstallReceiptCreated 1
    StrCpy $SetupJournalVerifyObjectHandle $SetupInstallReceiptHandle
    StrCpy $SetupJournalVerifyPath "\\?\$SetupFixedRoot\${SETUP_RECEIPT_NAME}"
    StrCpy $SetupJournalVerifyIdentity ""
    StrCpy $SetupJournalVerifyDirectory 0
    StrCpy $SetupJournalVerifyCreated 1
    Call ${PREFIX}ValidateFreshInstallJournalHandle
    StrCmp $SetupCode 0 receipt_write_file_created
    Goto receipt_write_done
receipt_write_file_created:
    StrCpy $SetupInstallReceiptIdentity $SetupJournalVerifyIdentity
    StrCpy $SetupCode 13
    System::Call '*(&i2 0xFEFF) p.r7'
    StrCmp $7 0 receipt_write_done
    System::Call 'kernel32::WriteFile(p $SetupInstallReceiptHandle, p r7, i 2, *i .r5, p 0) i.r6'
    System::Free $7
    StrCmp $6 0 receipt_write_done
    StrCmp $5 2 0 receipt_write_done
    StrLen $4 $SetupInstallReceiptRecord
    IntOp $4 $4 * 2 ; UTF-16LE bytes; no trailing NUL is written.
    System::Call 'kernel32::WriteFile(p $SetupInstallReceiptHandle, w "$SetupInstallReceiptRecord", i r4, *i .r5, p 0) i.r6'
    StrCmp $6 0 receipt_write_done
    StrCmp $5 $4 0 receipt_write_done
    System::Call 'kernel32::FlushFileBuffers(p $SetupInstallReceiptHandle) i.r6'
    StrCmp $6 0 receipt_write_done
    StrCpy $SetupJournalVerifyIdentity $SetupInstallReceiptIdentity
    Call ${PREFIX}ValidateFreshInstallJournalHandle
    StrCmp $SetupCode 0 receipt_write_file_verified
    Goto receipt_write_done
receipt_write_file_verified:
    StrCpy $SetupInstallReceiptIdentity $SetupJournalVerifyIdentity
    StrCpy $GuardHandle $SetupInstallReceiptHandle
    StrCpy $GuardDirectory 0
    Call ${PREFIX}HashHandleSha256
    StrCmp $SetupCode 0 receipt_write_file_hashed
    Goto receipt_write_done
receipt_write_file_hashed:
    StrCpy $SetupInstallReceiptHash $GuardHash
    StrCpy $GuardHandle $SetupInstallReceiptHandle
    StrCpy $GuardIdentity $SetupInstallReceiptIdentity
    StrCpy $GuardDirectory 0
    Call ${PREFIX}ReadOwnedReceipt
    StrCmp $SetupCode 0 receipt_write_receipt_parsed
    Goto receipt_write_done
receipt_write_receipt_parsed:
    ; ReadOwnedReceipt clears SetupCode on parse success; mismatches below must
    ; remain failures instead of accidentally returning the parser's zero.
    StrCpy $SetupCode 13
    StrCmp $SetupRecordedUninstallerHash $SetupInstallUninstallerHash receipt_write_uninstaller_matches
    Goto receipt_write_done
receipt_write_uninstaller_matches:
    StrCmp $SetupRecordedStartMenuHash $SetupInstallShortcutHash receipt_write_shortcut_matches
    Goto receipt_write_done
receipt_write_shortcut_matches:
    StrCmp $SetupRecordedDesktop 0 receipt_write_desktop_disabled
    Goto receipt_write_done
receipt_write_desktop_disabled:
    StrCmp $SetupRecordedDesktopHash "none" receipt_write_success
    Goto receipt_write_done
receipt_write_success:
    StrCpy $SetupCode 0
receipt_write_done:
    StrCmp $SetupInstallReceiptSecurityAttributes "" receipt_write_free_descriptor
    StrCmp $SetupInstallReceiptSecurityAttributes 0 receipt_write_free_descriptor
    System::Free $SetupInstallReceiptSecurityAttributes
    StrCpy $SetupInstallReceiptSecurityAttributes 0
receipt_write_free_descriptor:
    StrCmp $SetupInstallReceiptSecurityDescriptor "" receipt_write_security_cleanup_done
    StrCmp $SetupInstallReceiptSecurityDescriptor 0 receipt_write_security_cleanup_done
    System::Call 'kernel32::LocalFree(p $SetupInstallReceiptSecurityDescriptor) p.r0'
    StrCmp $0 0 receipt_write_descriptor_freed
    StrCpy $SetupCode 13
    Goto receipt_write_security_cleanup_done
receipt_write_descriptor_freed:
    StrCpy $SetupInstallReceiptSecurityDescriptor 0
receipt_write_security_cleanup_done:
    StrCpy $SetupInstallReceiptRecord ""
    Pop $GuardLastError
    Pop $GuardHash
    Pop $GuardIdentity
    Pop $GuardDirectory
    Pop $GuardHandle
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Dispose only the receipt created by this still-active install transaction.
; A partial WriteFile is recoverable through the original CREATE_NEW handle and
; captured file identity; after delete disposition, retries only close that
; same handle and never reopen or delete by path.
Function ${PREFIX}RollbackFreshInstallReceipt
    !insertmacro SetupSaveRegisters
    Push $GuardHandle
    Push $GuardDirectory
    Push $GuardIdentity
    Push $GuardHash
    Push $GuardLastError
    Push $GuardValue
    Push $GuardValueLength
    StrCpy $SetupCode 13
    StrCmp $SetupMode "install" receipt_rollback_mode
    Goto receipt_rollback_done
receipt_rollback_mode:
    StrCmp $SetupCopyTransactionActive 1 receipt_rollback_active
    Goto receipt_rollback_done
receipt_rollback_active:
    StrCmp $SetupInstallReceiptCreated "" receipt_rollback_empty_created
    StrCmp $SetupInstallReceiptCreated 0 receipt_rollback_empty_created
    StrCmp $SetupInstallReceiptCreated 1 receipt_rollback_created
    Goto receipt_rollback_done
receipt_rollback_created:
    StrCmp $SetupInstallReceiptHandle "" receipt_rollback_done
    StrCmp $SetupInstallReceiptHandle 0 receipt_rollback_done
    StrCmp $SetupInstallReceiptHandle -1 receipt_rollback_done
    StrCmp $SetupInstallReceiptDeletePending 1 receipt_rollback_close
    StrCmp $SetupInstallReceiptDeletePending "" receipt_rollback_validate
    StrCmp $SetupInstallReceiptDeletePending 0 receipt_rollback_validate
    Goto receipt_rollback_done
receipt_rollback_validate:
    Call ${PREFIX}ValidateFreshInstallJournalRoot
    StrCmp $SetupCode 0 receipt_rollback_root_valid
    Goto receipt_rollback_done
receipt_rollback_root_valid:
    StrCpy $SetupJournalVerifyObjectHandle $SetupInstallReceiptHandle
    StrCpy $SetupJournalVerifyPath "\\?\$SetupFixedRoot\${SETUP_RECEIPT_NAME}"
    StrCpy $SetupJournalVerifyIdentity $SetupInstallReceiptIdentity
    StrCpy $SetupJournalVerifyDirectory 0
    StrCpy $SetupJournalVerifyCreated 1
    Call ${PREFIX}ValidateFreshInstallJournalHandle
    StrCmp $SetupCode 0 receipt_rollback_handle_valid
    Goto receipt_rollback_done
receipt_rollback_handle_valid:
    StrCpy $SetupInstallReceiptIdentity $SetupJournalVerifyIdentity
    StrCpy $SetupCode 13
    ; If writing reached a complete hash, require the same bytes before disposal.
    StrCmp $SetupInstallReceiptHash "" receipt_rollback_dispose
    StrCpy $GuardValue $SetupInstallReceiptHash
    StrCpy $GuardValueLength 64
    Call ${PREFIX}ValidateHexText
    StrCmp $SetupCode 0 receipt_rollback_hash_format
    Goto receipt_rollback_done
receipt_rollback_hash_format:
    StrCpy $SetupCode 13
    StrCpy $GuardHandle $SetupInstallReceiptHandle
    StrCpy $GuardDirectory 0
    Call ${PREFIX}HashHandleSha256
    StrCmp $SetupCode 0 receipt_rollback_hash_read
    Goto receipt_rollback_done
receipt_rollback_hash_read:
    ; HashHandleSha256 clears SetupCode; a changed receipt must block disposal.
    StrCpy $SetupCode 13
    StrCmp $GuardHash $SetupInstallReceiptHash receipt_rollback_dispose
    Goto receipt_rollback_done
receipt_rollback_dispose:
    StrCpy $SetupCode 13
    System::Alloc 1
    Pop $0
    StrCmp $0 0 receipt_rollback_done
    System::Call '*$0(&i1 1)'
    System::Call 'kernel32::SetFileInformationByHandle(p $SetupInstallReceiptHandle, i 4, p r0, i 1) i.r1'
    System::Free $0
    StrCmp $1 0 receipt_rollback_done
    StrCpy $SetupInstallReceiptDeletePending 1
receipt_rollback_close:
    System::Call 'kernel32::CloseHandle(p $SetupInstallReceiptHandle) i.r0'
    StrCmp $0 0 receipt_rollback_done
    StrCpy $SetupInstallReceiptHandle 0
    StrCpy $SetupInstallReceiptIdentity ""
    StrCpy $SetupInstallReceiptHash ""
    StrCpy $SetupInstallReceiptCreated 0
    StrCpy $SetupInstallReceiptDeletePending 0
    StrCpy $SetupCode 0
    Goto receipt_rollback_done
receipt_rollback_empty_created:
    StrCmp $SetupInstallReceiptHandle "" receipt_rollback_empty_identity
    StrCmp $SetupInstallReceiptHandle 0 receipt_rollback_empty_identity
    Goto receipt_rollback_done
receipt_rollback_empty_identity:
    StrCmp $SetupInstallReceiptIdentity "" receipt_rollback_empty_hash
    Goto receipt_rollback_done
receipt_rollback_empty_hash:
    StrCmp $SetupInstallReceiptHash "" receipt_rollback_empty_pending
    Goto receipt_rollback_done
receipt_rollback_empty_pending:
    StrCmp $SetupInstallReceiptDeletePending "" receipt_rollback_empty_success
    StrCmp $SetupInstallReceiptDeletePending 0 receipt_rollback_empty_success
    Goto receipt_rollback_done
receipt_rollback_empty_success:
    StrCpy $SetupCode 0
receipt_rollback_done:
    StrCmp $SetupInstallReceiptSecurityAttributes "" receipt_rollback_free_descriptor
    StrCmp $SetupInstallReceiptSecurityAttributes 0 receipt_rollback_free_descriptor
    System::Free $SetupInstallReceiptSecurityAttributes
    StrCpy $SetupInstallReceiptSecurityAttributes 0
receipt_rollback_free_descriptor:
    StrCmp $SetupInstallReceiptSecurityDescriptor "" receipt_rollback_restore_stack
    StrCmp $SetupInstallReceiptSecurityDescriptor 0 receipt_rollback_restore_stack
    System::Call 'kernel32::LocalFree(p $SetupInstallReceiptSecurityDescriptor) p.r0'
    StrCmp $0 0 receipt_rollback_descriptor_freed
    StrCpy $SetupCode 13
    Goto receipt_rollback_restore_stack
receipt_rollback_descriptor_freed:
    StrCpy $SetupInstallReceiptSecurityDescriptor 0
receipt_rollback_restore_stack:
    Pop $GuardValueLength
    Pop $GuardValue
    Pop $GuardLastError
    Pop $GuardHash
    Pop $GuardIdentity
    Pop $GuardDirectory
    Pop $GuardHandle
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Roll back only transaction-recorded final files (NOTICE then app) plus the
; current private staging file if a copy stopped before registration. Every
; candidate is revalidated through its still-open original handle.
Function ${PREFIX}RollbackFreshInstallPayloadCopies
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 13
    StrCmp $SetupCopyTransactionActive 1 rollback_txn_active
    Goto rollback_txn_done
rollback_txn_active:
    StrCmp $SetupCopyTargetHandle "" rollback_current_handle_empty
    StrCmp $SetupCopyTargetHandle 0 rollback_current_handle_empty
    StrCmp $SetupCopyTargetDelete 1 rollback_current_owned
    Goto rollback_txn_done
rollback_current_owned:
    StrCmp $SetupCopyRootHandle "" rollback_txn_done
    StrCmp $SetupCopyRootHandle 0 rollback_txn_done
    StrCmp $SetupCopyRootIdentity "" rollback_txn_done
    StrCmp $GuardPathPins "" rollback_txn_done
    StrCmp $GuardPathPins 0 rollback_txn_done
    StrCmp $GuardPathPinCount 0 rollback_txn_done
    StrCpy $SetupTxnCheckHandle $SetupCopyTargetHandle
    StrCpy $SetupTxnCheckName $SetupCopyTargetName
    StrCpy $SetupTxnCheckIdentity $SetupCopyTargetIdentity
    StrCpy $SetupTxnCheckHash $SetupCopyExpectedHash
    StrCpy $SetupTxnCheckDeletePending $SetupCopyTargetDeletePending
    StrCpy $SetupTxnCheckAllowStage 1
    Call ${PREFIX}ValidateAndRevokeCopyHandle
    StrCmp $SetupCode 0 rollback_current_released
    StrCpy $SetupCopyTargetHandle $SetupTxnCheckHandle
    StrCpy $SetupCopyTargetIdentity $SetupTxnCheckIdentity
    StrCpy $SetupCopyTargetDeletePending $SetupTxnCheckDeletePending
    Goto rollback_txn_done
rollback_current_released:
    StrCpy $SetupCopyTargetHandle 0
    StrCpy $SetupCopyTargetIdentity ""
    StrCpy $SetupCopyTargetDelete 0
    StrCpy $SetupCopyTargetDeletePending 0
    StrCpy $SetupCopyStagingName ""
    Goto rollback_current_absent
rollback_current_handle_empty:
    StrCmp $SetupCopyTargetDelete "" rollback_current_delete_empty
    StrCmp $SetupCopyTargetDelete 0 rollback_current_delete_empty
    Goto rollback_txn_done
rollback_current_delete_empty:
    StrCmp $SetupCopyTargetIdentity "" rollback_current_identity_empty
    Goto rollback_txn_done
rollback_current_identity_empty:
    StrCmp $SetupCopyTargetDeletePending "" rollback_current_absent
    StrCmp $SetupCopyTargetDeletePending 0 rollback_current_absent
    Goto rollback_txn_done
rollback_current_absent:
    StrCmp $SetupCopyTxnNoticeCreated "" rollback_notice_absent
    StrCmp $SetupCopyTxnNoticeCreated 0 rollback_notice_absent
    StrCmp $SetupCopyTxnNoticeCreated 1 rollback_notice_owned
    Goto rollback_txn_done
rollback_notice_owned:
    StrCmp $SetupCopyTxnNoticeHandle 0 rollback_txn_done
    StrCmp $SetupCopyTxnNoticeIdentity "" rollback_txn_done
    StrCmp $SetupCopyTxnNoticeHash "" rollback_txn_done
    StrCmp $SetupCopyTxnNoticeHash "${SETUP_NOTICE_SHA256}" rollback_notice_hash_ok
    Goto rollback_txn_done
rollback_notice_hash_ok:
    StrCmp $SetupCopyRootHandle "" rollback_txn_done
    StrCmp $SetupCopyRootHandle 0 rollback_txn_done
    StrCmp $SetupCopyRootIdentity "" rollback_txn_done
    StrCmp $GuardPathPins "" rollback_txn_done
    StrCmp $GuardPathPins 0 rollback_txn_done
    StrCmp $GuardPathPinCount 0 rollback_txn_done
    StrCpy $SetupTxnCheckHandle $SetupCopyTxnNoticeHandle
    StrCpy $SetupTxnCheckName "${SETUP_NOTICE_NAME}"
    StrCpy $SetupTxnCheckIdentity $SetupCopyTxnNoticeIdentity
    StrCpy $SetupTxnCheckHash $SetupCopyTxnNoticeHash
    StrCpy $SetupTxnCheckDeletePending $SetupCopyTxnNoticeDeletePending
    StrCpy $SetupTxnCheckAllowStage 0
    Call ${PREFIX}ValidateAndRevokeCopyHandle
    StrCmp $SetupCode 0 rollback_notice_released
    StrCpy $SetupCopyTxnNoticeHandle $SetupTxnCheckHandle
    StrCpy $SetupCopyTxnNoticeDeletePending $SetupTxnCheckDeletePending
    Goto rollback_txn_done
rollback_notice_released:
    StrCpy $SetupCopyTxnNoticeHandle 0
    StrCpy $SetupCopyTxnNoticeIdentity ""
    StrCpy $SetupCopyTxnNoticeHash ""
    StrCpy $SetupCopyTxnNoticeCreated 0
    StrCpy $SetupCopyTxnNoticeDeletePending 0
rollback_notice_absent:
    StrCmp $SetupCopyTxnNoticeHandle "" rollback_notice_handle_empty
    StrCmp $SetupCopyTxnNoticeHandle 0 rollback_notice_handle_empty
    Goto rollback_txn_done
rollback_notice_handle_empty:
    StrCmp $SetupCopyTxnNoticeIdentity "" rollback_notice_identity_empty
    Goto rollback_txn_done
rollback_notice_identity_empty:
    StrCmp $SetupCopyTxnNoticeHash "" rollback_notice_hash_empty
    Goto rollback_txn_done
rollback_notice_hash_empty:
    StrCmp $SetupCopyTxnNoticeDeletePending "" rollback_notice_absent_done
    StrCmp $SetupCopyTxnNoticeDeletePending 0 rollback_notice_absent_done
    Goto rollback_txn_done
rollback_notice_absent_done:
    StrCmp $SetupCopyTxnAppCreated "" rollback_app_absent
    StrCmp $SetupCopyTxnAppCreated 0 rollback_app_absent
    StrCmp $SetupCopyTxnAppCreated 1 rollback_app_owned
    Goto rollback_txn_done
rollback_app_owned:
    StrCmp $SetupCopyTxnAppHandle 0 rollback_txn_done
    StrCmp $SetupCopyTxnAppIdentity "" rollback_txn_done
    StrCmp $SetupCopyTxnAppHash "" rollback_txn_done
    StrCmp $SetupCopyTxnAppHash "${SETUP_APP_SHA256}" rollback_app_hash_ok
    Goto rollback_txn_done
rollback_app_hash_ok:
    StrCmp $SetupCopyRootHandle "" rollback_txn_done
    StrCmp $SetupCopyRootHandle 0 rollback_txn_done
    StrCmp $SetupCopyRootIdentity "" rollback_txn_done
    StrCmp $GuardPathPins "" rollback_txn_done
    StrCmp $GuardPathPins 0 rollback_txn_done
    StrCmp $GuardPathPinCount 0 rollback_txn_done
    StrCpy $SetupTxnCheckHandle $SetupCopyTxnAppHandle
    StrCpy $SetupTxnCheckName "${SETUP_APP_NAME}"
    StrCpy $SetupTxnCheckIdentity $SetupCopyTxnAppIdentity
    StrCpy $SetupTxnCheckHash $SetupCopyTxnAppHash
    StrCpy $SetupTxnCheckDeletePending $SetupCopyTxnAppDeletePending
    StrCpy $SetupTxnCheckAllowStage 0
    Call ${PREFIX}ValidateAndRevokeCopyHandle
    StrCmp $SetupCode 0 rollback_app_released
    StrCpy $SetupCopyTxnAppHandle $SetupTxnCheckHandle
    StrCpy $SetupCopyTxnAppDeletePending $SetupTxnCheckDeletePending
    Goto rollback_txn_done
rollback_app_released:
    StrCpy $SetupCopyTxnAppHandle 0
    StrCpy $SetupCopyTxnAppIdentity ""
    StrCpy $SetupCopyTxnAppHash ""
    StrCpy $SetupCopyTxnAppCreated 0
    StrCpy $SetupCopyTxnAppDeletePending 0
rollback_app_absent:
    StrCmp $SetupCopyTxnAppHandle "" rollback_app_handle_empty
    StrCmp $SetupCopyTxnAppHandle 0 rollback_app_handle_empty
    Goto rollback_txn_done
rollback_app_handle_empty:
    StrCmp $SetupCopyTxnAppIdentity "" rollback_app_identity_empty
    Goto rollback_txn_done
rollback_app_identity_empty:
    StrCmp $SetupCopyTxnAppHash "" rollback_app_hash_empty
    Goto rollback_txn_done
rollback_app_hash_empty:
    StrCmp $SetupCopyTxnAppDeletePending "" rollback_app_absent_done
    StrCmp $SetupCopyTxnAppDeletePending 0 rollback_app_absent_done
    Goto rollback_txn_done
rollback_app_absent_done:
    Call ${PREFIX}RollbackFreshInstallJournal
    StrCmp $SetupCode 0 rollback_journal_cleaned
    Goto rollback_txn_done
rollback_journal_cleaned:
    StrCpy $SetupCode 0
rollback_txn_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; The only destructive file cleanup path. Final files must match their manifest
; hash; a still-staged partial copy is deleted only when its actual hash is
; stable across two reads on the CREATE_NEW/share-none original handle.
Function ${PREFIX}ValidateAndRevokeCopyHandle
    !insertmacro SetupSaveRegisters
    Push $GuardHandle
    Push $GuardPath
    Push $GuardDirectory
    Push $GuardIdentity
    Push $GuardHash
    Push $GuardLastError
    StrCpy $SetupCode 13
    StrCpy $SetupTxnCheckIsStage 0
    StrCmp $SetupCopyTransactionActive 1 txn_revoke_active
    Goto txn_revoke_done
txn_revoke_active:
    StrCmp $SetupTxnCheckHandle 0 txn_revoke_done
    StrCmp $SetupTxnCheckName "${SETUP_APP_NAME}" txn_revoke_name_app
    StrCmp $SetupTxnCheckName "${SETUP_NOTICE_NAME}" txn_revoke_name_notice
    Goto txn_revoke_done
txn_revoke_name_app:
    StrCmp $SetupTxnCheckHash "${SETUP_APP_SHA256}" txn_revoke_app_hash_ok
    Goto txn_revoke_done
txn_revoke_app_hash_ok:
    StrCmp $SetupTxnCheckAllowStage 0 txn_revoke_manifest_ok
    StrCmp $SetupTxnCheckAllowStage 1 txn_revoke_manifest_ok
    Goto txn_revoke_done
txn_revoke_name_notice:
    StrCmp $SetupTxnCheckHash "${SETUP_NOTICE_SHA256}" txn_revoke_notice_hash_ok
    Goto txn_revoke_done
txn_revoke_notice_hash_ok:
    StrCmp $SetupTxnCheckAllowStage 0 txn_revoke_manifest_ok
    StrCmp $SetupTxnCheckAllowStage 1 txn_revoke_manifest_ok
    Goto txn_revoke_done
txn_revoke_manifest_ok:
    StrCmp $SetupTxnCheckInfoBuffer "" txn_revoke_buffer_empty
    StrCmp $SetupTxnCheckInfoBuffer 0 txn_revoke_buffer_empty
    Goto txn_revoke_done
txn_revoke_buffer_empty:
    StrCpy $GuardHandle $SetupTxnCheckHandle
    StrCpy $GuardDirectory 0
    System::Alloc 52 ; BY_HANDLE_FILE_INFORMATION
    Pop $SetupTxnCheckInfoBuffer
    StrCmp $SetupTxnCheckInfoBuffer 0 txn_revoke_done
    System::Call 'kernel32::GetFileInformationByHandle(p $SetupTxnCheckHandle, p $SetupTxnCheckInfoBuffer) i.r1'
    StrCmp $1 0 txn_revoke_free_refuse
    System::Call '*$SetupTxnCheckInfoBuffer(i.r1, i, i, i, i, i, i, i, i, i, i.r5, i, i)'
    IntOp $2 $1 & 0x400 ; refuse reparse points
    StrCmp $2 0 0 txn_revoke_free_refuse
    IntOp $2 $1 & 0x10 ; refuse directories
    StrCmp $2 0 0 txn_revoke_free_refuse
    StrCmp $5 1 0 txn_revoke_free_refuse ; exactly one hard link
    System::Call 'kernel32::GetFinalPathNameByHandleW(p $SetupTxnCheckHandle, w .r1, i ${NSIS_MAX_STRLEN}, i 0) i.r2'
    StrCmp $2 0 txn_revoke_free_refuse
    IntCmpU $2 ${NSIS_MAX_STRLEN} txn_revoke_free_refuse txn_revoke_path_in_bounds txn_revoke_free_refuse
txn_revoke_path_in_bounds:
    StrCmp $1 "\\?\$SetupFixedRoot\$SetupTxnCheckName" txn_revoke_final_path
    StrCmp $SetupTxnCheckAllowStage 1 txn_revoke_maybe_stage
    Goto txn_revoke_free_refuse
txn_revoke_maybe_stage:
    StrCmp $1 "\\?\$SetupFixedRoot\.GitHubBackupTool.setup-stage" txn_revoke_stage_path
    Goto txn_revoke_free_refuse
txn_revoke_stage_path:
    StrCpy $SetupTxnCheckIsStage 1
    Goto txn_revoke_identity
txn_revoke_final_path:
    StrCpy $SetupTxnCheckIsStage 0
txn_revoke_identity:
    System::Call 'kernel32::GetFileInformationByHandleEx(p $SetupTxnCheckHandle, i 18, p $SetupTxnCheckInfoBuffer, i 24) i.r1'
    StrCmp $1 0 txn_revoke_free_refuse
    System::Call '*$SetupTxnCheckInfoBuffer(i.r3, i.r4, i.r5, i.r6, i.r7, i.r8)'
    StrCpy $GuardIdentity "$3:$4:$5:$6:$7:$8"
    StrCmp $SetupTxnCheckIdentity "" txn_revoke_capture_created_stage_identity
    StrCmp $GuardIdentity $SetupTxnCheckIdentity 0 txn_revoke_free_refuse
    Goto txn_revoke_identity_verified
txn_revoke_capture_created_stage_identity:
    ; A missing identity is recoverable only for the still-owned CREATE_NEW
    ; handle. Ledger entries never recapture or replace their recorded identity.
    StrCmp $SetupTxnCheckAllowStage 1 0 txn_revoke_free_refuse
    StrCmp $SetupCopyTargetDelete 1 0 txn_revoke_free_refuse
    StrCmp $SetupTxnCheckHandle $SetupCopyTargetHandle 0 txn_revoke_free_refuse
    StrCmp $SetupTxnCheckName $SetupCopyTargetName 0 txn_revoke_free_refuse
    StrCpy $SetupTxnCheckIdentity $GuardIdentity
txn_revoke_identity_verified:
    StrCmp $GuardIdentity $SetupTxnCheckIdentity 0 txn_revoke_free_refuse
    Call ${PREFIX}ValidatePrivateHandleAcl
    StrCmp $SetupCode 0 0 txn_revoke_free_refuse
    StrCpy $GuardHandle $SetupTxnCheckHandle
    Call ${PREFIX}HashHandleSha256
    StrCmp $SetupCode 0 0 txn_revoke_free_refuse
    StrCmp $SetupTxnCheckIsStage 1 txn_revoke_stage_hash
    StrCmp $GuardHash $SetupTxnCheckHash 0 txn_revoke_free_refuse
    Goto txn_revoke_dispose
txn_revoke_stage_hash:
    StrCpy $SetupTxnCheckActualHash $GuardHash
    StrCpy $GuardHandle $SetupTxnCheckHandle
    Call ${PREFIX}HashHandleSha256
    StrCmp $SetupCode 0 0 txn_revoke_free_refuse
    StrCmp $GuardHash $SetupTxnCheckActualHash 0 txn_revoke_free_refuse
txn_revoke_dispose:
    StrCmp $SetupTxnCheckDeletePending 1 txn_revoke_close
    System::Alloc 1
    Pop $1
    StrCmp $1 0 txn_revoke_free_refuse
    System::Call '*$1(&i1 1)' ; FILE_DISPOSITION_INFO.DeleteFile = TRUE
    System::Call 'kernel32::SetFileInformationByHandle(p $SetupTxnCheckHandle, i 4, p r1, i 1) i.r0'
    System::Free $1
    StrCmp $0 0 txn_revoke_free_refuse
    StrCpy $SetupTxnCheckDeletePending 1
txn_revoke_close:
    System::Call 'kernel32::CloseHandle(p $SetupTxnCheckHandle) i.r0'
    StrCmp $0 0 txn_revoke_close_failed
    StrCpy $SetupTxnCheckHandle 0
    StrCpy $SetupTxnCheckDeletePending 0
    StrCpy $SetupCode 0
    Goto txn_revoke_free_done
txn_revoke_close_failed:
    StrCpy $SetupCode 13
    Goto txn_revoke_free_done
txn_revoke_free_refuse:
    StrCpy $SetupCode 13
    Goto txn_revoke_free_done
txn_revoke_done:
    StrCpy $SetupCode 13
txn_revoke_free_done:
    StrCmp $SetupTxnCheckInfoBuffer "" txn_revoke_clear_guard
    StrCmp $SetupTxnCheckInfoBuffer 0 txn_revoke_clear_guard
    System::Free $SetupTxnCheckInfoBuffer
    StrCpy $SetupTxnCheckInfoBuffer 0
txn_revoke_clear_guard:
    StrCpy $GuardHandle 0
    StrCpy $GuardPath ""
    StrCpy $GuardDirectory 0
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
    StrCpy $SetupTxnCheckActualHash ""
    Pop $GuardLastError
    Pop $GuardHash
    Pop $GuardIdentity
    Pop $GuardDirectory
    Pop $GuardPath
    Pop $GuardHandle
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Rollback and release as one fail-closed operation. If either file deletion or
; any CloseHandle fails, its exact handle, identity and pending state remain
; attached to the active transaction; callers must not continue/commit.
Function ${PREFIX}AbortFreshInstallCopyTransaction
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 13
    StrCmp $SetupCopyTransactionActive 1 abort_txn_active
    Goto abort_txn_done
abort_txn_active:
    Call ${PREFIX}RollbackFreshInstallReceipt
    StrCmp $SetupCode 0 abort_txn_receipt_rollback_ok
    StrCpy $SetupCode 13
    Goto abort_txn_done
abort_txn_receipt_rollback_ok:
    Call ${PREFIX}RollbackFreshInstallPayloadCopies
    StrCmp $SetupCode 0 abort_txn_rollback_ok
    StrCpy $SetupCode 13
    Goto abort_txn_done
abort_txn_rollback_ok:
    Call ${PREFIX}ReleaseStagedCopyHandles
    StrCmp $SetupCode 0 abort_txn_release_ok
    StrCpy $SetupCode 13
    Goto abort_txn_done
abort_txn_release_ok:
    StrCpy $SetupCopyTransactionActive 0
    StrCpy $SetupCode 0
abort_txn_done:
    !insertmacro SetupRestoreRegisters
FunctionEnd

; Release only copy scratch, source and root/pin handles after the explicit
; transaction rollback has emptied its target ledger. Product-file disposition
; is exclusively handled by ValidateAndRevokeCopyHandle; no path is reopened.
Function ${PREFIX}ReleaseStagedCopyHandles
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 0
    StrCpy $9 0 ; retain scratch-memory cleanup failures across pin release
    ; This helper only closes non-mutating source/root resources. It must never
    ; discard an unregistered target or a file that still has rollback rights.
    StrCmp $SetupCopyTargetHandle "" staged_release_target_empty
    StrCmp $SetupCopyTargetHandle 0 staged_release_target_empty
    Goto staged_release_refuse_target
staged_release_target_empty:
    StrCmp $SetupCopyTargetDelete "" staged_release_delete_flag_empty
    StrCmp $SetupCopyTargetDelete 0 staged_release_delete_flag_empty
    Goto staged_release_refuse_target
staged_release_delete_flag_empty:
    StrCmp $SetupCopyTargetIdentity "" staged_release_target_identity_empty
    Goto staged_release_refuse_target
staged_release_target_identity_empty:
    StrCmp $SetupCopyTargetDeletePending "" staged_release_target_pending_empty
    StrCmp $SetupCopyTargetDeletePending 0 staged_release_target_pending_empty
    Goto staged_release_refuse_target
staged_release_target_pending_empty:
    StrCmp $SetupCopyTxnAppCreated "" staged_release_app_created_empty
    StrCmp $SetupCopyTxnAppCreated 0 staged_release_app_created_empty
    Goto staged_release_refuse_target
staged_release_app_created_empty:
    StrCmp $SetupCopyTxnNoticeCreated "" staged_release_notice_created_empty
    StrCmp $SetupCopyTxnNoticeCreated 0 staged_release_notice_created_empty
    Goto staged_release_refuse_target
staged_release_notice_created_empty:
    StrCmp $SetupCopyTxnAppHandle "" staged_release_app_handle_empty
    StrCmp $SetupCopyTxnAppHandle 0 staged_release_app_handle_empty
    Goto staged_release_refuse_target
staged_release_app_handle_empty:
    StrCmp $SetupCopyTxnNoticeHandle "" staged_release_notice_handle_empty
    StrCmp $SetupCopyTxnNoticeHandle 0 staged_release_notice_handle_empty
    Goto staged_release_refuse_target
staged_release_notice_handle_empty:
    StrCmp $SetupCopyTxnAppIdentity "" staged_release_app_identity_empty
    Goto staged_release_refuse_target
staged_release_app_identity_empty:
    StrCmp $SetupCopyTxnNoticeIdentity "" staged_release_notice_identity_empty
    Goto staged_release_refuse_target
staged_release_notice_identity_empty:
    StrCmp $SetupCopyTxnAppHash "" staged_release_app_hash_empty
    Goto staged_release_refuse_target
staged_release_app_hash_empty:
    StrCmp $SetupCopyTxnNoticeHash "" staged_release_notice_hash_empty
    Goto staged_release_refuse_target
staged_release_notice_hash_empty:
    StrCmp $SetupCopyTxnAppDeletePending "" staged_release_app_pending_empty
    StrCmp $SetupCopyTxnAppDeletePending 0 staged_release_app_pending_empty
    Goto staged_release_refuse_target
staged_release_app_pending_empty:
    StrCmp $SetupCopyTxnNoticeDeletePending "" staged_release_notice_pending_empty
    StrCmp $SetupCopyTxnNoticeDeletePending 0 staged_release_notice_pending_empty
    Goto staged_release_refuse_target
staged_release_notice_pending_empty:
    StrCmp $SetupJournalFileHandle "" staged_release_journal_file_handle_empty
    StrCmp $SetupJournalFileHandle 0 staged_release_journal_file_handle_empty
    Goto staged_release_refuse_target
staged_release_journal_file_handle_empty:
    StrCmp $SetupJournalFileCreated "" staged_release_journal_file_created_empty
    StrCmp $SetupJournalFileCreated 0 staged_release_journal_file_created_empty
    Goto staged_release_refuse_target
staged_release_journal_file_created_empty:
    StrCmp $SetupJournalFileIdentity "" staged_release_journal_file_identity_empty
    Goto staged_release_refuse_target
staged_release_journal_file_identity_empty:
    StrCmp $SetupJournalFileHash "" staged_release_journal_file_hash_empty
    Goto staged_release_refuse_target
staged_release_journal_file_hash_empty:
    StrCmp $SetupJournalFileDeletePending "" staged_release_journal_directory_handle_empty
    StrCmp $SetupJournalFileDeletePending 0 staged_release_journal_directory_handle_empty
    Goto staged_release_refuse_target
staged_release_journal_directory_handle_empty:
    StrCmp $SetupJournalDirectoryHandle "" staged_release_journal_directory_created_empty
    StrCmp $SetupJournalDirectoryHandle 0 staged_release_journal_directory_created_empty
    Goto staged_release_refuse_target
staged_release_journal_directory_created_empty:
    StrCmp $SetupJournalDirectoryCreated "" staged_release_journal_directory_identity_empty
    StrCmp $SetupJournalDirectoryCreated 0 staged_release_journal_directory_identity_empty
    Goto staged_release_refuse_target
staged_release_journal_directory_identity_empty:
    StrCmp $SetupJournalDirectoryIdentity "" staged_release_journal_directory_pending_empty
    Goto staged_release_refuse_target
staged_release_journal_directory_pending_empty:
    StrCmp $SetupJournalDirectoryDeletePending "" staged_release_journal_phase_empty
    StrCmp $SetupJournalDirectoryDeletePending 0 staged_release_journal_phase_empty
    Goto staged_release_refuse_target
staged_release_journal_phase_empty:
    StrCmp $SetupJournalPhase "" staged_release_preflight_ok
    Goto staged_release_refuse_target
staged_release_refuse_target:
    StrCpy $SetupCode 13
    Goto staged_release_done_end
staged_release_preflight_ok:
    StrCmp $SetupCopyBuffer "" staged_release_buffer_done
    StrCmp $SetupCopyBuffer 0 staged_release_buffer_done
    System::Free $SetupCopyBuffer
    StrCpy $SetupCopyBuffer 0
staged_release_buffer_done:
    StrCmp $SetupCopyInfoBuffer "" staged_release_info_done
    StrCmp $SetupCopyInfoBuffer 0 staged_release_info_done
    System::Free $SetupCopyInfoBuffer
    StrCpy $SetupCopyInfoBuffer 0
staged_release_info_done:
    StrCmp $SetupCopyRenameBuffer "" staged_release_rename_done
    StrCmp $SetupCopyRenameBuffer 0 staged_release_rename_done
    System::Free $SetupCopyRenameBuffer
    StrCpy $SetupCopyRenameBuffer 0
    StrCpy $SetupCopyRenameBufferBytes 0
staged_release_rename_done:
    StrCmp $SetupCopySecurityAttributes "" staged_release_attributes_done
    StrCmp $SetupCopySecurityAttributes 0 staged_release_attributes_done
    System::Free $SetupCopySecurityAttributes
    StrCpy $SetupCopySecurityAttributes 0
staged_release_attributes_done:
    StrCmp $SetupCopySecurityDescriptor "" staged_release_descriptor_done
    StrCmp $SetupCopySecurityDescriptor 0 staged_release_descriptor_done
    System::Call 'kernel32::LocalFree(p $SetupCopySecurityDescriptor) p.r0'
    StrCmp $0 0 staged_release_descriptor_freed
    StrCpy $SetupCode 13
    StrCpy $9 1
    Goto staged_release_descriptor_done
staged_release_descriptor_freed:
    StrCpy $SetupCopySecurityDescriptor 0
staged_release_descriptor_done:
    Call ${PREFIX}ReleaseFreshRootScratch
    StrCmp $SetupCode 0 staged_release_fresh_root_scratch_done
    StrCpy $9 1
    StrCpy $SetupCode 0
staged_release_fresh_root_scratch_done:
    Call ${PREFIX}ReleaseFreshInstallJournalScratch
    StrCmp $SetupCode 0 staged_release_journal_scratch_done
    StrCpy $9 1
    StrCpy $SetupCode 0
staged_release_journal_scratch_done:
    Goto staged_release_source
staged_release_source:
    StrCmp $SetupCopySourceHandle "" staged_release_root
    StrCmp $SetupCopySourceHandle 0 staged_release_root
    System::Call 'kernel32::CloseHandle(p $SetupCopySourceHandle) i.r0'
    StrCmp $0 0 staged_release_source_failed
    StrCmp $GuardHandle $SetupCopySourceHandle 0 staged_release_source_not_guard
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
    StrCpy $GuardPath ""
    StrCpy $GuardDirectory 0
staged_release_source_not_guard:
    StrCpy $SetupCopySourceHandle 0
    Goto staged_release_root
staged_release_source_failed:
    StrCpy $SetupCode 13
    Goto staged_release_done
staged_release_root:
    StrCmp $SetupCopyRootLeasePending 1 staged_release_capture_guard_root
    StrCmp $SetupCopyRootHandle "" staged_release_pins
    StrCmp $SetupCopyRootHandle 0 staged_release_pins
    System::Call 'kernel32::CloseHandle(p $SetupCopyRootHandle) i.r0'
    StrCmp $0 0 staged_release_root_failed
    StrCmp $GuardHandle $SetupCopyRootHandle 0 staged_release_root_not_guard
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""
    StrCpy $GuardHash ""
    StrCpy $GuardPath ""
    StrCpy $GuardDirectory 0
staged_release_root_not_guard:
    StrCpy $SetupCopyRootHandle 0
    StrCpy $SetupCopyRootIdentity ""
    Goto staged_release_pins
staged_release_root_failed:
    StrCpy $SetupCode 13
    Goto staged_release_done
staged_release_capture_guard_root:
    ; A failed empty-root acquisition may have retained its lease in GuardHandle.
    ; Transfer only the handle produced while this transaction's lease was pending.
    StrCmp $SetupCopyRootHandle "" staged_release_root_slot_empty
    StrCmp $SetupCopyRootHandle 0 staged_release_root_slot_empty
    Goto staged_release_root_failed
staged_release_root_slot_empty:
    StrCmp $GuardHandle "" staged_release_pending_root_absent
    StrCmp $GuardHandle 0 staged_release_pending_root_absent
    StrCmp $GuardHandle -1 staged_release_root_failed
    StrCpy $SetupCopyRootHandle $GuardHandle
    StrCpy $SetupCopyRootIdentity $GuardIdentity
    StrCpy $GuardHandle 0
    StrCpy $GuardIdentity ""
    StrCpy $SetupCopyRootLeasePending 0
    Goto staged_release_root
staged_release_pending_root_absent:
    ; No root handle survived acquisition/cleanup; pins and scratch may still
    ; need release, but a missing root itself is no longer a held resource.
    StrCpy $SetupCopyRootLeasePending 0
    Goto staged_release_pins
staged_release_pins:
    StrCmp $GuardPathPins "" staged_release_pins_pointer_empty
    StrCmp $GuardPathPins 0 staged_release_pins_pointer_empty
    Call ${PREFIX}ReleasePathPins
    StrCmp $SetupCode 0 staged_release_done
    StrCpy $SetupCode 13
    Goto staged_release_done
staged_release_pins_pointer_empty:
    StrCmp $GuardPathPinCount 0 staged_release_done
    StrCpy $SetupCode 13
staged_release_done:
    StrCmp $SetupCopyTargetHandle "" staged_release_stage_name_check_source
    StrCmp $SetupCopyTargetHandle 0 staged_release_stage_name_check_source
    Goto staged_release_state_incomplete
staged_release_stage_name_check_source:
    StrCmp $SetupCopySourceHandle "" staged_release_stage_name_check_root
    StrCmp $SetupCopySourceHandle 0 staged_release_stage_name_check_root
    Goto staged_release_state_incomplete
staged_release_stage_name_check_root:
    StrCmp $SetupCopyRootHandle "" staged_release_stage_name_check_pins
    StrCmp $SetupCopyRootHandle 0 staged_release_stage_name_check_pins
    Goto staged_release_state_incomplete
staged_release_stage_name_check_pins:
    StrCmp $SetupCopyRootIdentity "" staged_release_stage_name_check_root_pending
    Goto staged_release_state_incomplete
staged_release_stage_name_check_root_pending:
    StrCmp $SetupCopyRootLeasePending 0 staged_release_stage_name_check_pins_empty
    Goto staged_release_state_incomplete
staged_release_stage_name_check_pins_empty:
    StrCmp $GuardPathPins "" staged_release_stage_name_check_pin_count
    StrCmp $GuardPathPins 0 staged_release_stage_name_check_pin_count
    Goto staged_release_state_incomplete
staged_release_stage_name_check_pin_count:
    StrCmp $GuardPathPinCount 0 staged_release_clear_stage_name
    Goto staged_release_state_incomplete
staged_release_clear_stage_name:
    StrCpy $SetupCopyStagingName ""
    Goto staged_release_done_end
staged_release_state_incomplete:
    StrCpy $SetupCode 13
staged_release_done_end:
    StrCmp $9 0 +2
        StrCpy $SetupCode 13
    !insertmacro SetupRestoreRegisters
FunctionEnd
!macroend
!endif
