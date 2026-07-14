<#
.SYNOPSIS
Builds the client-visible HL2RP assembly and verifies it with s&box access control.

.DESCRIPTION
s&box strips every .Server.cs body from a game archive before compiling it for a
remote client. A normal generated-project build includes those files, so it cannot
detect whether the remotely streamed assembly will be accepted. This verifier builds
the equivalent non-server source set, suppresses MSBuild-only assembly metadata, and
passes the resulting binary through the installed Sandbox.Access verifier.
#>
#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SchemaRoot,

    [Parameter()]
    [string] $SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$schemaRootPath = (Resolve-Path -LiteralPath $SchemaRoot).Path
$generatedProject = Join-Path $schemaRootPath 'Code\hl2rp.csproj'
$managedRoot = Join-Path $SboxRoot 'bin\managed'
$generatedOutput = Join-Path $SboxRoot '.vs\output'
$accessAssemblyPath = Join-Path $managedRoot 'Sandbox.Access.dll'
$cecilAssemblyPath = Join-Path $managedRoot 'Mono.Cecil.dll'
$hexagonAssemblyPath = Join-Path $generatedOutput 'hexagon.dll'
$baseLibraryAssemblyPath = Join-Path $generatedOutput 'Base Library.dll'

foreach ($requiredPath in @(
    $generatedProject,
    $accessAssemblyPath,
    $cecilAssemblyPath,
    $hexagonAssemblyPath,
    $baseLibraryAssemblyPath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Client assembly access verification requires '$requiredPath'."
    }
}

$temporaryRoot = [System.IO.Path]::GetFullPath((Join-Path (
    [System.IO.Path]::GetTempPath()
) "hexagon-client-access-$PID-$([Guid]::NewGuid().ToString('N'))"))
$expectedTemporaryPrefix = [System.IO.Path]::GetFullPath((Join-Path (
    [System.IO.Path]::GetTempPath()
) 'hexagon-client-access-'))
if (-not $temporaryRoot.StartsWith(
        $expectedTemporaryPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use unexpected temporary client-access path '$temporaryRoot'."
}

try {
    $arguments = @(
        'build',
        $generatedProject,
        '--configuration', 'Release',
        '--no-restore',
        '--no-dependencies',
        '--nologo',
        '--disable-build-servers',
        '-m:1',
        '--property:DefaultItemExcludesInProjectFolder=**/*.Server.cs',
        "--property:OutputPath=$temporaryRoot\",
        '--property:TreatWarningsAsErrors=true',
        '--property:GenerateAssemblyInfo=false',
        '--property:GenerateTargetFrameworkAttribute=false'
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Client-equivalent HL2RP build failed with exit code $LASTEXITCODE."
    }

    $clientAssemblyPath = Join-Path $temporaryRoot 'hl2rp.dll'
    if (-not (Test-Path -LiteralPath $clientAssemblyPath -PathType Leaf)) {
        throw "Client-equivalent build did not produce '$clientAssemblyPath'."
    }

    [void][Reflection.Assembly]::LoadFrom($cecilAssemblyPath)
    [void][Reflection.Assembly]::LoadFrom($accessAssemblyPath)

    $resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
    $resolver.AddSearchDirectory($generatedOutput)
    $resolver.AddSearchDirectory($managedRoot)
	$resolver.AddSearchDirectory($temporaryRoot)
    $reader = [Mono.Cecil.ReaderParameters]::new()
    $reader.AssemblyResolver = $resolver
    $reader.InMemory = $true

    $access = [Sandbox.AccessControl]::new()
    $rulesField = [Sandbox.AccessControl].GetField(
        'Rules',
        [Reflection.BindingFlags]'Instance,NonPublic'
    )
    $assembliesField = [Sandbox.AccessControl].GetField(
        'Assemblies',
        [Reflection.BindingFlags]'Instance,NonPublic'
    )
    if ($null -eq $rulesField -or $null -eq $assembliesField) {
        throw 'The installed Sandbox.Access layout is incompatible with the client verifier.'
    }

    $rules = $rulesField.GetValue($access)
    $assemblies = $assembliesField.GetValue($access)
    [void]$rules.Whitelist.Add([regex]::new('^hexagon/.*$'))
    [void]$rules.Whitelist.Add([regex]::new('^Base Library/.*$'))

    $dependencies = [System.Collections.Generic.List[object]]::new()
    foreach ($dependencyPath in @($hexagonAssemblyPath, $baseLibraryAssemblyPath)) {
        $dependency = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dependencyPath, $reader)
        $dependencies.Add($dependency)
        if (-not $assemblies.TryAdd($dependency.Name, $dependency)) {
            throw "Sandbox.Access already contains an unexpected assembly named '$($dependency.Name.Name)'."
        }
    }

    $clientAssembly = $null
    $assemblyStream = $null
    $trustedStream = $null
    try {
        $clientAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($clientAssemblyPath, $reader)
        $clientAssembly.Name.Name = 'package.local.hl2rp'
        $assemblyStream = [System.IO.MemoryStream]::new()
        $clientAssembly.Write($assemblyStream)
        $assemblyStream.Position = 0

        $result = $access.VerifyAssembly($assemblyStream, [ref]$trustedStream, $false)
        if (-not $result.Success) {
            $messages = [System.Collections.Generic.List[string]]::new()
            foreach ($error in $result.Errors) {
                $messages.Add("access error: $error")
            }
            foreach ($violation in $result.WhitelistErrors) {
                $messages.Add("whitelist violation: $($violation.Item1)")
            }
            throw "Client-equivalent HL2RP assembly failed s&box access control:`n$($messages -join "`n")"
        }
    }
    finally {
        if ($null -ne $trustedStream) { $trustedStream.Dispose() }
        if ($null -ne $assemblyStream) { $assemblyStream.Dispose() }
        if ($null -ne $clientAssembly) { $clientAssembly.Dispose() }
        foreach ($dependency in $dependencies) { $dependency.Dispose() }
		$resolver.Dispose()
    }

    Write-Host 'Client-equivalent HL2RP assembly passed s&box access control.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath(
            (Resolve-Path -LiteralPath $temporaryRoot).Path
        )
        if (-not $resolvedTemporaryRoot.StartsWith(
                $expectedTemporaryPrefix,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected temporary path '$resolvedTemporaryRoot'."
        }
		$cleanupFailure = $null
		for ($attempt = 0; $attempt -lt 20; $attempt++) {
			try {
				[GC]::Collect()
				[GC]::WaitForPendingFinalizers()
				Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
				$cleanupFailure = $null
				break
			}
			catch [System.IO.IOException], [System.UnauthorizedAccessException] {
				$cleanupFailure = $_
				Start-Sleep -Milliseconds 100
			}
		}
		if ($null -ne $cleanupFailure -or (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
			throw "Client assembly access verification could not remove its temporary output '$resolvedTemporaryRoot': $cleanupFailure"
		}
    }
}
