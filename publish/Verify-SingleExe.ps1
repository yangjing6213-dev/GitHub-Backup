param(
    [Parameter(Mandatory = $true)]
    [string] $Directory
)

$ErrorActionPreference = 'Stop'

$folder = Get-Item -LiteralPath $Directory -Force
if (-not $folder.PSIsContainer -or ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Publish directory must be a regular directory.'
}

$items = @(Get-ChildItem -LiteralPath $folder.FullName -Force)
if ($items.Count -ne 1 -or $items[0].Name -cne 'GitHubBackup.exe' -or
    $items[0].PSIsContainer -or ($items[0].Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Distribution must contain exactly one regular, non-reparse GitHubBackup.exe and nothing else.'
}

$exe = $items[0]
if ($exe.Length -le 0) { throw 'Executable is empty.' }

$stream = [IO.File]::OpenRead($exe.FullName)
try {
    $reader = [IO.BinaryReader]::new($stream)
    if ($stream.Length -lt 256 -or $reader.ReadUInt16() -ne 0x5A4D) { throw 'Missing DOS MZ header.' }
    $stream.Position = 0x3C
    $peOffset = $reader.ReadInt32()
    if ($peOffset -lt 0x40 -or $peOffset -gt $stream.Length - 94) { throw 'Invalid PE header offset.' }
    $stream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Missing PE signature.' }
    $machine = $reader.ReadUInt16()
    $stream.Position = $peOffset + 24
    $magic = $reader.ReadUInt16()
    $stream.Position = $peOffset + 24 + 68
    $subsystem = $reader.ReadUInt16()
    if ($machine -ne 0x8664 -or $magic -ne 0x20B -or $subsystem -ne 2) {
        throw ('Expected AMD64 PE32+ Windows GUI; machine=0x{0:X4}, magic=0x{1:X4}, subsystem={2}.' -f $machine, $magic, $subsystem)
    }
}
finally { $stream.Dispose() }

$signature = (Get-AuthenticodeSignature -LiteralPath $exe.FullName).Status
if ($signature -ne [Management.Automation.SignatureStatus]::NotSigned) {
    throw "Expected an unsigned internal artifact; signature status is $signature."
}

# These are known synthetic test markers and legacy source filenames, not real credentials.
$markers = @(
    'SYNTHETIC_ORCHESTRATION_SECRET',
    'SYNTHETIC_RAW_PAGE_CANARY',
    'SYNTHETIC_CANARY_DO_NOT_LOG',
    'Backup-GitHubAccount.ps1',
    'Invoke-OneClickGitHubBackup.ps1',
    'Restore-GitHubMirror.ps1',
    'Restore-CodexGitSettings.ps1'
)
$bytes = [IO.File]::ReadAllBytes($exe.FullName)
$rawBytes = [Text.Encoding]::Latin1.GetString($bytes)
foreach ($marker in $markers) {
    $utf16Bytes = [Text.Encoding]::Latin1.GetString([Text.Encoding]::Unicode.GetBytes($marker))
    if ($rawBytes.Contains($marker, [StringComparison]::Ordinal) -or
        $rawBytes.Contains($utf16Bytes, [StringComparison]::Ordinal)) {
        throw "Known synthetic canary or legacy source filename embedded: $marker"
    }
}

[pscustomobject]@{
    Directory = $folder.FullName
    FileCount = $items.Count
    FileName = $exe.Name
    RegularNonReparseFile = $true
    SizeBytes = $exe.Length
    Sha256 = (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash
    PeMachine = 'AMD64 (0x8664)'
    PeFormat = 'PE32+ (0x20B)'
    PeSubsystem = 'Windows GUI (2)'
    SignatureStatus = [string] $signature
    BoundedMarkerCount = $markers.Count
    BoundedMarkerScan = 'PASS (ASCII and UTF-16LE byte patterns)'
}
