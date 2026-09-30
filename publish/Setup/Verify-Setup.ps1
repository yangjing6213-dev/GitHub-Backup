param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$EvidenceFile,
    [string]$InstalledDirectory,
    [switch]$RequireSigned
)

$ErrorActionPreference = 'Stop'
$contract = Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot 'SetupContract.psd1')
function Fail([string]$code) { throw $code }
function SafeExisting([string]$path, [bool]$folder) {
    if (-not [IO.Path]::IsPathFullyQualified($path) -or $path -match '(?i)(^|[\\/])\.local([\\/]|$)') { Fail 'SETUP_VERIFY_PATH_INVALID' }
    $full = [IO.Path]::GetFullPath($path)
    $walk = $full
    while ($walk) {
        if ([IO.File]::Exists($walk) -or [IO.Directory]::Exists($walk)) {
            if (([IO.File]::GetAttributes($walk) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Fail 'SETUP_VERIFY_PATH_INVALID' }
        }
        $next = [IO.Path]::GetDirectoryName($walk)
        if (-not $next -or $next -eq $walk) { break }
        $walk = $next
    }
    if ($folder -and -not [IO.Directory]::Exists($full)) { Fail 'SETUP_VERIFY_PATH_INVALID' }
    if (-not $folder -and -not [IO.File]::Exists($full)) { Fail 'SETUP_VERIFY_PATH_INVALID' }
    return $full
}
function Sha([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Hex64($value) { return $value -is [string] -and $value -cmatch '^[A-Fa-f0-9]{64}$' }
function PeInfo([string]$path, [bool]$internalApp = $false) {
    $invalid = if ($internalApp) { 'SETUP_VERIFY_INSTALLED_APP_PE_INVALID' } else { 'SETUP_VERIFY_PE_INVALID' }
    $stream = [IO.File]::OpenRead($path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($stream.Length -lt 256 -or $reader.ReadUInt16() -ne 0x5A4D) { Fail $invalid }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 0x40 -or $offset -gt $stream.Length - 94) { Fail $invalid }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550) { Fail $invalid }
        $machine = $reader.ReadUInt16()
        $stream.Position = $offset + 24
        $magic = $reader.ReadUInt16()
        $stream.Position = $offset + 92
        $subsystem = $reader.ReadUInt16()
        if ($magic -notin @(0x10B,0x20B) -or $subsystem -ne 2 -or
            ($internalApp -and ($machine -ne $contract.NativeMachine -or $magic -ne 0x20B))) { Fail $invalid }
    } finally { $stream.Dispose() }
}
try {
    $folder = SafeExisting $Directory $true
    $evidencePath = SafeExisting $EvidenceFile $false
    $items = @(Get-ChildItem -LiteralPath $folder -Force)
    if ($items.Count -ne 1 -or $items[0].Name -cne $contract.InstallerFile -or
        $items[0].PSIsContainer -or ($items[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $items[0].Length -le 0) { Fail 'SETUP_VERIFY_LAYOUT_INVALID' }
    $setup = $items[0].FullName
    PeInfo $setup
    $hash = Sha $setup
    try { $record = Get-Content -LiteralPath $evidencePath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable -Depth 16 }
    catch { Fail 'SETUP_VERIFY_EVIDENCE_INVALID' }
    if ($record -isnot [System.Collections.IDictionary] -or $record.schema -cne 1 -or
        -not (Hex64 $record.setupSha256) -or $record.setupSha256.ToUpperInvariant() -cne $hash -or
        -not (Hex64 $record.appSha256) -or $record.signatureMode -cnotin @('internal-unsigned','public-signed') -or
        $record.installerFile -cne $contract.InstallerFile) { Fail 'SETUP_VERIFY_EVIDENCE_INVALID' }
    $signature = Get-AuthenticodeSignature -LiteralPath $setup
    if ($RequireSigned) {
        if ($record.signatureMode -cne 'public-signed' -or $signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or -not $signature.TimeStamperCertificate -or
            $record.signerThumbprint -cne $signature.SignerCertificate.Thumbprint -or
            $record.timestampStatus -cne 'Valid' -or [string]::IsNullOrWhiteSpace($record.signingApprovalReference) -or
            -not (Hex64 $record.signedAppSha256) -or -not (Hex64 $record.signedUninstallerSha256) -or
            $record.signedAppSha256 -cne $record.appSha256) { Fail 'SETUP_VERIFY_SIGNATURE_INVALID' }
    } elseif ($record.signatureMode -ceq 'internal-unsigned' -and $signature.Status -ne [Management.Automation.SignatureStatus]::NotSigned) {
        Fail 'SETUP_VERIFY_SIGNATURE_INVALID'
    }
    $runtime = 'NOT_RUN'
    if ($InstalledDirectory) {
        $installed = SafeExisting $InstalledDirectory $true
        if ($record.installedFiles -isnot [array] -or $record.installedFiles.Count -ne 4 -or
            $record.runtimeAcceptance -cne 'PASS' -or $record.runtimeSetupSha256 -cne $hash) { Fail 'SETUP_VERIFY_INSTALL_EVIDENCE_INVALID' }
        $names = @($contract.AppFile,$contract.UninstallerFile,$contract.ReceiptFile,$contract.NoticeFile)
        $actual = @(Get-ChildItem -LiteralPath $installed -Force)
        if ($actual.Count -ne 4) { Fail 'SETUP_VERIFY_INSTALL_EVIDENCE_INVALID' }
        foreach ($name in $names) {
            $entry = @($record.installedFiles | Where-Object { $_.name -ceq $name })
            $file = Join-Path $installed $name
            if ($entry.Count -ne 1 -or -not (Hex64 $entry[0].sha256) -or -not [IO.File]::Exists($file) -or
                ([IO.File]::GetAttributes($file) -band [IO.FileAttributes]::ReparsePoint) -or
                (Sha $file) -cne $entry[0].sha256.ToUpperInvariant()) { Fail 'SETUP_VERIFY_INSTALL_EVIDENCE_INVALID' }
        }
        if ((Sha (Join-Path $installed $contract.AppFile)) -cne $record.appSha256.ToUpperInvariant()) { Fail 'SETUP_VERIFY_INSTALL_EVIDENCE_INVALID' }
        PeInfo (Join-Path $installed $contract.AppFile) $true
        if ($RequireSigned) {
            foreach ($name in @($contract.AppFile,$contract.UninstallerFile)) {
                $signedFile = Join-Path $installed $name
                $fileSignature = Get-AuthenticodeSignature -LiteralPath $signedFile
                if ($fileSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or -not $fileSignature.TimeStamperCertificate) { Fail 'SETUP_VERIFY_SIGNATURE_INVALID' }
                $expectedHash = if ($name -ceq $contract.AppFile) { $record.signedAppSha256 } else { $record.signedUninstallerSha256 }
                if ((Sha $signedFile) -cne $expectedHash.ToUpperInvariant()) { Fail 'SETUP_VERIFY_SIGNATURE_INVALID' }
            }
        }
        $runtime = 'PASS_RECORDED'
    }
    [pscustomobject]@{ Status='STATIC_PASS'; SetupSha256=$hash; AppSha256=$record.appSha256.ToUpperInvariant(); SignatureMode=$record.signatureMode; RuntimeAcceptance=$runtime }
} catch {
    $code = [string]$_.Exception.Message
    if ($code -notmatch '^SETUP_[A-Z_]+$') { $code = 'SETUP_VERIFY_UNEXPECTED_ERROR' }
    [Console]::Error.WriteLine($code)
    exit 1
}
