Set-StrictMode -Version Latest

function ConvertFrom-ReleaseEvidenceJson {
    param([Parameter(Mandatory)][string] $Json)
    $command = Get-Command ConvertFrom-Json
    if ($command.Parameters.ContainsKey('DateKind')) {
        return $Json | ConvertFrom-Json -DateKind String
    }
    return $Json | ConvertFrom-Json
}

function Get-LowerSha256Bytes {
    param([Parameter(Mandatory)][byte[]] $Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($Bytes)) -replace '-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Add-DeterministicZipEntry {
    param(
        [Parameter(Mandatory)][System.IO.Compression.ZipArchive] $Archive,
        [Parameter(Mandatory)][string] $EntryPath,
        [Parameter(Mandatory)][byte[]] $Bytes
    )
    $entry = $Archive.CreateEntry(
        $EntryPath,
        [System.IO.Compression.CompressionLevel]::Optimal)
    $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $stream = $entry.Open()
    try { $stream.Write($Bytes, 0, $Bytes.Length) }
    finally { $stream.Dispose() }
}

function New-ReleaseEvidenceBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $EvidencePath,
        [Parameter(Mandatory)][string] $OutputPath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $evidenceFullPath = (Resolve-Path -LiteralPath $EvidencePath).Path
    $evidenceDirectory = Split-Path -Parent $evidenceFullPath
    $evidence = ConvertFrom-ReleaseEvidenceJson -Json (
        Get-Content -LiteralPath $evidenceFullPath -Raw)
    if ([string]$evidence.format -cne 'hexagon-v2-manual-remote-acceptance/4') {
        throw 'Only format-v4 evidence can be bundled.'
    }

    $payloads = [System.Collections.Generic.SortedDictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)
    $canonicalEvidence = [System.Text.Encoding]::UTF8.GetBytes(
        ($evidence | ConvertTo-Json -Depth 20 -Compress))
    $payloads.Add('evidence/remote-acceptance.json', [pscustomobject]@{
        Path = 'evidence/remote-acceptance.json'
        Bytes = $canonicalEvidence
        Sha256 = Get-LowerSha256Bytes -Bytes $canonicalEvidence
    })

    $artifactsById = [System.Collections.Generic.SortedDictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($artifact in @($evidence.artifacts)) {
        $id = [string]$artifact.id
        if ($id -cnotmatch '^[a-z0-9][a-z0-9.-]*$' -or
            $artifactsById.ContainsKey($id)) {
            throw "Artifact ID '$id' is unsafe or duplicated for bundle construction."
        }
        $artifactsById.Add($id, $artifact)
    }
    foreach ($artifact in $artifactsById.Values) {
        $id = [string]$artifact.id
        $artifactPath = if ([System.IO.Path]::IsPathRooted([string]$artifact.path)) {
            [string]$artifact.path
        }
        else {
            Join-Path $evidenceDirectory ([string]$artifact.path)
        }
        $artifactPath = (Resolve-Path -LiteralPath $artifactPath).Path
        $bytes = [System.IO.File]::ReadAllBytes($artifactPath)
        $hash = Get-LowerSha256Bytes -Bytes $bytes
        if ($hash -cne [string]$artifact.sha256) {
            throw "Artifact '$id' changed before bundle construction."
        }
        $entryPath = "artifacts/$id/$([System.IO.Path]::GetFileName($artifactPath))"
        $payloads.Add($entryPath, [pscustomobject]@{
            Path = $entryPath
            Bytes = $bytes
            Sha256 = $hash
        })
    }

    $manifest = [ordered]@{
        format = 'hexagon-v2-release-evidence-bundle/1'
        evidence_format = [string]$evidence.format
        run_id = [string]$evidence.run_id
        hexagon_sha = [string]$evidence.hexagon_sha
        hl2rp_sha = [string]$evidence.hl2rp_sha
        source_fingerprint = [string]$evidence.source_fingerprint
        entries = @($payloads.Values | ForEach-Object {
            [ordered]@{
                path = $_.Path
                sha256 = $_.Sha256
                length = [long]$_.Bytes.Length
            }
        })
    }
    $manifestBytes = [System.Text.Encoding]::UTF8.GetBytes(
        ($manifest | ConvertTo-Json -Depth 10 -Compress))

    $outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = Split-Path -Parent $outputFullPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory) -and
        -not (Test-Path -LiteralPath $outputDirectory)) {
        [void](New-Item -ItemType Directory -Path $outputDirectory)
    }
    if (Test-Path -LiteralPath $outputFullPath) {
        Remove-Item -LiteralPath $outputFullPath -Force
    }

    $file = [System.IO.File]::Open(
        $outputFullPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $file,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $true)
        try {
            foreach ($payload in $payloads.Values) {
                Add-DeterministicZipEntry -Archive $archive -EntryPath $payload.Path -Bytes $payload.Bytes
            }
            Add-DeterministicZipEntry -Archive $archive -EntryPath 'manifest.json' -Bytes $manifestBytes
        }
        finally { $archive.Dispose() }
    }
    finally { $file.Dispose() }

    return [pscustomobject]@{
        Path = $outputFullPath
        Sha256 = (Get-FileHash -LiteralPath $outputFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        ManifestSha256 = Get-LowerSha256Bytes -Bytes $manifestBytes
        RunId = [string]$evidence.run_id
        SourceFingerprint = [string]$evidence.source_fingerprint
    }
}
