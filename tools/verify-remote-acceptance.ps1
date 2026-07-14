[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $EvidencePath,

    [Parameter()]
    [string] $HexagonRoot = (Split-Path -Parent $PSScriptRoot),

    [Parameter(Mandatory)]
    [string] $SchemaRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^[a-f0-9]{40}$')]
    [string] $HexagonSha,

    [Parameter(Mandatory)]
    [ValidatePattern('^[a-f0-9]{40}$')]
    [string] $HL2RPSha,

    [Parameter()]
    [switch] $WriteTemplate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredObservations = @(
    'character_ownership_isolated',
    'capability_denials_enforced',
    'both_remote_clients_ready',
    'private_snapshots_isolated',
    'character_switch_revokes_state',
    'combat_remote_flow',
    'scanner_remote_flow',
    'restraint_search_remote_flow',
    'vendor_remote_flow',
    'storage_remote_flow',
    'world_item_remote_flow',
    'restart_restores_all_state'
)

function Get-RepositoryHead {
    param([Parameter(Mandatory)][string] $Root)

    $global:LASTEXITCODE = 0
    $head = (& git -C $Root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -cnotmatch '^[a-f0-9]{40}$') {
        throw "Could not resolve a lowercase full Git HEAD for '$Root'."
    }
    return $head
}

function Get-SourceFingerprint {
    param([Parameter(Mandatory)][object[]] $Repositories)

    $sha = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($repository in $Repositories) {
            $resolved = (Resolve-Path -LiteralPath ([string]$repository.Root)).Path.TrimEnd('\')
            $global:LASTEXITCODE = 0
            [string[]]$tracked = @(& git -C $resolved ls-files --cached --others --exclude-standard)
            if ($LASTEXITCODE -ne 0) {
                throw "Could not enumerate tracked release inputs for '$resolved'."
            }
            [Array]::Sort($tracked, [System.StringComparer]::Ordinal)
            foreach ($trackedPath in $tracked) {
                $relative = $trackedPath.Replace('\', '/')
                # Evidence manifests are outputs of this verifier, not release inputs.
                if ($relative -match '(^|/)remote-acceptance(?:[.-][^/]*)?\.json$') { continue }
                $filePath = Join-Path $resolved $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
                if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
                    # A dirty pre-commit worktree can contain an indexed deletion. The release
                    # publisher separately requires a clean checkout, where every enumerated
                    # path is tracked and present; template verification still fingerprints the
                    # exact current tree used by local remediation checks.
                    continue
                }
                $pathBytes = [System.Text.Encoding]::UTF8.GetBytes(
                    "$([string]$repository.Label)/$relative`n")
                $sha.AppendData($pathBytes)
                $stream = [System.IO.File]::OpenRead($filePath)
                try {
                    $buffer = [byte[]]::new(65536)
                    while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $sha.AppendData($buffer, 0, $read)
                    }
                }
                finally {
                    $stream.Dispose()
                }
            }
        }
        return ([BitConverter]::ToString($sha.GetHashAndReset()) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$hexagonRootPath = (Resolve-Path -LiteralPath $HexagonRoot).Path
$schemaRootPath = (Resolve-Path -LiteralPath $SchemaRoot).Path
if ((Get-RepositoryHead -Root $hexagonRootPath) -cne $HexagonSha) {
    throw "Hexagon checkout HEAD does not match evidence SHA '$HexagonSha'."
}
if ((Get-RepositoryHead -Root $schemaRootPath) -cne $HL2RPSha) {
    throw "HL2RP checkout HEAD does not match evidence SHA '$HL2RPSha'."
}
$fingerprint = Get-SourceFingerprint -Repositories @(
    [pscustomobject]@{ Label = 'hexagon'; Root = $hexagonRootPath },
    [pscustomobject]@{ Label = 'hl2rp-hexagon'; Root = $schemaRootPath }
)
$evidenceFullPath = [System.IO.Path]::GetFullPath($EvidencePath)
$attestationStatement = 'I attest that I personally executed the documented Hexagon v2 dedicated-server/two-client run and that every passing observation references contemporaneous artifacts from that run.'

if ($WriteTemplate) {
    $observations = foreach ($name in $requiredObservations) {
        [ordered]@{
            name = $name
            passed = $false
            notes = ''
            artifact_ids = @()
        }
    }
    $template = [ordered]@{
        format = 'hexagon-v2-manual-remote-acceptance/3'
        evidence_kind = 'manual_operator_attestation'
        schema = 'hl2rp'
        runbook = 'hexagon/docs/testing.md#manual-dedicated-server-two-client-runbook'
        run_id = [Guid]::NewGuid().ToString('D')
        started_at_utc = ''
        completed_at_utc = ''
        hexagon_sha = $HexagonSha
        hl2rp_sha = $HL2RPSha
        source_fingerprint = $fingerprint
        operator = [ordered]@{
            name = ''
            attested_at_utc = ''
            statement = $attestationStatement
        }
        environment = [ordered]@{
            server_mode = 'dedicated'
            server_instance = ''
            client_a_instance = ''
            client_b_instance = ''
        }
        clients = @(
            [ordered]@{ role = 'A'; account_id = '0'; remote = $true },
            [ordered]@{ role = 'B'; account_id = '0'; remote = $true }
        )
        recovery = [ordered]@{
            pre_shutdown = [ordered]@{
                sequence = 0
                digest = ''
                artifact_id = 'server-pre-shutdown-log'
            }
            post_restart = [ordered]@{
                sequence = 0
                digest = ''
                artifact_id = 'server-post-restart-log'
            }
        }
        observations = @($observations)
        artifacts = @(
            [ordered]@{ id = 'server-pre-shutdown-log'; role = 'server'; kind = 'log'; path = ''; sha256 = ''; captured_at_utc = '' },
            [ordered]@{ id = 'server-post-restart-log'; role = 'server'; kind = 'log'; path = ''; sha256 = ''; captured_at_utc = '' },
            [ordered]@{ id = 'client-a-log'; role = 'client_a'; kind = 'log'; path = ''; sha256 = ''; captured_at_utc = '' },
            [ordered]@{ id = 'client-b-log'; role = 'client_b'; kind = 'log'; path = ''; sha256 = ''; captured_at_utc = '' }
        )
    }
    $directory = Split-Path -Parent $evidenceFullPath
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory)) {
        [void](New-Item -ItemType Directory -Path $directory)
    }
    $template | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $evidenceFullPath -Encoding utf8
    Write-Host "Manual remote-acceptance evidence template written to '$evidenceFullPath'." -ForegroundColor Yellow
    Write-Warning 'This template records a human-observed run. The verifier checks completeness, source binding, and artifact integrity; it does not execute or independently prove the remote scenarios.'
    return
}

if (-not (Test-Path -LiteralPath $evidenceFullPath -PathType Leaf)) {
    throw "Manual remote-acceptance evidence was not found at '$evidenceFullPath'."
}
$evidenceJson = Get-Content -LiteralPath $evidenceFullPath -Raw
$convertFromJson = Get-Command ConvertFrom-Json
$evidence = if ($convertFromJson.Parameters.ContainsKey('DateKind')) {
    $evidenceJson | ConvertFrom-Json -DateKind String
}
else {
    $evidenceJson | ConvertFrom-Json
}
if ($evidence.format -cne 'hexagon-v2-manual-remote-acceptance/3' -or
    $evidence.evidence_kind -cne 'manual_operator_attestation' -or
    $evidence.schema -cne 'hl2rp') {
    throw 'Remote acceptance evidence is not the supported manual-attestation format.'
}
if ($evidence.runbook -cne 'hexagon/docs/testing.md#manual-dedicated-server-two-client-runbook') {
    throw 'Remote acceptance evidence targets an unknown runbook revision.'
}
if ([string]$evidence.hexagon_sha -cne $HexagonSha -or
    [string]$evidence.hl2rp_sha -cne $HL2RPSha) {
    throw 'Remote acceptance evidence does not target both exact source SHAs.'
}
if ($evidence.source_fingerprint -cne $fingerprint) {
    throw "Remote acceptance evidence targets a different source fingerprint. Expected '$fingerprint'."
}

$runId = [Guid]::Empty
if (-not [Guid]::TryParse([string]$evidence.run_id, [ref]$runId) -or $runId -eq [Guid]::Empty) {
    throw 'Remote acceptance run_id is not a non-empty GUID.'
}
$started = [DateTimeOffset]::MinValue
$completed = [DateTimeOffset]::MinValue
$attested = [DateTimeOffset]::MinValue
$maximumAcceptedFutureTimestamp = [DateTimeOffset]::UtcNow.AddMinutes(5)
if (-not [DateTimeOffset]::TryParse([string]$evidence.started_at_utc, [ref]$started) -or
    $started.Offset -ne [TimeSpan]::Zero -or
    -not [DateTimeOffset]::TryParse([string]$evidence.completed_at_utc, [ref]$completed) -or
    $completed.Offset -ne [TimeSpan]::Zero -or $completed -le $started -or
    -not [DateTimeOffset]::TryParse([string]$evidence.operator.attested_at_utc, [ref]$attested) -or
    $attested.Offset -ne [TimeSpan]::Zero -or $attested -lt $completed) {
    throw 'Run and attestation timestamps must be ordered UTC DateTimeOffset values.'
}
if ([string]::IsNullOrWhiteSpace([string]$evidence.operator.name) -or
    [string]$evidence.operator.statement -cne $attestationStatement) {
    throw 'A named operator must provide the exact manual-run attestation statement.'
}
if ($evidence.environment.server_mode -cne 'dedicated') {
    throw 'The attested server mode must be dedicated.'
}
$instances = @(
    [string]$evidence.environment.server_instance,
    [string]$evidence.environment.client_a_instance,
    [string]$evidence.environment.client_b_instance
)
if ($instances.Where({ [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 -or
    @($instances | Sort-Object -Unique).Count -ne 3) {
    throw 'Evidence must name three distinct non-empty server/client process instances.'
}

$clients = @($evidence.clients)
if ($clients.Count -ne 2 -or @(Compare-Object @('A', 'B') @($clients.role | Sort-Object)).Count -ne 0 -or
    $clients.Where({ $_.remote -isnot [bool] -or $_.remote -ne $true }).Count -ne 0) {
    throw 'Evidence must identify two distinct authenticated remote client roles and accounts using actual JSON Boolean true remote flags.'
}
$authenticatedAccounts = [System.Collections.Generic.HashSet[UInt64]]::new()
foreach ($client in $clients) {
    $account = [UInt64]0
    if (-not [UInt64]::TryParse([string]$client.account_id, [ref]$account) -or $account -eq 0) {
        throw "Remote client role '$($client.role)' has an invalid authenticated account ID."
    }
    if (-not $authenticatedAccounts.Add($account)) {
        throw 'Evidence must identify two distinct authenticated account IDs after numeric parsing.'
    }
}
$evidenceDirectory = Split-Path -Parent $evidenceFullPath
$artifacts = @($evidence.artifacts)
$artifactIds = @{}
$artifactRecords = @{}
$artifactPathComparer = if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
    [System.StringComparer]::OrdinalIgnoreCase
}
else {
    [System.StringComparer]::Ordinal
}
$artifactPaths = [System.Collections.Generic.HashSet[string]]::new($artifactPathComparer)
foreach ($artifact in $artifacts) {
    $id = [string]$artifact.id
    if ([string]::IsNullOrWhiteSpace($id) -or $artifactIds.ContainsKey($id)) {
        throw "Artifact ID '$id' is blank or duplicated."
    }
    if ([string]$artifact.kind -notin @('log', 'screenshot', 'video', 'trace') -or
        [string]$artifact.role -notin @('server', 'client_a', 'client_b', 'observer')) {
        throw "Artifact '$id' has an unsupported kind or role."
    }
    $captured = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$artifact.captured_at_utc, [ref]$captured) -or
        $captured.Offset -ne [TimeSpan]::Zero -or $captured -lt $started -or $captured -gt $completed -or
        $captured -gt $maximumAcceptedFutureTimestamp) {
        throw "Artifact '$id' has no in-run UTC capture timestamp."
    }
    $artifactPath = if ([System.IO.Path]::IsPathRooted([string]$artifact.path)) {
        [string]$artifact.path
    }
    else {
        Join-Path $evidenceDirectory ([string]$artifact.path)
    }
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "Remote acceptance artifact '$artifactPath' is missing."
    }
    $artifactPath = [System.IO.Path]::GetFullPath(
        (Resolve-Path -LiteralPath $artifactPath).Path)
    if (-not $artifactPaths.Add($artifactPath)) {
        throw "Remote acceptance artifact path '$artifactPath' is reused by more than one artifact record."
    }
    if ([string]$artifact.sha256 -notmatch '^[a-f0-9]{64}$' -or
        (Get-FileSha256 -Path $artifactPath) -cne [string]$artifact.sha256) {
        throw "Remote acceptance artifact hash mismatch for '$id'."
    }
    $artifactIds[$id] = $artifactPath
    $artifactRecords[$id] = $artifact
}
if ($completed -gt $maximumAcceptedFutureTimestamp -or
    $attested -gt $maximumAcceptedFutureTimestamp) {
    throw 'Run completion and attestation timestamps must be no later than five minutes in the future.'
}
foreach ($role in @('client_a', 'client_b')) {
    if (@($artifacts | Where-Object { $_.role -ceq $role -and $_.kind -ceq 'log' }).Count -ne 1) {
        throw "Evidence must contain exactly one contemporaneous '$role' log artifact."
    }
}
if (@($artifacts | Where-Object { $_.role -ceq 'server' -and $_.kind -ceq 'log' }).Count -ne 2) {
    throw "Evidence must contain exactly two contemporaneous 'server' log artifacts."
}

function Get-VerifiedRecoverySnapshot {
    param(
        [Parameter(Mandatory)][string] $Phase,
        [Parameter(Mandatory)][object] $Snapshot
    )

    $sequence = [long]0
    if (-not [long]::TryParse([string]$Snapshot.sequence, [ref]$sequence) -or $sequence -le 0 -or
        [string]$Snapshot.digest -cnotmatch '^[a-f0-9]{64}$') {
        throw "Remote acceptance recovery snapshot '$Phase' has an invalid sequence or digest."
    }

    $artifactId = [string]$Snapshot.artifact_id
    if ([string]::IsNullOrWhiteSpace($artifactId) -or -not $artifactIds.ContainsKey($artifactId)) {
        throw "Remote acceptance recovery snapshot '$Phase' references missing artifact '$artifactId'."
    }
    $artifact = $artifactRecords[$artifactId]
    if ([string]$artifact.role -cne 'server' -or [string]$artifact.kind -cne 'log') {
        throw "Remote acceptance recovery snapshot '$Phase' must reference a hashed server log artifact."
    }

    $logText = Get-Content -LiteralPath $artifactIds[$artifactId] -Raw
    $phasePattern = [regex]::Escape($Phase)
    $markerPattern = "HL2RP_RECOVERY_SNAPSHOT[ `t]+phase=$phasePattern(?=[ `t])"
    $markerMatches = [regex]::Matches($logText, $markerPattern)
    if ($markerMatches.Count -ne 1) {
        throw "Server log artifact '$artifactId' must contain exactly one recovery marker for phase '$Phase'; found $($markerMatches.Count)."
    }

    $snapshotPattern = "HL2RP_RECOVERY_SNAPSHOT[ `t]+phase=$phasePattern[ `t]+sequence=(?<sequence>[1-9][0-9]*)[ `t]+digest=(?<digest>[a-f0-9]{64})(?=[ `t]*`r?$)"
    $snapshotMatches = [regex]::Matches(
        $logText,
        $snapshotPattern,
        [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if ($snapshotMatches.Count -ne 1) {
        throw "Server log artifact '$artifactId' does not contain one canonical '$Phase' recovery snapshot."
    }

    $loggedSequence = [long]::Parse(
        $snapshotMatches[0].Groups['sequence'].Value,
        [System.Globalization.CultureInfo]::InvariantCulture)
    $loggedDigest = $snapshotMatches[0].Groups['digest'].Value
    if ($loggedSequence -ne $sequence -or $loggedDigest -cne [string]$Snapshot.digest) {
        throw "Recovery snapshot '$Phase' does not match its hashed server log artifact '$artifactId'."
    }

    return [pscustomobject]@{
        Sequence = $sequence
        Digest = [string]$Snapshot.digest
        ArtifactId = $artifactId
    }
}

$preShutdown = Get-VerifiedRecoverySnapshot `
    -Phase 'pre_shutdown' `
    -Snapshot $evidence.recovery.pre_shutdown
$postRestart = Get-VerifiedRecoverySnapshot `
    -Phase 'post_restart' `
    -Snapshot $evidence.recovery.post_restart
if ($preShutdown.ArtifactId -eq $postRestart.ArtifactId -or
    $artifactPathComparer.Equals(
        [string]$artifactIds[$preShutdown.ArtifactId],
        [string]$artifactIds[$postRestart.ArtifactId])) {
    throw 'Pre-shutdown and post-restart recovery snapshots must reference distinct server log artifacts and files.'
}
if ($preShutdown.Sequence -ne $postRestart.Sequence -or
    $preShutdown.Digest -cne $postRestart.Digest) {
    throw 'Pre-shutdown and post-restart recovery sequence/digest must match exactly.'
}

$observations = @($evidence.observations)
if ($observations.Count -ne $requiredObservations.Count -or
    @($observations.name | Sort-Object -Unique).Count -ne $requiredObservations.Count) {
    throw 'Evidence must contain exactly one observation for every runbook scenario.'
}
foreach ($name in $requiredObservations) {
    $observation = @($observations | Where-Object { $_.name -ceq $name })
    if ($observation.Count -ne 1 -or
        $observation[0].passed -isnot [bool] -or
        $observation[0].passed -ne $true) {
        throw "Manual remote acceptance observation '$name' is absent or does not use actual JSON Boolean true for passed."
    }
    if ([string]::IsNullOrWhiteSpace([string]$observation[0].notes) -or
        [string]$observation[0].notes -match '^(pass|passed|ok)$') {
        throw "Observation '$name' requires a concrete description of what the operator observed."
    }
    $references = @($observation[0].artifact_ids)
    if ($references.Count -eq 0) {
        throw "Observation '$name' must reference at least one hashed artifact."
    }
    foreach ($reference in $references) {
        if (-not $artifactIds.ContainsKey([string]$reference)) {
            throw "Observation '$name' references unknown artifact '$reference'."
        }
    }
}

$restartObservation = @($observations | Where-Object { $_.name -ceq 'restart_restores_all_state' })[0]
$restartReferences = @($restartObservation.artifact_ids)
foreach ($recoveryArtifactId in @($preShutdown.ArtifactId, $postRestart.ArtifactId) | Sort-Object -Unique) {
    if ($recoveryArtifactId -cnotin $restartReferences) {
        throw "Observation 'restart_restores_all_state' must reference recovery artifact '$recoveryArtifactId'."
    }
}

Write-Host "Manual remote-acceptance evidence is complete and artifact-consistent for source $fingerprint." -ForegroundColor Green
Write-Warning 'This result validates a named operator attestation; it does not execute or independently prove the two-client scenarios.'
