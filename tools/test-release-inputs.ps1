[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'release-inputs.ps1')

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'hexagon-release-inputs-test-' + [Guid]::NewGuid().ToString('N'))
$sourceRoot = Join-Path $tempRoot 'source'
$cloneRoot = Join-Path $tempRoot 'clone'

function Assert-ThrowsLike {
    param(
        [Parameter(Mandatory)][string] $Expected,
        [Parameter(Mandatory)][scriptblock] $Action
    )
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike "*$Expected*") {
            throw "Expected failure containing '$Expected', got: $($_.Exception.Message)"
        }
        return
    }
    throw "Expected failure containing '$Expected', but the action succeeded."
}

function Invoke-GitFixture {
    param([Parameter(Mandatory)][string[]] $Arguments)
    [void](Invoke-ReleaseInputGit -Root $sourceRoot -Arguments $Arguments)
}

try {
    [void](New-Item -ItemType Directory -Path $sourceRoot)
    [void](New-Item -ItemType Directory -Path (
        Join-Path $sourceRoot 'Code'),
        (Join-Path $sourceRoot 'Code/Properties'),
        (Join-Path $sourceRoot 'ProjectSettings'))
    @'
Code/obj/
Code/bin/
Code/*.csproj
Code/Properties/launchSettings.json
tools/bin/
*.generated.*
tests/*.cs
ProjectSettings/Platform.config
'@ | Set-Content -LiteralPath (Join-Path $sourceRoot '.gitignore') -Encoding utf8
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $sourceRoot 'Code/test.csproj') -Encoding utf8
    'internal static class Program { }' |
        Set-Content -LiteralPath (Join-Path $sourceRoot 'Code/Program.cs') -Encoding utf8
    '{ "ClientsCanSpawnObjects": false }' |
        Set-Content -LiteralPath (Join-Path $sourceRoot 'ProjectSettings/Networking.config') -Encoding utf8
    '{ "runtime": "material-v1" }' |
        Set-Content -LiteralPath (Join-Path $sourceRoot 'Code/remote-acceptance-runtime.json') -Encoding utf8
    '{ "profiles": {} }' |
        Set-Content -LiteralPath (Join-Path $sourceRoot 'Code/Properties/launchSettings.json') -Encoding utf8

    [void](Invoke-ReleaseInputGit -Root $sourceRoot -Arguments @('init', '-q'))
    Invoke-GitFixture -Arguments @('config', 'user.name', 'Release Input Test')
    Invoke-GitFixture -Arguments @('config', 'user.email', 'release-input@example.invalid')
    Invoke-GitFixture -Arguments @('config', 'commit.gpgsign', 'false')
    Invoke-GitFixture -Arguments @(
        'add', '.gitignore', 'Code/Program.cs', 'Code/remote-acceptance-runtime.json',
        'ProjectSettings/Networking.config')
    Invoke-GitFixture -Arguments @('commit', '-q', '-m', 'fixture')

    $repositories = @([pscustomobject]@{ Label = 'fixture'; Root = $sourceRoot })
    $initial = Get-EffectiveReleaseInputs -Repositories $repositories -RequireClean
    if (@($initial.Files | Where-Object {
        $_.RelativePath -ceq 'Code/remote-acceptance-runtime.json'
    }).Count -ne 1) {
        throw 'A tracked path resembling an evidence output was omitted from effective inputs.'
    }
    Assert-GeneratedCompileInputsTracked `
        -ProjectPath (Join-Path $sourceRoot 'Code/test.csproj') `
        -Repositories $repositories

    [void](Invoke-ReleaseInputGit -Root $tempRoot -Arguments @(
        'clone', '-q', $sourceRoot, $cloneRoot))
    $clone = Get-EffectiveReleaseInputs -Repositories @(
        [pscustomobject]@{ Label = 'fixture'; Root = $cloneRoot }) -RequireClean
    if ($clone.Fingerprint -cne $initial.Fingerprint) {
        throw 'A clean clone did not reproduce the effective-input fingerprint.'
    }

    'internal static class PropertiesSourceMustNotBeIgnored { }' |
        Set-Content -LiteralPath (Join-Path $sourceRoot 'Code/Properties/Evil.cs') -Encoding utf8
    & git -C $sourceRoot check-ignore --quiet -- Code/Properties/Evil.cs
    if ($LASTEXITCODE -eq 0) {
        throw 'Code/Properties source was hidden by the generated-metadata ignore.'
    }
    if ($LASTEXITCODE -ne 1) {
        throw "git check-ignore failed with unexpected exit code $LASTEXITCODE."
    }
    Assert-ThrowsLike -Expected 'require a clean checkout' -Action {
        Get-EffectiveReleaseInputs -Repositories $repositories -RequireClean | Out-Null
    }
    Remove-Item -LiteralPath (Join-Path $sourceRoot 'Code/Properties/Evil.cs') -Force

    'internal static class AccountedUntrackedSource { }' |
        Set-Content -LiteralPath (Join-Path $sourceRoot 'Code/AccountedUntracked.cs') -Encoding utf8
    $dirtyWithUntracked = Get-EffectiveReleaseInputs -Repositories $repositories
    if (@($dirtyWithUntracked.Files | Where-Object {
        $_.RelativePath -ceq 'Code/AccountedUntracked.cs'
    }).Count -ne 1) {
        throw 'A nonignored untracked local compile input was not fingerprinted.'
    }
    Assert-ThrowsLike -Expected 'is not a tracked effective input' -Action {
        Assert-GeneratedCompileInputsTracked `
            -ProjectPath (Join-Path $sourceRoot 'Code/test.csproj') `
            -Repositories $repositories
    }
    Assert-GeneratedCompileInputsTracked `
        -ProjectPath (Join-Path $sourceRoot 'Code/test.csproj') `
        -Repositories $repositories `
        -AllowNonIgnoredUntracked
    Remove-Item -LiteralPath (Join-Path $sourceRoot 'Code/AccountedUntracked.cs') -Force

    'internal static class IgnoredCompiledSource { }' |
        Set-Content -LiteralPath (Join-Path $sourceRoot 'Code/Evil.generated.cs') -Encoding utf8
    Assert-ThrowsLike -Expected 'Ignored release material exists' -Action {
        Get-EffectiveReleaseInputs -Repositories $repositories | Out-Null
    }
    Assert-ThrowsLike -Expected 'is not a tracked effective input' -Action {
        Assert-GeneratedCompileInputsTracked `
            -ProjectPath (Join-Path $sourceRoot 'Code/test.csproj') `
            -Repositories $repositories
    }
    Remove-Item -LiteralPath (Join-Path $sourceRoot 'Code/Evil.generated.cs') -Force

    [void](New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'tests') -Force)
    'internal static class IgnoredTestSource { }' |
        Set-Content -LiteralPath (Join-Path $sourceRoot 'tests/Ignored.cs') -Encoding utf8
    Assert-ThrowsLike -Expected 'Ignored release material exists' -Action {
        Get-EffectiveReleaseInputs -Repositories $repositories -RequireClean | Out-Null
    }
    Remove-Item -LiteralPath (Join-Path $sourceRoot 'tests/Ignored.cs') -Force

    [void](New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'tools/bin') -Force)
    'internal static class IgnoredNonDerivedBinSource { }' |
        Set-Content -LiteralPath (Join-Path $sourceRoot 'tools/bin/Evil.cs') -Encoding utf8
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="../tools/bin/Evil.cs" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $sourceRoot 'Code/test.csproj') -Encoding utf8
    Assert-ThrowsLike -Expected 'Ignored release material exists' -Action {
        Get-EffectiveReleaseInputs -Repositories $repositories -RequireClean | Out-Null
    }
    Assert-ThrowsLike -Expected 'is not a tracked effective input' -Action {
        Assert-GeneratedCompileInputsTracked `
            -ProjectPath (Join-Path $sourceRoot 'Code/test.csproj') `
            -Repositories $repositories
    }
    Remove-Item -LiteralPath (Join-Path $sourceRoot 'tools/bin/Evil.cs') -Force

    '{ "ChatEnabled": true }' |
        Set-Content -LiteralPath (Join-Path $sourceRoot 'ProjectSettings/Platform.config') -Encoding utf8
    Assert-ThrowsLike -Expected 'Ignored release material exists' -Action {
        Get-EffectiveReleaseInputs -Repositories $repositories | Out-Null
    }
    Remove-Item -LiteralPath (Join-Path $sourceRoot 'ProjectSettings/Platform.config') -Force

    [void](New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'Code/obj'))
    'derived' | Set-Content -LiteralPath (Join-Path $sourceRoot 'Code/obj/derived.txt') -Encoding utf8
    'internal static class DerivedGeneratedSource { }' |
        Set-Content -LiteralPath (Join-Path $sourceRoot 'Code/obj/Derived.g.cs') -Encoding utf8
    [void](Get-EffectiveReleaseInputs -Repositories $repositories -RequireClean)

    '{ "ClientsCanSpawnObjects": false, "UpdateRate": 31 }' |
        Set-Content -LiteralPath (Join-Path $sourceRoot 'ProjectSettings/Networking.config') -Encoding utf8
    Assert-ThrowsLike -Expected 'require a clean checkout' -Action {
        Get-EffectiveReleaseInputs -Repositories $repositories -RequireClean | Out-Null
    }
    $changed = Get-EffectiveReleaseInputs -Repositories $repositories
    if ($changed.Fingerprint -ceq $initial.Fingerprint) {
        throw 'Changing a tracked ProjectSettings config did not change the fingerprint.'
    }

    Write-Host 'Release-input contract tests passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        $resolved = (Resolve-Path -LiteralPath $tempRoot).Path
        $base = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($base, [System.StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($resolved) -cnotmatch '^hexagon-release-inputs-test-[a-f0-9]{32}$') {
            throw "Refusing to remove unexpected test path '$resolved'."
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
