[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$BackupRoot = "D:\GitHub-Backups"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-NativeSafe {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    [pscustomobject]@{
        ExitCode = $exitCode
        Output   = @($output)
    }
}

if (-not (Get-Command "git" -ErrorAction SilentlyContinue)) {
    throw "Git was not found."
}
if (-not (Get-Command "gh" -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI was not found."
}

$snapshotRoot = Join-Path $BackupRoot "settings-snapshots"
New-Item -ItemType Directory -Path $snapshotRoot -Force | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$snapshotPath = Join-Path $snapshotRoot ("git-global-before-restore-{0}.txt" -f $stamp)

$configResult = Invoke-NativeSafe -FilePath "git" -Arguments @(
    "config", "--global", "--list", "--show-origin"
)
$configResult.Output | Set-Content -LiteralPath $snapshotPath -Encoding UTF8

$homeGitConfig = Join-Path $HOME ".gitconfig"
if (Test-Path -LiteralPath $homeGitConfig -PathType Leaf) {
    Copy-Item -LiteralPath $homeGitConfig -Destination (Join-Path $snapshotRoot ("dot-gitconfig-{0}.bak" -f $stamp)) -Force
}

$xdgGitConfig = Join-Path $HOME ".config\git\config"
if (Test-Path -LiteralPath $xdgGitConfig -PathType Leaf) {
    Copy-Item -LiteralPath $xdgGitConfig -Destination (Join-Path $snapshotRoot ("xdg-git-config-{0}.bak" -f $stamp)) -Force
}

$rewriteKey = "url.ssh://git@ssh.github.com:443/.insteadOf"
$rewriteResult = Invoke-NativeSafe -FilePath "git" -Arguments @(
    "config", "--global", "--unset-all", $rewriteKey
)
if ($rewriteResult.ExitCode -ne 0 -and $rewriteResult.ExitCode -ne 5) {
    throw "The legacy GitHub URL rewrite could not be removed."
}

$httpResult = Invoke-NativeSafe -FilePath "git" -Arguments @(
    "config", "--global", "--unset-all", "http.version", "^HTTP/1\.1$"
)
if ($httpResult.ExitCode -ne 0 -and $httpResult.ExitCode -ne 5) {
    throw "The legacy HTTP version setting could not be removed."
}

$setupResult = Invoke-NativeSafe -FilePath "gh" -Arguments @(
    "auth", "setup-git", "--hostname", "github.com"
)
if ($setupResult.ExitCode -ne 0) {
    throw "GitHub CLI could not restore its Git credential helper."
}

$remainingRewrite = Invoke-NativeSafe -FilePath "git" -Arguments @(
    "config", "--global", "--get-all", $rewriteKey
)

$remainingHttp = Invoke-NativeSafe -FilePath "git" -Arguments @(
    "config", "--global", "--get-all", "http.version"
)

Write-Host ""
Write-Host "[PASS] Legacy backup-specific global Git settings were removed."
Write-Host "[PASS] GitHub CLI credential helper was refreshed."
Write-Host ("[INFO] Settings snapshot: {0}" -f $snapshotPath)
Write-Host "[INFO] SSH keys and SSH config files were not deleted."

if ($remainingRewrite.ExitCode -eq 0 -and $remainingRewrite.Output.Count -gt 0) {
    Write-Host "[WARN] Another GitHub URL rewrite still exists in global Git config."
}
if ($remainingHttp.ExitCode -eq 0 -and $remainingHttp.Output.Count -gt 0) {
    Write-Host "[INFO] A non-targeted http.version value still exists and was preserved."
}

exit 0
