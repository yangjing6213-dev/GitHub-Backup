@echo off
setlocal
set "BACKUP_ROOT=D:\GitHub-Backups\yangjing6213-dev"
if not exist "%BACKUP_ROOT%" (
  echo Backup folder does not exist yet:
  echo %BACKUP_ROOT%
  echo Run 02-FULL-BACKUP.cmd first.
  pause
  exit /b 1
)
explorer.exe "%BACKUP_ROOT%"
endlocal
