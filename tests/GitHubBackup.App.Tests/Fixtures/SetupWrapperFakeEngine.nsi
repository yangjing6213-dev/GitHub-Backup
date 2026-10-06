; Inert fake engine. It only writes fixture evidence and waits for the fixture's release flag.
Unicode true
RequestExecutionLevel user
SetCompressor zlib
!include "SetupInputs.nsh"
Name "Synthetic wrapper fake engine"
OutFile "${WRAPPER_ENGINE_FILE}"
SilentInstall silent

Section "Synthetic engine"
    ReadEnvStr $0 "DOTNET_BUNDLE_EXTRACT_BASE_DIR"
    WriteINIStr "${WRAPPER_FIXTURE_ROOT}\engine-result.ini" "Engine" "BundleBase" "$0"
    WriteINIStr "${WRAPPER_FIXTURE_ROOT}\engine-result.ini" "Engine" "Temp" "$TEMP"
    WriteINIStr "${WRAPPER_FIXTURE_ROOT}\engine-result.ini" "Engine" "CommandLine" "$CMDLINE"
    CreateDirectory "$0"
    FileOpen $1 "$0\synthetic-runtime.bin" w
    FileWrite $1 "synthetic extraction"
    FileClose $1
    FileOpen $1 "${WRAPPER_FIXTURE_ROOT}\engine-ready.flag" w
    FileWrite $1 "ready"
    FileClose $1
    StrCpy $2 0
engine_wait:
    IfFileExists "${WRAPPER_FIXTURE_ROOT}\release-engine.flag" engine_released
    IntOp $2 $2 + 1
    IntCmp $2 400 engine_timeout
    Sleep 25
    Goto engine_wait
engine_released:
    SetErrorLevel ${WRAPPER_ENGINE_EXIT}
    Goto engine_done
engine_timeout:
    SetErrorLevel 81
engine_done:
SectionEnd
