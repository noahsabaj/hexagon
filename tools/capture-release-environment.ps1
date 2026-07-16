[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('server', 'client_a', 'client_b')]
    [string] $Role,

    [Parameter(Mandatory)]
    [string] $OutputPath,

    [Parameter()]
    [string] $SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox',

    [Parameter()]
    [string] $HexagonRoot = (Split-Path -Parent $PSScriptRoot),

    [Parameter(Mandatory)]
    [string] $SchemaRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'release-inputs.ps1')

function Get-RequiredFileRecord {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Path
    )
    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    return [ordered]@{
        name = $Name
        file_name = [System.IO.Path]::GetFileName($resolved)
        sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Get-RepositoryHead {
    param([Parameter(Mandatory)][string] $Root)

    $head = [string]@(Invoke-ReleaseInputGit -Root $Root -Arguments @(
        'rev-parse', 'HEAD'))[0]
    if ($head -cnotmatch '^[a-f0-9]{40}$') {
        throw "Could not resolve a lowercase full Git HEAD for '$Root'."
    }
    return $head
}

function Get-DotNetRuntimeRecord {
    param(
        [Parameter(Mandatory)][string] $RuntimeConfigPath,
        [Parameter(Mandatory)][string[]] $DotNetInfo
    )

    $configuration = Get-Content -LiteralPath $RuntimeConfigPath -Raw | ConvertFrom-Json
    $frameworkName = [string]$configuration.runtimeOptions.framework.name
    $requestedText = [string]$configuration.runtimeOptions.framework.version
    $requested = [Version]::new()
    if ([string]::IsNullOrWhiteSpace($frameworkName) -or
        -not [Version]::TryParse($requestedText, [ref]$requested)) {
        throw "Runtime config '$RuntimeConfigPath' has no supported framework/version contract."
    }

    [string[]] $runtimeInventory = @(& dotnet --list-runtimes)
    if ($LASTEXITCODE -ne 0) { throw 'dotnet --list-runtimes failed.' }
    $compatible = [System.Collections.Generic.List[Version]]::new()
    foreach ($line in $runtimeInventory) {
        $match = [regex]::Match(
            $line,
            '^(?<name>\S+)\s+(?<version>\S+)\s+\[[^\]]+\]$')
        if (-not $match.Success -or $match.Groups['name'].Value -cne $frameworkName) {
            continue
        }
        $candidate = [Version]::new()
        if ([Version]::TryParse($match.Groups['version'].Value, [ref]$candidate) -and
            $candidate.Major -eq $requested.Major -and
            $candidate.Minor -eq $requested.Minor -and
            $candidate -ge $requested) {
            $compatible.Add($candidate)
        }
    }
    if ($compatible.Count -eq 0) {
        throw "No installed '$frameworkName' runtime satisfies '$requestedText'."
    }
    $compatible.Sort()
    $resolvedVersion = $compatible[$compatible.Count - 1].ToString()

    $runtimeArchitectureMatch = [regex]::Match(
        ($DotNetInfo -join [Environment]::NewLine),
        '^\s*Architecture:\s*(?<value>\S+)\s*$',
        [System.Text.RegularExpressions.RegexOptions]::Multiline)
    $runtimeArchitecture = if ($runtimeArchitectureMatch.Success) {
        $runtimeArchitectureMatch.Groups['value'].Value
    }
    else {
        [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    }

    return [ordered]@{
        framework = $frameworkName
        requested_version = $requestedText
        version = $resolvedVersion
        architecture = $runtimeArchitecture
        runtime_config_file_name = [System.IO.Path]::GetFileName($RuntimeConfigPath)
        runtime_config_sha256 = (Get-FileHash `
            -LiteralPath $RuntimeConfigPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$sboxRootPath = (Resolve-Path -LiteralPath $SboxRoot).Path
$hexagonRootPath = (Resolve-Path -LiteralPath $HexagonRoot).Path
$schemaRootPath = (Resolve-Path -LiteralPath $SchemaRoot).Path
$sourceInputs = Get-EffectiveReleaseInputs -Repositories @(
    [pscustomobject]@{ Label = 'hexagon'; Root = $hexagonRootPath },
    [pscustomobject]@{ Label = 'hl2rp-hexagon'; Root = $schemaRootPath }) -RequireClean
$hexagonSha = Get-RepositoryHead -Root $hexagonRootPath
$hl2rpSha = Get-RepositoryHead -Root $schemaRootPath
$lock = Get-Content -LiteralPath (Join-Path $schemaRootPath 'hexagon.lock.json') -Raw |
    ConvertFrom-Json
if ([string]$lock.commit -cne $hexagonSha) {
    throw "HL2RP is not locked to the captured Hexagon source SHA '$hexagonSha'."
}

$versionPath = Join-Path $sboxRootPath '.version'
$versionContent = (Get-Content -LiteralPath $versionPath -Raw).Trim()
$steamAppsRoot = Split-Path -Parent (Split-Path -Parent $sboxRootPath)
$appManifestPath = Join-Path $steamAppsRoot 'appmanifest_590830.acf'
$appManifest = Get-Content -LiteralPath $appManifestPath -Raw
$buildMatch = [regex]::Match($appManifest, '"buildid"\s+"(?<id>[0-9]+)"')
if (-not $buildMatch.Success) {
    throw "Steam app manifest '$appManifestPath' has no buildid for app 590830."
}

$entryPoint = if ($Role -ceq 'server') { 'sbox-server.exe' } else { 'sbox.exe' }
[string[]] $dotnetInfo = @(& dotnet --info | Where-Object {
    -not [string]::IsNullOrWhiteSpace([string]$_)
})
if ($LASTEXITCODE -ne 0) { throw 'dotnet --info failed.' }
$runtimeConfigName = "$(
    [System.IO.Path]::GetFileNameWithoutExtension($entryPoint)).runtimeconfig.json"
$runtimeConfigPath = (Resolve-Path -LiteralPath (
    Join-Path $sboxRootPath $runtimeConfigName)).Path
$dotnetRuntime = Get-DotNetRuntimeRecord `
    -RuntimeConfigPath $runtimeConfigPath `
    -DotNetInfo $dotnetInfo

$record = [ordered]@{
    format = 'hexagon-v2-runtime-environment/1'
    role = $Role
    captured_at_utc = [DateTimeOffset]::UtcNow.ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    os = [ordered]@{
        description = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    }
    source = [ordered]@{
        hexagon_sha = $hexagonSha
        hl2rp_sha = $hl2rpSha
        effective_input_fingerprint = $sourceInputs.Fingerprint
    }
    dotnet = $dotnetRuntime
    sbox = [ordered]@{
        steam_app_id = 590830
        steam_build_id = $buildMatch.Groups['id'].Value
        version = $versionContent
        version_sha256 = (Get-FileHash -LiteralPath $versionPath -Algorithm SHA256).Hash.ToLowerInvariant()
        files = @(
            Get-RequiredFileRecord -Name 'entry_point' -Path (Join-Path $sboxRootPath $entryPoint)
            Get-RequiredFileRecord -Name 'engine2' -Path (Join-Path $sboxRootPath 'bin/win64/engine2.dll')
            Get-RequiredFileRecord -Name 'sandbox_engine' -Path (Join-Path $sboxRootPath 'bin/managed/Sandbox.Engine.dll')
        )
    }
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $fullOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory) -and
    -not (Test-Path -LiteralPath $outputDirectory)) {
    [void](New-Item -ItemType Directory -Path $outputDirectory)
}
$record | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $fullOutputPath -Encoding utf8
Write-Host "Captured $Role release environment to '$fullOutputPath'." -ForegroundColor Green
