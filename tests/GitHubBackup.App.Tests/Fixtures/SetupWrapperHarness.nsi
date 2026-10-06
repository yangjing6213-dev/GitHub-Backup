; Test-only: all paths are bound to a unique fixture. No product setup entry is called.
Unicode true
RequestExecutionLevel user
SetCompressor zlib
!include "SetupInputs.nsh"
!include "SetupGuards.nsh"
!include "WrapperMacros.nsh"
Name "Synthetic wrapper runtime test"
OutFile "${WRAPPER_HARNESS_OUTPUT}"
SilentInstall silent
!insertmacro SetupNativeFoundation ""

Var WrapperPluginHandle
Var WrapperExitCode
Var WrapperLaunchFailed
Var WrapperPreviousBundleDirectory
Var HarnessParentHandle
Var HarnessOwnerDescriptor
Var HarnessOwnerString
!insertmacro SetupWrapperFunctions ""

Function .onInstFailed
    ReadEnvStr $0 "DOTNET_BUNDLE_EXTRACT_BASE_DIR"
    WriteINIStr "${WRAPPER_FIXTURE_ROOT}\wrapper-result.ini" "Wrapper" "RestoredBundleBase" "$0"
    WriteINIStr "${WRAPPER_FIXTURE_ROOT}\wrapper-result.ini" "Wrapper" "PluginHandle" "$WrapperPluginHandle"
    WriteINIStr "${WRAPPER_FIXTURE_ROOT}\wrapper-result.ini" "Wrapper" "Status" "FAIL"
FunctionEnd

Section "Synthetic wrapper runtime"
    SetErrorLevel 80
    ; Derive the synthetic SID from the owned fixture, without querying profiles.
    System::Call 'kernel32::CreateFileW(w "${WRAPPER_FIXTURE_ROOT}", i 0x20081, i 3, p 0, i 3, i 0x02200000, p 0) p.r0'
    StrCmp $0 -1 harness_failed
    StrCpy $HarnessParentHandle $0
    System::Call 'advapi32::GetSecurityInfo(p $HarnessParentHandle, i 1, i 1, *p .r1, p 0, p 0, p 0, *p .r2) i.r3'
    StrCmp $3 0 0 harness_failed
    StrCmp $1 0 harness_failed
    StrCmp $2 0 harness_failed
    StrCpy $HarnessOwnerDescriptor $2
    System::Call 'advapi32::ConvertSidToStringSidW(p r1, *p .r4) i.r3'
    StrCmp $3 0 harness_failed
    StrCmp $4 0 harness_failed
    StrCpy $HarnessOwnerString $4
    System::Call 'kernel32::lstrcpynW(w .r5, p $HarnessOwnerString, i ${NSIS_MAX_STRLEN}) p'
    StrCpy $SetupOwnerSid $5
    System::Call 'kernel32::LocalFree(p $HarnessOwnerString) p'
    StrCpy $HarnessOwnerString 0
    System::Call 'kernel32::LocalFree(p $HarnessOwnerDescriptor) p'
    StrCpy $HarnessOwnerDescriptor 0
    System::Call 'kernel32::CloseHandle(p $HarnessParentHandle) i.r0'
    StrCmp $0 0 harness_failed
    StrCpy $HarnessParentHandle 0

    Call PreparePrivatePluginDirectory
    StrCmp $SetupCode 0 0 harness_failed
    WriteINIStr "${WRAPPER_FIXTURE_ROOT}\wrapper-result.ini" "Wrapper" "PluginPath" "$PLUGINSDIR"
    SetOutPath "$PLUGINSDIR"
    File /oname=FakeEngine.exe "${WRAPPER_ENGINE_FILE}"
    !insertmacro RunSetupEngine "" '"$PLUGINSDIR\FakeEngine.exe" --wrapper-fixture'
    StrCmp $WrapperPluginHandle 0 0 harness_failed
    ReadEnvStr $0 "DOTNET_BUNDLE_EXTRACT_BASE_DIR"
    StrCmp $0 "${WRAPPER_INITIAL_BUNDLE}" 0 harness_failed
    WriteINIStr "${WRAPPER_FIXTURE_ROOT}\wrapper-result.ini" "Wrapper" "RestoredBundleBase" "$0"
    WriteINIStr "${WRAPPER_FIXTURE_ROOT}\wrapper-result.ini" "Wrapper" "PluginHandle" "$WrapperPluginHandle"
    WriteINIStr "${WRAPPER_FIXTURE_ROOT}\wrapper-result.ini" "Wrapper" "Status" "PASS"
    SetErrorLevel 0
    Goto harness_done
harness_failed:
    Call ReleasePrivatePluginDirectory
    WriteINIStr "${WRAPPER_FIXTURE_ROOT}\wrapper-result.ini" "Wrapper" "Status" "FAIL"
    SetErrorLevel 80
harness_done:
SectionEnd
