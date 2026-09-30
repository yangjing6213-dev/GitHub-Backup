@echo off
setlocal
chcp 65001 >nul
set "SCRIPT_DIR=%~dp0"

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Restore-CodexGitSettings.ps1"
set "CODE=%ERRORLEVEL%"

echo.
pause
exit /b %CODE%
