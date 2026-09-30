@echo off
setlocal
chcp 65001 >nul
set "SCRIPT_DIR=%~dp0"
set "FAILED=0"

echo ==============================================
echo GitHub Backup Environment and Script Check
echo ==============================================
echo.

if not exist D:\ (
  echo [FAIL] Drive D: was not found.
  set "FAILED=1"
) else (
  echo [PASS] Drive D: is available.
)
echo.

echo [1/5] Checking Git...
where git >nul 2>&1
if errorlevel 1 (
  echo [FAIL] Git was not found.
  set "FAILED=1"
) else (
  git --version
)
echo.

echo [2/5] Checking GitHub CLI...
where gh >nul 2>&1
if errorlevel 1 (
  echo [FAIL] GitHub CLI ^(gh^) was not found.
  set "FAILED=1"
) else (
  gh --version
)
echo.

echo [3/5] Checking GitHub login...
where gh >nul 2>&1
if errorlevel 1 (
  echo [SKIP] GitHub CLI is not installed.
  set "FAILED=1"
) else (
  gh auth status --hostname github.com
  if errorlevel 1 set "FAILED=1"
)
echo.

echo [4/5] Checking Git LFS...
where git >nul 2>&1
if errorlevel 1 (
  echo [SKIP] Git is not installed.
  set "FAILED=1"
) else (
  git lfs version
  if errorlevel 1 set "FAILED=1"
)
echo.

echo [5/5] Checking PowerShell script syntax...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Test-GitHubBackupScripts.ps1"
if errorlevel 1 set "FAILED=1"
echo.

if "%FAILED%"=="0" (
  echo [PASS] Environment and scripts are ready.
) else (
  echo [FAIL] One or more checks failed. Do not run the backup yet.
)
echo.
pause
exit /b %FAILED%
