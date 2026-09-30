[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("Full", "Daily")]
    [string]$Mode = "Full",

    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$Owner = "yangjing6213-dev",

    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$BackupRoot = "D:\GitHub-Backups",

    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$KeyPath = "$HOME\.ssh\enhe-ai-github-ed25519"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:AgentStartedByTool = $false
$script:AgentExe = $null
$script:OriginalSshAuthSock = [Environment]::GetEnvironmentVariable("SSH_AUTH_SOCK", "Process")
$script:OriginalSshAgentPid = [Environment]::GetEnvironmentVariable("SSH_AGENT_PID", "Process")
$restoreSettingsScript = Join-Path $PSScriptRoot "Restore-CodexGitSettings.ps1"

function Write-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [ValidateSet("INFO", "PASS", "WARN", "FAIL")][string]$Level = "INFO"
    )

    Write-Host ("[{0}] {1}" -f $Level, $Message)
}

function Assert-Command {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command was not found: $Name"
    }
}

function Invoke-NativeInteractive {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        & $FilePath @Arguments
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

function Invoke-NativeCapture {
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

function Resolve-GitForWindowsTool {
    param([Parameter(Mandatory = $true)][string]$ToolName)

    $gitExe = (Get-Command "git" -ErrorAction Stop).Source
    $gitBinDir = Split-Path -Parent $gitExe
    $gitRoot = Split-Path -Parent $gitBinDir
    $bundledTool = Join-Path $gitRoot ("usr\bin\{0}.exe" -f $ToolName)

    if (Test-Path -LiteralPath $bundledTool) {
        return $bundledTool
    }

    $command = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    throw "Required SSH tool was not found: $ToolName"
}

function Start-TemporarySshAgent {
    $script:AgentExe = Resolve-GitForWindowsTool -ToolName "ssh-agent"
    $agentResult = Invoke-NativeCapture -FilePath $script:AgentExe -Arguments @("-s")

    if ($agentResult.ExitCode -ne 0) {
        throw "The temporary SSH agent could not be started."
    }

    $agentText = $agentResult.Output -join "`n"
    $sockMatch = [regex]::Match($agentText, "SSH_AUTH_SOCK=([^;]+);")
    $pidMatch = [regex]::Match($agentText, "SSH_AGENT_PID=([0-9]+);")

    if (-not $sockMatch.Success -or -not $pidMatch.Success) {
        throw "The temporary SSH agent returned an unexpected response."
    }

    [Environment]::SetEnvironmentVariable("SSH_AUTH_SOCK", $sockMatch.Groups[1].Value, "Process")
    [Environment]::SetEnvironmentVariable("SSH_AGENT_PID", $pidMatch.Groups[1].Value, "Process")
    $script:AgentStartedByTool = $true

    Write-Step -Level "PASS" -Message "Temporary SSH agent started."
}

function Stop-TemporarySshAgent {
    if ($script:AgentStartedByTool -and $null -ne $script:AgentExe) {
        try {
            $null = Invoke-NativeCapture -FilePath $script:AgentExe -Arguments @("-k")
            Write-Step -Level "PASS" -Message "Temporary SSH agent stopped."
        }
        catch {
            Write-Step -Level "WARN" -Message "Temporary SSH agent cleanup reported an error."
        }
    }

    [Environment]::SetEnvironmentVariable("SSH_AUTH_SOCK", $script:OriginalSshAuthSock, "Process")
    [Environment]::SetEnvironmentVariable("SSH_AGENT_PID", $script:OriginalSshAgentPid, "Process")
}

function Add-PrivateKeyToAgent {
    param([Parameter(Mandatory = $true)][string]$PrivateKeyPath)

    if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) {
        throw "GitHub private key was not found: $PrivateKeyPath"
    }

    $addExe = Resolve-GitForWindowsTool -ToolName "ssh-add"
    $resolvedKey = (Resolve-Path -LiteralPath $PrivateKeyPath).Path -replace "\\", "/"

    Write-Host ""
    Write-Step -Message "Enter the SSH private key passphrase once when prompted."
    $addCode = Invoke-NativeInteractive -FilePath $addExe -Arguments @($resolvedKey)
    if ($addCode -ne 0) {
        throw "The SSH private key could not be added to the temporary agent."
    }

    $listResult = Invoke-NativeCapture -FilePath $addExe -Arguments @("-l")
    if ($listResult.ExitCode -ne 0 -or $listResult.Output.Count -eq 0) {
        throw "The temporary SSH agent does not contain a usable key."
    }

    Write-Step -Level "PASS" -Message "SSH private key is available to Git."
}

function Test-Ssh443RepositoryAccess {
    param([Parameter(Mandatory = $true)][string]$RepositoryUrl)

    $transportConfig = "url.ssh://git@ssh.github.com:443/.insteadOf=https://github.com/"
    $result = Invoke-NativeCapture -FilePath "git" -Arguments @(
        "-c", $transportConfig,
        "ls-remote", $RepositoryUrl, "HEAD"
    )

    foreach ($line in $result.Output) {
        if ($null -ne $line -and "$line".Length -gt 0) {
            Write-Host $line
        }
    }

    if ($result.ExitCode -ne 0) {
        throw "SSH 443 repository access test failed. Backup was not started."
    }

    Write-Step -Level "PASS" -Message "SSH 443 repository access test passed."
}

function Get-TestRepositoryUrl {
    param([Parameter(Mandatory = $true)][string]$RepositoryOwner)

    $repoResult = Invoke-NativeCapture -FilePath "gh" -Arguments @(
        "repo", "list", $RepositoryOwner,
        "--limit", "10000",
        "--json", "name,url,diskUsage"
    )

    if ($repoResult.ExitCode -ne 0) {
        throw "GitHub repository list could not be read for the preflight test."
    }

    $json = $repoResult.Output -join [Environment]::NewLine
    $parsed = $json | ConvertFrom-Json
    $repositories = @()

    if ($parsed -is [System.Array]) {
        $repositories = @($parsed | ForEach-Object { $_ })
    }
    elseif ($null -ne $parsed) {
        $repositories = @($parsed)
    }

    if ($repositories.Count -eq 0) {
        throw "No GitHub repositories were available for the preflight test."
    }

    $testRepository = $repositories |
        Where-Object { [int64]$_.diskUsage -gt 0 } |
        Select-Object -First 1

    if ($null -eq $testRepository) {
        $testRepository = $repositories | Select-Object -First 1
    }

    return ([string]$testRepository.url) + ".git"
}

function Show-LatestSummary {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RepositoryOwner
    )

    $summaryFolder = Join-Path (Join-Path $Root $RepositoryOwner) "manifests"
    $latest = Get-ChildItem -LiteralPath $summaryFolder -Filter "summary-*.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $latest) {
        Write-Step -Level "WARN" -Message "No summary file was found."
        return
    }

    $summary = Get-Content -LiteralPath $latest.FullName -Raw | ConvertFrom-Json
    $failedCount = @($summary.failedRepositories).Count

    Write-Host ""
    Write-Host "Latest backup summary:"
    Write-Host ("  File         : {0}" -f $latest.FullName)
    Write-Host ("  Status       : {0}" -f $summary.status)
    Write-Host ("  Repositories : {0}" -f $summary.repositoryCount)
    Write-Host ("  Warnings     : {0}" -f $summary.warningCount)
    Write-Host ("  Failed       : {0}" -f $failedCount)
}

$exitCode = 1

try {
    Write-Host ""
    Write-Host "=============================================="
    Write-Host "One-Click GitHub Backup"
    Write-Host ("Mode   : {0}" -f $Mode)
    Write-Host ("Owner  : {0}" -f $Owner)
    Write-Host ("Output : {0}" -f $BackupRoot)
    Write-Host "=============================================="
    Write-Host ""

    Assert-Command "git"
    Assert-Command "gh"
    Assert-Command "powershell.exe"

    if (-not (Test-Path -LiteralPath "D:\")) {
        throw "Drive D: was not found."
    }

    $testScript = Join-Path $PSScriptRoot "Test-GitHubBackupScripts.ps1"
    $backupScript = Join-Path $PSScriptRoot "Backup-GitHubAccount.ps1"

    if (-not (Test-Path -LiteralPath $testScript -PathType Leaf)) {
        throw "Self-test script was not found: $testScript"
    }
    if (-not (Test-Path -LiteralPath $backupScript -PathType Leaf)) {
        throw "Backup script was not found: $backupScript"
    }
    if (-not (Test-Path -LiteralPath $restoreSettingsScript -PathType Leaf)) {
        throw "Git settings restore script was not found: $restoreSettingsScript"
    }

    $selfTestCode = Invoke-NativeInteractive -FilePath "powershell.exe" -Arguments @(
        "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", $testScript
    )
    if ($selfTestCode -ne 0) {
        throw "Tool self-test failed. Backup was not started."
    }

    $authCode = Invoke-NativeInteractive -FilePath "gh" -Arguments @(
        "auth", "status", "--hostname", "github.com"
    )
    if ($authCode -ne 0) {
        throw "GitHub CLI login is invalid. Run: gh auth login"
    }

    $lfsCode = Invoke-NativeInteractive -FilePath "git" -Arguments @("lfs", "version")
    if ($lfsCode -ne 0) {
        throw "Git LFS was not found."
    }

    Start-TemporarySshAgent
    Add-PrivateKeyToAgent -PrivateKeyPath $KeyPath

    $testRepositoryUrl = Get-TestRepositoryUrl -RepositoryOwner $Owner
    Write-Step -Message ("Testing repository access: {0}" -f $testRepositoryUrl)
    Test-Ssh443RepositoryAccess -RepositoryUrl $testRepositoryUrl

    $backupArguments = @(
        "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", $backupScript,
        "-Owner", $Owner,
        "-BackupRoot", $BackupRoot,
        "-GitTransport", "Ssh443"
    )

    if ($Mode -eq "Daily") {
        $backupArguments += "-SkipReleaseAssets"
    }

    Write-Host ""
    Write-Step -Message "Starting GitHub backup with process-scoped SSH 443 transport."
    $exitCode = Invoke-NativeInteractive -FilePath "powershell.exe" -Arguments $backupArguments

    Show-LatestSummary -Root $BackupRoot -RepositoryOwner $Owner

    if ($exitCode -eq 0) {
        Write-Step -Level "PASS" -Message "Backup completed successfully."
    }
    elseif ($exitCode -eq 2) {
        Write-Step -Level "WARN" -Message "Core backup completed with optional warnings."
    }
    else {
        Write-Step -Level "FAIL" -Message "Backup failed. Review the latest backup log."
    }
}
catch {
    Write-Host ""
    Write-Step -Level "FAIL" -Message $_.Exception.Message
    $exitCode = 1
}
finally {
    Stop-TemporarySshAgent
    Write-Step -Level "PASS" -Message "Temporary backup transport settings were cleared."

    try {
        if (-not (Get-Command "powershell.exe" -ErrorAction SilentlyContinue)) {
            throw "powershell.exe was not found for Git settings restoration."
        }

        if (Test-Path -LiteralPath $restoreSettingsScript -PathType Leaf) {
            $restoreCode = Invoke-NativeInteractive -FilePath "powershell.exe" -Arguments @(
                "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-File", $restoreSettingsScript,
                "-BackupRoot", $BackupRoot
            )

            if ($restoreCode -ne 0) {
                throw "Legacy global Git settings could not be restored."
            }

            Write-Step -Level "PASS" -Message "Codex Git settings were restored after the backup run."
        }
        else {
            throw "Git settings restore script was not found."
        }
    }
    catch {
        Write-Step -Level "FAIL" -Message $_.Exception.Message
        $exitCode = 1
    }
}

exit $exitCode
