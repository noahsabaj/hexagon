# Runs Hexagon's logic tests, then compiles Hexagon against the installed s&box engine.
# s&box only compiles a library as part of a game, so a game that uses Hexagon is required.
# The engine build is the only thing that checks components and Razor panels, so it is not optional.
[CmdletBinding()]
param(
    # A game that uses Hexagon. Defaults to the sibling HL2RP checkout.
    [string] $GameRoot,
    [string] $SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox',
    [int] $GenerateTimeoutSeconds = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $GameRoot) { $GameRoot = Join-Path (Split-Path -Parent $root) 'hl2rp-hexagon' }
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
$manifest = (Get-ChildItem -LiteralPath $GameRoot -Filter '*.sbproj' | Select-Object -First 1).FullName
$link = Join-Path $GameRoot 'Libraries\hexagon'
if (-not (Test-Path -LiteralPath $link) -or (Get-Item -LiteralPath $link).Target -ne $root) {
    throw "'$link' must be a junction to '$root' so the game compiles this checkout of Hexagon."
}
# s&box writes the library's project inside the library folder, which the junction points back here.
$project = Join-Path $root 'Code\hexagon.csproj'
$sboxDev = Join-Path $SboxRoot 'sbox-dev.exe'

function Invoke-Checked([string] $Description, [scriptblock] $Command) {
    Write-Host "==> $Description" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE." }
}

Invoke-Checked 'Running logic tests' {
    dotnet test (Join-Path $root 'Tests\Hexagon.Tests.csproj') --nologo --verbosity quiet
}

if (-not (Test-Path -LiteralPath $sboxDev -PathType Leaf)) { throw "s&box was not found at '$SboxRoot'." }
if (Get-Process -Name 'sbox-dev' -ErrorAction SilentlyContinue) {
    throw 'Close the s&box editor first: it holds the generated project open.'
}

# s&box writes the C# project when it opens the game. Open it hidden, wait for the file to settle, close it.
Write-Host '==> Generating the s&box project' -ForegroundColor Cyan
if (Test-Path -LiteralPath $project) { Remove-Item -LiteralPath $project -Force }
$editor = Start-Process -FilePath $sboxDev -ArgumentList "-project `"$manifest`"" -PassThru -WindowStyle Hidden
try {
    $deadline = [DateTime]::UtcNow.AddSeconds($GenerateTimeoutSeconds)
    $signature = $null
    $stableSince = [DateTime]::UtcNow
    while ($true) {
        if ($editor.HasExited) { throw "s&box exited with code $($editor.ExitCode) before generating the project." }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out waiting for s&box to generate '$project'." }
        if (Test-Path -LiteralPath $project -PathType Leaf) {
            $info = Get-Item -LiteralPath $project
            $current = "$($info.Length):$($info.LastWriteTimeUtc.Ticks)"
            if ($current -ne $signature) { $signature = $current; $stableSince = [DateTime]::UtcNow }
            elseif (([DateTime]::UtcNow - $stableSince).TotalSeconds -ge 2) { break }
        }
        Start-Sleep -Milliseconds 250
    }
}
finally {
    if (-not $editor.HasExited) { Stop-Process -Id $editor.Id -Force }
}

Invoke-Checked 'Compiling Hexagon against the engine, warnings as errors' {
    dotnet build $project --nologo --verbosity quiet -warnaserror
}

Write-Host 'Verified: logic tests pass and Hexagon compiles against s&box.' -ForegroundColor Green
