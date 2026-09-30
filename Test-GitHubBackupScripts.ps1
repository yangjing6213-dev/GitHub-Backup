[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$hasErrors = $false
$paths = @(
    (Join-Path $PSScriptRoot "Backup-GitHubAccount.ps1"),
    (Join-Path $PSScriptRoot "Restore-GitHubMirror.ps1"),
    (Join-Path $PSScriptRoot "Invoke-OneClickGitHubBackup.ps1"),
    (Join-Path $PSScriptRoot "Restore-CodexGitSettings.ps1"),
    (Join-Path $PSScriptRoot "Show-LastBackupStatus.ps1")
)

foreach ($path in $paths) {
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host "[FAIL] Script file was not found: $path"
        $hasErrors = $true
        continue
    }

    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors) | Out-Null

    if (@($parseErrors).Count -gt 0) {
        Write-Host "[FAIL] PowerShell syntax check failed: $path"
        foreach ($parseError in @($parseErrors)) {
            $message = "       Line {0}, column {1}: {2}" -f $parseError.Extent.StartLineNumber, $parseError.Extent.StartColumnNumber, $parseError.Message
            Write-Host $message
        }
        $hasErrors = $true
    }
    else {
        Write-Host "[PASS] PowerShell syntax is valid: $path"
    }

    $bytes = [System.IO.File]::ReadAllBytes($path)
    if (@($bytes | Where-Object { $_ -gt 127 }).Count -gt 0) {
        Write-Host "[FAIL] PowerShell source is not ASCII-safe: $path"
        $hasErrors = $true
    }
}

try {
    $sampleJson = '[{"name":"repo-one"},{"name":"repo-two"}]'
    $parsed = $sampleJson | ConvertFrom-Json
    $items = @()

    if ($parsed -is [System.Array]) {
        $items = @($parsed | ForEach-Object { $_ })
    }
    elseif ($null -ne $parsed) {
        $items = @($parsed)
    }

    if ($items.Count -ne 2 -or $items[0].name -ne "repo-one" -or $items[1].name -ne "repo-two") {
        throw "Expected two separate JSON objects."
    }

    Write-Host "[PASS] JSON list normalization test passed."
}
catch {
    Write-Host "[FAIL] JSON list normalization test failed: $($_.Exception.Message)"
    $hasErrors = $true
}

try {
    $backupText = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Backup-GitHubAccount.ps1") -Raw
    $launcherText = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Invoke-OneClickGitHubBackup.ps1") -Raw

    if ($backupText -notmatch 'GitTransport' -or
        $backupText -notmatch 'Ssh443' -or
        $backupText -notmatch 'url\.ssh://git@ssh\.github\.com:443/\.insteadOf=https://github\.com/') {
        throw "Process-scoped SSH 443 transport support is missing."
    }

    if ($launcherText -notmatch 'finally\s*\{' -or
        $launcherText -notmatch 'Stop-TemporarySshAgent' -or
        $launcherText -notmatch 'Restore-CodexGitSettings\.ps1' -or
        $launcherText -notmatch 'GitTransport' -or
        $launcherText -notmatch 'Ssh443') {
        throw "One-click cleanup or transport invocation is missing."
    }

    if ($launcherText -match 'git\s+config\s+--global') {
        throw "The one-click launcher must not change global Git settings."
    }

    Write-Host "[PASS] One-click isolated transport and cleanup checks passed."
}
catch {
    Write-Host "[FAIL] One-click transport test failed: $($_.Exception.Message)"
    $hasErrors = $true
}

if ($hasErrors) {
    exit 1
}

exit 0
