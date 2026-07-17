# Worktree cleanliness gate shared by the portable verifiers. Lock-pin verification
# compares HEADs only, so without this gate a local run could claim "passed against
# <sha>" while compiling arbitrary uncommitted bytes (2026-07-16 audit, DEPL-02).

function Assert-CleanWorktree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject[]] $Repositories,
        [Parameter()][switch] $AllowDirty
    )

    if ($AllowDirty) {
        Write-Host '==> Worktree cleanliness gate skipped (-AllowDirty): results attribute to UNCOMMITTED sources.' -ForegroundColor Yellow
        return $false
    }
    foreach ($repository in $Repositories) {
        $global:LASTEXITCODE = 0
        $status = & git -C $repository.Root status --porcelain=v1 --untracked-files=all
        if ($LASTEXITCODE -ne 0) {
            throw "Could not read worktree status for '$($repository.Label)' at '$($repository.Root)'."
        }
        $entries = @($status | Where-Object { $_ })
        if ($entries.Count -gt 0) {
            throw ("The $($repository.Label) worktree is dirty; commit or stash before attributing " +
                "results to a commit SHA, or pass -AllowDirty for an explicitly unattributed run:`n" +
                ($entries -join "`n"))
        }
    }
    return $true
}
