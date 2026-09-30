param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$InputReceipt,
    [Parameter(Mandatory)][string]$NsisRoot,
    [Parameter(Mandatory)][string]$ToolReceipt,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$contract = Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot 'SetupContract.psd1')
$oldPayloadHash = '3CFAD42412ECD3F35F18ECCD5D4B583ADC04D61DF7748C799AF7A5333E091A6B'
$nsisArchiveHash = '56581F90DB321581C5381193D796FFFCF2D24B2F8FED2160A6C6A3BAA67F2C4F'

function Fail([string]$code) { throw $code }
function IsWithin([string]$child, [string]$parent) {
    return $child.StartsWith($parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
function SafePath([string]$value, [bool]$mustExist) {
    if ([string]::IsNullOrWhiteSpace($value) -or -not [IO.Path]::IsPathFullyQualified($value)) { Fail 'SETUP_PATH_INVALID' }
    $full = [IO.Path]::GetFullPath($value)
    if ($full -match '(?i)(^|[\\/])\.local([\\/]|$)' -or $full -match '[\x00-\x1f]') { Fail 'SETUP_PATH_INVALID' }
    $walk = $full
    while ($walk) {
        if ([IO.File]::Exists($walk) -or [IO.Directory]::Exists($walk)) {
            if (([IO.File]::GetAttributes($walk) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Fail 'SETUP_PATH_REPARSE' }
        }
        $next = [IO.Path]::GetDirectoryName($walk)
        if (-not $next -or $next -eq $walk) { break }
        $walk = $next
    }
    if ($mustExist -and -not ([IO.File]::Exists($full) -or [IO.Directory]::Exists($full))) { Fail 'SETUP_INPUT_MISSING' }
    return $full
}
function JsonRecord([string]$file, [string[]]$keys, [string]$code) {
    if (-not [IO.File]::Exists($file)) { Fail $code }
    try {
        if (([IO.FileInfo]$file).Length -lt 2 -or ([IO.FileInfo]$file).Length -gt 2097152) { Fail $code }
        $json = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable -Depth 16
        if ($json -isnot [System.Collections.IDictionary] -or $json.Count -ne $keys.Count) { Fail $code }
        foreach ($key in $keys) { if (-not $json.Contains($key)) { Fail $code } }
        return $json
    } catch { Fail $code }
}
function Sha([string]$file) { return (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToUpperInvariant() }
function Hex64($value) { return $value -is [string] -and $value -cmatch '^[A-Fa-f0-9]{64}$' }
function Hex40($value) { return $value -is [string] -and $value -cmatch '^[A-Fa-f0-9]{40}$' }
function RelativePart([string]$value) {
    if ($value -isnot [string] -or $value -notmatch '^[^\\/]+(?:[\\/][^\\/]+)*$' -or
        $value -match '(^|[\\/])(?:\.|\.\.|\.local)([\\/]|$)' -or $value -match '[:\x00-\x1f]') { Fail 'SETUP_SOURCE_MANIFEST_INVALID' }
    return ($value -split '[\\/]') -join [IO.Path]::DirectorySeparatorChar
}
function PeInfo([string]$file) {
    $stream = [IO.File]::OpenRead($file)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($stream.Length -lt 256 -or $reader.ReadUInt16() -ne 0x5A4D) { Fail 'SETUP_PAYLOAD_PE_INVALID' }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 0x40 -or $offset -gt $stream.Length - 94) { Fail 'SETUP_PAYLOAD_PE_INVALID' }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550) { Fail 'SETUP_PAYLOAD_PE_INVALID' }
        $machine = $reader.ReadUInt16()
        $stream.Position = $offset + 24
        $magic = $reader.ReadUInt16()
        $stream.Position = $offset + 92
        $subsystem = $reader.ReadUInt16()
        if ($machine -ne $contract.NativeMachine -or $magic -ne 0x20B -or $subsystem -ne 2) { Fail 'SETUP_PAYLOAD_PE_INVALID' }
    } finally { $stream.Dispose() }
}
function New-SetupIncludeLines([string]$stagedApp, [string]$stagedNotice, [string]$outputFile,
    [string]$version, [string]$appHash, [string]$sourceCommit, [hashtable]$productContract,
    [string]$noticeHash, [string]$payloadKind) {
    @(
        ('!define SETUP_APP_FILE "{0}"' -f $stagedApp)
        ('!define SETUP_NOTICE_FILE "{0}"' -f $stagedNotice)
        ('!define SETUP_OUTPUT_FILE "{0}"' -f $outputFile)
        ('!define SETUP_APP_VERSION "{0}"' -f $version)
        ('!define SETUP_APP_SHA256 "{0}"' -f $appHash)
        ('!define SETUP_SOURCE_COMMIT "{0}"' -f $sourceCommit)
        ('!define SETUP_INSTALL_SUFFIX "{0}"' -f $productContract.InstallSuffix)
        ('!define SETUP_SUPPORTED_BUILD {0}' -f $productContract.SupportedBuild)
        ('!define SETUP_NATIVE_MACHINE {0}' -f $productContract.NativeMachine)
        ('!define SETUP_PRODUCT_ID "{0}"' -f $productContract.ProductId)
        ('!define SETUP_APP_NAME "{0}"' -f $productContract.AppFile)
        ('!define SETUP_UNINSTALLER_NAME "{0}"' -f $productContract.UninstallerFile)
        ('!define SETUP_RECEIPT_NAME "{0}"' -f $productContract.ReceiptFile)
        ('!define SETUP_NOTICE_NAME "{0}"' -f $productContract.NoticeFile)
        ('!define SETUP_NOTICE_SHA256 "{0}"' -f $noticeHash)
        ('!define SETUP_PAYLOAD_KIND "{0}"' -f $payloadKind)
    )
}
function Copy-VerifiedToolTree([string]$sourceRoot, [string]$workRoot, [array]$entries) {
    $stagedRoot = Join-Path $workRoot 'nsis'
    if ([IO.Directory]::Exists($stagedRoot) -or [IO.File]::Exists($stagedRoot)) { Fail 'SETUP_STAGED_TOOL_INVALID' }
    [IO.Directory]::CreateDirectory($stagedRoot) | Out-Null
    foreach ($entry in $entries) {
        $source = SafePath (Join-Path $sourceRoot $entry.relativePath) $true
        if ((Sha $source) -cne $entry.sha256.ToUpperInvariant()) { Fail 'SETUP_TOOL_FILE_INVALID' }
        $staged = Join-Path $stagedRoot $entry.relativePath
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($staged)) | Out-Null
        [IO.File]::Copy($source,$staged)
        if ((Sha (SafePath $staged $true)) -cne $entry.sha256.ToUpperInvariant()) { Fail 'SETUP_STAGED_TOOL_INVALID' }
    }
    return $stagedRoot
}
try {
    $payload = SafePath $PayloadDirectory $true
    $receiptFile = SafePath $InputReceipt $true
    $nsis = SafePath $NsisRoot $true
    $toolFile = SafePath $ToolReceipt $true
    $work = SafePath $WorkDirectory $false
    $output = SafePath $OutputDirectory $false
    if (-not [IO.Directory]::Exists($payload) -or -not [IO.Directory]::Exists($nsis)) { Fail 'SETUP_INPUT_MISSING' }
    $buildRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\setup'))
    if (-not (IsWithin $work $buildRoot) -or -not (IsWithin $output $buildRoot) -or
        [IO.Directory]::Exists($work) -or [IO.File]::Exists($work) -or
        [IO.Directory]::Exists($output) -or [IO.File]::Exists($output) -or
        (IsWithin $work $output) -or (IsWithin $output $work) -or $work -eq $output -or
        (IsWithin $work $payload) -or (IsWithin $payload $work) -or
        (IsWithin $output $payload) -or (IsWithin $payload $output) -or
        (IsWithin $work $nsis) -or (IsWithin $nsis $work) -or
        (IsWithin $output $nsis) -or (IsWithin $nsis $output) -or
        (IsWithin $receiptFile $work) -or (IsWithin $receiptFile $output) -or
        (IsWithin $toolFile $work) -or (IsWithin $toolFile $output)) { Fail 'SETUP_OUTPUT_PATH_INVALID' }
    $payloadItems = @(Get-ChildItem -LiteralPath $payload -Force)
    if ($payloadItems.Count -ne 1 -or $payloadItems[0].Name -cne $contract.AppFile -or
        $payloadItems[0].PSIsContainer -or ($payloadItems[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $payloadItems[0].Length -le 0) { Fail 'SETUP_PAYLOAD_LAYOUT_INVALID' }
    $app = $payloadItems[0].FullName
    $appHash = Sha $app
    if ($appHash -eq $oldPayloadHash) { Fail 'SETUP_OLD_PAYLOAD_REJECTED' }
    PeInfo $app
    $input = JsonRecord $receiptFile @('schema','sourceCommit','appSourceTree','sourceFiles','payloadSha256','appFileVersion','payloadKind','buildEvidence') 'SETUP_INPUT_RECEIPT_INVALID'
    if ($input.schema -cne 1 -or -not (Hex40 $input.sourceCommit) -or
        -not (Hex40 $input.appSourceTree) -or -not (Hex64 $input.payloadSha256) -or $input.payloadSha256.ToUpperInvariant() -cne $appHash -or
        $input.payloadKind -cnotin @('internal-unsigned','public-signed') -or
        $input.appFileVersion -isnot [string] -or $input.appFileVersion -notmatch '^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$' -or
        $input.buildEvidence -isnot [string] -or $input.buildEvidence -match '(?i)placeholder|todo|example') { Fail 'SETUP_INPUT_RECEIPT_INVALID' }
    if ($input.sourceFiles -isnot [array] -or $input.sourceFiles.Count -eq 0) { Fail 'SETUP_SOURCE_MANIFEST_INVALID' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $input.sourceFiles) {
        if ($entry -isnot [System.Collections.IDictionary] -or $entry.Count -ne 2 -or -not $entry.Contains('relativePath') -or -not $entry.Contains('sha256') -or -not (Hex64 $entry.sha256)) { Fail 'SETUP_SOURCE_MANIFEST_INVALID' }
        $relative = RelativePart $entry.relativePath
        if (($relative -split '\\')[0] -notin @('src', 'publish') -and $relative -cne 'Directory.Packages.props' -and $relative -cne 'NuGet.Config') { Fail 'SETUP_SOURCE_MANIFEST_INVALID' }
        if (-not $seen.Add($relative)) { Fail 'SETUP_SOURCE_MANIFEST_INVALID' }
        if (-not [IO.File]::Exists((Join-Path $root $relative))) { Fail 'SETUP_SOURCE_MANIFEST_INVALID' }
        $source = SafePath (Join-Path $root $relative) $true
        if (-not (IsWithin $source $root) -or -not [IO.File]::Exists($source) -or (Sha $source) -cne $entry.sha256.ToUpperInvariant()) { Fail 'SETUP_SOURCE_MANIFEST_INVALID' }
    }
    $evidenceRelative = RelativePart $input.buildEvidence
    if (-not ($evidenceRelative.StartsWith('docs\verification\',[StringComparison]::OrdinalIgnoreCase) -or
        $evidenceRelative.StartsWith('artifacts\setup\',[StringComparison]::OrdinalIgnoreCase))) { Fail 'SETUP_BUILD_EVIDENCE_MISSING' }
    if (-not [IO.File]::Exists((Join-Path $root $evidenceRelative))) { Fail 'SETUP_BUILD_EVIDENCE_MISSING' }
    $evidence = SafePath (Join-Path $root $evidenceRelative) $true
    if (-not [IO.File]::Exists($evidence) -or -not (IsWithin $evidence $root)) { Fail 'SETUP_BUILD_EVIDENCE_MISSING' }
    $buildKeys = @('schema','sourceCommit','appSourceTree','payloadSha256','appFileVersion','publishExitCode','payloadVerificationStatus','policyResolutionReference')
    if ($input.payloadKind -ceq 'public-signed') {
        $build = JsonRecord $evidence ($buildKeys + 'signing') 'SETUP_SIGNED_PAYLOAD_INVALID'
    } else {
        $build = JsonRecord $evidence $buildKeys 'SETUP_BUILD_EVIDENCE_INVALID'
    }
    if ($build.schema -cne 1 -or $build.sourceCommit -cne $input.sourceCommit -or $build.appSourceTree -cne $input.appSourceTree -or
        $build.payloadSha256 -cne $appHash -or $build.appFileVersion -cne $input.appFileVersion -or
        $build.publishExitCode -cne 0 -or $build.payloadVerificationStatus -cne 'PASS' -or
        [string]::IsNullOrWhiteSpace($build.policyResolutionReference) -or $build.policyResolutionReference -match '(?i)placeholder|todo|example') { Fail 'SETUP_BUILD_EVIDENCE_INVALID' }
    if ($input.payloadKind -ceq 'public-signed') {
        $signatureEvidence = $build.signing
        $signKeys = @('schema','preSignSha256','postSignSha256','signerThumbprint','timestampStatus','approvalReference')
        if ($signatureEvidence -isnot [System.Collections.IDictionary] -or $signatureEvidence.Count -ne $signKeys.Count) { Fail 'SETUP_SIGNED_PAYLOAD_INVALID' }
        foreach ($key in $signKeys) { if (-not $signatureEvidence.Contains($key)) { Fail 'SETUP_SIGNED_PAYLOAD_INVALID' } }
        if ($signatureEvidence.schema -cne 1 -or -not (Hex64 $signatureEvidence.preSignSha256) -or
            $signatureEvidence.postSignSha256 -cne $appHash -or $signatureEvidence.signerThumbprint -notmatch '^[A-Fa-f0-9]{40}$' -or
            $signatureEvidence.timestampStatus -cne 'Valid' -or
            [string]::IsNullOrWhiteSpace($signatureEvidence.approvalReference) -or
            $signatureEvidence.approvalReference -match '(?i)placeholder|todo|example') { Fail 'SETUP_SIGNED_PAYLOAD_INVALID' }
    }
    $tool = JsonRecord $toolFile @('version','officialReleaseUrl','archiveProvenance','archiveSha256','files') 'SETUP_TOOL_RECEIPT_INVALID'
    if ($tool.version -cne $contract.NsisVersion -or
        $tool.officialReleaseUrl -cne 'https://sourceforge.net/projects/nsis/files/NSIS%203/3.12/nsis-3.12.zip/download' -or
        [string]::IsNullOrWhiteSpace($tool.archiveProvenance) -or $tool.archiveSha256 -cne $nsisArchiveHash -or
        $tool.files -isnot [array] -or $tool.files.Count -eq 0) { Fail 'SETUP_TOOL_RECEIPT_INVALID' }
    $toolSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $tool.files) {
        if ($entry -isnot [System.Collections.IDictionary] -or $entry.Count -ne 2 -or -not $entry.Contains('relativePath') -or -not $entry.Contains('sha256') -or -not (Hex64 $entry.sha256)) { Fail 'SETUP_TOOL_RECEIPT_INVALID' }
        $relative = RelativePart $entry.relativePath
        if (-not $toolSeen.Add($relative)) { Fail 'SETUP_TOOL_RECEIPT_INVALID' }
        if (-not [IO.File]::Exists((Join-Path $nsis $relative))) { Fail 'SETUP_TOOL_FILE_INVALID' }
        $file = SafePath (Join-Path $nsis $relative) $true
        if (-not (IsWithin $file $nsis) -or -not [IO.File]::Exists($file) -or (Sha $file) -cne $entry.sha256.ToUpperInvariant()) { Fail 'SETUP_TOOL_FILE_INVALID' }
    }
    if (-not $toolSeen.Contains('makensis.exe') -or -not $toolSeen.Contains('NSIS.exe') -or -not $toolSeen.Contains('Include\MUI2.nsh') -or -not $toolSeen.Contains('Plugins\x86-unicode\System.dll')) { Fail 'SETUP_TOOL_COMPOSITION_INVALID' }
    $actualToolFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($nsis)
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Fail 'SETUP_TOOL_FILE_INVALID' }
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
            else {
                $relative = [IO.Path]::GetRelativePath($nsis,$item.FullName)
                if (-not $actualToolFiles.Add($relative) -or -not $toolSeen.Contains($relative)) { Fail 'SETUP_TOOL_COMPOSITION_INVALID' }
            }
        }
    }
    if ($actualToolFiles.Count -ne $toolSeen.Count) { Fail 'SETUP_TOOL_COMPOSITION_INVALID' }
    $compiler = Join-Path $nsis 'makensis.exe'
    if (-not [IO.File]::Exists($compiler)) { Fail 'SETUP_TOOL_FILE_INVALID' }
    $toolVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $nsis 'NSIS.exe'))
    if ($toolVersion.FileVersion -cne $contract.NsisVersion -or $toolVersion.ProductVersion -cne $contract.NsisVersion) { Fail 'SETUP_TOOL_VERSION_INVALID' }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion
    if ($version -cne $input.appFileVersion) { Fail 'SETUP_PAYLOAD_VERSION_INVALID' }
    if ($input.payloadKind -ceq 'internal-unsigned') {
        try { $verified = & (Join-Path $root 'publish\Verify-SingleExe.ps1') -Directory $payload }
        catch { Fail 'SETUP_INTERNAL_PAYLOAD_INVALID' }
        if ($verified.Sha256 -cne $appHash -or $verified.SignatureStatus -cne 'NotSigned') { Fail 'SETUP_INTERNAL_PAYLOAD_INVALID' }
    } else {
        $appSignature = Get-AuthenticodeSignature -LiteralPath $app
        if ($appSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or -not $appSignature.TimeStamperCertificate) { Fail 'SETUP_SIGNED_PAYLOAD_INVALID' }
        if ($signatureEvidence.signerThumbprint -cne $appSignature.SignerCertificate.Thumbprint) { Fail 'SETUP_SIGNED_PAYLOAD_INVALID' }
    }
    $outputFile = Join-Path $output $contract.InstallerFile
    if ($CheckOnly) {
        [pscustomobject]@{ Status='CHECK_ONLY_PASS'; PayloadSha256=$appHash; AppVersion=$version; SourceCommit=$input.sourceCommit; OutputFile=$outputFile }
        exit 0
    }
    $script = Join-Path $PSScriptRoot 'GitHubBackup.nsi'
    if (-not [IO.File]::Exists($script)) { Fail 'SETUP_INSTALLER_SOURCE_MISSING' }
    $noticeSource = Join-Path $PSScriptRoot $contract.NoticeFile
    if ((Get-Content -LiteralPath $noticeSource -Raw -Encoding UTF8) -match 'PENDING_LICENSE') { Fail 'SETUP_LICENSE_PENDING' }
    foreach ($path in @($work,$output,$script,$app,$compiler)) { if ($path -match '[\$!"`\x00-\x1f]') { Fail 'SETUP_PATH_UNREPRESENTABLE' } }
    [IO.Directory]::CreateDirectory($work) | Out-Null
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $stagedNsis = Copy-VerifiedToolTree $nsis $work $tool.files
    $stagedCompiler = Join-Path $stagedNsis 'makensis.exe'
    $stagedApp = Join-Path $work $contract.AppFile
    [IO.File]::Copy($app,$stagedApp)
    if ((Sha $stagedApp) -cne $appHash) { Fail 'SETUP_STAGED_HASH_MISMATCH' }
    $stagedNotice = Join-Path $work $contract.NoticeFile
    $noticeHash = Sha $noticeSource
    [IO.File]::Copy($noticeSource,$stagedNotice)
    if ((Sha $stagedNotice) -cne $noticeHash) { Fail 'SETUP_STAGED_HASH_MISMATCH' }
    $include = Join-Path $work 'SetupInputs.nsh'
    $lines = @(New-SetupIncludeLines $stagedApp $stagedNotice $outputFile $version $appHash $input.sourceCommit $contract $noticeHash $input.payloadKind)
    [IO.File]::WriteAllLines($include,$lines,[Text.UTF8Encoding]::new($false))
    $oldNsisDir = $env:NSISDIR
    $oldNsisConfigDir = $env:NSISCONFDIR
    try {
        $env:NSISDIR = $stagedNsis
        $env:NSISCONFDIR = $stagedNsis
        & $stagedCompiler /NOCONFIG /V3 "/DSETUP_INPUTS=$include" $script
        if ($LASTEXITCODE -ne 0) { Fail 'SETUP_COMPILER_FAILED' }
    } finally {
        $env:NSISDIR = $oldNsisDir
        $env:NSISCONFDIR = $oldNsisConfigDir
    }
    if (-not [IO.File]::Exists($outputFile)) { Fail 'SETUP_OUTPUT_MISSING' }
    [pscustomobject]@{ Status='BUILT_UNVERIFIED'; PayloadSha256=$appHash; AppVersion=$version; SourceCommit=$input.sourceCommit; OutputFile=$outputFile }
} catch {
    $code = [string]$_.Exception.Message
    if ($code -notmatch '^SETUP_[A-Z_]+$') { $code = 'SETUP_UNEXPECTED_ERROR' }
    [Console]::Error.WriteLine($code)
    exit 1
}
