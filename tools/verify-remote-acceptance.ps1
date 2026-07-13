[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $EvidencePath,

    [Parameter()]
    [string] $HexagonRoot = (Split-Path -Parent $PSScriptRoot),

    [Parameter(Mandatory)]
    [string] $SchemaRoot,

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

function Get-SourceFingerprint {
    param([Parameter(Mandatory)][string[]] $Roots)

    $sha = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($root in $Roots) {
            $resolved = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\')
            $files = Get-ChildItem -LiteralPath $resolved -Recurse -File | Where-Object {
                $_.FullName -notmatch '[\\/](\.git|bin|obj|\.vs|Libraries)[\\/]' -and
                $_.Name -notmatch '^remote-acceptance(?:[.-].*)?\.json$' -and
                $_.Extension -in @('.cs', '.razor', '.scss', '.json', '.scene', '.sbproj', '.ps1', '.md')
            } | Sort-Object FullName
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($resolved.Length + 1).Replace('\', '/')
                $pathBytes = [System.Text.Encoding]::UTF8.GetBytes("$relative`n")
                $sha.AppendData($pathBytes)
                $stream = [System.IO.File]::OpenRead($file.FullName)
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
$fingerprint = Get-SourceFingerprint -Roots @($hexagonRootPath, $schemaRootPath)
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
        format = 'hexagon-v2-manual-remote-acceptance/1'
        evidence_kind = 'manual_operator_attestation'
        schema = 'hl2rp'
        runbook = 'hexagon/docs/testing.md#manual-dedicated-server-two-client-runbook'
        run_id = [Guid]::NewGuid().ToString('D')
        started_at_utc = ''
        completed_at_utc = ''
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
        recovery = [ordered]@{ sequence = 0; digest = '' }
        observations = @($observations)
        artifacts = @(
            [ordered]@{ id = 'server-log'; role = 'server'; kind = 'log'; path = ''; sha256 = ''; captured_at_utc = '' },
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
$evidence = Get-Content -LiteralPath $evidenceFullPath -Raw | ConvertFrom-Json
if ($evidence.format -cne 'hexagon-v2-manual-remote-acceptance/1' -or
    $evidence.evidence_kind -cne 'manual_operator_attestation' -or
    $evidence.schema -cne 'hl2rp') {
    throw 'Remote acceptance evidence is not the supported manual-attestation format.'
}
if ($evidence.runbook -cne 'hexagon/docs/testing.md#manual-dedicated-server-two-client-runbook') {
    throw 'Remote acceptance evidence targets an unknown runbook revision.'
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
    @($clients.account_id | Sort-Object -Unique).Count -ne 2 -or
    $clients.Where({ -not [bool]$_.remote }).Count -ne 0) {
    throw 'Evidence must identify two distinct authenticated remote client roles and accounts.'
}
foreach ($client in $clients) {
    $account = [UInt64]0
    if (-not [UInt64]::TryParse([string]$client.account_id, [ref]$account) -or $account -eq 0) {
        throw "Remote client role '$($client.role)' has an invalid authenticated account ID."
    }
}
if ([long]$evidence.recovery.sequence -le 0 -or [string]$evidence.recovery.digest -notmatch '^[a-f0-9]{64}$') {
    throw 'Remote acceptance recovery sequence/digest is invalid.'
}

$evidenceDirectory = Split-Path -Parent $evidenceFullPath
$artifacts = @($evidence.artifacts)
$artifactIds = @{}
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
        $captured.Offset -ne [TimeSpan]::Zero -or $captured -lt $started -or $captured -gt $completed) {
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
    if ([string]$artifact.sha256 -notmatch '^[a-f0-9]{64}$' -or
        (Get-FileSha256 -Path $artifactPath) -cne [string]$artifact.sha256) {
        throw "Remote acceptance artifact hash mismatch for '$id'."
    }
    $artifactIds[$id] = $artifactPath
}
foreach ($role in @('server', 'client_a', 'client_b')) {
    if (@($artifacts | Where-Object { $_.role -ceq $role -and $_.kind -ceq 'log' }).Count -ne 1) {
        throw "Evidence must contain exactly one contemporaneous '$role' log artifact."
    }
}

$observations = @($evidence.observations)
if ($observations.Count -ne $requiredObservations.Count -or
    @($observations.name | Sort-Object -Unique).Count -ne $requiredObservations.Count) {
    throw 'Evidence must contain exactly one observation for every runbook scenario.'
}
foreach ($name in $requiredObservations) {
    $observation = @($observations | Where-Object { $_.name -ceq $name })
    if ($observation.Count -ne 1 -or $observation[0].passed -ne $true) {
        throw "Manual remote acceptance observation '$name' is absent or was not attested as passing."
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

Write-Host "Manual remote-acceptance evidence is complete and artifact-consistent for source $fingerprint." -ForegroundColor Green
Write-Warning 'This result validates a named operator attestation; it does not execute or independently prove the two-client scenarios.'
