# Has the editor compile a game's assets when any has changed since it last did.
#
# The engine reads an asset compiled: a dedicated server finds an item, a faction or a scene only in
# the compiled file beside it, named as the asset with _c after it, and only the editor writes those.
# An asset written outside the editor, or changed since, is read as it was, or not at all, until the
# editor has opened the project. A play test once ran against a faction with none of its new ranks.
[CmdletBinding()]
param(
    # A game that uses Hexagon. Defaults to the sibling HL2RP checkout.
    [string] $GameRoot,
    [string] $SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox',
    [int] $TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $GameRoot) { $GameRoot = Join-Path (Split-Path -Parent $root) 'hl2rp-hexagon' }
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
$manifest = (Get-ChildItem -LiteralPath $GameRoot -Filter '*.sbproj' | Select-Object -First 1).FullName

# Assets written as JSON compile one for one, to the same name with _c after it. Pictures and sounds
# compile to other names, and are left out.
function Test-JsonAsset([System.IO.FileInfo] $File) {
    if ($File.Name -match '\.meta$|_[a-z]$') { return $false }
    $stream = $File.OpenRead()
    try {
        $start = [byte[]]::new(16)
        $read = $stream.Read($start, 0, $start.Length)
        return ([System.Text.Encoding]::UTF8.GetString($start, 0, $read).TrimStart([char] 0xFEFF).TrimStart()).StartsWith('{')
    }
    finally { $stream.Dispose() }
}

# Those whose compiled file is missing, or older than they are.
function Get-Stale {
    @(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Assets') -Recurse -File | Where-Object { Test-JsonAsset $_ } | Where-Object {
        $compiled = "$($_.FullName)_c"
        -not (Test-Path -LiteralPath $compiled) -or (Get-Item -LiteralPath $compiled).LastWriteTimeUtc -lt $_.LastWriteTimeUtc
    })
}

$stale = @(Get-Stale)
if ($stale.Count -eq 0) { return }
Write-Host "==> Compiling $($stale.Count) changed asset(s) in the editor: $(($stale | Select-Object -First 6 | ForEach-Object Name) -join ', ')$(if ($stale.Count -gt 6) { ', ...' })" -ForegroundColor Cyan

$started = Get-Date
$editorLog = Join-Path $SboxRoot 'logs\sbox-dev.log'
# The editor writes this once it has imported the project's assets, compiling what changed.
function Test-Imported {
    if (-not (Test-Path -LiteralPath $editorLog)) { return $false }
    foreach ($line in @(Get-Content -LiteralPath $editorLog -Tail 200)) {
        if ($line -match '^(\d{4}/\d\d/\d\d \d\d:\d\d:\d\d)\.\d+\s+\[Startup\] \.\.refresh complete' -and
            [DateTime]::ParseExact($Matches[1], 'yyyy/MM/dd HH:mm:ss', $null) -ge $started.AddSeconds(-1)) { return $true }
    }
    return $false
}

$open = Get-Process -Name 'sbox-dev' -ErrorAction SilentlyContinue
$editor = $null
try {
    if ($open) {
        # An open editor compiles an asset as soon as it changes on disk, so it is given a moment.
        $deadline = $started.AddSeconds(30)
        while ((Get-Date) -lt $deadline -and @(Get-Stale | Where-Object { -not (Test-Path -LiteralPath "$($_.FullName)_c") }).Count -gt 0) { Start-Sleep -Milliseconds 500 }
        Start-Sleep -Seconds 3
    }
    else {
        # RenderDoc, when it is on the PATH, is loaded into the editor and slows every native library the
        # engine loads while starting, so the editor is started without it.
        $pathWithoutRenderDoc = ($env:Path -split ';' | Where-Object { $_ -and -not (Test-Path -LiteralPath (Join-Path $_ 'renderdoc.dll')) }) -join ';'
        $editor = Start-Process -FilePath (Join-Path $SboxRoot 'sbox-dev.exe') -ArgumentList "-project `"$manifest`"" -PassThru -WindowStyle Hidden -Environment @{ Path = $pathWithoutRenderDoc }
        $deadline = $started.AddSeconds($TimeoutSeconds)
        while (-not (Test-Imported)) {
            if ($editor.HasExited) { throw "s&box exited with code $($editor.ExitCode) before compiling the assets." }
            if ((Get-Date) -ge $deadline) { throw "Timed out waiting for s&box to compile the assets." }
            Start-Sleep -Milliseconds 500
        }
    }
}
finally {
    if ($editor -and -not $editor.HasExited) { Stop-Process -Id $editor.Id -Force; [void]$editor.WaitForExit(10000) }
}

# The editor compiles only what changed in content. An asset only touched since, by a checkout, is
# already compiled as it is, so its compiled file is marked current, and not compiled again next time.
$missing = @()
foreach ($asset in $stale) {
    $compiled = "$($asset.FullName)_c"
    if (-not (Test-Path -LiteralPath $compiled)) { $missing += $asset.Name; continue }
    $file = Get-Item -LiteralPath $compiled
    if ($file.LastWriteTimeUtc -lt $asset.LastWriteTimeUtc) { $file.LastWriteTimeUtc = [DateTime]::UtcNow }
}
if ($missing) { throw "The editor did not compile $($missing -join ', '). Open the project in the editor to see why." }
Write-Host "    compiled in $([int]((Get-Date) - $started).TotalSeconds) s" -ForegroundColor DarkGray
