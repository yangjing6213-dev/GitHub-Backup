; -*- coding: utf-8 -*-
; The NSIS wrapper stages private inputs and runs the app's fixed-path setup entry.
Unicode true
RequestExecutionLevel user
SetCompressor zlib
!ifndef SETUP_INPUTS
    !error "SETUP_INPUTS_REQUIRED"
!endif
!include "${SETUP_INPUTS}"
!include "MUI2.nsh"
!include "SetupGuards.nsh"

Name "GitHub 备份工具"
!ifdef EXPORT_UNINST
    !ifdef IMPORT_UNINST
        !error "SETUP_COMPILER_MODE_CONFLICT"
    !endif
    OutFile "${SETUP_EXPORT_OUTPUT_FILE}"
!else
    !ifndef IMPORT_UNINST
        !error "SETUP_COMPILER_MODE_REQUIRED"
    !endif
    !ifndef SETUP_MANIFEST_FILE
        !error "SETUP_MANIFEST_REQUIRED"
    !endif
    !ifndef SETUP_MANIFEST_SHA256
        !error "SETUP_MANIFEST_HASH_REQUIRED"
    !endif
    OutFile "${SETUP_OUTPUT_FILE}"
!endif
InstallDir "$LOCALAPPDATA\${SETUP_INSTALL_SUFFIX}"
!define SETUP_START_MENU_NAME "GitHub 备份工具.lnk"
!define MUI_WELCOMEPAGE_TITLE "欢迎使用 GitHub 备份工具安装向导"
!define MUI_WELCOMEPAGE_TEXT "此向导只为当前用户安装，不会请求管理员权限，也不会自动启动程序。"
!define MUI_FINISHPAGE_TITLE "安装完成"
!define MUI_FINISHPAGE_TEXT "安装完成后，程序不会自动启动。"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!ifndef IMPORT_UNINST
    !insertmacro MUI_UNPAGE_CONFIRM
    !insertmacro MUI_UNPAGE_INSTFILES
!endif
!insertmacro MUI_LANGUAGE "SimpChinese"
; No automatic app launch, desktop shortcut or user-selectable directory page.

!insertmacro SetupNativeFoundation ""
!ifndef IMPORT_UNINST
    !insertmacro SetupNativeFoundation "un."
!endif

Var WrapperPluginHandle
Var WrapperExitCode
Var WrapperLaunchFailed
Var WrapperPreviousBundleDirectory

!macro SetupWrapperFunctions PREFIX
Function ${PREFIX}PreparePrivatePluginDirectory
    !insertmacro SetupSaveRegisters
    StrCpy $SetupCode 13
    StrCpy $WrapperPluginHandle 0
    StrCpy $5 0
    StrCpy $7 0
    StrCpy $8 0
    StrCpy $9 0
    InitPluginsDir
    StrCmp $PLUGINSDIR "" wrapper_directory_done
    ; Only NSIS's own newly created scratch directory receives this DACL.
    ; READ_CONTROL | WRITE_DAC | FILE_READ_ATTRIBUTES | FILE_LIST_DIRECTORY;
    ; deny delete sharing until extraction and the synchronous engine finish.
    System::Call 'kernel32::CreateFileW(w "$PLUGINSDIR", i 0x60081, i 3, p 0, i 3, i 0x02200000, p 0) p.r0'
    StrCmp $0 -1 wrapper_directory_done
    StrCpy $WrapperPluginHandle $0
    System::Alloc 52
    Pop $9
    StrCmp $9 0 wrapper_directory_done
    System::Call 'kernel32::GetFileInformationByHandle(p $WrapperPluginHandle, p r9) i.r1'
    StrCmp $1 0 wrapper_directory_done
    System::Call '*$9(i.r1)'
    IntOp $2 $1 & 0x410 ; directory, never a reparse point
    IntCmp $2 0x10 0 wrapper_directory_done wrapper_directory_done
    System::Call 'kernel32::GetFinalPathNameByHandleW(p $WrapperPluginHandle, w .r1, i ${NSIS_MAX_STRLEN}, i 0) i.r2'
    StrCmp $2 0 wrapper_directory_done
    IntCmpU $2 ${NSIS_MAX_STRLEN} wrapper_directory_done 0 wrapper_directory_done
    StrCmp $1 '\\?\$PLUGINSDIR' 0 wrapper_directory_done
    ; The scratch directory's original owner must match this exact user before
    ; changing its permissions. Approved system/admin owners are not sufficient.
    System::Call 'advapi32::GetSecurityInfo(p $WrapperPluginHandle, i 1, i 1, *p .r4, p 0, p 0, p 0, *p .r7) i.r1'
    StrCmp $1 0 0 wrapper_directory_done
    StrCmp $4 0 wrapper_directory_done
    StrCmp $7 0 wrapper_directory_done
    System::Call 'advapi32::ConvertSidToStringSidW(p r4, *p .r5) i.r1'
    StrCmp $1 0 wrapper_directory_done
    StrCmp $5 0 wrapper_directory_done
    System::Call 'kernel32::lstrlenW(p r5) i.r6'
    StrCmp $6 0 wrapper_directory_done
    IntCmpU $6 ${NSIS_MAX_STRLEN} wrapper_directory_done 0 wrapper_directory_done
    System::Call 'kernel32::lstrcpynW(w .r6, p r5, i ${NSIS_MAX_STRLEN}) p'
    StrCmpS $6 $SetupOwnerSid 0 wrapper_directory_done
    StrCpy $0 $SetupOwnerSid
    StrCmp $0 "" wrapper_directory_done
    System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "O:$0D:P(A;OICI;GA;;;$0)(A;OICI;GA;;;SY)(A;OICI;GA;;;BA)", i 1, *p .r8, p 0) i.r1'
    StrCmp $1 0 wrapper_directory_done
    StrCmp $8 0 wrapper_directory_done
    System::Call 'advapi32::SetKernelObjectSecurity(p $WrapperPluginHandle, i 0x80000004, p r8) i.r1'
    StrCmp $1 0 wrapper_directory_done
    Push $GuardHandle
    StrCpy $GuardHandle $WrapperPluginHandle
    Call ${PREFIX}ValidatePrivateHandleAcl
    Pop $GuardHandle
wrapper_directory_done:
    StrCmp $5 0 +2
        System::Call 'kernel32::LocalFree(p r5) p'
    StrCmp $7 0 +2
        System::Call 'kernel32::LocalFree(p r7) p'
    StrCmp $8 0 +2
        System::Call 'kernel32::LocalFree(p r8) p'
    StrCmp $9 0 +2
        System::Free $9
    !insertmacro SetupRestoreRegisters
FunctionEnd

Function ${PREFIX}ReleasePrivatePluginDirectory
    !insertmacro SetupSaveRegisters
    ; NSIS cleans its scratch directory before process exit. Leave it first so
    ; the current-directory handle does not keep that directory from removal.
    SetOutPath "$TEMP"
    StrCmp $WrapperPluginHandle 0 wrapper_directory_released
    System::Call 'kernel32::CloseHandle(p $WrapperPluginHandle) i.r0'
    StrCmp $0 0 wrapper_directory_release_failed
    StrCpy $WrapperPluginHandle 0
wrapper_directory_released:
    !insertmacro SetupRestoreRegisters
    Return
wrapper_directory_release_failed:
    StrCpy $SetupCode 13
    !insertmacro SetupRestoreRegisters
FunctionEnd
!macroend

!insertmacro SetupWrapperFunctions ""
!ifndef IMPORT_UNINST
    !insertmacro SetupWrapperFunctions "un."
!endif

!macro RunSetupEngine PREFIX COMMAND
    ReadEnvStr $WrapperPreviousBundleDirectory "DOTNET_BUNDLE_EXTRACT_BASE_DIR"
    System::Call 'kernel32::SetEnvironmentVariableW(w "DOTNET_BUNDLE_EXTRACT_BASE_DIR", w "$PLUGINSDIR\.net") i.r0'
    ${If} $0 == 0
        Call ${PREFIX}ReleasePrivatePluginDirectory
        SetErrorLevel 13
        Abort "无法准备安装程序的临时目录。"
    ${EndIf}
    StrCpy $WrapperExitCode 13
    StrCpy $WrapperLaunchFailed 0
    ClearErrors
    ExecWait '${COMMAND}' $WrapperExitCode
    IfErrors 0 +2
        StrCpy $WrapperLaunchFailed 1
    ${If} $WrapperPreviousBundleDirectory == ""
        System::Call 'kernel32::SetEnvironmentVariableW(w "DOTNET_BUNDLE_EXTRACT_BASE_DIR", p 0) i.r0'
    ${Else}
        System::Call 'kernel32::SetEnvironmentVariableW(w "DOTNET_BUNDLE_EXTRACT_BASE_DIR", w "$WrapperPreviousBundleDirectory") i.r0'
    ${EndIf}
    ${If} $0 == 0
        StrCpy $WrapperLaunchFailed 1
    ${EndIf}
    StrCpy $SetupCode 0
    Call ${PREFIX}ReleasePrivatePluginDirectory
    ${If} $WrapperLaunchFailed != 0
    ${OrIf} $SetupCode != 0
        SetErrorLevel 13
        Abort "安装程序未能完成，请保留错误信息。"
    ${EndIf}
    SetErrorLevel $WrapperExitCode
    ${If} $WrapperExitCode != 0
        ${If} $WrapperExitCode == 10
            Abort "仅支持 Windows 11 25H2 x64，请以普通用户运行，不要以管理员身份运行。"
        ${ElseIf} $WrapperExitCode == 11
            Abort "安装参数不受支持，安装位置固定，不能更改安装目录。"
        ${ElseIf} $WrapperExitCode == 12
            Abort "检测到旧版本。请先关闭程序，在文件资源管理器地址栏输入 $LOCALAPPDATA\Programs\GitHubBackupTool 并运行 Uninstall.exe。卸载不会删除备份、设置、日志或凭据。完成后再运行此安装程序。"
        ${Else}
            Abort "文件被占用，或安装位置、权限存在冲突。请先关闭 GitHub 备份程序后重试，无需删除备份。"
        ${EndIf}
    ${EndIf}
!macroend

Function .onInit
    SetShellVarContext current
    SetRegView 64
    StrCpy $SetupMode "install"
    Call ValidateHost
    ${If} $SetupCode != 0
        SetErrorLevel $SetupCode
        Abort
    ${EndIf}
    ; Ignore environment-derived defaults; use the native Known Folder result.
    StrCpy $INSTDIR $SetupFixedRoot
    Call ValidateDirectoryArguments
    ${If} $SetupCode != 0
        SetErrorLevel $SetupCode
        Abort
    ${EndIf}
FunctionEnd

Section "安装"
    ; Revalidate the fixed Known Folder target before extracting any input.
    Call ValidateHost
    ${If} $SetupCode != 0
        SetErrorLevel $SetupCode
        Abort
    ${EndIf}
    Call ValidateDirectoryArguments
    ${If} $SetupCode != 0
        SetErrorLevel $SetupCode
        Abort
    ${EndIf}
!ifdef IMPORT_UNINST
    Call PreparePrivatePluginDirectory
    ${If} $SetupCode != 0
        Call ReleasePrivatePluginDirectory
        SetErrorLevel 13
        Abort "无法创建仅当前用户可写的临时目录。"
    ${EndIf}
    SetOutPath "$PLUGINSDIR"
    ClearErrors
    File /oname=${SETUP_APP_NAME} "${SETUP_APP_FILE}"
    File /oname=${SETUP_UNINSTALLER_NAME} "${SETUP_UNINSTALLER_FILE}"
    File /oname=${SETUP_NOTICE_NAME} "${SETUP_NOTICE_FILE}"
    File /oname=SetupManifest.json "${SETUP_MANIFEST_FILE}"
    ${If} ${Errors}
        Call ReleasePrivatePluginDirectory
        SetErrorLevel 13
        Abort "无法解压安装文件。"
    ${EndIf}
    !insertmacro RunSetupEngine "" '"$PLUGINSDIR\${SETUP_APP_NAME}" --setup-install "$PLUGINSDIR\SetupManifest.json" "${SETUP_MANIFEST_SHA256}"'
!else
    ; The export artifact is never run; !uninstfinalize exports the native EXE.
    InitPluginsDir
    WriteUninstaller "$PLUGINSDIR\${SETUP_UNINSTALLER_NAME}"
!endif
SectionEnd

!ifndef IMPORT_UNINST
Function un.onInit
    SetShellVarContext current
    SetRegView 64
    StrCpy $SetupMode "uninstall"
    Call un.ValidateHost
    ${If} $SetupCode != 0
        SetErrorLevel $SetupCode
        Abort
    ${EndIf}
    Call un.ValidateDirectoryArguments
    ${If} $SetupCode != 0
        SetErrorLevel $SetupCode
        Abort
    ${EndIf}
    ; Normal NSIS launch runs a temporary self-copy. A manually forced direct
    ; mapped copy must refuse before the engine can remove any owned file.
    GetFullPathName $0 "$EXEPATH"
    ${If} $0 == "$SetupFixedRoot\${SETUP_UNINSTALLER_NAME}"
        SetErrorLevel 13
        Abort "SETUP_UNINSTALLER_MUST_SELF_COPY"
    ${EndIf}
FunctionEnd

Section "Uninstall"
    Call un.ValidateHost
    ${If} $SetupCode != 0
        SetErrorLevel $SetupCode
        Abort
    ${EndIf}
    Call un.ValidateDirectoryArguments
    ${If} $SetupCode != 0
        SetErrorLevel $SetupCode
        Abort
    ${EndIf}
    Call un.PreparePrivatePluginDirectory
    ${If} $SetupCode != 0
        Call un.ReleasePrivatePluginDirectory
        SetErrorLevel 13
        Abort "无法创建仅当前用户可写的临时目录。"
    ${EndIf}
    SetOutPath "$PLUGINSDIR"
    ClearErrors
    File /oname=${SETUP_APP_NAME} "${SETUP_APP_FILE}"
    ${If} ${Errors}
        Call un.ReleasePrivatePluginDirectory
        SetErrorLevel 13
        Abort "无法解压卸载所需的程序。"
    ${EndIf}
    !insertmacro RunSetupEngine "un." '"$PLUGINSDIR\${SETUP_APP_NAME}" --setup-uninstall'
SectionEnd
!endif

!ifdef EXPORT_UNINST
    ; Documented native export/import pattern; only WorkDirectory is affected.
    !uninstfinalize 'echo copy /Y "%1" "Uninstall.exe"&copy /Y "%1" "Uninstall.exe" >nul' = 0
!endif
