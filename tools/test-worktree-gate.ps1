[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'worktree-gate.ps1')

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'hexagon-worktree-gate-test-' + [Guid]::NewGuid().ToString('N'))

try {
    [void](New-Item -ItemType Directory -Path $tempRoot)
    $global:LASTEXITCODE = 0
    & git -C $tempRoot init -q
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the scratch repository.' }
    & git -C $tempRoot config user.name 'Worktree Gate Test'
    & git -C $tempRoot config user.email 'worktree@example.invalid'
    & git -C $tempRoot config commit.gpgsign false
    'tracked' | Set-Content -LiteralPath (Join-Path $tempRoot 'tracked.txt') -Encoding utf8
    & git -C $tempRoot add tracked.txt
    & git -C $tempRoot commit -q -m 'fixture'
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit the scratch fixture.' }
    $repositories = @([pscustomobject]@{ Label = 'scratch'; Root = $tempRoot })

    if (-not (Assert-CleanWorktree -Repositories $repositories)) {
        throw 'A clean worktree must attribute results (return $true).'
    }

    'scratch' | Set-Content -LiteralPath (Join-Path $tempRoot 'untracked.txt') -Encoding utf8
    $rejected = $false
    try { [void](Assert-CleanWorktree -Repositories $repositories) }
    catch { $rejected = $_.Exception.Message -match 'worktree is dirty' }
    if (-not $rejected) {
        throw 'An untracked file must fail the cleanliness gate.'
    }

    if (Assert-CleanWorktree -Repositories $repositories -AllowDirty) {
        throw '-AllowDirty must return $false so callers drop SHA attribution.'
    }

    'modified' | Add-Content -LiteralPath (Join-Path $tempRoot 'tracked.txt')
    $rejected = $false
    try { [void](Assert-CleanWorktree -Repositories $repositories) }
    catch { $rejected = $_.Exception.Message -match 'worktree is dirty' }
    if (-not $rejected) {
        throw 'A modified tracked file must fail the cleanliness gate.'
    }

    Write-Host 'Worktree-gate contract tests passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
