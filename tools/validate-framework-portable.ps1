[CmdletBinding()]
param(
    [Parameter()]
    [string] $HexagonRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $HexagonRoot).Path
$manifestPath = Join-Path $root 'hexagon.sbproj'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Missing Hexagon package manifest '$manifestPath'."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$typeProperty = $manifest.PSObject.Properties['Type']
$standaloneProperty = $manifest.PSObject.Properties['IsStandaloneOnly']
$legacyWhitelistProperty = $manifest.PSObject.Properties['IsWhitelistDisabled']
$metadataProperty = $manifest.PSObject.Properties['Metadata']
$metadata = if ($null -eq $metadataProperty) { $null } else { $metadataProperty.Value }
$compilerProperty = if ($null -eq $metadata) { $null } else { $metadata.PSObject.Properties['Compiler'] }
$compiler = if ($null -eq $compilerProperty) { $null } else { $compilerProperty.Value }
$compilerWhitelistProperty = if ($null -eq $compiler) { $null } else { $compiler.PSObject.Properties['Whitelist'] }
$startupProperty = if ($null -eq $metadata) { $null } else { $metadata.PSObject.Properties['StartupScene'] }
$dedicatedStartupProperty = if ($null -eq $metadata) { $null } else { $metadata.PSObject.Properties['DedicatedServerStartupScene'] }
$isStandaloneOnly = $null -ne $standaloneProperty -and $standaloneProperty.Value -eq $true

if ($null -eq $typeProperty -or [string]$typeProperty.Value -cne 'library') {
    throw "Hexagon package type must be 'library'."
}
if ($null -ne $legacyWhitelistProperty) {
    throw 'Hexagon must not declare top-level IsWhitelistDisabled; s&box game/library compilation uses IsStandaloneOnly and Metadata.Compiler.Whitelist.'
}
if ($isStandaloneOnly -or
    $null -eq $compilerWhitelistProperty -or $compilerWhitelistProperty.Value -ne $true) {
    throw 'Hexagon must remain platform-whitelisted; the consuming standalone game owns raw OS persistence.'
}
if ($null -ne $startupProperty -or $null -ne $dedicatedStartupProperty) {
    throw 'The Hexagon library must not own a startup scene.'
}

$assetsRoot = Join-Path $root 'Assets'
foreach ($asset in Get-ChildItem -LiteralPath $assetsRoot -Recurse -File -ErrorAction SilentlyContinue) {
    $relative = [System.IO.Path]::GetRelativePath($assetsRoot, $asset.FullName).Replace('\', '/').ToLowerInvariant()
    if ($relative.EndsWith('.scene', [System.StringComparison]::Ordinal)) {
        throw "The Hexagon library must not own scene '$relative'."
    }
    if (-not $relative.StartsWith('hexagon/', [System.StringComparison]::Ordinal)) {
        throw "Hexagon library asset '$relative' is outside the required 'hexagon/' namespace."
    }
}

$sandboxImports = @(Get-ChildItem -LiteralPath (Join-Path $root 'Code/V2') -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](Infrastructure|Runtime)[\\/]' } |
    Select-String -Pattern '^\s*using\s+Sandbox(?:\.|;)' -CaseSensitive)
if ($sandboxImports.Count -gt 0) {
    $locations = $sandboxImports | ForEach-Object { "$($_.Path):$($_.LineNumber)" }
    throw ('Portable Hexagon layers import Sandbox APIs:' + [Environment]::NewLine + ($locations -join [Environment]::NewLine))
}

Write-Host 'Portable Hexagon framework validation passed.' -ForegroundColor Green
