[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string] $SchemaRoot,
    [Parameter(Mandatory)][string] $RemoteAcceptanceEvidence,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string] $HexagonSha,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string] $HL2RPSha,
    [Parameter()][ValidatePattern('^[a-z0-9_.-]+/[a-z0-9_.-]+$')]
    [string] $HexagonRepository = 'noahsabaj/hexagon',
    [Parameter()][ValidatePattern('^[a-z0-9_.-]+/[a-z0-9_.-]+$')]
    [string] $HL2RPRepository = 'noahsabaj/hl2rp-hexagon',
    [Parameter()][string] $HexagonRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter()][string] $BundlePath,
    [Parameter()][string] $GitHubCommand = 'gh',
    [Parameter()][string] $VerificationScript = (Join-Path $PSScriptRoot 'verify.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'release-inputs.ps1')
. (Join-Path $PSScriptRoot 'release-evidence-bundle.ps1')

$statusContext = 'sbox / release-evidence'
$hexagonRootPath = (Resolve-Path -LiteralPath $HexagonRoot).Path
$schemaRootPath = (Resolve-Path -LiteralPath $SchemaRoot).Path
$evidencePath = (Resolve-Path -LiteralPath $RemoteAcceptanceEvidence).Path

function Get-Head {
    param([Parameter(Mandatory)][string] $Root)
    $head = [string]@(Invoke-ReleaseInputGit -Root $Root -Arguments @('rev-parse', 'HEAD'))[0]
    if ($head -cnotmatch '^[a-f0-9]{40}$') { throw "Could not resolve a Git HEAD for '$Root'." }
    return $head
}

function Invoke-GitHub {
    param(
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter()][switch] $AllowFailure
    )
    $global:LASTEXITCODE = 0
    [string[]] $output = @(& $GitHubCommand @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "GitHub command failed ($exitCode): gh $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function ConvertFrom-GitHubJson {
    param([Parameter(Mandatory)][object] $Result)
    $json = $Result.Output -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($json)) { throw 'GitHub returned no JSON.' }
    return $json | ConvertFrom-Json
}

function Get-ReleaseTagCommit {
    param(
        [Parameter(Mandatory)][string] $Repository,
        [Parameter(Mandatory)][string] $Tag
    )

    $reference = ConvertFrom-GitHubJson -Result (Invoke-GitHub -Arguments @(
        'api', "repos/$Repository/git/ref/tags/$Tag"))
    $object = $reference.object
    for ($depth = 0; $depth -lt 8; $depth++) {
        $type = [string]$object.type
        $sha = [string]$object.sha
        if ($sha -cnotmatch '^[a-f0-9]{40}$') {
            throw "Release tag '$Tag' returned an invalid Git object SHA."
        }
        if ($type -ceq 'commit') { return $sha }
        if ($type -cne 'tag') {
            throw "Release tag '$Tag' resolves to unsupported Git object type '$type'."
        }
        $tagObject = ConvertFrom-GitHubJson -Result (Invoke-GitHub -Arguments @(
            'api', "repos/$Repository/git/tags/$sha"))
        $object = $tagObject.object
    }
    throw "Release tag '$Tag' exceeds the supported annotated-tag depth."
}

function Publish-Status {
    param(
        [Parameter(Mandatory)][string] $Repository,
        [Parameter(Mandatory)][string] $Sha,
        [Parameter(Mandatory)][ValidateSet('pending', 'success', 'failure')][string] $State,
        [Parameter(Mandatory)][string] $Description,
        [Parameter()][string] $TargetUrl
    )
    $arguments = @(
        'api', '--method', 'POST', "repos/$Repository/statuses/$Sha",
        '-f', "state=$State", '-f', "context=$statusContext", '-f', "description=$Description")
    if (-not [string]::IsNullOrWhiteSpace($TargetUrl)) {
        $arguments += @('-f', "target_url=$TargetUrl")
    }
    [void](Invoke-GitHub -Arguments $arguments)
}

function Publish-PairedFailure {
    param([Parameter(Mandatory)][string] $RunId, [Parameter()][string] $TargetUrl)
    $errors = [System.Collections.Generic.List[string]]::new()
    foreach ($target in @(
        [pscustomobject]@{ Repository = $HexagonRepository; Sha = $HexagonSha },
        [pscustomobject]@{ Repository = $HL2RPRepository; Sha = $HL2RPSha })) {
        try {
            Publish-Status -Repository $target.Repository -Sha $target.Sha -State failure `
                -Description "Release evidence $RunId failed for the coordinated source pair" `
                -TargetUrl $TargetUrl
        }
        catch { $errors.Add($_.Exception.Message) }
    }
    if ($errors.Count -gt 0) {
        throw "Could not compensate both release-evidence statuses: $($errors -join ' | ')"
    }
}

function Assert-OutsideRepository {
    param([Parameter(Mandatory)][string] $Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    foreach ($root in @($hexagonRootPath, $schemaRootPath)) {
        $prefix = $root.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if ($full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Release evidence and bundles must be outside both Git worktrees: '$full'."
        }
    }
}

$releaseInputs = Get-EffectiveReleaseInputs -Repositories @(
    [pscustomobject]@{ Label = 'hexagon'; Root = $hexagonRootPath },
    [pscustomobject]@{ Label = 'hl2rp-hexagon'; Root = $schemaRootPath }) -RequireClean
if ((Get-Head -Root $hexagonRootPath) -cne $HexagonSha) {
    throw "Hexagon checkout does not match requested source SHA '$HexagonSha'."
}
if ((Get-Head -Root $schemaRootPath) -cne $HL2RPSha) {
    throw "HL2RP checkout does not match requested source SHA '$HL2RPSha'."
}

$lock = Get-Content -LiteralPath (Join-Path $schemaRootPath 'hexagon.lock.json') -Raw | ConvertFrom-Json
if ([string]$lock.repository -cne $HexagonRepository -or [string]$lock.commit -cne $HexagonSha) {
    throw 'HL2RP is not locked to the exact Hexagon repository and source SHA being attested.'
}

$evidence = ConvertFrom-ReleaseEvidenceJson -Json (Get-Content -LiteralPath $evidencePath -Raw)
if ([string]$evidence.format -cne 'hexagon-v2-manual-remote-acceptance/4') {
    throw 'Release publication requires format-v4 remote acceptance evidence.'
}
if ([string]$evidence.hexagon_sha -cne $HexagonSha -or
    [string]$evidence.hl2rp_sha -cne $HL2RPSha -or
    [string]$evidence.source_fingerprint -cne $releaseInputs.Fingerprint) {
    throw 'Release evidence is not bound to the exact clean source pair and effective-input fingerprint.'
}
$runIdValue = [Guid]::Empty
if (-not [Guid]::TryParse([string]$evidence.run_id, [ref]$runIdValue) -or
    $runIdValue -eq [Guid]::Empty) {
    throw 'Release evidence run_id must be a non-empty GUID.'
}
$runId = $runIdValue.ToString('D')
$tag = "sbox-evidence-$runId"

Assert-OutsideRepository -Path $evidencePath
if ([string]::IsNullOrWhiteSpace($BundlePath)) {
    $BundlePath = Join-Path (Split-Path -Parent $evidencePath) "$tag.zip"
}
Assert-OutsideRepository -Path $BundlePath

[void](Invoke-GitHub -Arguments @('auth', 'status'))
$immutable = ConvertFrom-GitHubJson -Result (Invoke-GitHub -Arguments @(
    'api', "repos/$HL2RPRepository/immutable-releases"))
if ($immutable.enabled -isnot [bool] -or -not $immutable.enabled) {
    throw "Immutable releases are not enabled for '$HL2RPRepository'."
}

$bundle = New-ReleaseEvidenceBundle -EvidencePath $evidencePath -OutputPath $BundlePath
$assetName = [System.IO.Path]::GetFileName($bundle.Path)
$assetDigest = "sha256:$($bundle.Sha256)"
$assetLength = (Get-Item -LiteralPath $bundle.Path).Length
$releaseBody = @(
    "Hexagon SHA: $HexagonSha",
    "HL2RP SHA: $HL2RPSha",
    "Run ID: $runId",
    "Source fingerprint: $($releaseInputs.Fingerprint)",
    "Bundle SHA-256: $($bundle.Sha256)"
) -join "`n"

if (-not $PSCmdlet.ShouldProcess(
    "$HexagonRepository@$HexagonSha and $HL2RPRepository@$HL2RPSha",
    "Publish immutable coordinated release evidence $tag")) {
    Write-Host "Preflight and deterministic bundle construction passed: $($bundle.Path)" -ForegroundColor Yellow
    return
}

$statusPublicationStarted = $false
$releaseUrl = ''
try {
    # Set this before the first POST: a failed response can be ambiguous even
    # when GitHub already committed the status, so compensation must be tried.
    $statusPublicationStarted = $true
    foreach ($target in @(
        [pscustomobject]@{ Repository = $HexagonRepository; Sha = $HexagonSha },
        [pscustomobject]@{ Repository = $HL2RPRepository; Sha = $HL2RPSha })) {
        Publish-Status -Repository $target.Repository -Sha $target.Sha -State pending `
            -Description "Validating immutable release evidence $runId"
    }
    & $VerificationScript `
        -SchemaRoot $schemaRootPath `
        -RemoteAcceptanceEvidence $evidencePath `
        -HexagonSha $HexagonSha `
        -HL2RPSha $HL2RPSha
    if ($LASTEXITCODE -ne 0) {
        throw "Full local verification failed with exit code $LASTEXITCODE."
    }

    $postVerificationInputs = Get-EffectiveReleaseInputs -Repositories @(
        [pscustomobject]@{ Label = 'hexagon'; Root = $hexagonRootPath },
        [pscustomobject]@{ Label = 'hl2rp-hexagon'; Root = $schemaRootPath }) -RequireClean
    if ($postVerificationInputs.Fingerprint -cne $releaseInputs.Fingerprint -or
        (Get-Head -Root $hexagonRootPath) -cne $HexagonSha -or
        (Get-Head -Root $schemaRootPath) -cne $HL2RPSha) {
        throw 'Release inputs or exact source heads changed during verification.'
    }
    $postVerificationLock = Get-Content -LiteralPath (
        Join-Path $schemaRootPath 'hexagon.lock.json') -Raw | ConvertFrom-Json
    if ([string]$postVerificationLock.repository -cne $HexagonRepository -or
        [string]$postVerificationLock.commit -cne $HexagonSha) {
        throw 'HL2RP lock consistency changed during verification.'
    }

    # The bundle snapshotted the evidence before the multi-minute verify window, and
    # the evidence file necessarily lives outside both worktrees, so the fingerprint
    # recheck above cannot cover it. Re-read, re-bind, and canonicalize identically:
    # a mismatch means verification validated different bytes than the bundle carries.
    $postVerificationEvidence = ConvertFrom-ReleaseEvidenceJson -Json (
        Get-Content -LiteralPath $evidencePath -Raw)
    $postVerificationCanonical = [System.Text.Encoding]::UTF8.GetBytes(
        ($postVerificationEvidence | ConvertTo-Json -Depth 20 -Compress))
    if ((Get-LowerSha256Bytes -Bytes $postVerificationCanonical) -cne $bundle.EvidenceSha256 -or
        [string]$postVerificationEvidence.hexagon_sha -cne $HexagonSha -or
        [string]$postVerificationEvidence.hl2rp_sha -cne $HL2RPSha -or
        [string]$postVerificationEvidence.source_fingerprint -cne $releaseInputs.Fingerprint -or
        [string]$postVerificationEvidence.run_id -cne $runId) {
        throw 'Release evidence changed during verification.'
    }
    $freshBundleDigest = "sha256:$((Get-FileHash -LiteralPath $bundle.Path -Algorithm SHA256).Hash.ToLowerInvariant())"
    if ($freshBundleDigest -cne $assetDigest) {
        throw 'Release evidence bundle bytes changed during verification.'
    }

    $releaseResult = Invoke-GitHub -Arguments @(
        'api', "repos/$HL2RPRepository/releases/tags/$tag") -AllowFailure
    if ($releaseResult.ExitCode -ne 0) {
        [void](Invoke-GitHub -Arguments @(
            'release', 'create', $tag, $bundle.Path,
            '--repo', $HL2RPRepository,
            '--target', $HL2RPSha,
            '--title', "Hexagon s&box evidence $runId",
            '--notes', $releaseBody,
            '--prerelease', '--draft'))
        [void](Invoke-GitHub -Arguments @(
            'release', 'edit', $tag, '--repo', $HL2RPRepository, '--draft=false'))
    }

    $release = $null
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        $release = ConvertFrom-GitHubJson -Result (Invoke-GitHub -Arguments @(
            'api', "repos/$HL2RPRepository/releases/tags/$tag"))
        if ($release.immutable -is [bool] -and $release.immutable) { break }
        if ($attempt -lt 5) { Start-Sleep -Seconds 1 }
    }
    $matchingAssets = @($release.assets | Where-Object { $_.name -ceq $assetName })
    if ([string]$release.tag_name -cne $tag -or
        $release.immutable -isnot [bool] -or -not $release.immutable -or
        $release.draft -isnot [bool] -or $release.draft -or
        $release.prerelease -isnot [bool] -or -not $release.prerelease -or
        [string]$release.target_commitish -cne $HL2RPSha -or
        [string]$release.body -cne $releaseBody -or
        $matchingAssets.Count -ne 1 -or
        [string]$matchingAssets[0].digest -cne $assetDigest -or
        [long]$matchingAssets[0].size -ne $assetLength) {
        throw "Release '$tag' is not the exact immutable evidence release for this run and digest."
    }
    if ((Get-ReleaseTagCommit -Repository $HL2RPRepository -Tag $tag) -cne $HL2RPSha) {
        throw "Release tag '$tag' does not resolve to the exact HL2RP source commit."
    }
    $releaseUrl = [string]$release.html_url
    if ([string]::IsNullOrWhiteSpace($releaseUrl)) {
        throw "Release '$tag' has no publication URL."
    }

    foreach ($target in @(
        [pscustomobject]@{ Repository = $HexagonRepository; Sha = $HexagonSha },
        [pscustomobject]@{ Repository = $HL2RPRepository; Sha = $HL2RPSha })) {
        Publish-Status -Repository $target.Repository -Sha $target.Sha -State success `
            -Description "Evidence $runId sha256:$($bundle.Sha256)" `
            -TargetUrl $releaseUrl
    }
}
catch {
    $original = $_
    if ($statusPublicationStarted) {
        try { Publish-PairedFailure -RunId $runId -TargetUrl $releaseUrl }
        catch { throw "$($original.Exception.Message) Compensation also failed: $($_.Exception.Message)" }
    }
    throw $original
}

Write-Host "Published immutable release evidence $tag for the coordinated source pair." -ForegroundColor Green
