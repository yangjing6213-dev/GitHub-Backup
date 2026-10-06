; Two-process, read-only journal classifier harness. Writer and reader are
; compiled separately and run against one unique artifacts/setup fixture.
Unicode true
RequestExecutionLevel user
SetCompressor zlib

!ifndef SETUP_JOURNAL_FIXTURE_PARENT
    !error "SETUP_JOURNAL_FIXTURE_PARENT_REQUIRED"
!endif
!ifndef SETUP_JOURNAL_HARNESS_OUTPUT
    !error "SETUP_JOURNAL_HARNESS_OUTPUT_REQUIRED"
!endif
!ifndef SETUP_GUARD_SOURCE
    !error "SETUP_GUARD_SOURCE_REQUIRED"
!endif

!define SETUP_INSTALL_SUFFIX "Programs\GitHubBackupToolSynthetic"
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

!include "${SETUP_GUARD_SOURCE}"
Name "Synthetic journal read test"
OutFile "${SETUP_JOURNAL_HARNESS_OUTPUT}"
SilentInstall silent
!insertmacro SetupNativeFoundation ""

Var HarnessParentHandle
Var HarnessOwnerPointer
Var HarnessSecurityDescriptor
Var HarnessOwnerText
Var HarnessSecurityAttributes
Var HarnessJournalHandle
Var HarnessDirectoryHandle
Var HarnessCreatePath
Var HarnessRecord
Var HarnessLineBytes
Var HarnessStage

Function HarnessResolveOwnerSid
    StrCpy $SetupCode 13
    StrCpy $HarnessStage 81
    StrCpy $HarnessParentHandle 0
    StrCpy $HarnessOwnerPointer 0
    StrCpy $HarnessSecurityDescriptor 0
    StrCpy $HarnessOwnerText 0
    System::Call 'kernel32::CreateFileW(w "${SETUP_JOURNAL_FIXTURE_PARENT}", i 0x20081, i 3, p 0, i 3, i 0x02200000, p 0) p.r0 ?e'
    Pop $7
    StrCmp $0 -1 harness_owner_done
    StrCpy $HarnessParentHandle $0
    StrCpy $HarnessStage 82
    System::Call 'advapi32::GetSecurityInfo(p $HarnessParentHandle, i 1, i 1, *p .r1, p 0, p 0, p 0, *p .r2) i.r3'
    StrCmp $3 0 0 harness_owner_done
    StrCmp $1 0 harness_owner_done
    StrCmp $2 0 harness_owner_done
    StrCpy $HarnessOwnerPointer $1
    StrCpy $HarnessSecurityDescriptor $2
    StrCpy $HarnessStage 83
    System::Call 'advapi32::ConvertSidToStringSidW(p $HarnessOwnerPointer, *p .r4) i.r3'
    StrCmp $3 0 harness_owner_done
    StrCmp $4 0 harness_owner_done
    StrCpy $HarnessOwnerText $4
    StrCpy $HarnessStage 84
    System::Call 'kernel32::lstrlenW(p $HarnessOwnerText) i.r3'
    IntCmpU $3 ${NSIS_MAX_STRLEN} harness_owner_done 0 harness_owner_done
    StrCmp $3 0 harness_owner_done
    StrCpy $HarnessStage 85
    System::Call 'kernel32::lstrcpynW(w .r5, p $HarnessOwnerText, i ${NSIS_MAX_STRLEN}) p'
    StrCpy $SetupOwnerSid $5
    StrCpy $SetupCode 0
harness_owner_done:
    StrCmp $HarnessOwnerText 0 harness_owner_descriptor
    System::Call 'kernel32::LocalFree(p $HarnessOwnerText) p.r0'
    StrCmp $0 0 harness_owner_text_freed
    StrCpy $HarnessStage 86
    Goto harness_owner_free_failed
harness_owner_text_freed:
    StrCpy $HarnessOwnerText 0
harness_owner_descriptor:
    StrCmp $HarnessSecurityDescriptor 0 harness_owner_handle
    System::Call 'kernel32::LocalFree(p $HarnessSecurityDescriptor) p.r0'
    StrCmp $0 0 harness_owner_descriptor_freed
    StrCpy $HarnessStage 87
    Goto harness_owner_free_failed
harness_owner_descriptor_freed:
    StrCpy $HarnessSecurityDescriptor 0
harness_owner_handle:
    StrCmp $HarnessParentHandle 0 harness_owner_result
    System::Call 'kernel32::CloseHandle(p $HarnessParentHandle) i.r0'
    StrCmp $0 0 harness_owner_parent_close_failed harness_owner_parent_closed
harness_owner_parent_close_failed:
    StrCpy $HarnessStage 88
    Goto harness_owner_free_failed
harness_owner_parent_closed:
    StrCpy $HarnessParentHandle 0
harness_owner_result:
    StrCmp $SetupCode 0 harness_owner_return
    StrCpy $SetupOwnerSid ""
    Goto harness_owner_return
harness_owner_free_failed:
    StrCpy $SetupCode 13
harness_owner_return:
FunctionEnd

Function HarnessCreatePrivateDirectory
    StrCpy $SetupCode 13
    StrCpy $HarnessSecurityDescriptor 0
    StrCpy $HarnessSecurityAttributes 0
    System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "O:$SetupOwnerSidD:P(A;;GA;;;$SetupOwnerSid)(A;;GA;;;SY)(A;;GA;;;BA)", i 1, *p .r8, p 0) i.r7'
    StrCmp $7 0 harness_directory_done
    StrCmp $8 0 harness_directory_done
    StrCpy $HarnessSecurityDescriptor $8
    System::Call '*(i 12, p r8, i 0) p.r9'
    StrCmp $9 0 harness_directory_done
    StrCpy $HarnessSecurityAttributes $9
    System::Call 'kernel32::CreateDirectoryW(w "$HarnessCreatePath", p $HarnessSecurityAttributes) i.r0 ?e'
    Pop $7
    StrCmp $0 0 harness_directory_done
    System::Call 'kernel32::CreateFileW(w "$HarnessCreatePath", i 0x20081, i 3, p 0, i 3, i 0x02200000, p 0) p.r0 ?e'
    Pop $7
    StrCmp $0 -1 harness_directory_done
    StrCpy $HarnessDirectoryHandle $0
    System::Call 'kernel32::CloseHandle(p $HarnessDirectoryHandle) i.r0'
    StrCmp $0 0 harness_directory_close_failed
    StrCpy $HarnessDirectoryHandle 0
    StrCpy $SetupCode 0
    Goto harness_directory_done
harness_directory_close_failed:
    StrCpy $SetupCode 13
harness_directory_done:
    StrCmp $HarnessDirectoryHandle 0 harness_directory_security
    System::Call 'kernel32::CloseHandle(p $HarnessDirectoryHandle) i.r0'
    StrCmp $0 0 0 harness_directory_security
    StrCpy $HarnessDirectoryHandle 0
harness_directory_security:
    StrCmp $HarnessSecurityAttributes 0 harness_directory_descriptor
    System::Free $HarnessSecurityAttributes
    StrCpy $HarnessSecurityAttributes 0
harness_directory_descriptor:
    StrCmp $HarnessSecurityDescriptor 0 harness_directory_return
    System::Call 'kernel32::LocalFree(p $HarnessSecurityDescriptor) p.r0'
    StrCmp $0 0 0 harness_directory_free_failed
    StrCpy $HarnessSecurityDescriptor 0
    Goto harness_directory_return
harness_directory_free_failed:
    StrCpy $SetupCode 13
harness_directory_return:
FunctionEnd

!ifdef SETUP_JOURNAL_WRITE_MODE
Section "Write synthetic journal"
    SetErrorLevel 80
    StrCpy $HarnessStage 81
    Call HarnessResolveOwnerSid
    StrCmp $SetupCode 0 0 harness_writer_done
    StrCpy $HarnessStage 82
    StrCpy $SetupLocalAppData "${SETUP_JOURNAL_FIXTURE_PARENT}\LocalData"
    StrCpy $SetupFixedRoot "$SetupLocalAppData\${SETUP_INSTALL_SUFFIX}"
    StrCpy $HarnessCreatePath $SetupLocalAppData
    Call HarnessCreatePrivateDirectory
    StrCmp $SetupCode 0 0 harness_writer_done
    StrCpy $HarnessStage 83
    StrCpy $HarnessCreatePath "$SetupLocalAppData\Programs"
    Call HarnessCreatePrivateDirectory
    StrCmp $SetupCode 0 0 harness_writer_done
    StrCpy $HarnessStage 84
    StrCpy $HarnessCreatePath $SetupFixedRoot
    Call HarnessCreatePrivateDirectory
    StrCmp $SetupCode 0 0 harness_writer_done
    StrCpy $HarnessStage 85
    StrCpy $HarnessCreatePath "$SetupFixedRoot\.GitHubBackupTool.state"
    Call HarnessCreatePrivateDirectory
    StrCmp $SetupCode 0 0 harness_writer_done

    StrCpy $HarnessStage 86
    StrCpy $HarnessSecurityDescriptor 0
    StrCpy $HarnessSecurityAttributes 0
    System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "O:$SetupOwnerSidD:P(A;;GA;;;$SetupOwnerSid)(A;;GA;;;SY)(A;;GA;;;BA)", i 1, *p .r8, p 0) i.r7'
    StrCmp $7 0 harness_writer_done
    StrCmp $8 0 harness_writer_done
    StrCpy $HarnessSecurityDescriptor $8
    System::Call '*(i 12, p r8, i 0) p.r9'
    StrCmp $9 0 harness_writer_done
    StrCpy $HarnessSecurityAttributes $9
    System::Call 'kernel32::CreateFileW(w "$SetupFixedRoot\.GitHubBackupTool.state\journal.ini", i 0xC0010000, i 0, p $HarnessSecurityAttributes, i 1, i 0x80, p 0) p.r0 ?e'
    Pop $7
    StrCmp $0 -1 harness_writer_done
    StrCpy $HarnessJournalHandle $0
    !if "${SETUP_JOURNAL_PHASE}" == "FILES_WRITTEN"
        StrCpy $HarnessRecord "[GitHubBackupSetupJournal]$\r$\nschema=1$\r$\nproductId=${SETUP_PRODUCT_ID}$\r$\nownerSid=$SetupOwnerSid$\r$\nappVersion=${SETUP_APP_VERSION}$\r$\nsourceCommit=${SETUP_SOURCE_COMMIT}$\r$\npayloadSha256=${SETUP_APP_SHA256}$\r$\n${SETUP_APP_NAME}=${SETUP_APP_SHA256}$\r$\n${SETUP_NOTICE_NAME}=${SETUP_NOTICE_SHA256}$\r$\nphase=PREPARED$\r$\nphase=FILES_WRITTEN$\r$\n"
    !else
        StrCpy $HarnessRecord "[GitHubBackupSetupJournal]$\r$\nschema=1$\r$\nproductId=${SETUP_PRODUCT_ID}$\r$\nownerSid=$SetupOwnerSid$\r$\nappVersion=${SETUP_APP_VERSION}$\r$\nsourceCommit=${SETUP_SOURCE_COMMIT}$\r$\npayloadSha256=${SETUP_APP_SHA256}$\r$\n${SETUP_APP_NAME}=${SETUP_APP_SHA256}$\r$\n${SETUP_NOTICE_NAME}=${SETUP_NOTICE_SHA256}$\r$\nphase=PREPARED$\r$\n"
    !endif
    StrLen $HarnessLineBytes $HarnessRecord
    IntOp $HarnessLineBytes $HarnessLineBytes * 2
    System::Call '*(&i2 0xFEFF) p.r0'
    StrCmp $0 0 harness_writer_done
    System::Call 'kernel32::WriteFile(p $HarnessJournalHandle, p r0, i 2, *i .r1, p 0) i.r2'
    System::Free $0
    StrCmp $2 0 harness_writer_done
    StrCmp $1 2 0 harness_writer_done
    System::Call 'kernel32::WriteFile(p $HarnessJournalHandle, w "$HarnessRecord", i $HarnessLineBytes, *i .r1, p 0) i.r2'
    StrCmp $2 0 harness_writer_done
    StrCmp $1 $HarnessLineBytes 0 harness_writer_done
    System::Call 'kernel32::FlushFileBuffers(p $HarnessJournalHandle) i.r0'
    StrCmp $0 0 harness_writer_done
    System::Call 'kernel32::CloseHandle(p $HarnessJournalHandle) i.r0'
    StrCmp $0 0 harness_writer_done
    StrCpy $HarnessJournalHandle 0
    StrCpy $SetupCode 0
harness_writer_done:
    StrCmp $HarnessJournalHandle 0 +2
        System::Call 'kernel32::CloseHandle(p $HarnessJournalHandle) i'
    StrCmp $HarnessSecurityAttributes 0 +2
        System::Free $HarnessSecurityAttributes
    StrCmp $HarnessSecurityDescriptor 0 +2
        System::Call 'kernel32::LocalFree(p $HarnessSecurityDescriptor) p'
    StrCmp $SetupCode 0 0 harness_writer_fail
    SetErrorLevel 0
    Goto harness_writer_exit
harness_writer_fail:
    SetErrorLevel $HarnessStage
harness_writer_exit:
SectionEnd
!else
Section "Read synthetic journal"
    SetErrorLevel 90
    StrCpy $SetupMode "install"
    StrCpy $SetupLocalAppData "${SETUP_JOURNAL_FIXTURE_PARENT}\LocalData"
    StrCpy $SetupFixedRoot "$SetupLocalAppData\${SETUP_INSTALL_SUFFIX}"
    Call HarnessResolveOwnerSid
    StrCmp $SetupCode 0 harness_reader_owner_ok
    StrCpy $HarnessRecord "READER_REJECTED|owner|$SetupCode"
    Goto harness_reader_write_result
harness_reader_owner_ok:
    StrCpy $GuardValue "pin-value-preserved"
    StrCpy $GuardValueLength 777
    Call PinExistingInstallAncestors
    StrCmp $SetupCode 0 harness_reader_pins_ok
    StrCpy $HarnessRecord "READER_REJECTED|pins|$SetupCode"
    Goto harness_reader_write_result
harness_reader_pins_ok:
    StrCmp $GuardValue "pin-value-preserved" 0 harness_reader_scratch_changed
    StrCmp $GuardValueLength 777 0 harness_reader_scratch_changed
    Call InspectFreshInstallJournal
    StrCpy $HarnessRecord "$SetupJournalReadStatus|$SetupJournalReadPhase|$SetupCode"
    Goto harness_reader_write_result
harness_reader_scratch_changed:
    StrCpy $HarnessRecord "READER_REJECTED|pin-scratch|13"
harness_reader_write_result:
    FileOpen $0 "${SETUP_JOURNAL_FIXTURE_PARENT}\read-result.txt" w
    IfErrors harness_reader_done
    FileWrite $0 "$HarnessRecord$\r$\n"
    FileClose $0
    StrCpy $SetupCode 0
harness_reader_done:
    SetErrorLevel 0
SectionEnd
!endif
