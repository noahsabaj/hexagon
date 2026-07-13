[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SchemaRoot,

    [Parameter(Mandatory)]
    [string] $RemoteAcceptanceEvidence,

    [Parameter(Mandatory)]
    [ValidatePattern('^[a-f0-9]{40}$')]
    [string] $HexagonSha,

    [Parameter(Mandatory)]
    [ValidatePattern('^[a-f0-9]{40}$')]
    [string] $HL2RPSha,

    [Parameter()]
    [ValidatePattern('^[a-z0-9_.-]+/[a-z0-9_.-]+$')]
    [string] $HexagonRepository = 'noahsabaj/hexagon',

    [Parameter()]
    [ValidatePattern('^[a-z0-9_.-]+/[a-z0-9_.-]+$')]
    [string] $HL2RPRepository = 'noahsabaj/hl2rp-hexagon'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$hexagonRoot = Split-Path -Parent $PSScriptRoot
$schemaRootPath = (Resolve-Path -LiteralPath $SchemaRoot).Path

function Get-Head {
    param([Parameter(Mandatory)][string] $Root)
    $head = (& git -C $Root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[a-f0-9]{40}$') {
        throw "Could not resolve a Git HEAD for '$Root'."
    }
    return $head
}

function Assert-Clean {
    param([Parameter(Mandatory)][string] $Root)
    $changes = @(& git -C $Root status --porcelain=v1)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) {
        throw "Release evidence may only be published from a clean checkout: '$Root'."
    }
}

Assert-Clean -Root $hexagonRoot
Assert-Clean -Root $schemaRootPath
if ((Get-Head -Root $hexagonRoot) -cne $HexagonSha) {
    throw "Hexagon checkout does not match requested source SHA '$HexagonSha'."
}
if ((Get-Head -Root $schemaRootPath) -cne $HL2RPSha) {
    throw "HL2RP checkout does not match requested source SHA '$HL2RPSha'."
}

$lock = Get-Content -LiteralPath (Join-Path $schemaRootPath 'hexagon.lock.json') -Raw | ConvertFrom-Json
if ([string]$lock.repository -cne $HexagonRepository -or [string]$lock.commit -cne $HexagonSha) {
    throw 'HL2RP is not locked to the exact Hexagon repository and source SHA being attested.'
}

& (Join-Path $PSScriptRoot 'verify.ps1') `
    -SchemaRoot $schemaRootPath `
    -RemoteAcceptanceEvidence $RemoteAcceptanceEvidence `
    -HexagonSha $HexagonSha `
    -HL2RPSha $HL2RPSha
if ($LASTEXITCODE -ne 0) {
    throw "Full local verification failed with exit code $LASTEXITCODE."
}

foreach ($target in @(
    [pscustomobject]@{ Repository = $HexagonRepository; Sha = $HexagonSha },
    [pscustomobject]@{ Repository = $HL2RPRepository; Sha = $HL2RPSha }
)) {
    & gh api --method POST "repos/$($target.Repository)/statuses/$($target.Sha)" `
        -f state=success `
        -f context='sbox / release-evidence' `
        -f description='Full verifier and two-client evidence passed for both locked source SHAs'
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish release evidence for $($target.Repository)@$($target.Sha)."
    }
}

Write-Host "Published sbox / release-evidence for Hexagon $HexagonSha and HL2RP $HL2RPSha." -ForegroundColor Green
