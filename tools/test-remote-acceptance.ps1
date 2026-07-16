[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifier = Join-Path $PSScriptRoot 'verify-remote-acceptance.ps1'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("hexagon-remote-evidence-test-" + [Guid]::NewGuid().ToString('N'))
$hexagonRoot = Join-Path $tempRoot 'hexagon'
$schemaRoot = Join-Path $tempRoot 'hl2rp-hexagon'
$evidenceRoot = Join-Path $tempRoot 'evidence'
$evidencePath = Join-Path $evidenceRoot 'remote-acceptance.json'

function Invoke-GitChecked {
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string[]] $Arguments
    )

    $global:LASTEXITCODE = 0
    $output = @(& git -C $Root @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed in '$Root': $($output -join [Environment]::NewLine)"
    }
    return $output
}

function Set-EvidenceFileHash {
    param(
        [Parameter(Mandatory)][object] $Evidence,
        [Parameter(Mandatory)][string] $ArtifactId
    )

    $artifact = @($Evidence.artifacts | Where-Object { $_.id -ceq $ArtifactId })
    if ($artifact.Count -ne 1) {
        throw "Fixture artifact '$ArtifactId' is absent or duplicated."
    }
    $artifact[0].sha256 = (Get-FileHash -LiteralPath (
        Join-Path $evidenceRoot ([string]$artifact[0].path)) -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Save-Evidence {
    param([Parameter(Mandatory)][object] $Evidence)
    $Evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $evidencePath -Encoding utf8
}

function ConvertFrom-EvidenceJson {
    param([Parameter(Mandatory)][string] $Json)

    $convertFromJson = Get-Command ConvertFrom-Json
    if ($convertFromJson.Parameters.ContainsKey('DateKind')) {
        return $Json | ConvertFrom-Json -DateKind String
    }
    return $Json | ConvertFrom-Json
}

function Invoke-Verifier {
    & $verifier `
        -EvidencePath $evidencePath `
        -HexagonRoot $hexagonRoot `
        -SchemaRoot $schemaRoot `
        -HexagonSha $hexagonSha `
        -HL2RPSha $schemaSha
}

function Assert-VerifierFailure {
    param(
        [Parameter(Mandatory)][string] $ExpectedMessage,
        [Parameter(Mandatory)][scriptblock] $Action
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "Expected verifier failure containing '$ExpectedMessage', got: $($_.Exception.Message)"
        }
        return
    }
    throw "Expected verifier failure containing '$ExpectedMessage', but validation passed."
}

try {
    [void](New-Item -ItemType Directory -Path $hexagonRoot, $schemaRoot, $evidenceRoot)
    foreach ($repository in @($hexagonRoot, $schemaRoot)) {
        [void](Invoke-GitChecked -Root $repository -Arguments @('init', '-q'))
        [void](Invoke-GitChecked -Root $repository -Arguments @('config', 'user.name', 'Hexagon Test'))
        [void](Invoke-GitChecked -Root $repository -Arguments @('config', 'user.email', 'hexagon-test@example.invalid'))
        [void](Invoke-GitChecked -Root $repository -Arguments @('config', 'commit.gpgsign', 'false'))
        Set-Content -LiteralPath (Join-Path $repository 'tracked.txt') -Value 'source-bound fixture' -Encoding utf8
        [void](Invoke-GitChecked -Root $repository -Arguments @('add', 'tracked.txt'))
        [void](Invoke-GitChecked -Root $repository -Arguments @('commit', '-q', '-m', 'fixture'))
    }

    $hexagonSha = ([string](Invoke-GitChecked -Root $hexagonRoot -Arguments @('rev-parse', 'HEAD'))).Trim()
    $schemaSha = ([string](Invoke-GitChecked -Root $schemaRoot -Arguments @('rev-parse', 'HEAD'))).Trim()
    & $verifier `
        -EvidencePath $evidencePath `
        -HexagonRoot $hexagonRoot `
        -SchemaRoot $schemaRoot `
        -HexagonSha $hexagonSha `
        -HL2RPSha $schemaSha `
        -WriteTemplate

    $evidence = ConvertFrom-EvidenceJson -Json (Get-Content -LiteralPath $evidencePath -Raw)
    $fixtureNow = [DateTimeOffset]::UtcNow
    $captured = $fixtureNow.AddMinutes(-2).ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    $evidence.started_at_utc = $fixtureNow.AddMinutes(-3).ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    $evidence.completed_at_utc = $fixtureNow.AddMinutes(-1).ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    $evidence.operator.name = 'Verifier Contract Test'
    $evidence.operator.attested_at_utc = $fixtureNow.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    $evidence.environment.server_instance = 'dedicated-initial-and-restart'
    $evidence.environment.client_a_instance = 'remote-client-a'
    $evidence.environment.client_b_instance = 'remote-client-b'
    $evidence.clients[0].account_id = '76561198000000001'
    $evidence.clients[1].account_id = '76561198000000002'

    $digest = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'
    $sequence = 42
    $evidence.recovery.pre_shutdown.sequence = $sequence
    $evidence.recovery.pre_shutdown.digest = $digest
    $evidence.recovery.post_restart.sequence = $sequence
    $evidence.recovery.post_restart.digest = $digest

    $paths = @{
        'server-pre-shutdown-log' = 'server-pre-shutdown.log'
        'server-post-restart-log' = 'server-post-restart.log'
        'client-a-log' = 'client-a.log'
        'client-b-log' = 'client-b.log'
        'server-environment' = 'server-environment.json'
        'client-a-environment' = 'client-a-environment.json'
        'client-b-environment' = 'client-b-environment.json'
    }
    foreach ($artifact in $evidence.artifacts) {
        $artifact.path = $paths[[string]$artifact.id]
        $artifact.captured_at_utc = $captured
    }
    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['server-pre-shutdown-log']) `
        -Value "2026/07/13 20:00:00.0000`t[Generic] HL2RP_RECOVERY_SNAPSHOT phase=pre_shutdown sequence=$sequence digest=$digest`t" `
        -Encoding utf8
    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['server-post-restart-log']) `
        -Value "2026/07/13 20:01:00.0000`t[Generic] HL2RP_RECOVERY_SNAPSHOT phase=post_restart sequence=$sequence digest=$digest`t" `
        -Encoding utf8
    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['client-a-log']) -Value 'authenticated remote client A' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['client-b-log']) -Value 'authenticated remote client B' -Encoding utf8
    foreach ($role in @('server', 'client_a', 'client_b')) {
        $artifactId = if ($role -ceq 'server') { 'server-environment' } else { "$($role.Replace('_', '-'))-environment" }
        $entryPoint = if ($role -ceq 'server') { 'sbox-server.exe' } else { 'sbox.exe' }
        $entryHash = if ($role -ceq 'server') { ('a' * 64) -join '' } else { ('b' * 64) -join '' }
        $runtime = [ordered]@{
            format = 'hexagon-v2-runtime-environment/2'
            role = $role
            captured_at_utc = $captured
            os = [ordered]@{ description = 'Fixture Windows'; architecture = 'X64' }
            source = [ordered]@{
                hexagon_sha = $hexagonSha
                hl2rp_sha = $schemaSha
                effective_input_fingerprint = $evidence.source_fingerprint
            }
            dotnet = [ordered]@{
                framework = 'Microsoft.NETCore.App'
                requested_version = '10.0.0'
                version = '10.0.9'
                architecture = 'x64'
                runtime_config_file_name = if ($role -ceq 'server') {
                    'sbox-server.runtimeconfig.json'
                }
                else {
                    'sbox.runtimeconfig.json'
                }
                runtime_config_sha256 = if ($role -ceq 'server') {
                    ('1' * 64) -join ''
                }
                else {
                    ('2' * 64) -join ''
                }
            }
            sbox = [ordered]@{
                distribution = 'source_build'
                engine_source_sha = ('8' * 40) -join ''
                compatibility = [ordered]@{
                    steam_app_id = 590830
                    steam_build_id = '12345678'
                    version = 'fixture-version'
                    version_sha256 = ('e' * 64) -join ''
                }
                files = @(
                    [ordered]@{ name = 'entry_point'; file_name = $entryPoint; sha256 = $entryHash },
                    [ordered]@{ name = 'engine2'; file_name = 'engine2.dll'; sha256 = ('c' * 64) -join '' },
                    [ordered]@{ name = 'sandbox_engine'; file_name = 'Sandbox.Engine.dll'; sha256 = ('d' * 64) -join '' }
                )
            }
        }
        $runtime | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (
            Join-Path $evidenceRoot $paths[$artifactId]) -Encoding utf8
    }
    foreach ($artifactId in $paths.Keys) {
        Set-EvidenceFileHash -Evidence $evidence -ArtifactId $artifactId
    }

    foreach ($observation in $evidence.observations) {
        $observation.passed = $true
        $observation.notes = "Observed fixture behavior for $($observation.name)."
        $observation.artifact_ids = @('client-a-log')
    }
    $evidence.observations[0].artifact_ids = @('client-a-log', 'client-b-log')
    $restart = @($evidence.observations | Where-Object { $_.name -ceq 'restart_restores_all_state' })[0]
    $restart.artifact_ids = @('server-pre-shutdown-log', 'server-post-restart-log')
    Save-Evidence -Evidence $evidence

    Invoke-Verifier

    $validEvidenceJson = Get-Content -LiteralPath $evidencePath -Raw
    $validClientEnvironmentJson = Get-Content -LiteralPath (
        Join-Path $evidenceRoot $paths['client-a-environment']) -Raw

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $clientRuntime = ConvertFrom-EvidenceJson -Json $validClientEnvironmentJson
    @($clientRuntime.sbox.files | Where-Object { $_.name -ceq 'engine2' })[0].file_name = 'different.dll'
    $clientRuntime | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (
        Join-Path $evidenceRoot $paths['client-a-environment']) -Encoding utf8
    Set-EvidenceFileHash -Evidence $evidence -ArtifactId 'client-a-environment'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage "must hash exact file 'engine2.dll'" -Action { Invoke-Verifier }
    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['client-a-environment']) `
        -Value $validClientEnvironmentJson -Encoding utf8 -NoNewline

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $evidence.format = 'hexagon-v2-manual-remote-acceptance/3'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'not the supported manual-attestation format' -Action { Invoke-Verifier }

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $clientRuntime = ConvertFrom-EvidenceJson -Json $validClientEnvironmentJson
    $clientRuntime.format = 'hexagon-v2-runtime-environment/1'
    $clientRuntime | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (
        Join-Path $evidenceRoot $paths['client-a-environment']) -Encoding utf8
    Set-EvidenceFileHash -Evidence $evidence -ArtifactId 'client-a-environment'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'has an unsupported format or role' -Action { Invoke-Verifier }
    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['client-a-environment']) `
        -Value $validClientEnvironmentJson -Encoding utf8 -NoNewline

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $clientRuntime = ConvertFrom-EvidenceJson -Json $validClientEnvironmentJson
    $clientRuntime.sbox.engine_source_sha = ('9' * 40) -join ''
    $clientRuntime | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (
        Join-Path $evidenceRoot $paths['client-a-environment']) -Encoding utf8
    Set-EvidenceFileHash -Evidence $evidence -ArtifactId 'client-a-environment'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'does not match the server s&box fingerprint' -Action { Invoke-Verifier }
    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['client-a-environment']) `
        -Value $validClientEnvironmentJson -Encoding utf8 -NoNewline

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $clientRuntime = ConvertFrom-EvidenceJson -Json $validClientEnvironmentJson
    $clientRuntime.sbox.distribution = 'steam'
    $clientRuntime | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (
        Join-Path $evidenceRoot $paths['client-a-environment']) -Encoding utf8
    Set-EvidenceFileHash -Evidence $evidence -ArtifactId 'client-a-environment'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'has inconsistent runtime distribution provenance' -Action { Invoke-Verifier }
    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['client-a-environment']) `
        -Value $validClientEnvironmentJson -Encoding utf8 -NoNewline

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $clientRuntime = ConvertFrom-EvidenceJson -Json $validClientEnvironmentJson
    @($clientRuntime.sbox.files | Where-Object { $_.name -ceq 'engine2' })[0].sha256 = ('f' * 64) -join ''
    $clientRuntime | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (
        Join-Path $evidenceRoot $paths['client-a-environment']) -Encoding utf8
    Set-EvidenceFileHash -Evidence $evidence -ArtifactId 'client-a-environment'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'does not match the server engine/runtime fingerprint' -Action { Invoke-Verifier }
    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['client-a-environment']) `
        -Value $validClientEnvironmentJson -Encoding utf8 -NoNewline

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $clientRuntime = ConvertFrom-EvidenceJson -Json $validClientEnvironmentJson
    $clientRuntime.source.effective_input_fingerprint = ('f' * 64) -join ''
    $clientRuntime | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (
        Join-Path $evidenceRoot $paths['client-a-environment']) -Encoding utf8
    Set-EvidenceFileHash -Evidence $evidence -ArtifactId 'client-a-environment'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'does not match the exact source/effective-input fingerprint' -Action { Invoke-Verifier }
    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['client-a-environment']) `
        -Value $validClientEnvironmentJson -Encoding utf8 -NoNewline

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $evidence.clients[1].account_id = '076561198000000001'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'after numeric parsing' -Action { Invoke-Verifier }

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $extraArtifactPath = Join-Path $evidenceRoot 'unreferenced-observer.log'
    Set-Content -LiteralPath $extraArtifactPath -Value 'must not be published' -Encoding utf8
    $evidence.artifacts = @($evidence.artifacts) + [pscustomobject]@{
        id = 'unreferenced-observer-log'
        role = 'observer'
        kind = 'log'
        path = 'unreferenced-observer.log'
        sha256 = (Get-FileHash -LiteralPath $extraArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
        captured_at_utc = $captured
    }
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'artifacts are unreferenced' -Action { Invoke-Verifier }

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $evidence.clients[0].remote = 'false'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'actual JSON Boolean true remote flags' -Action { Invoke-Verifier }

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $evidence.observations[0].passed = 'true'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'actual JSON Boolean true for passed' -Action { Invoke-Verifier }

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $preShutdownArtifact = @($evidence.artifacts | Where-Object { $_.id -ceq 'server-pre-shutdown-log' })[0]
    $postRestartArtifact = @($evidence.artifacts | Where-Object { $_.id -ceq 'server-post-restart-log' })[0]
    $postRestartArtifact.path = Join-Path '.' ([string]$preShutdownArtifact.path)
    $postRestartArtifact.sha256 = $preShutdownArtifact.sha256
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'is reused by more than one artifact record' -Action { Invoke-Verifier }

    $futureFixtureNow = [DateTimeOffset]::UtcNow
    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $evidence.completed_at_utc = $futureFixtureNow.AddMinutes(7).ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    $evidence.operator.attested_at_utc = $futureFixtureNow.AddMinutes(8).ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'no later than five minutes in the future' -Action { Invoke-Verifier }

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $evidence.completed_at_utc = $futureFixtureNow.AddMinutes(7).ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    $evidence.operator.attested_at_utc = $futureFixtureNow.AddMinutes(8).ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    $evidence.artifacts[0].captured_at_utc = $futureFixtureNow.AddMinutes(6).ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'has no in-run UTC capture timestamp' -Action { Invoke-Verifier }

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $evidence.operator.attested_at_utc = $futureFixtureNow.AddMinutes(6).ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'no later than five minutes in the future' -Action { Invoke-Verifier }

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $evidence.recovery.post_restart.artifact_id = 'server-pre-shutdown-log'
    $restart = @($evidence.observations | Where-Object { $_.name -ceq 'restart_restores_all_state' })[0]
    $restart.artifact_ids = @('server-pre-shutdown-log', 'server-post-restart-log')
    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['server-pre-shutdown-log']) `
        -Value @(
            "2026/07/13 20:00:00.0000`t[Generic] HL2RP_RECOVERY_SNAPSHOT phase=pre_shutdown sequence=$sequence digest=$digest`t",
            "2026/07/13 20:01:00.0000`t[Generic] HL2RP_RECOVERY_SNAPSHOT phase=post_restart sequence=$sequence digest=$digest`t"
        ) `
        -Encoding utf8
    Set-EvidenceFileHash -Evidence $evidence -ArtifactId 'server-pre-shutdown-log'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'must reference distinct server log artifacts and files' -Action { Invoke-Verifier }

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $evidence.artifacts = @($evidence.artifacts | Where-Object { $_.id -cne 'server-post-restart-log' })
    $evidence.recovery.post_restart.artifact_id = 'server-pre-shutdown-log'
    $restart = @($evidence.observations | Where-Object { $_.name -ceq 'restart_restores_all_state' })[0]
    $restart.artifact_ids = @('server-pre-shutdown-log')
    Set-EvidenceFileHash -Evidence $evidence -ArtifactId 'server-pre-shutdown-log'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage "exactly two contemporaneous 'server' log artifacts" -Action { Invoke-Verifier }

    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['server-pre-shutdown-log']) `
        -Value "2026/07/13 20:00:00.0000`t[Generic] HL2RP_RECOVERY_SNAPSHOT phase=pre_shutdown sequence=$sequence digest=$digest`t" `
        -Encoding utf8

    $evidence = ConvertFrom-EvidenceJson -Json $validEvidenceJson
    $evidence.recovery.post_restart.digest = 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'does not match its hashed server log artifact' -Action { Invoke-Verifier }

    $evidence.recovery.post_restart.sequence = $sequence + 1
    $evidence.recovery.post_restart.digest = 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff'
    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['server-post-restart-log']) `
        -Value "2026/07/13 20:01:00.0000`t[Generic] HL2RP_RECOVERY_SNAPSHOT phase=post_restart sequence=$($sequence + 1) digest=$($evidence.recovery.post_restart.digest)`t" `
        -Encoding utf8
    Set-EvidenceFileHash -Evidence $evidence -ArtifactId 'server-post-restart-log'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'must match exactly' -Action { Invoke-Verifier }

    $evidence.recovery.post_restart.sequence = $sequence
    $evidence.recovery.post_restart.digest = $digest
    Set-Content -LiteralPath (Join-Path $evidenceRoot $paths['server-post-restart-log']) `
        -Value @(
            "2026/07/13 20:01:00.0000`t[Generic] HL2RP_RECOVERY_SNAPSHOT phase=post_restart sequence=$sequence digest=$digest`t",
            "2026/07/13 20:01:01.0000`t[Generic] HL2RP_RECOVERY_SNAPSHOT phase=post_restart sequence=$sequence digest=$digest`t"
        ) `
        -Encoding utf8
    Set-EvidenceFileHash -Evidence $evidence -ArtifactId 'server-post-restart-log'
    Save-Evidence -Evidence $evidence
    Assert-VerifierFailure -ExpectedMessage 'must contain exactly one recovery marker' -Action { Invoke-Verifier }

    Write-Host 'Remote-acceptance verifier contract tests passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        $resolvedTempRoot = (Resolve-Path -LiteralPath $tempRoot).Path.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
        $resolvedTempBase = [System.IO.Path]::GetFullPath(
            [System.IO.Path]::GetTempPath()).TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $tempLeaf = [System.IO.Path]::GetFileName($resolvedTempRoot)
        if (-not $resolvedTempRoot.StartsWith($resolvedTempBase, [System.StringComparison]::OrdinalIgnoreCase) -or
            $tempLeaf -cnotmatch '^hexagon-remote-evidence-test-[a-f0-9]{32}$') {
            throw "Refusing to recursively remove unexpected verifier-test path '$resolvedTempRoot'."
        }
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }
}
