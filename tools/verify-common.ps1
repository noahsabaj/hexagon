# Helpers shared by the verification profiles in both repositories. HL2RP dot-sources this from
# its locked sibling Hexagon checkout, the same way it already loads worktree-gate.ps1, so the
# two portable profiles cannot drift apart again.

Set-StrictMode -Version Latest

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

function Get-MemberValues {
    param([Parameter(Mandatory)][object] $Object, [Parameter(Mandatory)][string] $Name)
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return @() }
    return @($property.Value)
}

function Assert-NoVulnerablePackages {
    param(
        [Parameter(Mandatory)][string] $Project,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $AuditJson
    )

    # The gate parses the pinned schema instead of grepping for '"severity"':
    # a source without vulnerability data returns a clean-looking report that the
    # substring probe passed vacuously (2026-07-16 audit, DEPE-01). Data availability
    # itself is enforced by the NuGet.config <auditSources> declaration, which makes
    # the audited restore raise NU1905 (an error under -warnaserror) when missing.
    $audit = ($AuditJson -join "`n") | ConvertFrom-Json
    if ([int]$audit.version -ne 1) {
        throw "NuGet vulnerability report for '$Project' is not the pinned schema version 1."
    }
    $vulnerable = [System.Collections.Generic.List[string]]::new()
    foreach ($projectEntry in Get-MemberValues -Object $audit -Name 'projects') {
        foreach ($framework in Get-MemberValues -Object $projectEntry -Name 'frameworks') {
            $packages = @(Get-MemberValues -Object $framework -Name 'topLevelPackages') +
                @(Get-MemberValues -Object $framework -Name 'transitivePackages')
            foreach ($package in $packages) {
                if (@(Get-MemberValues -Object $package -Name 'vulnerabilities').Count -gt 0) {
                    $vulnerable.Add([string]$package.id)
                }
            }
        }
    }
    if ($vulnerable.Count -gt 0) {
        throw "NuGet reported vulnerable dependencies for '$Project': $(($vulnerable | Sort-Object -Unique) -join ', ')."
    }
}

function Get-RepositoryHead {
    param([Parameter(Mandatory)][string] $Root)

    $global:LASTEXITCODE = 0
    $head = (& git -C $Root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -cnotmatch '^[a-f0-9]{40}$') {
        throw "Could not resolve a lowercase full Git HEAD for '$Root'."
    }
    return $head
}
