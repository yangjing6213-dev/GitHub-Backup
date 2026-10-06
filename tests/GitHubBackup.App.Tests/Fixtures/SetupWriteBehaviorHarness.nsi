; Synthetic-only probe for NSIS overwrite behavior. It never launches the
; uninstaller or shortcut target and never reads a user profile.
Unicode true
RequestExecutionLevel user
SetCompressor zlib

!ifndef SETUP_WRITE_FIXTURE_PARENT
    !error "SETUP_WRITE_FIXTURE_PARENT_REQUIRED"
!endif
!ifndef SETUP_WRITE_HARNESS_OUTPUT
    !error "SETUP_WRITE_HARNESS_OUTPUT_REQUIRED"
!endif

Name "Synthetic NSIS write behavior probe"
OutFile "${SETUP_WRITE_HARNESS_OUTPUT}"
SilentInstall silent

Var HarnessFailure
Var HarnessDiagnostic
Var HarnessErrorCode
Var HarnessParentHandle
Var HarnessParentOwner
Var HarnessOwnerString
Var HarnessOwnerDescriptor
Var HarnessOwnerSid
Var HarnessSecurityDescriptor
Var HarnessSecurityAttributes
Var HarnessIdentityHandle
Var HarnessIdentity
Var HarnessCreatePath
Var HarnessCreateText
Var HarnessCreateBytes
Var HarnessCreateHandle
Var HarnessUninstallerPath
Var HarnessUninstallerHandle
Var HarnessUninstallerOriginalIdentity
Var HarnessUninstallerPathHandle
Var HarnessUninstallerStatus
Var HarnessUninstallerIdentityStatus
Var HarnessShortcutPath
Var HarnessShortcutHandle
Var HarnessShortcutOriginalIdentity
Var HarnessShortcutPathHandle
Var HarnessShortcutStatus
Var HarnessShortcutIdentityStatus
Var HarnessResultHandle

Section "Probe only synthetic files"
    SetErrorLevel 90
    StrCpy $HarnessFailure 90
    StrCpy $HarnessDiagnostic "startup"
    StrCpy $HarnessParentHandle 0
    StrCpy $HarnessParentOwner 0
    StrCpy $HarnessOwnerString 0
    StrCpy $HarnessOwnerDescriptor 0
    StrCpy $HarnessSecurityDescriptor 0
    StrCpy $HarnessSecurityAttributes 0
    StrCpy $HarnessUninstallerHandle 0
    StrCpy $HarnessUninstallerPathHandle 0
    StrCpy $HarnessShortcutHandle 0
    StrCpy $HarnessShortcutPathHandle 0
    StrCpy $HarnessResultHandle 0

    StrCpy $HarnessFailure 91
    CreateDirectory "${SETUP_WRITE_FIXTURE_PARENT}\runtime-temp"
    IfErrors harness_fail
    System::Call 'kernel32::SetEnvironmentVariableW(w "TEMP", w "${SETUP_WRITE_FIXTURE_PARENT}\runtime-temp") i.r0'
    StrCmp $0 0 harness_fail
    System::Call 'kernel32::SetEnvironmentVariableW(w "TMP", w "${SETUP_WRITE_FIXTURE_PARENT}\runtime-temp") i.r0'
    StrCmp $0 0 harness_fail
    InitPluginsDir

    ; Read the owner SID from the synthetic fixture parent ACL only. No token,
    ; Known Folder, registry, account lookup, or profile path is queried.
    StrCpy $HarnessFailure 92
    System::Call 'kernel32::CreateFileW(w "${SETUP_WRITE_FIXTURE_PARENT}", i 0x20081, i 3, p 0, i 3, i 0x02200000, p 0) p.r0 ?e'
    Pop $HarnessErrorCode
    StrCmp $0 -1 harness_fail
    StrCpy $HarnessParentHandle $0
    StrCpy $HarnessFailure 93
    System::Call 'advapi32::GetSecurityInfo(p $HarnessParentHandle, i 1, i 1, *p .r1, p 0, p 0, p 0, *p .r2) i.r3'
    StrCmp $3 0 harness_security_owner_check
    StrCpy $HarnessFailure 931
    StrCpy $HarnessErrorCode $3
    Goto harness_fail
harness_security_owner_check:
    StrCmp $1 0 harness_security_owner_missing
    StrCmp $2 0 harness_security_descriptor_missing
    StrCpy $HarnessParentOwner $1
    StrCpy $HarnessOwnerDescriptor $2
    Goto harness_security_owner_ready
harness_security_owner_missing:
    StrCpy $HarnessFailure 932
    Goto harness_fail
harness_security_descriptor_missing:
    StrCpy $HarnessFailure 933
    Goto harness_fail
harness_security_owner_ready:
    StrCpy $HarnessFailure 94
    System::Call 'advapi32::ConvertSidToStringSidW(p $HarnessParentOwner, *p .r4) i.r3'
    StrCmp $3 0 harness_fail
    StrCmp $4 0 harness_fail
    StrCpy $HarnessOwnerString $4
    StrCpy $HarnessFailure 95
    System::Call 'kernel32::lstrlenW(p $HarnessOwnerString) i.r3'
    StrCpy $HarnessDiagnostic "sid-string-length=$3"
    IntCmpU $3 ${NSIS_MAX_STRLEN} harness_sid_length_equal harness_sid_length_ok harness_sid_length_long
harness_sid_length_equal:
    StrCpy $HarnessDiagnostic "sid-string-length-equals-limit"
    Goto harness_fail
harness_sid_length_long:
    StrCpy $HarnessDiagnostic "sid-string-length-exceeds-limit=$3"
    Goto harness_fail
harness_sid_length_ok:
    System::Call 'kernel32::lstrcpynW(w .r5, p $HarnessOwnerString, i ${NSIS_MAX_STRLEN}) p'
    StrCpy $HarnessDiagnostic "sid-string-copied"
    StrCpy $HarnessOwnerSid $5
    StrCpy $HarnessFailure 96
    System::Call 'kernel32::LocalFree(p $HarnessOwnerString) p.r3'
    StrCpy $HarnessOwnerString 0
    StrCmp $3 0 0 harness_fail
    System::Call 'kernel32::LocalFree(p $HarnessOwnerDescriptor) p.r3'
    StrCpy $HarnessOwnerDescriptor 0
    StrCmp $3 0 0 harness_fail
    StrCpy $HarnessFailure 97
    System::Call 'kernel32::CloseHandle(p $HarnessParentHandle) i.r3'
    StrCpy $HarnessParentHandle 0
    StrCmp $3 0 harness_fail

    ; The explicit protected DACL grants full access only to the current SID,
    ; SYSTEM, and Administrators. Apply it to the synthetic root and all three
    ; test files created below.
    StrCpy $HarnessFailure 98
    StrCpy $R0 "$HarnessOwnerSid"
    System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "O:$R0D:P(A;;GA;;;$R0)(A;;GA;;;SY)(A;;GA;;;BA)", i 1, *p .r6, p 0) i.r7 ?e'
    Pop $HarnessErrorCode
    StrCmp $7 0 harness_fail
    StrCmp $6 0 harness_fail
    StrCpy $HarnessSecurityDescriptor $6
    System::Call '*(i 12, p r6, i 0) p.r8'
    StrCmp $8 0 harness_fail
    StrCpy $HarnessSecurityAttributes $8
    System::Call 'kernel32::CreateDirectoryW(w "${SETUP_WRITE_FIXTURE_PARENT}\product", p $HarnessSecurityAttributes) i.r9 ?e'
    Pop $HarnessErrorCode
    StrCmp $9 0 harness_fail

    StrCpy $HarnessFailure 95
    StrCpy $HarnessCreatePath "${SETUP_WRITE_FIXTURE_PARENT}\product\uninstaller-placeholder.exe"
    StrCpy $HarnessCreateText "UNINSTALLER-PLACEHOLDER"
    StrCpy $HarnessCreateBytes 46
    StrCpy $HarnessDiagnostic "creating uninstaller placeholder"
    Call CreateProtectedPlaceholder
    StrCmp $HarnessCreateHandle -1 harness_fail
    StrCmp $HarnessCreateHandle 0 harness_fail
    StrCpy $HarnessUninstallerPath $HarnessCreatePath
    StrCpy $HarnessUninstallerHandle $HarnessCreateHandle
    StrCpy $HarnessIdentityHandle $HarnessUninstallerHandle
    StrCpy $HarnessDiagnostic "capturing uninstaller placeholder identity"
    Call CaptureHarnessIdentity
    StrCmp $HarnessIdentity "" harness_fail
    StrCpy $HarnessUninstallerOriginalIdentity $HarnessIdentity

    StrCpy $HarnessFailure 96
    StrCpy $HarnessCreatePath "${SETUP_WRITE_FIXTURE_PARENT}\product\shortcut-placeholder.lnk"
    StrCpy $HarnessCreateText "SHORTCUT-PLACEHOLDER"
    StrCpy $HarnessCreateBytes 40
    StrCpy $HarnessDiagnostic "creating shortcut placeholder"
    Call CreateProtectedPlaceholder
    StrCmp $HarnessCreateHandle -1 harness_fail
    StrCmp $HarnessCreateHandle 0 harness_fail
    StrCpy $HarnessShortcutPath $HarnessCreatePath
    StrCpy $HarnessShortcutHandle $HarnessCreateHandle
    StrCpy $HarnessIdentityHandle $HarnessShortcutHandle
    StrCpy $HarnessDiagnostic "capturing shortcut placeholder identity"
    Call CaptureHarnessIdentity
    StrCmp $HarnessIdentity "" harness_fail
    StrCpy $HarnessShortcutOriginalIdentity $HarnessIdentity

    StrCpy $HarnessFailure 97
    StrCpy $HarnessCreatePath "${SETUP_WRITE_FIXTURE_PARENT}\product\target-not-launched.txt"
    StrCpy $HarnessCreateText "NEVER-LAUNCH"
    StrCpy $HarnessCreateBytes 24
    Call CreateProtectedPlaceholder
    StrCmp $HarnessCreateHandle -1 harness_fail
    StrCmp $HarnessCreateHandle 0 harness_fail
    System::Call 'kernel32::CloseHandle(p $HarnessCreateHandle) i.r0'
    StrCpy $HarnessCreateHandle 0
    StrCmp $0 0 harness_fail

    ; Release only the DACL scratch allocations. Each placeholder handle stays
    ; open with read/write/delete sharing while NSIS attempts both operations.
    StrCpy $HarnessFailure 98
    System::Free $HarnessSecurityAttributes
    StrCpy $HarnessSecurityAttributes 0
    System::Call 'kernel32::LocalFree(p $HarnessSecurityDescriptor) p.r0'
    StrCpy $HarnessSecurityDescriptor 0
    StrCmp $0 0 0 harness_fail
    StrCpy $HarnessFailure 99
    ClearErrors
    WriteUninstaller "$HarnessUninstallerPath"
    IfErrors harness_uninstaller_error
    StrCpy $HarnessUninstallerStatus "success"
    Goto harness_check_uninstaller_path
harness_uninstaller_error:
    StrCpy $HarnessUninstallerStatus "error"
harness_check_uninstaller_path:
    System::Call 'kernel32::CreateFileW(w "$HarnessUninstallerPath", i 0x80000000, i 7, p 0, i 3, i 0x80, p 0) p.r0'
    StrCmp $0 -1 harness_uninstaller_path_missing
    StrCpy $HarnessUninstallerPathHandle $0
    StrCpy $HarnessIdentityHandle $HarnessUninstallerPathHandle
    Call CaptureHarnessIdentity
    StrCmp $HarnessIdentity "" harness_fail
    StrCmp $HarnessIdentity $HarnessUninstallerOriginalIdentity 0 harness_uninstaller_replaced
    StrCpy $HarnessUninstallerIdentityStatus "unchanged"
    Goto harness_probe_shortcut
harness_uninstaller_replaced:
    StrCpy $HarnessUninstallerIdentityStatus "replaced"
    Goto harness_probe_shortcut
harness_uninstaller_path_missing:
    StrCpy $HarnessUninstallerIdentityStatus "missing"

harness_probe_shortcut:
    StrCpy $HarnessFailure 100
    ClearErrors
    CreateShortCut "$HarnessShortcutPath" "${SETUP_WRITE_FIXTURE_PARENT}\product\target-not-launched.txt"
    IfErrors harness_shortcut_error
    StrCpy $HarnessShortcutStatus "success"
    Goto harness_check_shortcut_path
harness_shortcut_error:
    StrCpy $HarnessShortcutStatus "error"
harness_check_shortcut_path:
    System::Call 'kernel32::CreateFileW(w "$HarnessShortcutPath", i 0x80000000, i 7, p 0, i 3, i 0x80, p 0) p.r0'
    StrCmp $0 -1 harness_shortcut_path_missing
    StrCpy $HarnessShortcutPathHandle $0
    StrCpy $HarnessIdentityHandle $HarnessShortcutPathHandle
    Call CaptureHarnessIdentity
    StrCmp $HarnessIdentity "" harness_fail
    StrCmp $HarnessIdentity $HarnessShortcutOriginalIdentity 0 harness_shortcut_replaced
    StrCpy $HarnessShortcutIdentityStatus "unchanged"
    Goto harness_write_result
harness_shortcut_replaced:
    StrCpy $HarnessShortcutIdentityStatus "replaced"
    Goto harness_write_result
harness_shortcut_path_missing:
    StrCpy $HarnessShortcutIdentityStatus "missing"

harness_write_result:
    StrCpy $HarnessFailure 101
    FileOpen $HarnessResultHandle "${SETUP_WRITE_FIXTURE_PARENT}\write-behavior-results.txt" w
    IfErrors harness_fail
    FileWrite $HarnessResultHandle "WriteUninstaller=$HarnessUninstallerStatus; PathIdentity=$HarnessUninstallerIdentityStatus; OriginalHandleOpen=yes$\r$\n"
    FileWrite $HarnessResultHandle "CreateShortCut=$HarnessShortcutStatus; PathIdentity=$HarnessShortcutIdentityStatus; OriginalHandleOpen=yes$\r$\n"
    FileClose $HarnessResultHandle
    StrCpy $HarnessResultHandle 0

    Call CleanupHarnessHandles
    SetErrorLevel 0
    Goto harness_done

harness_fail:
    StrCmp $HarnessResultHandle 0 harness_fail_recorded
    Goto harness_fail_cleanup
harness_fail_recorded:
    FileOpen $HarnessResultHandle "${SETUP_WRITE_FIXTURE_PARENT}\write-behavior-failure.txt" w
    IfErrors harness_fail_cleanup
    FileWrite $HarnessResultHandle "HarnessFailure=$HarnessFailure; LastError=$HarnessErrorCode; Diagnostic=$HarnessDiagnostic$\r$\n"
    FileClose $HarnessResultHandle
    StrCpy $HarnessResultHandle 0
harness_fail_cleanup:
    Call CleanupHarnessHandles
    SetErrorLevel $HarnessFailure
    Abort "SYNTHETIC_WRITE_BEHAVIOR_PROBE_FAILED"
harness_done:
SectionEnd

Function CreateProtectedPlaceholder
    StrCpy $HarnessCreateHandle 0
    System::Call 'kernel32::SetLastError(i 0)'
    System::Call 'kernel32::CreateFileW(w "$HarnessCreatePath", i 0xC0010000, i 7, p $HarnessSecurityAttributes, i 1, i 0x80, p 0) p.r0 ?e'
    Pop $HarnessErrorCode
    StrCpy $HarnessDiagnostic "placeholder CreateFileW result=$0 error=$HarnessErrorCode"
    StrCmp $0 -1 harness_placeholder_failed
    StrCmp $0 0 harness_placeholder_failed
    StrCpy $HarnessCreateHandle $0
    System::Call 'kernel32::SetLastError(i 0)'
    System::Call 'kernel32::WriteFile(p $HarnessCreateHandle, w "$HarnessCreateText", i $HarnessCreateBytes, *i .r1, p 0) i.r2 ?e'
    Pop $HarnessErrorCode
    StrCpy $HarnessDiagnostic "placeholder WriteFile result=$2 bytes=$1 error=$HarnessErrorCode"
    StrCmp $2 0 harness_placeholder_failed
    StrCmp $1 $HarnessCreateBytes 0 harness_placeholder_failed
    Return
harness_placeholder_failed:
    StrCpy $HarnessCreateHandle -1
FunctionEnd

Function CaptureHarnessIdentity
    StrCpy $HarnessIdentity ""
    System::Call 'kernel32::SetLastError(i 0)'
    System::Alloc 24
    Pop $0
    StrCmp $0 0 harness_identity_done
    System::Call 'kernel32::GetFileInformationByHandleEx(p $HarnessIdentityHandle, i 18, p r0, i 24) i.r1 ?e'
    Pop $HarnessErrorCode
    StrCpy $HarnessDiagnostic "GetFileInformationByHandleEx result=$1 error=$HarnessErrorCode"
    StrCmp $1 0 harness_identity_free
    System::Call '*$0(i.r2, i.r3, i.r4, i.r5, i.r6, i.r7)'
    StrCpy $HarnessIdentity "$2:$3:$4:$5:$6:$7"
    StrCpy $HarnessDiagnostic "FILE_ID_INFO captured"
harness_identity_free:
    System::Free $0
harness_identity_done:
FunctionEnd

Function CleanupHarnessHandles
    StrCmp $HarnessParentHandle 0 cleanup_owner_string
    System::Call 'kernel32::CloseHandle(p $HarnessParentHandle)'
    StrCpy $HarnessParentHandle 0
cleanup_owner_string:
    StrCmp $HarnessOwnerString 0 cleanup_owner_descriptor
    System::Call 'kernel32::LocalFree(p $HarnessOwnerString)'
    StrCpy $HarnessOwnerString 0
cleanup_owner_descriptor:
    StrCmp $HarnessOwnerDescriptor 0 cleanup_security_attributes
    System::Call 'kernel32::LocalFree(p $HarnessOwnerDescriptor)'
    StrCpy $HarnessOwnerDescriptor 0
cleanup_security_attributes:
    StrCmp $HarnessSecurityAttributes 0 cleanup_security_descriptor
    System::Free $HarnessSecurityAttributes
    StrCpy $HarnessSecurityAttributes 0
cleanup_security_descriptor:
    StrCmp $HarnessSecurityDescriptor 0 cleanup_create_handle
    System::Call 'kernel32::LocalFree(p $HarnessSecurityDescriptor)'
    StrCpy $HarnessSecurityDescriptor 0
cleanup_create_handle:
    StrCmp $HarnessCreateHandle 0 cleanup_uninstaller_handle
    StrCmp $HarnessCreateHandle -1 cleanup_uninstaller_handle
    System::Call 'kernel32::CloseHandle(p $HarnessCreateHandle)'
    StrCpy $HarnessCreateHandle 0
cleanup_uninstaller_handle:
    StrCmp $HarnessUninstallerHandle 0 cleanup_uninstaller_path_handle
    System::Call 'kernel32::CloseHandle(p $HarnessUninstallerHandle)'
    StrCpy $HarnessUninstallerHandle 0
cleanup_uninstaller_path_handle:
    StrCmp $HarnessUninstallerPathHandle 0 cleanup_shortcut_handle
    System::Call 'kernel32::CloseHandle(p $HarnessUninstallerPathHandle)'
    StrCpy $HarnessUninstallerPathHandle 0
cleanup_shortcut_handle:
    StrCmp $HarnessShortcutHandle 0 cleanup_shortcut_path_handle
    System::Call 'kernel32::CloseHandle(p $HarnessShortcutHandle)'
    StrCpy $HarnessShortcutHandle 0
cleanup_shortcut_path_handle:
    StrCmp $HarnessShortcutPathHandle 0 cleanup_result_handle
    System::Call 'kernel32::CloseHandle(p $HarnessShortcutPathHandle)'
    StrCpy $HarnessShortcutPathHandle 0
cleanup_result_handle:
    StrCmp $HarnessResultHandle 0 cleanup_done
    FileClose $HarnessResultHandle
    StrCpy $HarnessResultHandle 0
cleanup_done:
FunctionEnd

; Required for the installer compiler directive; this section is never run by
; the probe and no generated uninstaller is ever launched.
Section "Uninstall"
SectionEnd
