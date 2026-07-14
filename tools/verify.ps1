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
not launch or independently prove that external run. Passing -SkipSboxSmoke
also skips the authoritative engine whitelist, startup, hotload, and recovery
evidence; a generated-project build alone does not prove those engine paths.

.EXAMPLE
./tools/verify.ps1 -RemoteAcceptanceEvidence C:\HexagonReleaseEvidence\run-id\remote-acceptance.json

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
    [ValidatePattern('^[a-f0-9]{40}$')]
    [string] $HexagonSha,

    [Parameter()]
    [ValidatePattern('^[a-f0-9]{40}$')]
    [string] $HL2RPSha,

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

function Get-NormalizedAbsolutePath {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter()][string] $BasePath
    )

    $candidate = $Path
    if (-not [System.IO.Path]::IsPathRooted($candidate)) {
        if ([string]::IsNullOrWhiteSpace($BasePath)) {
            throw "A base path is required to resolve relative path '$Path'."
        }
        $candidate = Join-Path $BasePath $candidate
    }

    return [System.IO.Path]::GetFullPath($candidate).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
}

function Resolve-DirectoryTarget {
    param(
        [Parameter(Mandatory)][string] $Path
    )

    $item = Get-Item -LiteralPath $Path -Force
    $candidate = $item.FullName
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        $targets = @($item.Target | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($targets.Count -ne 1) {
            throw "'$Path' must resolve through exactly one directory target; found $($targets.Count)."
        }

        $candidate = $targets[0]
        if (-not [System.IO.Path]::IsPathRooted($candidate)) {
            $candidate = Join-Path $item.Parent.FullName $candidate
        }
    }

    return Get-NormalizedAbsolutePath -Path (Resolve-Path -LiteralPath $candidate).Path
}

function Test-ExactPath {
    param(
        [Parameter(Mandatory)][string] $Left,
        [Parameter(Mandatory)][string] $Right
    )

    return [System.StringComparer]::OrdinalIgnoreCase.Equals(
        (Get-NormalizedAbsolutePath -Path $Left),
        (Get-NormalizedAbsolutePath -Path $Right)
    )
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
        if (-not $NoRestore) {
            Invoke-CheckedCommand -Description "Restoring locked and audited dependencies: $relativeProject" -FilePath 'dotnet' -Arguments @(
                'restore', $testProject.FullName, '--locked-mode', '--nologo', '-warnaserror',
                '-p:NuGetAudit=true', '-p:NuGetAuditMode=all'
            )
        }
        $testArguments = @(
            'test', $testProject.FullName, '--configuration', 'Release', '--no-restore', '--nologo', '--warnaserror'
        )
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
    $resolvedLibraryTarget = Resolve-DirectoryTarget -Path $libraryLink
    if (-not (Test-ExactPath -Left $resolvedLibraryTarget -Right $hexagonRoot)) {
        throw "'$libraryLink' resolves to '$resolvedLibraryTarget', not the expected sibling Hexagon source '$hexagonRoot'. Remove the stale Libraries/hexagon entry and retry."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedLibraryTarget 'hexagon.sbproj') -PathType Leaf)) {
        throw "The expected sibling Hexagon source '$resolvedLibraryTarget' does not contain hexagon.sbproj."
    }
    Write-Host "==> Verified Libraries/hexagon source binding: $resolvedLibraryTarget" -ForegroundColor Cyan

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

    [xml] $generatedProjectXml = Get-Content -LiteralPath $generatedProject -Raw
    $projectDirectory = Split-Path -Parent $generatedProject
    $projectReferences = @($generatedProjectXml.SelectNodes("//*[local-name()='ProjectReference']"))
    $resolvedHexagonReferences = @(
        foreach ($projectReference in $projectReferences) {
            $include = $projectReference.GetAttribute('Include')
            if ([string]::IsNullOrWhiteSpace($include)) {
                continue
            }

            $resolvedReference = Get-NormalizedAbsolutePath -Path $include -BasePath $projectDirectory
            if ([System.StringComparer]::OrdinalIgnoreCase.Equals(
                    [System.IO.Path]::GetFileName($resolvedReference),
                    'hexagon.csproj')) {
                $resolvedReference
            }
        }
    )
    $expectedLibraryProject = Get-NormalizedAbsolutePath -Path $generatedLibraryProject
    if ($resolvedHexagonReferences.Count -ne 1 -or
        -not (Test-ExactPath -Left $resolvedHexagonReferences[0] -Right $expectedLibraryProject)) {
        $observedReferences = if ($resolvedHexagonReferences.Count -eq 0) {
            '<none>'
        }
        else {
            $resolvedHexagonReferences -join '; '
        }
        throw "Generated project '$generatedProject' must reference exactly '$expectedLibraryProject'; observed Hexagon project references: $observedReferences."
    }
    Write-Host "==> Verified generated HL2RP source binding: $expectedLibraryProject" -ForegroundColor Cyan

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

    $pwshCommand = Get-Command 'pwsh' -ErrorAction SilentlyContinue
    if ($null -eq $pwshCommand) {
        throw 'PowerShell 7 (pwsh) is required for s&box client assembly access verification.'
    }
    Invoke-CheckedCommand -Description 'Verifying the client-visible HL2RP assembly against s&box access control' `
        -FilePath $pwshCommand.Source `
        -Arguments @(
            '-NoLogo',
            '-NoProfile',
            '-File', (Join-Path $PSScriptRoot 'verify-client-assembly-access.ps1'),
            '-SchemaRoot', $schemaRootPath,
            '-SboxRoot', $SboxRoot
        )

    if (-not $SkipSboxSmoke) {
        & (Join-Path $PSScriptRoot 'smoke-sbox.ps1') `
            -SchemaManifest $schemaManifest `
            -SboxRoot $SboxRoot `
            -TimeoutSeconds $SmokeTimeoutSeconds
    }
    else {
        Write-Warning 's&box smoke was explicitly skipped; authoritative engine whitelist, startup, hotload, and exact-recovery evidence was not exercised.'
    }
}

if ($SkipRemoteAcceptance) {
    Write-Warning 'Remote two-client acceptance was explicitly skipped; the full release gate is not satisfied.'
}
elseif ([string]::IsNullOrWhiteSpace($RemoteAcceptanceEvidence)) {
    throw 'A real dedicated-server/two-authenticated-client evidence manifest is required. Pass -RemoteAcceptanceEvidence, or explicitly use -SkipRemoteAcceptance for an incomplete local-only run.'
}
else {
    $evidenceHexagonSha = if ([string]::IsNullOrWhiteSpace($HexagonSha)) {
        Get-RepositoryHead -Root $hexagonRoot
    }
    else { $HexagonSha }
    $evidenceHL2RPSha = if ([string]::IsNullOrWhiteSpace($HL2RPSha)) {
        Get-RepositoryHead -Root $schemaRootPath
    }
    else { $HL2RPSha }
    & (Join-Path $PSScriptRoot 'verify-remote-acceptance.ps1') `
        -EvidencePath $RemoteAcceptanceEvidence `
        -HexagonRoot $hexagonRoot `
        -SchemaRoot $schemaRootPath `
        -HexagonSha $evidenceHexagonSha `
        -HL2RPSha $evidenceHL2RPSha
}

if ($SkipRemoteAcceptance) {
    Write-Host 'Hexagon local verification passed; manual two-client acceptance remains outstanding.' -ForegroundColor Green
}
else {
    Write-Host 'Hexagon local verification and manual remote-evidence consistency checks passed.' -ForegroundColor Green
}
