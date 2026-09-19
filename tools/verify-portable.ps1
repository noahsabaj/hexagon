[CmdletBinding()]
param(
    [Parameter()]
    [switch] $AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tests/Hexagon.V2.Tests.csproj'

. (Join-Path $PSScriptRoot 'worktree-gate.ps1')
. (Join-Path $PSScriptRoot 'verify-common.ps1')

$attributed = Assert-CleanWorktree -Repositories @(
    [pscustomobject]@{ Label = 'hexagon'; Root = $root }) -AllowDirty:$AllowDirty

# The audited restore always runs: a cache-hit locked restore is cheap, and a skipped
# restore previously left the vulnerability grep as the only (vacuous) gate.
Invoke-CheckedCommand -Description 'Restoring locked Hexagon dependencies with NuGet audit' -FilePath 'dotnet' -Arguments @(
    'restore', $project, '--locked-mode', '--nologo', '-warnaserror',
    '-p:NuGetAudit=true', '-p:NuGetAuditMode=all'
)

$auditJson = @(& dotnet package list --project $project --vulnerable --include-transitive --format json --output-version 1 --no-restore)
if ($LASTEXITCODE -ne 0) {
    throw "NuGet vulnerability inventory failed with exit code $LASTEXITCODE."
}
Assert-NoVulnerablePackages -Project $project -AuditJson $auditJson

Invoke-CheckedCommand -Description 'Running Hexagon neutral tests' -FilePath 'dotnet' -Arguments @(
    'test', $project, '--configuration', 'Release', '--no-restore', '--nologo', '--warnaserror',
    '--filter', 'TestCategory!=CrossRepository'
)

Write-Host '==> Running worktree-gate contract tests' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'test-worktree-gate.ps1')

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

if ($attributed) {
    Write-Host 'Hexagon portable verification passed.' -ForegroundColor Green
}
else {
    Write-Host 'Hexagon portable verification passed against UNCOMMITTED sources (no SHA attribution).' -ForegroundColor Yellow
}
