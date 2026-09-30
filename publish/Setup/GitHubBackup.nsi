; -*- coding: utf-8 -*-
; Task 2 entry checkpoint. The native foundation is shared with SetupHarness,
; but full ownership, transaction and uninstall authorization are unfinished.
; The final !error prevents packaging an incomplete product installer.
Unicode true
RequestExecutionLevel user
SetCompressor zlib
!ifndef SETUP_INPUTS
    !error "SETUP_INPUTS_REQUIRED"
!endif
!include "${SETUP_INPUTS}"
!include "MUI2.nsh"
!include "SetupGuards.nsh"
; Stop before OutFile: no build can emit a partial or misleading installer.
!error "SETUP_LIFECYCLE_NOT_IMPLEMENTED"

Name "GitHub 备份工具"
OutFile "${SETUP_OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\${SETUP_INSTALL_SUFFIX}"
!define SETUP_START_MENU_NAME "GitHub 备份工具.lnk"
!define MUI_WELCOMEPAGE_TITLE "欢迎使用 GitHub 备份工具安装向导"
!define MUI_WELCOMEPAGE_TEXT "此向导只为当前用户安装，不会请求管理员权限，也不会自动启动程序。"
!define MUI_FINISHPAGE_TITLE "安装完成"
!define MUI_FINISHPAGE_TEXT "安装完成后，程序不会自动启动。"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
; No automatic app launch, desktop shortcut or user-selectable directory page.

!insertmacro SetupNativeFoundation ""
!insertmacro SetupNativeFoundation "un."

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
    ; Revalidate the fixed Known Folder target before any future write chain.
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
    SetErrorLevel 21
    Abort "SETUP_LIFECYCLE_NOT_IMPLEMENTED"
SectionEnd

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
    SetErrorLevel 21
    Abort "SETUP_LIFECYCLE_NOT_IMPLEMENTED"
SectionEnd

; The guard above remains unconditional until exact ownership and transaction
; interfaces are implemented and independently reviewed. This is not a release.
