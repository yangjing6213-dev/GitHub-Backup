[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$Owner = "yangjing6213-dev",

    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$BackupRoot = "D:\GitHub-Backups",

    [Parameter(Mandatory = $false)]
    [switch]$SkipReleaseAssets,

    [Parameter(Mandatory = $false)]
    [switch]$SkipMetadata,

    [Parameter(Mandatory = $false)]
    [ValidateSet("Default", "Ssh443")]
    [string]$GitTransport = "Default"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Keep native command input/output in UTF-8 on Windows PowerShell 5.1.
try {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [Console]::InputEncoding = $utf8NoBom
    [Console]::OutputEncoding = $utf8NoBom
    $global:OutputEncoding = $utf8NoBom
    $chcp = Join-Path $env:SystemRoot "System32\chcp.com"
    if (Test-Path -LiteralPath $chcp) {
        & $chcp 65001 | Out-Null
    }
}
catch {
    # Encoding setup is best effort. The script source itself is ASCII-safe.
}

$ownerRoot    = Join-Path $BackupRoot $Owner
$mirrorRoot   = Join-Path $ownerRoot "mirrors"
$wikiRoot     = Join-Path $ownerRoot "wikis"
$releaseRoot  = Join-Path $ownerRoot "releases"
$metadataRoot = Join-Path $ownerRoot "metadata"
$manifestRoot = Join-Path $ownerRoot "manifests"
$logRoot      = Join-Path $ownerRoot "logs"

@($ownerRoot, $mirrorRoot, $wikiRoot, $releaseRoot, $metadataRoot, $manifestRoot, $logRoot) |
    ForEach-Object { New-Item -ItemType Directory -Path $_ -Force | Out-Null }

$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$logPath = Join-Path $logRoot "backup-$runId.log"
$script:WarningCount = 0
$script:FailedRepositories = New-Object 'System.Collections.Generic.List[string]'

function Write-Log {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR")][string]$Level = "INFO"
    )

    $line = "{0} [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    Write-Host $line
}

function Assert-Command {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command was not found: $Name. Install it and make sure it is in PATH."
    }
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $effectiveArguments = @($Arguments)
    if ($FilePath -eq "git" -and $GitTransport -eq "Ssh443") {
        $transportConfig = "url.ssh://git@ssh.github.com:443/.insteadOf=https://github.com/"
        $effectiveArguments = @("-c", $transportConfig) + $effectiveArguments
    }

    Write-Log ("RUN: {0} {1}" -f $FilePath, ($effectiveArguments -join " "))

    # Windows PowerShell 5.1 can treat normal native stderr output as a
    # terminating error when ErrorActionPreference is Stop. Git writes normal
    # progress messages (for example, "Cloning into...") to stderr, so use the
    # process exit code as the source of truth for native commands.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & $FilePath @effectiveArguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    foreach ($line in @($output)) {
        if ($null -ne $line -and "$line".Length -gt 0) {
            Add-Content -LiteralPath $logPath -Value ("    " + $line) -Encoding UTF8
        }
    }

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "Command failed (ExitCode=$exitCode): $FilePath $($effectiveArguments -join ' ')"
    }

    [pscustomobject]@{
        ExitCode = $exitCode
        Output   = @($output)
    }
}

function Save-GitHubApiJson {
    param(
        [Parameter(Mandatory = $true)][string]$Endpoint,
        [Parameter(Mandatory = $true)][string]$Destination,
        [switch]$Paginate
    )

    $arguments = @("api", $Endpoint)
    if ($Paginate) {
        $arguments += @("--paginate", "--slurp")
    }

    $result = Invoke-NativeCommand -FilePath "gh" -Arguments $arguments -AllowFailure
    if ($result.ExitCode -ne 0) {
        $script:WarningCount++
        Write-Log "Metadata export failed: $Endpoint" "WARN"
        return
    }

    ($result.Output -join [Environment]::NewLine) |
        Set-Content -LiteralPath $Destination -Encoding UTF8
}

function ConvertTo-SafeFileName {
    param([Parameter(Mandatory = $true)][string]$Value)
    return ($Value -replace '[<>:"/\\|?*]', '_')
}

function ConvertFrom-JsonList {
    param([Parameter(Mandatory = $true)][string]$Json)

    # Windows PowerShell 5.1 may emit a top-level JSON array as one array
    # object. Flatten exactly one top-level array so each repository or release
    # becomes one pipeline item.
    $parsed = $Json | ConvertFrom-Json
    if ($null -eq $parsed) {
        return
    }

    if ($parsed -is [System.Array]) {
        foreach ($item in $parsed) {
            $item
        }
        return
    }

    $parsed
}

try {
    Assert-Command "git"
    Assert-Command "gh"

    Write-Log "Git transport mode: $GitTransport"

    $auth = Invoke-NativeCommand -FilePath "gh" -Arguments @("auth", "status", "--hostname", "github.com") -AllowFailure
    if ($auth.ExitCode -ne 0) {
        throw "GitHub CLI is not logged in, or the login is invalid. Run: gh auth login"
    }

    $lfsProbe = Invoke-NativeCommand -FilePath "git" -Arguments @("lfs", "version") -AllowFailure
    $gitLfsAvailable = ($lfsProbe.ExitCode -eq 0)
    if (-not $gitLfsAvailable) {
        $script:WarningCount++
        Write-Log "Git LFS was not detected. Normal Git history will be backed up, but LFS objects will be incomplete." "WARN"
    }

    Write-Log "Reading all repositories owned by $Owner, including private, archived, and forked repositories."
    $repoListResult = Invoke-NativeCommand -FilePath "gh" -Arguments @(
        "repo", "list", $Owner,
        "--limit", "10000",
        "--json", "name,nameWithOwner,url,isPrivate,isArchived,isFork,hasWikiEnabled,updatedAt,diskUsage"
    )

    $repoListJson = $repoListResult.Output -join [Environment]::NewLine
    $manifestPath = Join-Path $manifestRoot "repositories-$runId.json"
    $repoListJson | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $repositories = @(ConvertFrom-JsonList -Json $repoListJson)

    if ($repositories.Count -eq 0) {
        throw "No repositories were returned for owner: $Owner"
    }

    foreach ($candidate in $repositories) {
        if ($candidate -is [System.Array]) {
            throw "Repository list parsing returned a nested array. Backup stopped before cloning."
        }
        if ([string]::IsNullOrWhiteSpace([string]$candidate.name) -or
            [string]::IsNullOrWhiteSpace([string]$candidate.nameWithOwner) -or
            [string]::IsNullOrWhiteSpace([string]$candidate.url)) {
            throw "Repository list contains an invalid item. Backup stopped before cloning."
        }
    }

    Write-Log "Repository count: $($repositories.Count)"

    foreach ($repo in $repositories) {
        $fullName = [string]$repo.nameWithOwner
        $repoName = [string]$repo.name
        $cloneUrl = ([string]$repo.url) + ".git"
        $mirrorPath = Join-Path $mirrorRoot ($repoName + ".git")

        Write-Log "Starting repository backup: $fullName"

        try {
            if (Test-Path -LiteralPath $mirrorPath) {
                Invoke-NativeCommand -FilePath "git" -Arguments @(
                    "-C", $mirrorPath, "remote", "set-url", "origin", $cloneUrl
                ) | Out-Null
                Invoke-NativeCommand -FilePath "git" -Arguments @(
                    "-C", $mirrorPath, "remote", "update", "--prune"
                ) | Out-Null
            }
            else {
                Invoke-NativeCommand -FilePath "git" -Arguments @(
                    "clone", "--mirror", $cloneUrl, $mirrorPath
                ) | Out-Null
            }

            if ($gitLfsAvailable) {
                $lfsResult = Invoke-NativeCommand -FilePath "git" -Arguments @(
                    "-C", $mirrorPath, "lfs", "fetch", "--all"
                ) -AllowFailure

                if ($lfsResult.ExitCode -ne 0) {
                    $script:WarningCount++
                    Write-Log "Git LFS object backup failed: $fullName" "WARN"
                }
            }

            Invoke-NativeCommand -FilePath "git" -Arguments @(
                "-C", $mirrorPath, "fsck", "--full", "--no-dangling"
            ) | Out-Null

            if ([bool]$repo.hasWikiEnabled) {
                $wikiUrl = ([string]$repo.url) + ".wiki.git"
                $wikiPath = Join-Path $wikiRoot ($repoName + ".wiki.git")

                if (Test-Path -LiteralPath $wikiPath) {
                    $wikiResult = Invoke-NativeCommand -FilePath "git" -Arguments @(
                        "-C", $wikiPath, "remote", "set-url", "origin", $wikiUrl
                    ) -AllowFailure
                    if ($wikiResult.ExitCode -eq 0) {
                        $wikiResult = Invoke-NativeCommand -FilePath "git" -Arguments @(
                            "-C", $wikiPath, "remote", "update", "--prune"
                        ) -AllowFailure
                    }
                }
                else {
                    $wikiResult = Invoke-NativeCommand -FilePath "git" -Arguments @(
                        "clone", "--mirror", $wikiUrl, $wikiPath
                    ) -AllowFailure
                }

                if ($wikiResult.ExitCode -ne 0) {
                    $script:WarningCount++
                    Write-Log "Wiki backup failed. The repository may have Wiki enabled without any Wiki pages: $fullName" "WARN"
                }
            }

            if (-not $SkipMetadata) {
                $repoMetadataPath = Join-Path $metadataRoot $repoName
                New-Item -ItemType Directory -Path $repoMetadataPath -Force | Out-Null

                $repo | ConvertTo-Json -Depth 20 |
                    Set-Content -LiteralPath (Join-Path $repoMetadataPath "repository-summary.json") -Encoding UTF8

                Save-GitHubApiJson -Endpoint "repos/$fullName" -Destination (Join-Path $repoMetadataPath "repository.json")
                Save-GitHubApiJson -Endpoint "repos/$fullName/issues?state=all&per_page=100" -Destination (Join-Path $repoMetadataPath "issues.pages.json") -Paginate
                Save-GitHubApiJson -Endpoint "repos/$fullName/pulls?state=all&per_page=100" -Destination (Join-Path $repoMetadataPath "pull-requests.pages.json") -Paginate
                Save-GitHubApiJson -Endpoint "repos/$fullName/issues/comments?per_page=100" -Destination (Join-Path $repoMetadataPath "issue-comments.pages.json") -Paginate
                Save-GitHubApiJson -Endpoint "repos/$fullName/pulls/comments?per_page=100" -Destination (Join-Path $repoMetadataPath "review-comments.pages.json") -Paginate
                Save-GitHubApiJson -Endpoint "repos/$fullName/releases?per_page=100" -Destination (Join-Path $repoMetadataPath "releases.pages.json") -Paginate
                Save-GitHubApiJson -Endpoint "repos/$fullName/labels?per_page=100" -Destination (Join-Path $repoMetadataPath "labels.pages.json") -Paginate
                Save-GitHubApiJson -Endpoint "repos/$fullName/milestones?state=all&per_page=100" -Destination (Join-Path $repoMetadataPath "milestones.pages.json") -Paginate
                Save-GitHubApiJson -Endpoint "repos/$fullName/actions/workflows?per_page=100" -Destination (Join-Path $repoMetadataPath "workflows.pages.json") -Paginate
            }

            if (-not $SkipReleaseAssets) {
                $releaseListResult = Invoke-NativeCommand -FilePath "gh" -Arguments @(
                    "release", "list", "--repo", $fullName, "--limit", "1000",
                    "--json", "tagName,name,isDraft,isPrerelease,publishedAt"
                ) -AllowFailure

                if ($releaseListResult.ExitCode -ne 0) {
                    $script:WarningCount++
                    Write-Log "Release list could not be read: $fullName" "WARN"
                }
                else {
                    $releaseListJson = $releaseListResult.Output -join [Environment]::NewLine
                    $releaseRepoPath = Join-Path $releaseRoot $repoName
                    New-Item -ItemType Directory -Path $releaseRepoPath -Force | Out-Null
                    $releaseListJson |
                        Set-Content -LiteralPath (Join-Path $releaseRepoPath "releases.json") -Encoding UTF8

                    $releases = @(ConvertFrom-JsonList -Json $releaseListJson)
                    foreach ($release in $releases) {
                        $tagName = [string]$release.tagName
                        if ([string]::IsNullOrWhiteSpace($tagName)) {
                            continue
                        }

                        $safeTag = ConvertTo-SafeFileName $tagName
                        $releasePath = Join-Path $releaseRepoPath $safeTag
                        New-Item -ItemType Directory -Path $releasePath -Force | Out-Null

                        $releaseViewResult = Invoke-NativeCommand -FilePath "gh" -Arguments @(
                            "release", "view", $tagName, "--repo", $fullName,
                            "--json", "tagName,name,isDraft,isPrerelease,publishedAt,assets"
                        ) -AllowFailure

                        if ($releaseViewResult.ExitCode -ne 0) {
                            $script:WarningCount++
                            Write-Log "Release details could not be read: $fullName / $tagName" "WARN"
                            continue
                        }

                        $releaseViewJson = $releaseViewResult.Output -join [Environment]::NewLine
                        $releaseViewJson |
                            Set-Content -LiteralPath (Join-Path $releasePath "release.json") -Encoding UTF8

                        $releaseDetails = $releaseViewJson | ConvertFrom-Json
                        if (@($releaseDetails.assets).Count -gt 0) {
                            $downloadResult = Invoke-NativeCommand -FilePath "gh" -Arguments @(
                                "release", "download", $tagName, "--repo", $fullName,
                                "--dir", $releasePath, "--clobber"
                            ) -AllowFailure

                            if ($downloadResult.ExitCode -ne 0) {
                                $script:WarningCount++
                                Write-Log "Release asset download failed: $fullName / $tagName" "WARN"
                            }
                        }
                    }
                }
            }

            Write-Log "Repository backup completed: $fullName"
        }
        catch {
            $script:FailedRepositories.Add($fullName)
            Write-Log "Repository backup failed: $fullName; $($_.Exception.Message)" "ERROR"
        }
    }

    $status = if ($script:FailedRepositories.Count -gt 0) {
        "FAIL"
    }
    elseif ($script:WarningCount -gt 0) {
        "PARTIAL"
    }
    else {
        "PASS"
    }

    $summary = [pscustomobject]@{
        status             = $status
        owner              = $Owner
        startedRunId       = $runId
        completedAt        = (Get-Date).ToString("o")
        repositoryCount    = $repositories.Count
        warningCount       = $script:WarningCount
        failedRepositories = @($script:FailedRepositories)
        backupRoot         = $ownerRoot
        manifest           = $manifestPath
        log                = $logPath
    }

    $summaryPath = Join-Path $manifestRoot "summary-$runId.json"
    $summary | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $summaryPath -Encoding UTF8

    Write-Log "FINAL STATUS: $status; repositories=$($repositories.Count); warnings=$script:WarningCount; failed=$($script:FailedRepositories.Count)"
    Write-Host "Summary: $summaryPath"

    switch ($status) {
        "PASS"    { exit 0 }
        "PARTIAL" { exit 2 }
        default   { exit 1 }
    }
}
catch {
    Write-Log $_.Exception.Message "ERROR"
    exit 1
}
