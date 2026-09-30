@echo off
setlocal
chcp 65001 >nul
set "SCRIPT_DIR=%~dp0"

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Invoke-OneClickGitHubBackup.ps1" -Mode Daily
set "CODE=%ERRORLEVEL%"

echo.
if "%CODE%"=="0" (
  echo [PASS] One-click daily backup completed successfully.
) else if "%CODE%"=="2" (
  echo [PARTIAL] Core backup completed with optional warnings.
) else (
  echo [FAIL] One-click daily backup failed.
)

echo.
pause
exit /b %CODE%
