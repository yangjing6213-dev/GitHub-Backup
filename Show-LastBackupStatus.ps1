[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$Owner = "yangjing6213-dev",

    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$BackupRoot = "D:\GitHub-Backups"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$manifestRoot = Join-Path (Join-Path $BackupRoot $Owner) "manifests"
$latest = Get-ChildItem -LiteralPath $manifestRoot -Filter "summary-*.json" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $latest) {
    Write-Host "[FAIL] No backup summary was found."
    exit 1
}

$summary = Get-Content -LiteralPath $latest.FullName -Raw | ConvertFrom-Json
$failedRepositories = @($summary.failedRepositories)

Write-Host ""
Write-Host "=============================================="
Write-Host "Latest GitHub Backup Status"
Write-Host "=============================================="
Write-Host ("Summary file : {0}" -f $latest.FullName)
Write-Host ("Status       : {0}" -f $summary.status)
Write-Host ("Owner        : {0}" -f $summary.owner)
Write-Host ("Repositories : {0}" -f $summary.repositoryCount)
Write-Host ("Warnings     : {0}" -f $summary.warningCount)
Write-Host ("Failed       : {0}" -f $failedRepositories.Count)
Write-Host ("Completed at : {0}" -f $summary.completedAt)
Write-Host ""

if ($failedRepositories.Count -gt 0) {
    Write-Host "Failed repositories:"
    foreach ($repository in $failedRepositories) {
        Write-Host ("  - {0}" -f $repository)
    }
    exit 1
}

if ($summary.status -eq "PASS") {
    Write-Host "[PASS] All repositories were backed up without warnings."
    exit 0
}

if ($summary.status -eq "PARTIAL") {
    Write-Host "[PASS] All core repositories were backed up. Optional warnings remain."
    exit 0
}

Write-Host "[FAIL] The latest backup did not complete successfully."
exit 1
