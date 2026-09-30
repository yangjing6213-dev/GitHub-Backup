# Internal single-EXE publish

From the repository worktree, publish with the existing locked restore inputs:

```powershell
Remove-Item Env:\GITHUB_BACKUP_LIVE_ROOT -ErrorAction SilentlyContinue
if (Test-Path Env:\GITHUB_BACKUP_LIVE_ROOT) { throw 'LIVE_ROOT_NOT_ISOLATED' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
dotnet publish src/GitHubBackup.App/GitHubBackup.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o '<fresh internal output directory>'
pwsh -NoProfile -File publish/Verify-SingleExe.ps1 -Directory '<same output directory>'
```

Check the exact output directory and ancestors before publishing. Use `D:\GitHub-Backup-Tool\artifacts\publish\internal` only if it does not contain prior files; otherwise choose a fresh versioned sibling. Never clear or overwrite an existing artifact. The verifier requires exactly one regular non-reparse `GitHubBackup.exe`, checks AMD64/PE32+/Windows GUI headers, SHA-256, size, unsigned signature status, and absence of seven known synthetic canaries or legacy source filenames as ASCII or UTF-16LE byte patterns at any file offset. This bounded scan does not prove that no other credential or source bytes could be embedded.

The output targets Windows x64 and carries its .NET runtime. Git, `gh` and Git LFS remain separately detected dependencies; installing any missing tool requires user consent. This is a local unsigned internal test artifact. Startup/normal exit, visual UI, clean-machine portability, real login/backup, and signing are NOT_RUN until separately authorized and checked; this file does not claim public-release acceptance.
