@echo off
setlocal
chcp 65001 >nul
set "SCRIPT_DIR=%~dp0"
set "BACKUP_ROOT=D:\GitHub-Backups"
set "OWNER=yangjing6213-dev"

if not exist D:\ (
  echo [FAIL] Drive D: was not found.
  pause
  exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Test-GitHubBackupScripts.ps1"
if errorlevel 1 (
  echo.
  echo [FAIL] Script syntax check failed. The backup was not started.
  pause
  exit /b 1
)

echo.
echo ==============================================
echo GitHub Daily Backup ^(release assets skipped^)
echo Account: %OWNER%
echo Output : %BACKUP_ROOT%
echo ==============================================
echo.

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Backup-GitHubAccount.ps1" -Owner "%OWNER%" -BackupRoot "%BACKUP_ROOT%" -SkipReleaseAssets
set "CODE=%ERRORLEVEL%"

echo.
if "%CODE%"=="0" (
  echo [PASS] Daily backup completed successfully.
) else if "%CODE%"=="2" (
  echo [PARTIAL] Core backup completed, but one or more optional items had warnings.
) else (
  echo [FAIL] Backup failed. Review the error above and the latest log, if created, under:
  echo %BACKUP_ROOT%\%OWNER%\logs
)

echo.
echo Summary folder:
echo %BACKUP_ROOT%\%OWNER%\manifests
pause
exit /b %CODE%
