[CmdletBinding()]
param(
    [Parameter()]
    [switch] $SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tests/Hexagon.V2.Tests.csproj'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string] $Description,
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $Arguments
    )

    Write-Host "==> $Description" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

if (-not $SkipRestore) {
    Invoke-CheckedCommand -Description 'Restoring locked Hexagon dependencies with NuGet audit' -FilePath 'dotnet' -Arguments @(
        'restore', $project, '--locked-mode', '--nologo', '-warnaserror',
        '-p:NuGetAudit=true', '-p:NuGetAuditMode=all'
    )
}

$auditJson = & dotnet package list --project $project --vulnerable --include-transitive --format json --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "NuGet vulnerability inventory failed with exit code $LASTEXITCODE."
}
if (($auditJson -join "`n") -match '"severity"\s*:') {
    throw 'NuGet reported one or more vulnerable direct or transitive dependencies.'
}

Invoke-CheckedCommand -Description 'Running Hexagon neutral tests' -FilePath 'dotnet' -Arguments @(
    'test', $project, '--configuration', 'Release', '--no-restore', '--nologo', '--warnaserror',
    '--filter', 'TestCategory!=CrossRepository'
)

Write-Host '==> Running remote-acceptance verifier contract tests' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'test-remote-acceptance.ps1')

Write-Host '==> Running release-environment capture contract tests' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'test-release-environment.ps1')

Write-Host '==> Running release-input contract tests' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'test-release-inputs.ps1')

Write-Host '==> Running release-publisher contract tests' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'test-release-publisher.ps1')

Write-Host '==> Running portable framework/project validation' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'validate-framework-portable.ps1') -HexagonRoot $root

$sandboxImports = @(Get-ChildItem -LiteralPath (Join-Path $root 'Code/V2') -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](Infrastructure|Runtime)[\\/]' } |
    Select-String -Pattern '^\s*using\s+Sandbox(?:\.|;)' -CaseSensitive)
if ($sandboxImports.Count -gt 0) {
    $locations = $sandboxImports | ForEach-Object { "$($_.Path):$($_.LineNumber)" }
    throw "Portable Hexagon layers import Sandbox APIs:`n$($locations -join "`n")"
}

Write-Host 'Hexagon portable verification passed.' -ForegroundColor Green
