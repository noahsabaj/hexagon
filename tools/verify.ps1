<#
.SYNOPSIS
Runs the complete Hexagon/HL2RP verification pipeline.

.DESCRIPTION
Validates package/resource ownership, runs both Sandbox-independent v2 test
projects, generates and builds the real s&box projects with warnings as errors,
then launches isolated hidden editor runs to exercise whitelist/asset startup,
transactional interaction, drain, and exact persistence recovery. A complete
release invocation also checks the completeness, source binding, and artifact
integrity of a named operator's manual two-remote-client attestation. It does
not launch or independently prove that external run.

.EXAMPLE
./tools/verify.ps1 -RemoteAcceptanceEvidence ./remote-acceptance.json

.EXAMPLE
./tools/verify.ps1 -SkipRemoteAcceptance
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string] $SchemaRoot,

    [Parameter()]
    [string] $SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox',

    [Parameter()]
    [switch] $SkipTests,

    [Parameter()]
    [switch] $SkipSboxBuild,

    [Parameter()]
    [switch] $SkipSboxSmoke,

    [Parameter()]
    [switch] $NoRestore,

    [Parameter()]
    [string] $RemoteAcceptanceEvidence,

    [Parameter()]
    [switch] $SkipRemoteAcceptance,

    [Parameter()]
    [ValidateRange(10, 300)]
    [int] $GenerateTimeoutSeconds = 120,

    [Parameter()]
    [ValidateRange(15, 300)]
    [int] $SmokeTimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$hexagonRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SchemaRoot)) {
    $SchemaRoot = Join-Path (Split-Path -Parent $hexagonRoot) 'hl2rp-hexagon'
}
$schemaRootPath = (Resolve-Path -LiteralPath $SchemaRoot).Path
$schemaManifest = Join-Path $schemaRootPath 'hl2rp.sbproj'

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

Write-Host '==> Validating package and asset layout' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'validate-project.ps1') `
    -HexagonRoot $hexagonRoot `
    -SchemaRoot $schemaRootPath `
    -SboxRoot $SboxRoot

if (-not $SkipTests) {
    $testProjects = @(
        Get-ChildItem -LiteralPath (Join-Path $hexagonRoot 'tests') -Filter '*.csproj' -File -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath (Join-Path $schemaRootPath 'tests') -Filter '*.csproj' -File -ErrorAction SilentlyContinue
    ) | Sort-Object FullName

    if ($testProjects.Count -eq 0) {
        throw 'No tracked conformance test projects were found.'
    }

    foreach ($testProject in $testProjects) {
        $relativeProject = $testProject.FullName
        $testArguments = @('test', $testProject.FullName, '--configuration', 'Release', '--nologo', '--warnaserror')
        if ($NoRestore) {
            $testArguments += '--no-restore'
        }
        Invoke-CheckedCommand -Description "Running conformance tests: $relativeProject" -FilePath 'dotnet' -Arguments $testArguments
    }
}

if (-not $SkipSboxBuild) {
    $sboxDev = Join-Path $SboxRoot 'sbox-dev.exe'
    if (-not (Test-Path -LiteralPath $sboxDev -PathType Leaf)) {
        throw "sbox-dev.exe was not found at '$sboxDev'. Pass -SboxRoot or use -SkipSboxBuild."
    }

    # Local package references are resolved from the game project's ignored Libraries
    # directory. Use a junction so verification always compiles the exact sibling source.
    $librariesRoot = Join-Path $schemaRootPath 'Libraries'
    $libraryLink = Join-Path $librariesRoot 'hexagon'
    if (-not (Test-Path -LiteralPath $librariesRoot -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $librariesRoot)
    }
    if (-not (Test-Path -LiteralPath $libraryLink -PathType Container)) {
        [void](New-Item -ItemType Junction -Path $libraryLink -Target $hexagonRoot)
    }
    if (-not (Test-Path -LiteralPath (Join-Path $libraryLink 'hexagon.sbproj') -PathType Leaf)) {
        throw "'$libraryLink' does not resolve to a Hexagon source checkout. Remove the stale Libraries/hexagon entry and retry."
    }

    $generatedProject = Join-Path $schemaRootPath 'Code\hl2rp.csproj'
    $generatedLibraryProject = Join-Path $hexagonRoot 'Code\hexagon.csproj'
    if (Test-Path -LiteralPath $generatedProject -PathType Leaf) {
        Remove-Item -LiteralPath $generatedProject -Force
    }

    Write-Host '==> Generating the s&box solution' -ForegroundColor Cyan
    $escapedManifest = $schemaManifest.Replace('"', '\"')
    $editor = Start-Process -FilePath $sboxDev -ArgumentList "-project `"$escapedManifest`"" -PassThru -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds($GenerateTimeoutSeconds)
    $stableSignature = $null
    $stableSince = [DateTime]::MinValue

    try {
        while ($true) {
            if ($editor.HasExited) {
                throw "s&box exited with code $($editor.ExitCode) before generating '$generatedProject'."
            }
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "Timed out after $GenerateTimeoutSeconds seconds waiting for s&box to generate '$generatedProject'."
            }

            if ((Test-Path -LiteralPath $generatedProject -PathType Leaf) -and
                (Test-Path -LiteralPath $generatedLibraryProject -PathType Leaf)) {
                $gameProjectInfo = Get-Item -LiteralPath $generatedProject
                $libraryProjectInfo = Get-Item -LiteralPath $generatedLibraryProject
                $signature = "$($gameProjectInfo.Length):$($gameProjectInfo.LastWriteTimeUtc.Ticks)|$($libraryProjectInfo.Length):$($libraryProjectInfo.LastWriteTimeUtc.Ticks)"
                if ($signature -eq $stableSignature) {
                    if (([DateTime]::UtcNow - $stableSince).TotalMilliseconds -ge 1000) {
                        break
                    }
                }
                else {
                    $stableSignature = $signature
                    $stableSince = [DateTime]::UtcNow
                }
            }
            else {
                $stableSignature = $null
                $stableSince = [DateTime]::MinValue
            }

            Start-Sleep -Milliseconds 250
        }
    }
    finally {
        if (-not $editor.HasExited) {
            Stop-Process -Id $editor.Id -Force
            $editor.WaitForExit()
        }
    }

    # The editor compiler can release the shared .vs output assembly just after
    # its process exits. Avoid turning a transient MSB3026 retry into an error.
    Start-Sleep -Milliseconds 1000

    if (-not (Test-Path -LiteralPath $generatedProject -PathType Leaf) -or
        -not (Test-Path -LiteralPath $generatedLibraryProject -PathType Leaf)) {
        throw 'Generated s&box projects disappeared after generation; another verifier may be running concurrently.'
    }

    if (-not $NoRestore) {
        Invoke-CheckedCommand -Description 'Restoring the generated s&box project' -FilePath 'dotnet' -Arguments @('restore', $generatedProject, '--nologo')
    }

    Invoke-CheckedCommand -Description 'Building the generated s&box project with warnings as errors' -FilePath 'dotnet' -Arguments @(
        'build',
        $generatedProject,
        '--configuration', 'Debug',
        '--no-restore',
        '--nologo',
        '--disable-build-servers',
        '-m:1',
        '--warnaserror'
    )

    if (-not $SkipSboxSmoke) {
        & (Join-Path $PSScriptRoot 'smoke-sbox.ps1') `
            -SchemaManifest $schemaManifest `
            -SboxRoot $SboxRoot `
            -TimeoutSeconds $SmokeTimeoutSeconds
    }
}

if ($SkipRemoteAcceptance) {
    Write-Warning 'Remote two-client acceptance was explicitly skipped; the full release gate is not satisfied.'
}
elseif ([string]::IsNullOrWhiteSpace($RemoteAcceptanceEvidence)) {
    throw 'A real dedicated-server/two-authenticated-client evidence manifest is required. Pass -RemoteAcceptanceEvidence, or explicitly use -SkipRemoteAcceptance for an incomplete local-only run.'
}
else {
    & (Join-Path $PSScriptRoot 'verify-remote-acceptance.ps1') `
        -EvidencePath $RemoteAcceptanceEvidence `
        -HexagonRoot $hexagonRoot `
        -SchemaRoot $schemaRootPath
}

if ($SkipRemoteAcceptance) {
    Write-Host 'Hexagon local verification passed; manual two-client acceptance remains outstanding.' -ForegroundColor Green
}
else {
    Write-Host 'Hexagon local verification and manual remote-evidence consistency checks passed.' -ForegroundColor Green
}
