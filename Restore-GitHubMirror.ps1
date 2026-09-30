[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ })]
    [string]$MirrorPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https://github\.com/[^/]+/[^/]+(?:\.git)?$')]
    [string]$DestinationUrl,

    [Parameter(Mandatory = $false)]
    [switch]$IncludeLfs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $DestinationUrl.EndsWith(".git")) {
    $DestinationUrl += ".git"
}

Write-Warning "git push --mirror makes the destination refs match the backup and can delete existing refs. Use only with a new empty repository."

if ($PSCmdlet.ShouldProcess($DestinationUrl, "Run git push --mirror from $MirrorPath")) {
    & git -C $MirrorPath fsck --full --no-dangling
    if ($LASTEXITCODE -ne 0) {
        throw "Backup mirror integrity check failed. Restore was stopped."
    }

    & git -C $MirrorPath push --mirror $DestinationUrl
    if ($LASTEXITCODE -ne 0) {
        throw "Git mirror restore failed."
    }

    if ($IncludeLfs) {
        & git -C $MirrorPath lfs push --all $DestinationUrl
        if ($LASTEXITCODE -ne 0) {
            throw "Git history was restored, but Git LFS object restore failed."
        }
    }

    Write-Host "Restore completed: $DestinationUrl"
}
