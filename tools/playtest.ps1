# Plays a Hexagon game in the real s&box editor and checks what happens.
#
# Nothing here is faked: the editor boots the project, enters play mode, and a small editor-only
# driver component calls the same client requests the HUD calls. Each one travels the real RPC path
# and is judged by the real host rules. The run uses a throwaway data folder and deletes it after.
[CmdletBinding()]
param(
    # The game to play. Defaults to the sibling HL2RP checkout, whose scene and assets the scenario expects.
    [string] $GameRoot,
    [string] $SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox',
    [int] $TimeoutSeconds = 480,
    # Plays in the editor that is already open on the game, which takes code edits in as they are
    # saved, and leaves it open. Starting the editor is most of a run.
    [switch] $Attach,
    # Stops once the section whose name starts with this is played. Each section builds on the ones
    # before it, so they are played from the first.
    [string] $Until
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $GameRoot) { $GameRoot = Join-Path (Split-Path -Parent $root) 'hl2rp-hexagon' }
$manifest = (Get-ChildItem -LiteralPath (Resolve-Path -LiteralPath $GameRoot).Path -Filter '*.sbproj' | Select-Object -First 1).FullName
$sboxDev = Join-Path $SboxRoot 'sbox-dev.exe'
$endpoint = 'http://127.0.0.1:7269/mcp'
$hud = 'b7200000-0000-4000-8000-000000000001'
$dataRoot = "playtest-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$description = "A tired resident of the city."
# What the inventory shows of a key: its cut, worked out from the pattern the scene gives each lock.
$cityCut = '1-4-6-3-2'
$flatCut = '5-3-3-2-7'

if (-not $Attach -and (Get-Process -Name 'sbox-dev' -ErrorAction SilentlyContinue)) { throw 'Close the s&box editor first, or pass -Attach to play in it.' }
if ($Until) {
    $sections = Select-String -LiteralPath $PSCommandPath -Pattern "^\s*Section '(.+)'\s*$" | ForEach-Object { $_.Matches[0].Groups[1].Value -replace "''", "'" }
    if (-not ($sections | Where-Object { $_ -like "$Until*" })) { throw "No section's name starts with '$Until'. The sections are: $($sections -join '; ')." }
}

$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(60)
$http.DefaultRequestHeaders.Accept.ParseAdd('application/json, text/event-stream')
$script:requestId = 0
$script:failures = 0
$script:nonce = 0
# The host lets a player ask for twelve things at once and four more each second, and refuses the rest
# (HexagonServices.HostSpend). A test is held to that like any player, so it keeps the same count, two
# short for the moment a request takes to arrive. Reading its own state, or moving its own view, asks
# the host for nothing.
$requests = 'create', 'enter', 'leave', 'delete', 'retire', 'say', 'introduce', 'door', 'open', 'take', 'put', 'close',
    'pickup', 'drop', 'use', 'equip', 'move', 'split', 'merge', 'tune', 'act', 'attack', 'offer', 'offerdoor', 'answer', 'staff',
    'wear', 'strip', 'item', 'read', 'write', 'fill', 'stamp', 'show', 'closepaper',
    'layout', 'lean', 'standup', 'mapdraw', 'mapword', 'mapwipe', 'mapmove', 'mapmouse'
$script:allowance = 10.0
$script:counted = [DateTime]::UtcNow
$script:lastProblem = 'none'
$script:replayed = $false
$script:untilPlayed = $false
$script:consoleFrom = 0
$untilReached = 'hexagon-playtest: the -Until section was played'

function Invoke-Editor([string] $Method, $Params) {
    $script:requestId++
    $body = [ordered]@{ jsonrpc = '2.0'; id = $script:requestId; method = $Method; params = $Params } |
        ConvertTo-Json -Depth 12 -Compress
    $content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, 'application/json')
    try {
        $response = $http.PostAsync($endpoint, $content).GetAwaiter().GetResult()
        $payload = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw "HTTP $([int]$response.StatusCode): $payload" }
        return $payload | ConvertFrom-Json
    }
    finally { $content.Dispose() }
}

function Invoke-Tool([string] $Name, $Arguments = @{}) {
    $reply = Invoke-Editor 'tools/call' ([ordered]@{ name = 'call_tool'; arguments = [ordered]@{ name = $Name; arguments = $Arguments } })
    $text = ($reply.result.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n"
    if ($reply.result.PSObject.Properties['isError'] -and $reply.result.isError) { throw "$Name failed: $text" }
    return $text
}

# Saves what the game camera sees, so a person (or the model running this) can look at it.
function Snapshot([string] $Name, [double] $Settle = 0.3) {
    # The HUD lays out a panel a frame after it opens, and a harm cue fades in over a third of a second
    # (HarmCues); the picture waits for whichever it shows.
    Start-Sleep -Milliseconds ([int] ($Settle * 1000))
    try {
        $reply = Invoke-Editor 'tools/call' ([ordered]@{ name = 'call_tool'; arguments = [ordered]@{ name = 'camera_screenshot'; arguments = @{} } })
        $image = $reply.result.content | Where-Object { $_.type -eq 'image' } | Select-Object -First 1
        if (-not $image) { Write-Host "       no picture: $(($reply.result.content | ForEach-Object { $_.text }) -join ' ')" -ForegroundColor DarkGray; return }
        $path = Join-Path ([IO.Path]::GetTempPath()) "hexagon-look-$Name.png"
        [IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($image.data))
        Write-Host "       picture: $path" -ForegroundColor DarkGray
    }
    catch { Write-Host "       no picture: $($_.Exception.Message)" -ForegroundColor DarkGray }
}

function Wait-Until([string] $What, [scriptblock] $Condition) {
    while ([DateTime]::UtcNow -lt $script:deadline) {
        if ($editor -and $editor.HasExited) { throw "s&box exited with code $($editor.ExitCode) while waiting for $What." }
        try { if (& $Condition) { return } } catch { $script:lastProblem = $_.Exception.Message }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for $What. Last problem: $($script:lastProblem)"
}

# The editor builds the game's code in the background and swaps it in when it is done. While it does,
# the game cannot find its own types: a scene played then loses every Hexagon component, and a
# command sent then fails inside the editor. So nothing is played until the code is in.
function Wait-Built {
    Wait-Until 'the code to build' { -not (Invoke-Tool 'editor_status' | ConvertFrom-Json).IsCompiling }
    $status = Invoke-Tool 'editor_status' | ConvertFrom-Json
    if ($status.LastCompileSucceeded -eq $false) { throw "The code did not compile ($($status.LastCompileErrors) errors); compile_status lists them." }
}

# Sends one driver command and returns what the game reported back through the console.
function Send([string] $Command) {
    if (($Command -split '\|')[0] -in $requests) {
        $now = [DateTime]::UtcNow
        $script:allowance = [Math]::Min(10, $script:allowance + ($now - $script:counted).TotalSeconds * 4)
        $script:counted = $now
        if ($script:allowance -lt 1) {
            Start-Sleep -Milliseconds ([int] ((1 - $script:allowance) * 250))
            $script:allowance = 1
            $script:counted = [DateTime]::UtcNow
        }
        $script:allowance--
    }
    # The editor skips a property write that does not change the value, so a repeated command would
    # never run. A trailing nonce makes every write distinct; the driver ignores extra arguments.
    $script:nonce++
    $tagged = "$Command|#$($script:nonce)"
    $write = [ordered]@{ id = $hud; type = 'DevDriver'; properties = @{ Command = $tagged } }
    # The editor runs a call on whichever of its loops takes it up first. The menu's loop looks types up
    # among the menu's, and so does the editor while it swaps the code in: neither can describe the
    # driver, and the write fails while being checked, before anything is set. The command never ran,
    # so it is sent again. Anything else, the driver's own failures included, is a failure.
    for ($attempt = 1; ; $attempt++) {
        try { [void](Invoke-Tool 'set_component' $write); break }
        catch {
            $problem = $_.Exception.Message
            if ($attempt -ge 20 -or $problem -notmatch 'NullReferenceException' -or $problem -notmatch 'ApplyComponentProperties' -or $problem -match 'set_Command') { throw }
            if ($attempt % 5 -eq 0) { Wait-Built }
            Start-Sleep -Milliseconds ([Math]::Min(1000, 50 * $attempt))
        }
    }
    # The driver runs the command as it is set and logs what happened, and the editor takes one call
    # a frame, so the reply is normally there at the first look. It is found by its tag, never by
    # being the latest line.
    $marker = "[dev] $tagged => "
    $until = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $line = (Invoke-Tool 'read_console' @{ limit = 40; filter = '[dev]' }) -split "`n" | Where-Object { $_.Contains($marker) } | Select-Object -Last 1
        if ($line) {
            # The editor ends each console line with CRLF, and only the LF is split on.
            return $line.Substring($line.IndexOf($marker) + $marker.Length).TrimEnd()
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    return 'no reply'
}

# Asks until the reply matches, and returns the last reply. A request is judged by the host a frame or
# two after it is sent, and later on a busy machine, so the test waits for what it expects rather than
# for a fixed time. A reply that never matches is returned for a check to fail on.
function Poll([scriptblock] $Ask, [string] $Pattern, [int] $Seconds = 10) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ($true) {
        $reply = & $Ask
        if ($reply -match $Pattern -or [DateTime]::UtcNow -ge $until) { return $reply }
        Start-Sleep -Milliseconds 200
    }
}

# Where a place beside something named in the scene is: x, y and z. The side is the object's own front,
# back, left or right; the distance is from its face on that side; the height is above the floor there.
# Places are found by what is there, so a test survives the map being moved about.
function Spot([string] $Name, [string] $Side, [double] $Distance, [double] $Height = 0) {
    $reply = Send "spot|$Name|$Side|$Distance|$Height"
    if ($reply -notmatch '^(-?\d+) (-?\d+) (-?\d+)') { throw "No place beside '$Name': $reply" }
    return [int[]] @($Matches[1], $Matches[2], $Matches[3])
}

# Whether a reply puts someone within reach of a place, on the ground: the first x,y in it, after pos=
# where there is one, against the place's.
function Near([string] $Reply, [int[]] $At, [double] $Range = 40) {
    if ($Reply -notmatch '(?:pos=|^)(-?[\d.]+),(-?[\d.]+)') { return $false }
    return [Math]::Abs([double] $Matches[1] - $At[0]) -lt $Range -and [Math]::Abs([double] $Matches[2] - $At[1]) -lt $Range
}

# The state, led by whether the character stands near a place, for a check to read.
function State-Near([int[]] $At) { $state = Send 'state'; "near=$(Near $state $At) $state" }

# Moves the character as staff would, and waits until the player's own pawn is there. Until then, what
# the client claims about where it stands is judged against the old place.
function Teleport([string] $Name, [int[]] $At) {
    [void](Send "console|hexagon_teleport `"$Name`" $($At -join ' ')")
    $until = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if (Near (Send 'state') $At 32) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $until)
}

# Waits until the player's own pawn has stopped falling: it stands where two looks in a row agree.
function Wait-Landed {
    $last = $null
    $until = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $height = if ((Send 'state') -match 'pos=[-\d.]+,[-\d.]+,(-?[\d.]+)') { [double] $Matches[1] }
        if ($null -ne $last -and $null -ne $height -and [Math]::Abs($height - $last) -lt 0.5) { return }
        $last = $height
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $until)
}

# Checks a reply. Given the question instead, in braces, it asks until the answer is the one wanted.
function Expect([string] $What, $State, [string] $Pattern) {
    if ($State -is [scriptblock]) { $State = Poll $State $Pattern }
    if ($State -match $Pattern) { Write-Host "  ok   $What" -ForegroundColor Green }
    else {
        $script:failures++
        Write-Host "  FAIL $What" -ForegroundColor Red
        Write-Host "       wanted /$Pattern/ in: $State" -ForegroundColor DarkGray
    }
}

function Start-Play {
    while ($true) {
        Wait-Built
        # The asset system may still be indexing right after the assembly loads.
        Wait-Until 'the scene to open' {
            [void](Invoke-Tool 'open_scene' @{ path = 'scenes/main.scene' })
            (Invoke-Tool 'editor_status' | ConvertFrom-Json).ActiveScenePath -eq 'scenes/main.scene'
        }
        # Started too early, the editor plays an empty default scene and still reports that it is
        # playing. Only a session on the real scene counts; anything else is stopped and retried.
        Wait-Until 'play mode on the real scene' {
            if ((Invoke-Tool 'play_start' | ConvertFrom-Json).Scene -eq 'main') { return $true }
            [void](Invoke-Tool 'play_stop')
            $false
        }
        # Attach the driver once, then wait for the pawn: adding it again on every retry fails once it exists.
        Wait-Until 'the driver component' { [void](Invoke-Tool 'add_component' @{ id = $hud; type = 'DevDriver' }); $true }
        # A scene played while the editor was still taking the code in has no Hexagon system, so nobody
        # ever arrives. That is the editor, not the game: the scene is played again, as soon as the
        # driver says so. A game that lets nobody in still fails, at the deadline.
        $arrival = [DateTime]::UtcNow.AddSeconds(60)
        $unmanaged = 0
        while ([DateTime]::UtcNow -lt $arrival -and $unmanaged -lt 3) {
            if ($editor -and $editor.HasExited) { throw "s&box exited with code $($editor.ExitCode) while waiting for the local player." }
            if ([DateTime]::UtcNow -ge $script:deadline) { throw "Timed out waiting for the local player. Last problem: $($script:lastProblem)" }
            try {
                $reply = Send 'state'
                if ($reply -match '^has=') { return }
                if ($reply -eq 'no hexagon system') { $unmanaged++ }
                $script:lastProblem = "driver said: $reply"
            }
            catch { $script:lastProblem = $_.Exception.Message }
            Start-Sleep -Milliseconds 500
        }
        Write-Host "       nobody arrived ($($script:lastProblem)); playing the scene again" -ForegroundColor DarkGray
        $script:replayed = $true
        [void](Invoke-Tool 'play_stop')
        Start-Sleep -Seconds 2
    }
}

# RenderDoc, when it is on the PATH, is loaded into the editor, and with it every native library
# the engine loads while starting is slow. The editor is started without it.
$pathWithoutRenderDoc = ($env:Path -split ';' | Where-Object { $_ -and -not (Test-Path -LiteralPath (Join-Path $_ 'renderdoc.dll')) }) -join ';'
function Start-Editor {
    $script:editor = Start-Process -FilePath $sboxDev -WorkingDirectory $SboxRoot -PassThru -WindowStyle Hidden `
        -Environment @{ Path = $pathWithoutRenderDoc } -ArgumentList "-project `"$manifest`" +hexagon_data_root $dataRoot"
    Wait-Until 'the editor control endpoint' {
        (Invoke-Editor 'initialize' ([ordered]@{
            protocolVersion = '2025-03-26'; capabilities = @{}; clientInfo = @{ name = 'hexagon-playtest'; version = '1' }
        })).result.protocolVersion
    }
}

# The editor opens the scene once the game's code is built, and the code is then in. Now and then
# the scene opens while the build is still going, and that editor never takes the finished build
# in, though it reports it built. Its scene stays open, without the game's components, for good.
# Returns false for an editor in that state, so that it is started again.
function Wait-GameCode {
    $stuck = $null
    while ([DateTime]::UtcNow -lt $script:deadline) {
        if ($editor.HasExited) { throw "s&box exited with code $($editor.ExitCode) while waiting for the game assembly." }
        try { if ((Invoke-Tool 'get_component_type' @{ name = 'DevDriver' }) -match 'Hexagon') { return $true } }
        catch { $script:lastProblem = $_.Exception.Message }
        $status = try { Invoke-Tool 'editor_status' | ConvertFrom-Json } catch { $null }
        if ($status -and $status.ActiveScenePath -and -not $status.IsCompiling -and $status.LastCompileSucceeded) {
            if (-not $stuck) { $stuck = [Diagnostics.Stopwatch]::StartNew() }
            elseif ($stuck.Elapsed.TotalSeconds -ge 15) { return $false }
        }
        else { $stuck = $null }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for the game assembly. Last problem: $($script:lastProblem)"
}

# Where the editor keeps the game's data, as its console says when asked.
function Get-DataRoot {
    [void](Invoke-Tool 'console_command' @{ command = 'hexagon_data_root' })
    $line = (Invoke-Tool 'read_console' @{ limit = 5; filter = 'hexagon_data_root - "' }) -split "`n" |
        Where-Object { $_ -match 'hexagon_data_root - "' } | Select-Object -Last 1
    if ($line -notmatch 'hexagon_data_root - "([^"]*)"') { throw 'The editor did not say where it keeps the game''s data.' }
    return $Matches[1]
}

# Where the console is now, so that the run reads only what it logged itself.
function Get-ConsoleCursor {
    if ((Invoke-Tool 'read_console' @{ limit = 1 }) -match 'Cursor: (\d+)\s*$') { return [long] $Matches[1] }
    return 0
}

# Checks what the game logged during the run, and stops playing.
function Complete-Session {
    # The editor looks the driver's type up among the menu's now and then (see Send), or while the
    # assembly is still loading, and a scene played again was first played while the code was going in.
    # That is about this harness, not the game, so it is not counted.
    $notCounted = if ($script:replayed) { 'could not find Hexagon\.' } else { 'could not find Hexagon\.Dev\.DevDriver' }
    $problems = @((Invoke-Tool 'read_console' @{ limit = 500; minimumLevel = 'Warn'; since = $script:consoleFrom }) -split "`n" |
        Where-Object { $_ -match 'Hexagon|Exception|\[store\]|\[scene\]|\[death\]|\[restore\]|Whitelist violation|hexagon\.' -and $_ -notmatch "Bad texture|Error loading resource|$notCounted" })
    Expect 'the session logged no game warnings or errors' "$($problems.Count) problem lines: $($problems -join ' | ')" '^0 problem'
    [void](Invoke-Tool 'play_stop')
}

# Begins the next section, or ends the run once the -Until section has been played.
function Section([string] $Name) {
    if ($script:untilPlayed) { Complete-Session; throw $untilReached }
    if ($Until -and $Name -like "$Until*") { $script:untilPlayed = $true }
    Write-Host "==> $Name" -ForegroundColor Cyan
}

$script:deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$editor = $null
$priorDataRoot = $null
try {
    if ($Attach) {
        Write-Host "==> Playing in the open editor (data folder '$dataRoot')" -ForegroundColor Cyan
        try { [void](Invoke-Editor 'initialize' ([ordered]@{ protocolVersion = '2025-03-26'; capabilities = @{}; clientInfo = @{ name = 'hexagon-playtest'; version = '1' } })) }
        catch { throw "No editor answers at $endpoint. Open the game in the editor first, or run without -Attach." }
        $ident = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).Ident
        $status = Invoke-Tool 'editor_status' | ConvertFrom-Json
        if ($status.Project -ne $ident) { throw "The open editor has '$($status.Project)' open, not '$ident'." }
        if ($status.IsPlaying) { [void](Invoke-Tool 'play_stop') }
        # A code edit saved a moment ago may not have been noticed yet.
        Start-Sleep -Seconds 1
        Wait-Built
        Wait-Until 'the game assembly' { (Invoke-Tool 'get_component_type' @{ name = 'DevDriver' }) -match 'Hexagon' }
        # Played in the city the editor keeps, the scenario would write into it. The editor is pointed
        # at this run's own folder and asked again; nothing is played until it says so.
        $priorDataRoot = Get-DataRoot
        [void](Invoke-Tool 'console_command' @{ command = "hexagon_data_root $dataRoot" })
        $pointed = Get-DataRoot
        if ($pointed -ne $dataRoot) { throw "The editor keeps the game's data in '$pointed', not in this run's '$dataRoot'; nothing was played." }
    }
    else {
        Write-Host "==> Starting the editor (data folder '$dataRoot')" -ForegroundColor Cyan
        Start-Editor
        if (-not (Wait-GameCode)) {
            Write-Host '       the editor opened the scene before the code was built, and never took the code in; starting it again' -ForegroundColor DarkGray
            Stop-Process -Id $editor.Id -Force
            $editor.WaitForExit()
            Start-Editor
            if (-not (Wait-GameCode)) { throw 'The editor twice opened the scene before the code was built.' }
        }
    }
    $script:consoleFrom = Get-ConsoleCursor

    Section 'Characters'
    Start-Play
    Expect 'a new city has no characters' (Send 'state') 'has=False .*characters=\[\]'
    # A train that came at its time of day in the middle of a run would bring goods nothing here expects.
    [void](Send 'console|hexagon_train "Supply train" hold')
    Snapshot 'characters'
    [void](Send 'panel|newcharacter')
    Snapshot 'new-character' 1
    # Everyone is made the same way, belonging to no faction: a body for life, hair, and clothes from the
    # game's wardrobe to arrive in.
    [void](Send "create|John Doe|$description|citizen|hair_slickedback,flat_cap,shirt_and_waistcoat,trousers,boots")
    [void](Send "create|J0hn Doe|$description|citizen")
    Expect 'an account lives one life at a time' { Send 'state' } 'characters=\[John Doe\].*You already have a living character'
    # One account plays both sides here, so this city allows two lives at once.
    Expect 'a game may allow more' (Send 'policy|living|2') 'living=2'
    [void](Send "create|J0hn Doe|$description|citizen")
    [void](Send "create|Officer Kane|$description|human01|cp_mask")
    [void](Send "create|Officer Kane|$description|human01|jumpsuit,trousers")
    $state = Poll { Send 'state' } 'cannot be worn with'
    Expect 'the look-alike name was refused with a reason' $state 'characters=\[John Doe\].*reads like it'
    Expect 'nobody arrives in a uniform: it is not in the wardrobe' $state 'That is not on offer\.'
    Expect 'nor in clothes that cannot be worn together' $state 'The blue jumpsuit cannot be worn with the trousers\.'

    Section 'Entering the city, chat, inventory'
    [void](Send 'enter|John Doe')
    [void](Send 'delete|John Doe')
    Expect 'a character that has lived is not deleted' { Send 'state' } 'characters=\[John Doe\].*has lived in the city'
    [void](Send 'say|Hello there')
    [void](Send 'say|/me waves')
    [void](Send 'say|//brb')
    $state = Poll { Send 'state' } '\[OOC\] [^:/]+: brb'
    Expect 'the owner was told its own name, and that he belongs to no faction' $state "has=True name='John Doe' faction='' "
    Expect 'a new character arrives with nothing but its clothes and its papers' $state 'items=\[Identification card@0,0\] wearing=\[Flat cap, Shirt and waistcoat, Trousers, Black boots\] '
    Expect 'and looks it: the cap he wears hides his hair' $state 'look=\[flatcap,waistcoat_and_shirt,trousers\.smart,black_boots\] '
    Expect 'speech, emote and out-of-character lines were delivered' $state 'John Doe says "Hello there" / \*\* John Doe waves / \[OOC\] [^:/]+: brb'

    Section 'Clothes are put on and taken off by hand'
    [void](Send 'wear|Flat cap')
    $state = Poll { Send 'state' } 'items=\[Flat cap@1,0, Identification card@0,0\]'
    Expect 'taken off, the cap is carried' $state 'items=\[Flat cap@1,0, Identification card@0,0\] wearing=\[Shirt and waistcoat, Trousers, Black boots\] '
    Expect 'and the hair it hid shows' $state 'look=\[hair_slickedback,waistcoat_and_shirt,trousers\.smart,black_boots\] '
    [void](Send 'item|drop|Black boots')
    Expect 'what is worn is taken off before it is dropped' { Send 'state' } 'wearing=\[Shirt and waistcoat, Trousers, Black boots\] .*Take it off first\.'
    [void](Send 'wear|Flat cap')
    Expect 'put back on, it is worn again and takes no room' { Send 'state' } 'items=\[Identification card@0,0\] wearing=\[Shirt and waistcoat, Trousers, Black boots, Flat cap\] look=\[waistcoat_and_shirt,trousers\.smart,black_boots,flatcap\] '
    [void](Send 'panel|inventory')
    Snapshot 'dressed'
    [void](Send 'panel|none')

    # Some refusals leave the state as it was, so the test waits for the host to have recorded the
    # request before it looks; requests are judged in the order they are sent.
    Section 'Doors'
    # Beside the first door; above it, on the roof; through it, on its far side; behind the wall beside
    # it; and by the locker.
    $door = Spot 'Door 1' left 30 60
    $roof = Spot 'Door 1' right 60 400
    $through = Spot 'Door 1' right 24 8
    $behindWall = Spot 'Wall' right 30 60
    $byLocker = Spot 'Locker' right 20 8
    [void](Send 'door|Door 1|use')
    Expect 'a door out of reach was refused' { Send 'state' } 'Door 1:open=False.*too far away'
    # goto is what a cheat does: the client simply declares itself somewhere else.
    [void](Send "goto|$($door -join '|')")
    [void](Send 'door|Door 1|use')
    [void](Poll { Send 'journal' } '(?<!\S)verb\.door\.use!=2(?!\S)')
    Expect 'claiming to stand at the door did not open it' (Send 'state') 'Door 1:open=False.*too far away'
    Expect 'the host sent the claimant back' { State-Near $door } '^near=False'
    Expect 'and put the claim on record' { Send 'journal' } 'movement\.implausible!=[1-9]'
    # The map has a roof right above the doors. Falling is believed; passing through a floor is not.
    Teleport 'John Doe' $roof
    Wait-Landed
    [void](Send "goto|$($door -join '|')")
    [void](Send 'door|Door 1|use')
    [void](Poll { Send 'journal' } '(?<!\S)verb\.door\.use!=3(?!\S)')
    Expect 'dropping through the roof to the door below did not open it' (Send 'state') 'Door 1:open=False.*too far away'
    # The host puts him back on the roof, and only then is he moved down to the door.
    [void](Poll { State-Near $roof } '^near=True')
    Teleport 'John Doe' $door
    [void](Send 'door|Door 1|use')
    [void](Send 'door|Door 2|use')
    [void](Send 'door|Door 1|lock')
    $state = Poll { Send 'state' } 'You have no key that fits'
    Expect 'a door in reach opened' $state 'Door 1:open=True,locked=False'
    Expect 'a door authored as locked stayed shut' $state 'Door 2:open=False,locked=True.*The door is locked'
    Expect 'without a key, nobody locks a door' $state 'You have no key that fits'

    Section 'A key is an item, cut to its lock'
    [void](Send 'console|hexagon_give "John Doe" keycard 1 city')
    [void](Send 'door|Door 1|lock')
    $state = Poll { Send 'state' } 'Door 1:open=False,locked=True'
    Expect 'an operator issued a keycard cut to the city''s locks, and its holder sees its cut' $state "items=\[Identification card@0,0, Keycard#$cityCut@1,0\]"
    Expect 'never the pattern, which would say what it opens' "$($state -notmatch '#city') $state" '^True '
    Expect 'holding the keycard let a citizen lock the door' $state 'Door 1:open=False,locked=True'
    # A closed door is a wall. One step through it is no faster than walking, and is still refused. Locking
    # also shut it, and a door still swinging is no wall yet (Player.HostBlocked), so the step waits.
    [void](Poll { Send 'state' } 'Door 1:open=False,locked=True,swinging=False')
    [void](Send "goto|$($through -join '|')")
    Expect 'stepping through the locked door did not get past it' { State-Near $door } '^near=True'
    Expect 'and the host recorded it as a wall' { Send 'journal|movement.implausible' } "ok=False .*claimed=$($through -join ',') reason=wall"
    [void](Send 'door|Door 1|lock')
    [void](Send 'drop|Keycard')
    [void](Send 'door|Door 1|lock')
    [void](Poll { Send 'journal' } '(?<!\S)verb\.door\.lock!=2(?!\S)')
    $state = Send 'state'
    Expect 'the dropped keycard left the inventory' $state 'items=\[Identification card@0,0\] '
    Expect 'and lies in the world' $state 'ground=\[Keycard\]'
    Expect 'without the keycard the door stayed unlocked' $state 'Door 1:open=False,locked=False.*no key that fits'

    Section 'Holders: the ground, a stocked crate'
    [void](Send 'pickup|Keycard')
    Expect 'picking it up is the same transfer in reverse' { Send 'state' } "Keycard#$cityCut@1,0.*ground=\[\]"
    [void](Send 'open|Crate')
    Expect 'the crate holds what its author stocked it with, placed the first time it is opened' { Send 'state' } 'open=\[Crate: Rationx4, Waterx2, Bandagex2, Tokenx40\]'
    foreach ($pile in 1..4) { [void](Send 'take|0') }
    Expect 'whole piles are taken whole, money with the rest' { Send 'state' } "items=\[Identification card@0,0, Keycard#$cityCut@1,0, Rationx4@2,0, Waterx2@3,0, Bandagex2@4,0, Tokenx40@5,0\] .*open=\[Crate: \]"
    [void](Send 'put|Keycard')
    [void](Send 'door|Door 1|lock')
    [void](Poll { Send 'journal' } '(?<!\S)verb\.door\.lock!=3(?!\S)')
    $state = Send 'state'
    Expect 'the crate shows its contents to whoever opened it' $state "open=\[Crate: Keycard#$cityCut\]"
    Expect 'a keycard in a crate opens nothing' "$($state -notmatch 'items=\[[^\]]*Keycard') $state" '^True .*Door 1:open=False,locked=False'
    [void](Send 'use|Water')
    Expect 'drinking used one water up, in view of anyone nearby' { Send 'state' } 'Water@3,0.*John Doe drinks from a can of water'
    [void](Send 'move|Ration|4|3')
    Expect 'an item moved to the requested cell' { Send 'state' } 'Rationx4@4,3'
    [void](Send 'close')
    Expect 'the crate closed' { Send 'state' } 'open=\[: \]'
    # A wall stands beside the first door. From its far side the crate is near enough, and out of reach.
    Teleport 'John Doe' $behindWall
    Wait-Landed
    [void](Send 'open|Crate')
    Expect 'a crate behind a wall cannot be reached, however near' { Send 'state' } 'open=\[: \].*You cannot reach it from here'
    Teleport 'John Doe' $door
    Wait-Landed

    Section 'Harm, and being down'
    # Staff hurt him where a weapon would: a wound to a leg stays until it is treated, and he limps on it.
    [void](Send 'console|hexagon_hurt "John Doe" 40 "left leg"')
    $state = Poll { Send 'state' } 'health=60 '
    Expect 'harm lowered health' $state 'health=60 down=False'
    Expect 'and the wound to his leg stays: he walks on it, slowly, and cannot run' $state 'wounds=\[left leg\] limping=True breathing=False speed=60 '
    Expect 'he is told where he was hit' $state 'You are hit in the left leg\.'
    Snapshot 'hurt' 0.6
    [void](Send 'use|Bandage')
    Expect 'a bandage treated the wound, healed a little, and was used up' { Send 'state' } 'Bandage@4,0.*health=90 .*wounds=\[\] limping=False breathing=False speed=320 .*Your left leg is treated\. / \*\* John Doe binds a wound'
    # A wound to an arm drops what is in hand, and the hand takes nothing until the wound is treated.
    [void](Send 'console|hexagon_give "John Doe" zip_tie')
    [void](Poll { Send 'state' } 'Zip tie@')
    [void](Send 'equip|Zip tie')
    [void](Poll { Send 'state' } "held='Zip tie'")
    [void](Send 'console|hexagon_hurt "John Doe" 10 "right arm"')
    $state = Poll { Send 'state' } 'wounds=\[right arm\]'
    Expect 'a wound to his arm dropped what he held' $state "items=\[Identification card@0,0, Rationx4@4,3, Water@3,0, Bandage@4,0, Tokenx40@5,0\] .*health=80 down=False restrained=False held='' .*wounds=\[right arm\] .*ground=\[Zip tie\]"
    [void](Send 'equip|Ration')
    Expect 'and that hand takes nothing' { Send 'state' } "held='' .*Your arm will not hold anything until the wound is treated\."
    [void](Send 'pickup|Zip tie')
    [void](Send 'console|hexagon_give "John Doe" bandage')
    [void](Poll { Send 'state' } 'Bandagex2@4,0.*Zip tie@')
    [void](Send 'use|Bandage')
    Expect 'treated, the arm is whole again' { Send 'state' } 'Bandage@4,0.*health=100 .*wounds=\[\] .*Your right arm is treated\.'
    [void](Send 'equip|Ration')
    Expect 'and holds what it is given' { Send 'state' } "held='Ration'"
    # Bound, his hands are behind his back, where anyone sees them. A test has one client, so he binds himself.
    Expect 'bound with the tie he picked up' (Send 'bind|John Doe') '^bound$'
    [void](Poll { Send 'state' } 'restrained=True')
    [void](Send 'thirdperson|1')
    Snapshot 'bound' 2
    [void](Send 'camera|Crate|left|20|70|self|40')
    Snapshot 'bound-front' 1
    [void](Send 'camera')
    [void](Send 'thirdperson|0')
    [void](Send 'console|hexagon_release "John Doe"')
    Expect 'released, what was in his hand is put away' { Send 'state' } "restrained=False held='' "
    [void](Send 'console|hexagon_hurt "John Doe" 500')
    [void](Send 'door|Door 1|use')
    [void](Send 'leave')
    [void](Poll { Send 'journal' } '(?<!\S)city\.leave!=1(?!\S)')
    $state = Send 'state'
    Expect 'at no health the character is down, not dead' $state 'has=True .*health=0 down=True'
    Expect 'someone who is down cannot leave the city' $state 'cannot leave the city like this'
    Expect 'or act on the world' (Send 'journal|verb.door.use') 'ok=False'
    $lying = Send 'sleepers'
    Expect 'his body lies limp where he fell' $lying '^-?\d+,-?\d+,down,limp$'
    # A body takes a moment to fall; he sees from it all the way down.
    Expect 'and he sees from it, on the ground' { Send 'state' } 'speed=\d+ view=-?(\d|[12]\d) '
    Snapshot 'down' 0.6
    [void](Send 'camera|Crate|left|20|70|self|0')
    Snapshot 'down-body' 1
    [void](Send 'camera')
    [void](Send 'console|hexagon_revive "John Doe"')
    $state = Poll { Send 'state' } 'health=25 down=False .*view=6\d '
    Expect 'helped up, barely, still breathing hard, and out of his own eyes again' $state 'health=25 down=False .*breathing=True speed=320 view=6\d .*sleepers=0 '
    Expect 'he stands where his body lay' "near=$(Near $state ([int[]] (($lying -split ',')[0..1] + 0))) $state" '^near=True'
    Teleport 'John Doe' $door

    Section 'Owning a door is title; its lock takes its key'
    [void](Send 'console|hexagon_door "Door 2" assign "John Doe"')
    [void](Send 'door|Door 2|lock')
    [void](Poll { Send 'journal' } '(?<!\S)verb\.door\.lock!=4(?!\S)')
    $state = Send 'state'
    Expect 'staff assigned the flat to him' $state 'owned=\[Door 2\]'
    Expect 'but owning it is not holding its key' $state 'Door 2:open=False,locked=True.*You have no key that fits'
    [void](Send 'open|Locker')
    Expect 'a locked locker opens only to a key that fits' { Send 'state' } 'It is locked, and you have no key that fits'
    [void](Send 'open|Crate')
    [void](Send 'take|0')
    [void](Send 'open|Locker')
    Expect 'the city keycard opens the locker, stocked as its author placed it' { Send 'state' } "open=\[Locker: Pistol, Pistol roundx30, Zip tiex2, Radio, Keycard#$cityCut, Key#$flatCut, Civil Protection mask, Civil Protection jacket, Civil Protection trousers, Black boots, Civil Authority stamp, Recruit's armband, Unit's armband, Officer's armband, Divisional Leader's armband, Token blankx20, Loupe\]"
    [void](Send 'take|Key')
    [void](Send 'door|Door 2|lock')
    [void](Send 'door|Door 2|use')
    Expect 'the spare key from the locker turned the flat''s lock' { Send 'state' } "Key#$flatCut@2,0.*Door 2:open=True,locked=False.*owned=\[Door 2\]"
    Snapshot 'locker'
    [void](Send 'close')
    [void](Send 'console|hexagon_door "Door 2" release')
    Expect 'staff can take a title back' { Send 'state' } 'owned=\[\]'

    Section 'Papers: ink stays, and a stamp makes them official'
    [void](Send 'read|Identification card')
    $state = Poll { Send 'state' } "paper='Identification card'"
    Expect 'he arrived with his card: his name in its blank, and the city office''s stamp on it' $state "paper='Identification card' shownby='' lines=\[\] fields=\[Name=John Doe\] stamps=\[Combine Civil Authority@0\] "
    Snapshot 'papers'
    [void](Send 'fill|Occupation|Dock worker')
    Expect 'without a pen, nothing is written' { Send 'state' } 'fields=\[Name=John Doe\] .*You have nothing to write with\.'
    [void](Send 'closepaper')
    $byStationery = Spot 'Stationery' front 20 8
    Teleport 'John Doe' $byStationery
    [void](Send 'open|Stationery')
    Expect 'a box of paper and pens stands open to anyone' { Send 'state' } 'open=\[Stationery box: Paperx10, Penx2\]'
    [void](Send 'take|Pen')
    [void](Send 'take|Paper')
    [void](Send 'close')
    [void](Send 'read|Identification card')
    [void](Send 'fill|Occupation|Dock worker')
    [void](Send 'fill|occupation|Smuggler')
    Expect 'with a pen he fills in a blank, once: ink stays' { Send 'state' } 'fields=\[Name=John Doe, Occupation=Dock worker\] .*The Occupation is already filled in\.'
    [void](Send 'read|Paper')
    # Nothing written takes nothing off the pile: the next check finds the pile one sheet short, not two.
    [void](Send 'write|')
    Expect 'with nothing to write, nothing is written' { Send 'state' } 'Paperx10@1,1\] .*Write something\.'
    [void](Send 'write|Meet me at the docks at nine.')
    $state = Poll { Send 'state' } 'lines=\[Meet me'
    Expect 'written on, a pile of paper gives up one sheet' $state "Penx2@0,1, Paperx9@1,1, Paper@2,1\] .*paper='Paper' shownby='' lines=\[Meet me at the docks at nine\.\] "
    [void](Send 'write|Come alone.')
    Expect 'the next line goes below it' { Send 'state' } 'lines=\[Meet me at the docks at nine\. / Come alone\.\] '
    # The office's stamp is in the locker his keycard opens. Whoever holds it can make a paper official.
    [void](Send 'open|Locker')
    [void](Send 'take|Civil Authority stamp')
    [void](Send 'close')
    [void](Send 'stamp|Civil Authority stamp')
    [void](Send 'write|By order of the Civil Authority.')
    $state = Poll { Send 'state' } 'Civil Authority\.\]'
    Expect 'the stamp goes on over what was written by then, and a line written after it lies below it' $state 'lines=\[Meet me at the docks at nine\. / Come alone\. / By order of the Civil Authority\.\] fields=\[\] stamps=\[Combine Civil Authority@2\] '
    Snapshot 'letter'
    Expect 'the paper does not say whose hand it was; the journal does' { Send 'journal|paper.write' } "ok=True actor='John Doe' .*text=By order of the Civil Authority\."
    Expect 'and whose hand stamped it' (Send 'journal|paper.stamp') "ok=True actor='John Doe' .*stamp=items/ca_stamp\.item"
    [void](Send 'closepaper')
    [void](Send 'open|Locker')
    [void](Send 'put|Civil Authority stamp')
    [void](Send 'close')
    [void](Send 'open|Stationery')
    [void](Send 'put|Pen')
    [void](Send 'put|Paper@1,1')
    Expect 'he puts back the pens and the blank paper, and keeps his letter' { Send 'state' } 'Paper@2,1.*open=\[Stationery box: Penx2, Paperx9\]'
    [void](Send 'close')
    Teleport 'John Doe' $door

    Section 'Maps: laid out on a table, marked in grease pencil, counters moved by hand'
    $byTable = Spot 'Planning table' back 20 8
    Teleport 'John Doe' $byTable
    [void](Send 'open|Map case')
    Expect 'a case by the planning table holds a city map and grease pencils' { Send 'state' } 'open=\[Map case: City map, Grease pencilx2\]'
    [void](Send 'take|City map')
    [void](Send 'close')
    [void](Send 'face|Planning table|level')
    [void](Send 'layout|City map')
    Expect 'he lays the map out on the table in front of him' { Send 'maps' } '^City map@4\d\d,1\d\d,62 leaning=False marks=\[\] counters=\[\]$'
    Expect 'and carries it no longer' "$((Send 'state') -notmatch 'City map@') x" '^True'
    [void](Send 'lean')
    Expect 'he leans over it' { Send 'state' } "leaning='City map'"
    [void](Send 'mapdraw|100,200;300,400;500,450')
    Expect 'with nothing to write with, nothing goes on the map' { Send 'journal|map.draw' } "ok=False actor='John Doe' .*reason=Denied"
    Expect 'and it stays clean' (Send 'maps') 'marks=\[\]'
    [void](Send 'standup')
    Expect 'he stands up from it' { Send 'state' } "leaning='' "
    [void](Send 'open|Map case')
    [void](Send 'take|Grease pencil')
    [void](Send 'close')
    [void](Send 'lean')
    [void](Poll { Send 'state' } "leaning='City map'")
    [void](Send 'mapdraw|100,200;300,400;500,450')
    Expect 'with a grease pencil he draws a line across it, in red' { Send 'maps' } 'marks=\[1:line3#B31E17\]'
    [void](Send 'mapword|520|470|Rally here')
    Expect 'and writes a word where it ends' { Send 'maps' } "marks=\[1:line3#B31E17, 2:'Rally here'#B31E17\]"
    [void](Send 'mapmove|0|800|150')
    [void](Send 'mapmove|2|200|700')
    Expect 'the counters that came with it are stood on it by hand' { Send 'maps' } 'counters=\[0@800,150, 2@200,700\]'
    [void](Send 'mapmove|2|box')
    Expect 'and one goes back in its box' { Send 'maps' } 'counters=\[0@800,150\]$'
    [void](Send 'mapwipe|1')
    Expect 'the line wipes off, and only the line' { Send 'maps' } "marks=\[2:'Rally here'#B31E17\]"
    [void](Send 'mapwipe|1')
    Expect 'what is gone cannot be wiped off again' { Send 'journal|map.wipe' } 'ok=False .*mark=1 reason=NotFound'
    # The engine's world input hands the paper the mouse of whoever leans over it; the driver hands it
    # the same presses, drags and releases, at places on the map.
    [void](Send 'maptool|draw')
    foreach ($step in 'press|200|300', 'drag|400|320', 'drag|600|500', 'release|700|520') { [void](Send "mapmouse|$step") }
    Expect 'dragged across the map, the hand draws a line through where it went' { Send 'maps' } "marks=\[2:'Rally here'#B31E17, 3:line4#B31E17\]"
    # The second of six counters, in its box along the map's near edge.
    foreach ($step in 'press|286|1083', 'drag|300|600', 'release|300|250') { [void](Send "mapmouse|$step") }
    Expect 'a counter is taken out of its box by hand and stood on the map' { Send 'maps' } 'counters=\[0@800,150, 1@300,250\]'
    [void](Send 'maptool|wipe')
    foreach ($step in 'press|525|470', 'release|525|470') { [void](Send "mapmouse|$step") }
    Expect 'a word is wiped off by hand' { Send 'maps' } 'marks=\[3:line4#B31E17\]'
    [void](Send 'maptool|write|Depot')
    foreach ($step in 'press|150|150', 'release|150|150') { [void](Send "mapmouse|$step") }
    Expect 'and with a word ready, a click writes it where the hand is' { Send 'maps' } "marks=\[3:line4#B31E17, 4:'Depot'#B31E17\]"
    Snapshot 'map' 0.5
    [void](Send 'standup')
    [void](Poll { Send 'state' } "leaning='' ")
    [void](Send 'pickup|City map')
    Expect 'picked up, the map is carried again' { Send 'state' } 'City map@'
    Expect 'and nothing of it is left on the table' { Send 'maps' } '^$'
    [void](Send 'layout|City map')
    Expect 'laid out again, what was on it went with it' { Send 'maps' } "^City map@4\d\d,1\d\d,62 leaning=False marks=\[3:line4#B31E17, 4:'Depot'#B31E17\] counters=\[0@800,150, 1@300,250\]$"
    [void](Send 'camera|Planning table|back|50|70|Planning table|10')
    Snapshot 'map-table' 1
    [void](Send 'camera')
    Expect 'the map does not say whose hand marked it; the journal does' (Send 'journal|map.write') "ok=True actor='John Doe' .*text=Depot"
    Expect 'and whose hand moved a counter' (Send 'journal|map.move') "ok=True actor='John Doe' .*counter=1 at=300,250"
    [void](Send 'open|Map case')
    [void](Send 'put|Grease pencil')
    Expect 'he puts the grease pencils back, and leaves the map out' { Send 'state' } 'open=\[Map case: Grease pencilx2\]'
    [void](Send 'close')
    Teleport 'John Doe' $door

    Section 'Staff work in game, on the record'
    $steamId = Send 'steamid'
    [void](Send 'staff|who')
    Expect 'someone who is not staff is refused' { Send 'state' } 'staff=False said=\[You are not staff\.\]'
    [void](Send "console|hexagon_staff $steamId 1")
    [void](Send 'staff|who')
    Expect 'staff status comes only from the console, and then who is who is visible' { Send 'state' } 'staff=True said=\[[^/]* is John Doe at '
    [void](Send 'panel|staff')
    Snapshot 'staff'
    [void](Send 'panel|none')
    [void](Send 'staff|give "John Doe" radio')
    Expect 'a staff command does what the console command does' { Send 'state' } 'Radio@0,1.*said=\[Gave Radio to John Doe\.\]'
    [void](Send 'staff|journal door.assign')
    Expect 'staff can read the journal' { Send 'state' } 'said=\[\d\d:\d\d:\d\d door\.assign by console on Door:\w+ \(owner=John Doe\)'
    [void](Send 'staff|journal "REFUSED by John Doe (line=who)"')
    Expect 'including what staff themselves did, refusals too' { Send 'state' } 'staff\.command REFUSED by John Doe \(line=who\)'
    [void](Send "console|hexagon_staff $steamId 0")
    [void](Send 'staff|who')
    Expect 'and it can be taken away' { Send 'state' } 'staff=False said=\[You are not staff\.\]'

    Section 'Radio'
    [void](Send 'say|/r hello')
    Expect 'an untuned radio carries nothing' { Send 'state' } 'no radio tuned to anything'
    [void](Send 'tune|Radio|55')
    Expect 'a frequency outside the band is refused' { Send 'state' } 'A frequency is between'
    [void](Send 'tune|Radio|101.50')
    [void](Send 'say|/r testing')
    Expect 'the dial belongs to the radio, and the speaker hears themselves on it' { Send 'state' } 'Radio~101\.5@0,1.*\[101\.5\] John Doe: "testing"'

    Section 'What is left in a crate is anyone''s'
    [void](Send 'open|Crate')
    [void](Send 'put|Keycard')
    [void](Send 'put|Bandage')
    Expect 'he left the keycard and a bandage in the crate' { Send 'state' } "open=\[Crate: Keycard#$cityCut, Bandage\]"
    [void](Send 'close')

    Section 'A faction is people: its uniform is handed over and put on'
    [void](Send 'leave')
    [void](Send "create|Officer Kane|$description|human01|hair_shortscruffy_brown,flannel_shirt,jeans")
    [void](Send 'enter|Officer Kane')
    [void](Poll { Send 'state' } "has=True name='Officer Kane'")
    Teleport 'Officer Kane' $door
    # Staff appoint a faction's head; its own people recruit the rest. Either way he is only told he belongs.
    [void](Send 'console|hexagon_faction "Officer Kane" civil_protection')
    [void](Send 'door|Door 1|lock')
    [void](Poll { Send 'journal' } '(?<!\S)verb\.door\.lock!=5(?!\S)')
    $state = Send 'state'
    Expect 'staff appointed him to a faction, at its head' $state "name='Officer Kane' faction='Civil Protection' rank='Divisional Leader' "
    Expect 'which changed nothing about him: he wears what he arrived in' $state 'wearing=\[Flannel shirt, Jeans\] look=\[human01,hair_shortscruffy_brown,flannel_shirt,jeans\] '
    Expect 'he arrives with nothing but his papers, and his faction lets him lock nothing' $state 'items=\[Identification card@0,0\] .*Door 1:open=False,locked=False.*no key that fits'
    [void](Send 'open|Crate')
    [void](Send 'take|0')
    [void](Send 'open|Locker')
    foreach ($pile in 1..3) { [void](Send 'take|0') }
    # After the radio and the locker's own keycard, the uniform.
    foreach ($piece in 1..4) { [void](Send 'take|2') }
    [void](Send 'wear|Civil Protection mask')
    [void](Send 'wear|Civil Protection jacket')
    [void](Send 'wear|Civil Protection trousers')
    Expect 'nothing is taken off to make way for what is put on' { Send 'state' } 'wearing=\[Flannel shirt, Jeans, Civil Protection mask, Civil Protection jacket\] .*Take off the jeans first\.'
    [void](Send 'wear|Jeans')
    [void](Send 'wear|Civil Protection trousers')
    [void](Send 'wear|Black boots')
    $state = Poll { Send 'state' } 'trousers, Black boots\] '
    Expect 'the uniform is on him, over the shirt he came in' $state 'wearing=\[Flannel shirt, Civil Protection mask, Civil Protection jacket, Civil Protection trousers, Black boots\] '
    Expect 'and anyone can see it' $state 'look=\[human01,hair_shortscruffy_brown,flannel_shirt,gasmask,black_leather_jacket,jeans_black,black_boots\] '
    [void](Send 'put|Jeans')
    Expect 'his own jeans stay in the locker, with the armbands of every rank' { Send 'state' } "open=\[Locker: Radio, Keycard#$cityCut, Recruit's armband, Unit's armband, Officer's armband, Divisional Leader's armband, Token blankx20, Loupe, Civil Authority stamp, Jeans\]"
    [void](Send 'close')
    [void](Send 'door|Door 1|lock')
    $state = Poll { Send 'state' } 'Door 1:open=False,locked=True'
    Expect 'with the keycard from the crate he locked the door, which also shut it' $state 'Door 1:open=False,locked=True'
    Expect 'a character put in the air comes down: a driven client still has a body' { Send 'state' } 'pos=[-\d.]+,[-\d.]+,[0-4]\.'
    Expect 'a pistol and thirty rounds from the locker' $state "items=\[Identification card@0,0, Keycard#$cityCut@1,0, Pistol@2,0, Pistol roundx30@4,0, Zip tiex2@5,0\]"
    [void](Send 'split|Pistol round|10')
    Expect 'part of a pile can be set aside' { Send 'state' } 'Pistol roundx20@4,0.*Pistol roundx10@0,1'
    [void](Send 'merge|Pistol round@0,1|Pistol round@4,0')
    [void](Send 'equip|Pistol')
    Expect 'what is in hand is appearance: everyone is sent it' { Send 'state' } "held='Pistol'"
    [void](Send 'thirdperson|1')
    [void](Send 'face|Locker')
    # The camera eases out behind him, then is placed where each picture wants it.
    Snapshot 'officer' 2
    [void](Send 'camera|Crate|left|20|70|self|40')
    Snapshot 'officer-front' 1
    [void](Send 'camera|Crate|left|10|130|self|120')
    Snapshot 'officer-front-high' 1
    [void](Send 'camera')
    [void](Send 'thirdperson|0')
    [void](Send 'menu|Door 2')
    Snapshot 'verb-menu' 0.6
    [void](Send 'menu')
    [void](Send 'panel|inventory')
    Snapshot 'inventory'
    [void](Send 'panel|none')
    Snapshot 'crate-and-locker'
    Expect 'and put back' "$((Send 'state') -notmatch 'roundx10') $(Send 'state')" '^True .*Pistol roundx30@'

    Section 'Leaving does not end a scene'
    [void](Send 'console|hexagon_hurt "Officer Kane" 500')
    # Moved after he fell, so only the save as the city closes knows where he is now.
    Teleport 'Officer Kane' $byLocker
    [void](Send 'leave')
    [void](Poll { Send 'journal' } '(?<!\S)city\.leave!=2(?!\S)')
    Expect 'down, he cannot leave, so the city closes with him lying there' (Send 'state') 'has=True .*down=True .*cannot leave the city like this'

    Section 'Restart'
    [void](Invoke-Tool 'play_stop')
    Wait-Until 'play mode to stop' { -not (Invoke-Tool 'editor_status' | ConvertFrom-Json).IsPlaying }
    Start-Play
    $state = Send 'state'
    Expect 'both characters survived the restart' $state 'characters=\[John Doe, Officer Kane\]'
    Expect 'the locked door survived the restart' $state 'Door 1:open=False,locked=True'
    Expect 'and so did the open one' $state 'Door 2:open=True,locked=False'
    Expect 'a new session starts with an empty chat log' $state 'chat=\[\]'
    Expect 'the game''s own rules are back: death is final, one life at a time, a day between' (Send 'policy') 'death=Permanent living=1 hours=24'
    [void](Send 'enter|John Doe')
    # What someone left lying looks like is sent only once the network has stopped sending it to those too far away.
    $state = Poll { Send 'state' } 'sleepers=1 '
    Expect 'what he carried survived the restart, keys cut as they were' $state "items=\[Identification card@0,0, Rationx4@4,3, Water@3,0, Tokenx40@5,0, Key#$flatCut@2,0, Paper@2,1, Radio~101\.5@0,1\] wearing=\[Shirt and waistcoat, Trousers, Black boots, Flat cap\] "
    Expect 'the title staff took back is still nobody''s' $state 'owned=\[\]'
    Expect 'the map left out on the table survived the restart, with what was on it' { Send 'maps' } "^City map@4\d\d,1\d\d,62 leaning=False marks=\[3:line4#B31E17, 4:'Depot'#B31E17\] counters=\[0@800,150, 1@300,250\]$"
    Expect 'whoever was down when the city closed is still lying there' $state 'sleepers=1 '
    $sleepers = Send 'sleepers'
    Expect 'where he was when it closed, which only the closing save knew, lying limp' "$(Near $sleepers $byLocker) $sleepers" '^True .*,down,limp$'
    [void](Send 'camera|Locker|front|60|60|person|0')
    Snapshot 'lying' 1
    [void](Send 'camera')
    # The swinging door nudges the pawn, so this is a neighbourhood, not a point.
    Expect 'the character returned to where it stood' "near=$(Near $state $door) $state" '^near=True'
    [void](Send 'open|Crate')
    Expect 'what was left in the crate survived the restart' { Send 'state' } 'open=\[Crate: Bandage\]'
    [void](Send 'close')
    [void](Send 'read|Paper')
    Expect 'and so did what was written and stamped on his letter' { Send 'state' } "paper='Paper' .*lines=\[Meet me at the docks at nine\. / Come alone\. / By order of the Civil Authority\.\] fields=\[\] stamps=\[Combine Civil Authority@2\] "
    [void](Send 'closepaper')

    Section 'Death is final'
    [void](Send 'act|person.finish')
    Expect 'someone left lying can be finished, and leaves a body' { Send 'state' } 'bodies=1 .*sleepers=0 '
    Expect 'the body wears what he wore' (Send 'state') 'bodylooks=\[human01,hair_shortscruffy_brown,flannel_shirt,gasmask,black_leather_jacket,jeans_black,black_boots\] '
    [void](Send 'camera|Locker|front|60|60|body|0')
    Snapshot 'dead' 1
    [void](Send 'camera')
    [void](Send 'act|corpse.search')
    $state = Poll { Send 'state' } 'open=\[Body: '
    Expect 'what he carried is on the body' $state "open=\[Body: Identification card, Keycard#$cityCut, Pistol, Pistol roundx30, Zip tiex2\]"
    Expect 'and what he wore is still on it' $state 'openworn=\[Flannel shirt, Civil Protection mask, Civil Protection jacket, Civil Protection trousers, Black boots\] '
    [void](Send 'take|Pistol')
    [void](Send 'strip|Civil Protection mask')
    Expect 'what is taken off a body is gone from it, for anyone to see' { Send 'state' } 'bodylooks=\[human01,hair_shortscruffy_brown,flannel_shirt,black_leather_jacket,jeans_black,black_boots\] '
    [void](Send 'wear|Civil Protection mask')
    Expect 'and put on, it is a disguise' { Send 'state' } 'wearing=\[Shirt and waistcoat, Trousers, Black boots, Flat cap, Civil Protection mask\] look=\[waistcoat_and_shirt,trousers\.smart,black_boots,flatcap,gasmask\] '
    [void](Send 'leave')
    Expect 'the dead stay on the list, as dead' { Send 'state' } 'characters=\[John Doe, Officer Kane\(dead\)\]'
    [void](Send 'enter|Officer Kane')
    Expect 'and nobody plays them again' { Send 'state' } 'Officer Kane is dead\.'

    Section 'A life is ended by retiring; the next one waits'
    [void](Send "console|hexagon_staff $steamId 1")
    [void](Send 'retire|John Doe')
    Expect 'retiring is a death' { Send 'state' } 'characters=\[John Doe\(dead\), Officer Kane\(dead\)\]'
    [void](Send "create|Max Muster|$description|citizen|jumpsuit")
    [void](Send 'enter|Max Muster')
    $state = Poll { Send 'state' } 'Max Muster arrives in the city'
    Expect 'the next character is made at once' $state 'characters=\[John Doe\(dead\), Officer Kane\(dead\), Max Muster\(waits\)\]'
    Expect 'but enters the city a day after the death' $state 'Max Muster arrives in the city in 2[34] hours'
    [void](Send 'staff|restore "Officer Kane"')
    Expect 'staff restore nobody while the account has its living character' { Send 'state' } 'said=\[Their account already has Max Muster alive\. That character is deleted or retired first\.\]'
    [void](Send 'delete|Max Muster')
    Expect 'one that never lived is deleted, freeing its name' { Send 'state' } 'characters=\[John Doe\(dead\), Officer Kane\(dead\)\]'

    Section 'Staff undo a death that broke the rules'
    [void](Send 'staff|restore "Officer Kane"')
    Expect 'he is alive again' { Send 'state' } 'characters=\[John Doe\(dead\), Officer Kane\] .*said=\[Officer Kane is alive again'
    [void](Send "console|hexagon_staff $steamId 0")
    [void](Send 'enter|Officer Kane')
    $state = Poll { Send 'state' } 'health=25 down=False .*bodies=0 '
    Expect 'he gets up barely, with what was still on the body: what was taken stays taken' $state "items=\[Identification card@0,0, Keycard#$cityCut@1,0, Pistol roundx30@4,0, Zip tiex2@5,0\] wearing=\[Flannel shirt, Civil Protection jacket, Civil Protection trousers, Black boots\] .*health=25 down=False .*bodies=0 "
    Expect 'where the body lay' "near=$(Near $state $byLocker) $state" '^near=True'

    Section 'The journal'
    $journal = Send 'journal'
    Expect 'every item came from a named source: staff, the scene as built, or what a character arrived in' $journal '(?<!\S)item\.issue=39(?!\S)'
    Expect 'what was used up, or went with a retired or deleted character, went through a sink' $journal '(?<!\S)item\.destroy=19(?!\S)'
    Expect 'every hand-over is on record, bodies, restores, stripping and what a wounded arm let fall included' $journal '(?<!\S)item\.move=54(?!\S)'
    Expect 'putting on and taking off is on record' $journal '(?<!\S)verb\.item\.wear=8(?!\S)'
    Expect 'and the refused clash' $journal '(?<!\S)verb\.item\.wear!=1(?!\S)'
    Expect 'something worn is not dropped' $journal '(?<!\S)verb\.item\.drop!=1(?!\S)'
    Expect 'refused locks are on record' $journal '(?<!\S)verb\.door\.lock!=5(?!\S)'
    Expect 'allowed locks too' $journal '(?<!\S)verb\.door\.lock=4(?!\S)'
    Expect 'refused uses are on record' $journal '(?<!\S)verb\.door\.use!=5(?!\S)'
    Expect 'and the locker that would not open, and the crate behind a wall' $journal '(?<!\S)verb\.container\.open!=2(?!\S)'
    Expect 'the operator is on record too, appointing him' $journal '(?<!\S)operator\.faction=1(?!\S)'
    Expect 'and so is what staff did to a door' $journal '(?<!\S)door\.assign=1(?!\S).*(?<!\S)door\.release=1(?!\S)'
    Expect 'every character made, and every one refused' $journal '(?<!\S)character\.create=3(?!\S).*(?<!\S)character\.create!=4(?!\S)|(?<!\S)character\.create!=4(?!\S).*(?<!\S)character\.create=3(?!\S)'
    Expect 'the refused deletion and the allowed one' $journal '(?<!\S)character\.delete!=1(?!\S).*(?<!\S)character\.delete=1(?!\S)'
    Expect 'the death, the retirement and the restore' $journal '(?<!\S)character\.death=1(?!\S).*(?<!\S)character\.retire=1(?!\S).*(?<!\S)operator\.restore=1(?!\S)'
    Expect 'someone left lying is acted on like anyone else, on the record' $journal '(?<!\S)verb\.person\.finish=1(?!\S)'
    Expect 'the dead and the waiting were turned away at the gate' $journal '(?<!\S)city\.enter!=2(?!\S)'
    Expect 'speech is on record' $journal '(?<!\S)chat\.say=1(?!\S).*(?<!\S)chat\.me=4(?!\S).*(?<!\S)chat\.ooc=1(?!\S)'
    Expect 'every wound, and every one treated' $journal '(?<!\S)character\.wounded=2(?!\S).*(?<!\S)character\.treated=2(?!\S)'
    Expect 'and the rearranging of piles, a sheet taken off a pile to write on included' $journal '(?<!\S)item\.split=2(?!\S).*(?<!\S)item\.merge=1(?!\S)'
    Expect 'every line written, and the blank filled in' $journal '(?<!\S)paper\.write=3(?!\S).*(?<!\S)paper\.fill=1(?!\S)|(?<!\S)paper\.fill=1(?!\S).*(?<!\S)paper\.write=3(?!\S)'
    Expect 'the blank written without a pen and the one written twice, refused' $journal '(?<!\S)paper\.fill!=2(?!\S)'
    Expect 'and the line with nothing in it' $journal '(?<!\S)paper\.write!=1(?!\S)'
    Expect 'and the stamp' $journal '(?<!\S)paper\.stamp=1(?!\S)'
    Expect 'a map laid out twice, and leant over twice' $journal '(?<!\S)verb\.item\.layout=2(?!\S).*(?<!\S)verb\.map\.lean=2(?!\S)'
    Expect 'every line drawn on it, and the one refused for want of a pencil' $journal '(?<!\S)map\.draw!=1(?!\S).*(?<!\S)map\.draw=2(?!\S)'
    Expect 'every word written on it' $journal '(?<!\S)map\.write=2(?!\S)'
    Expect 'every counter moved' $journal '(?<!\S)map\.move=4(?!\S)'
    Expect 'and everything wiped off, and the wipe of what was already gone' $journal '(?<!\S)map\.wipe=2(?!\S).*(?<!\S)map\.wipe!=1(?!\S)'
    Expect 'this client was told no other name' (Send 'proxies') '^[^A-Za-z]*$'

    Complete-Session
}
catch {
    if ($_.Exception.Message -ne $untilReached) { throw }
}
finally {
    if ($editor -and -not $editor.HasExited) { Stop-Process -Id $editor.Id -Force }
    # An open editor is left as it was found, but for the scene it plays.
    if ($Attach -and $null -ne $priorDataRoot) {
        try {
            if ((Invoke-Tool 'editor_status' | ConvertFrom-Json).IsPlaying) { [void](Invoke-Tool 'play_stop') }
            [void](Invoke-Tool 'console_command' @{ command = "hexagon_data_root `"$priorDataRoot`"" })
        }
        catch { Write-Host "Could not point the editor back at '$priorDataRoot': $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    $http.Dispose()
    # A failed run keeps its data, journal included: that is the evidence of what the host decided.
    $kept = Get-ChildItem -LiteralPath (Join-Path $SboxRoot 'data') -Recurse -Directory -Filter $dataRoot -ErrorAction SilentlyContinue
    if ($script:failures -gt 0) { $kept | ForEach-Object { Write-Host "Data kept at $($_.FullName)" -ForegroundColor DarkGray } }
    else { $kept | Remove-Item -Recurse -Force }
}

if ($script:failures -gt 0) { throw "$($script:failures) play-test expectation(s) failed." }
if ($Until -and -not $script:untilPlayed) { throw "No section's name starts with '$Until'." }
Write-Host "Play test passed in the real engine$(if ($Until) { ", through '$Until'" })." -ForegroundColor Green
