[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$capture = Join-Path $PSScriptRoot 'capture-release-environment.ps1'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'hexagon-release-environment-test-' + [Guid]::NewGuid().ToString('N'))
$hexagonRoot = Join-Path $tempRoot 'hexagon'
$schemaRoot = Join-Path $tempRoot 'hl2rp-hexagon'
$runtimeRoot = Join-Path $tempRoot 'source-runtime'
$steamContextRoot = Join-Path $tempRoot 'steam/steamapps/common/sbox'
$steamAppsRoot = Join-Path $tempRoot 'steam/steamapps'
$outputPath = Join-Path $tempRoot 'evidence/server-environment.json'

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

function Assert-CaptureFailure {
    param(
        [Parameter(Mandatory)][string] $ExpectedMessage,
        [Parameter(Mandatory)][scriptblock] $Action
    )
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "Expected capture failure containing '$ExpectedMessage', got: $($_.Exception.Message)"
        }
        return
    }
    throw "Expected capture failure containing '$ExpectedMessage', but capture passed."
}

try {
    [void](New-Item -ItemType Directory -Path `
        $hexagonRoot, $schemaRoot, $runtimeRoot, $steamContextRoot)
    foreach ($repository in @($hexagonRoot, $schemaRoot)) {
        [void](Invoke-GitChecked -Root $repository -Arguments @('init', '-q'))
        [void](Invoke-GitChecked -Root $repository -Arguments @('config', 'user.name', 'Hexagon Test'))
        [void](Invoke-GitChecked -Root $repository -Arguments @('config', 'user.email', 'hexagon-test@example.invalid'))
        [void](Invoke-GitChecked -Root $repository -Arguments @('config', 'commit.gpgsign', 'false'))
    }

    Set-Content -LiteralPath (Join-Path $hexagonRoot 'tracked.txt') `
        -Value 'hexagon source fixture' -Encoding utf8
    [void](Invoke-GitChecked -Root $hexagonRoot -Arguments @('add', 'tracked.txt'))
    [void](Invoke-GitChecked -Root $hexagonRoot -Arguments @('commit', '-q', '-m', 'fixture'))
    $hexagonSha = ([string](Invoke-GitChecked -Root $hexagonRoot -Arguments @('rev-parse', 'HEAD'))).Trim()

    Set-Content -LiteralPath (Join-Path $schemaRoot 'tracked.txt') `
        -Value 'schema source fixture' -Encoding utf8
    [ordered]@{ commit = $hexagonSha } | ConvertTo-Json | Set-Content `
        -LiteralPath (Join-Path $schemaRoot 'hexagon.lock.json') -Encoding utf8
    [void](Invoke-GitChecked -Root $schemaRoot -Arguments @('add', 'tracked.txt', 'hexagon.lock.json'))
    [void](Invoke-GitChecked -Root $schemaRoot -Arguments @('commit', '-q', '-m', 'fixture'))

    $runtimeConfiguration = [ordered]@{
        runtimeOptions = [ordered]@{
            framework = [ordered]@{
                name = 'Microsoft.NETCore.App'
                version = '10.0.0'
            }
        }
    } | ConvertTo-Json -Depth 5
    foreach ($runtimeConfigName in @('sbox.runtimeconfig.json', 'sbox-server.runtimeconfig.json')) {
        Set-Content -LiteralPath (Join-Path $runtimeRoot $runtimeConfigName) `
            -Value $runtimeConfiguration -Encoding utf8
    }
    foreach ($relativePath in @(
        'sbox.exe',
        'sbox-server.exe',
        'bin/win64/engine2.dll',
        'bin/managed/Sandbox.Engine.dll')) {
        $path = Join-Path $runtimeRoot $relativePath
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force)
        Set-Content -LiteralPath $path -Value "fixture:$relativePath" -Encoding utf8
    }

    Set-Content -LiteralPath (Join-Path $steamContextRoot '.version') `
        -Value 'fixture-compatible-version' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $steamAppsRoot 'appmanifest_590830.acf') `
        -Value '"AppState" { "appid" "590830" "buildid" "24226936" }' -Encoding utf8

    $engineSourceSha = ('8' * 40) -join ''
    & $capture `
        -Role server `
        -OutputPath $outputPath `
        -SboxRoot $runtimeRoot `
        -RuntimeDistribution source_build `
        -EngineSourceSha $engineSourceSha `
        -SteamContextRoot $steamContextRoot `
        -HexagonRoot $hexagonRoot `
        -SchemaRoot $schemaRoot

    $record = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    if ([string]$record.format -cne 'hexagon-v2-runtime-environment/2' -or
        [string]$record.sbox.distribution -cne 'source_build' -or
        [string]$record.sbox.engine_source_sha -cne $engineSourceSha -or
        [int]$record.sbox.compatibility.steam_app_id -ne 590830 -or
        [string]$record.sbox.compatibility.steam_build_id -cne '24226936' -or
        @($record.sbox.files).Count -ne 3) {
        throw 'Source-built runtime capture did not emit the required v2 provenance contract.'
    }

    Assert-CaptureFailure -ExpectedMessage 'requires -EngineSourceSha' -Action {
        & $capture `
            -Role server `
            -OutputPath $outputPath `
            -SboxRoot $runtimeRoot `
            -RuntimeDistribution source_build `
            -SteamContextRoot $steamContextRoot `
            -HexagonRoot $hexagonRoot `
            -SchemaRoot $schemaRoot
    }
    Assert-CaptureFailure -ExpectedMessage 'must use its own root as the Steam metadata context' -Action {
        & $capture `
            -Role server `
            -OutputPath $outputPath `
            -SboxRoot $runtimeRoot `
            -RuntimeDistribution steam `
            -SteamContextRoot $steamContextRoot `
            -HexagonRoot $hexagonRoot `
            -SchemaRoot $schemaRoot
    }

    Write-Host 'Release-environment capture contract tests passed.' -ForegroundColor Green
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
        if (-not $resolvedTempRoot.StartsWith(
            $resolvedTempBase,
            [System.StringComparison]::OrdinalIgnoreCase) -or
            $tempLeaf -cnotmatch '^hexagon-release-environment-test-[a-f0-9]{32}$') {
            throw "Refusing to recursively remove unexpected capture-test path '$resolvedTempRoot'."
        }
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }
}
