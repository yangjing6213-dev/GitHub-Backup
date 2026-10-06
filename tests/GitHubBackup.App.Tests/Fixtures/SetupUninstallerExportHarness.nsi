; Synthetic-only compiler fixture. This file is never run as an installer.
Unicode true
RequestExecutionLevel user
SetCompressor zlib

!ifndef SETUP_INPUTS
    !error "SETUP_INPUTS_REQUIRED"
!endif
!include "${SETUP_INPUTS}"

Name "Synthetic uninstaller export/import test"
!ifdef EXPORT_UNINST
    OutFile "${SETUP_EXPORT_OUTPUT_FILE}"
!else
    OutFile "${SETUP_OUTPUT_FILE}"
!endif

Section "Synthetic install"
!ifdef IMPORT_UNINST
    File /oname=Uninstall.exe "${SETUP_UNINSTALLER_FILE}"
!else
    WriteUninstaller "Uninstall.exe"
!endif
SectionEnd

!ifndef IMPORT_UNINST
Section "Uninstall"
    SetErrorLevel 0
SectionEnd
!endif

!ifdef EXPORT_UNINST
    ; Keep the exact documented NSIS export pattern, but output only in WorkDirectory.
    !uninstfinalize 'echo copy /Y "%1" "Uninstall.exe"&copy /Y "%1" "Uninstall.exe" >nul'
!endif
