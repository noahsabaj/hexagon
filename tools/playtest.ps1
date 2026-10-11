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
    [int] $TimeoutSeconds = 480
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

if (Get-Process -Name 'sbox-dev' -ErrorAction SilentlyContinue) { throw 'Close the s&box editor first.' }

$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(60)
$http.DefaultRequestHeaders.Accept.ParseAdd('application/json, text/event-stream')
$script:requestId = 0
$script:failures = 0
$script:nonce = 0
# The host lets a player ask for twelve things at once and four more each second, and refuses the rest
# (GameManager.HostSpend). A test is held to that like any player, so it keeps the same count, two
# short for the moment a request takes to arrive. Reading its own state, or moving its own view, asks
# the host for nothing.
$requests = 'create', 'enter', 'leave', 'delete', 'retire', 'say', 'introduce', 'door', 'open', 'take', 'put', 'close',
    'pickup', 'drop', 'use', 'equip', 'move', 'split', 'merge', 'tune', 'act', 'attack', 'offer', 'offerdoor', 'answer', 'staff'
$script:allowance = 10.0
$script:counted = [DateTime]::UtcNow
$script:lastProblem = 'none'
$script:replayed = $false

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
        if ($editor.HasExited) { throw "s&box exited with code $($editor.ExitCode) while waiting for $What." }
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
    try { [void](Invoke-Tool 'set_component' $write) }
    catch {
        # Swapping the code in, the editor briefly cannot describe the driver, and fails while checking
        # the write, before setting anything: the command never ran, so it is sent again once the
        # code is in. Anything else, the driver's own failures included, is a failure.
        $problem = $_.Exception.Message
        if ($problem -notmatch 'NullReferenceException' -or $problem -notmatch 'ApplyComponentProperties' -or $problem -match 'set_Command') { throw }
        Write-Host "       the editor was swapping the code in; sending '$Command' again" -ForegroundColor DarkGray
        Start-Sleep -Seconds 2
        Wait-Built
        [void](Invoke-Tool 'set_component' $write)
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

# Moves the character as staff would, and waits until the player's own pawn is there. Until then, what
# the client claims about where it stands is judged against the old place.
function Teleport([string] $Name, [int] $X, [int] $Y, [int] $Z) {
    [void](Send "console|hexagon_teleport `"$Name`" $X $Y $Z")
    $until = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if ((Send 'state') -match 'pos=(-?[\d.]+),(-?[\d.]+),' -and
            [Math]::Abs([double] $Matches[1] - $X) -lt 32 -and [Math]::Abs([double] $Matches[2] - $Y) -lt 32) { return }
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
        # A scene played while the editor was still taking the code in has no game manager, so nobody
        # ever arrives. That is the editor, not the game: the scene is played again. A game that lets
        # nobody in still fails, at the deadline.
        $arrival = [DateTime]::UtcNow.AddSeconds(60)
        while ([DateTime]::UtcNow -lt $arrival) {
            if ($editor.HasExited) { throw "s&box exited with code $($editor.ExitCode) while waiting for the local player." }
            if ([DateTime]::UtcNow -ge $script:deadline) { throw "Timed out waiting for the local player. Last problem: $($script:lastProblem)" }
            try {
                $reply = Send 'state'
                if ($reply -match '^has=') { return }
                $script:lastProblem = "driver said: $reply"
            }
            catch { $script:lastProblem = $_.Exception.Message }
            Start-Sleep -Milliseconds 500
        }
        Write-Host "       nobody arrived in a minute ($($script:lastProblem)); playing the scene again" -ForegroundColor DarkGray
        $script:replayed = $true
        [void](Invoke-Tool 'play_stop')
        Start-Sleep -Seconds 2
    }
}

Write-Host "==> Starting the editor (data folder '$dataRoot')" -ForegroundColor Cyan
# RenderDoc, when it is on the PATH, is loaded into the editor, and with it every native library
# the engine loads while starting is slow. The editor is started without it.
$pathWithoutRenderDoc = ($env:Path -split ';' | Where-Object { $_ -and -not (Test-Path -LiteralPath (Join-Path $_ 'renderdoc.dll')) }) -join ';'
$editor = Start-Process -FilePath $sboxDev -WorkingDirectory $SboxRoot -PassThru -WindowStyle Hidden `
    -Environment @{ Path = $pathWithoutRenderDoc } -ArgumentList "-project `"$manifest`" +hexagon_data_root $dataRoot"
$script:deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
try {
    Wait-Until 'the editor control endpoint' {
        (Invoke-Editor 'initialize' ([ordered]@{
            protocolVersion = '2025-03-26'; capabilities = @{}; clientInfo = @{ name = 'hexagon-playtest'; version = '1' }
        })).result.protocolVersion
    }
    Wait-Until 'the game assembly' { (Invoke-Tool 'get_component_type' @{ name = 'DevDriver' }) -match 'Hexagon' }

    Write-Host '==> Characters' -ForegroundColor Cyan
    Start-Play
    Expect 'a new city has no characters' (Send 'state') 'has=False .*characters=\[\]'
    Snapshot 'characters'
    [void](Send "create|John Doe|$description|citizen")
    [void](Send "create|J0hn Doe|$description|citizen")
    Expect 'an account lives one life at a time' { Send 'state' } 'characters=\[John Doe\].*You already have a living character'
    # One account plays both sides here, so this city allows two lives at once.
    Expect 'a game may allow more' (Send 'policy|living|2') 'living=2'
    [void](Send "create|J0hn Doe|$description|citizen")
    [void](Send "create|Officer Kane|$description|civil_protection")
    $state = Poll { Send 'state' } 'not whitelisted for Civil Protection'
    Expect 'the look-alike name was refused with a reason' $state 'characters=\[John Doe\].*reads like it'
    Expect 'a whitelisted faction refused an unlisted account' $state 'not whitelisted for Civil Protection'

    Write-Host '==> Entering the city, chat, inventory' -ForegroundColor Cyan
    [void](Send 'enter|John Doe')
    [void](Send 'delete|John Doe')
    Expect 'a character that has lived is not deleted' { Send 'state' } 'characters=\[John Doe\].*has lived in the city'
    [void](Send 'say|Hello there')
    [void](Send 'say|/me waves')
    [void](Send 'say|//brb')
    $state = Poll { Send 'state' } '\[OOC\] [^:/]+: brb'
    Expect 'the owner was told its own name and faction' $state "has=True name='John Doe' faction='Citizen' "
    Expect 'a new character arrives with nothing but its clothes' $state 'items=\[\] '
    Expect 'speech, emote and out-of-character lines were delivered' $state 'John Doe says "Hello there" / \*\* John Doe waves / \[OOC\] [^:/]+: brb'

    # Some refusals leave the state as it was, so the test waits for the host to have recorded the
    # request before it looks; requests are judged in the order they are sent.
    Write-Host '==> Doors' -ForegroundColor Cyan
    [void](Send 'door|Door 1|use')
    Expect 'a door out of reach was refused' { Send 'state' } 'Door 1:open=False.*too far away'
    # goto is what a cheat does: the client simply declares itself somewhere else.
    [void](Send 'goto|240|-150|60')
    [void](Send 'door|Door 1|use')
    [void](Poll { Send 'journal' } '(?<!\S)verb\.door\.use!=2(?!\S)')
    Expect 'claiming to stand at the door did not open it' (Send 'state') 'Door 1:open=False.*too far away'
    Expect 'the host sent the claimant back' { Send 'state' } 'pos=(?!2[2-5]\d\.?\d*,-1[2-6]\d)'
    Expect 'and put the claim on record' { Send 'journal' } 'movement\.implausible!=[1-9]'
    # The map has a roof right above the doors. Falling is believed; passing through a floor is not.
    Teleport 'John Doe' 272 -240 400
    Wait-Landed
    [void](Send 'goto|240|-150|60')
    [void](Send 'door|Door 1|use')
    [void](Poll { Send 'journal' } '(?<!\S)verb\.door\.use!=3(?!\S)')
    Expect 'dropping through the roof to the door below did not open it' (Send 'state') 'Door 1:open=False.*too far away'
    # The host puts him back on the roof, and only then is he moved down to the door.
    [void](Poll { Send 'state' } 'pos=2[5-9]\d\.?\d*,-2[2-6]\d')
    Teleport 'John Doe' 240 -150 60
    [void](Send 'door|Door 1|use')
    [void](Send 'door|Door 2|use')
    [void](Send 'door|Door 1|lock')
    $state = Poll { Send 'state' } 'You have no key that fits'
    Expect 'a door in reach opened' $state 'Door 1:open=True,locked=False'
    Expect 'a door authored as locked stayed shut' $state 'Door 2:open=False,locked=True.*The door is locked'
    Expect 'without a key, nobody locks a door' $state 'You have no key that fits'

    Write-Host '==> A key is an item, cut to its lock' -ForegroundColor Cyan
    [void](Send 'console|hexagon_give "John Doe" keycard 1 city')
    [void](Send 'door|Door 1|lock')
    $state = Poll { Send 'state' } 'Door 1:open=False,locked=True'
    Expect 'an operator issued a keycard cut to the city''s locks, and its holder sees its cut' $state "items=\[Keycard#$cityCut@0,0\]"
    Expect 'never the pattern, which would say what it opens' "$($state -notmatch '#city') $state" '^True '
    Expect 'holding the keycard let a citizen lock the door' $state 'Door 1:open=False,locked=True'
    # A closed door is a wall. One step through it is no faster than walking, and is still refused. Locking
    # also shut it, and a door still swinging is no wall yet (Player.HostBlocked), so the step waits.
    [void](Poll { Send 'state' } 'Door 1:open=False,locked=True,swinging=False')
    [void](Send 'goto|240|-205|8')
    Expect 'stepping through the locked door did not get past it' { Send 'state' } 'pos=2[2-5]\d\.?\d*,-1[2-6]\d'
    Expect 'and the host recorded it as a wall' { Send 'journal|movement.implausible' } 'ok=False .*claimed=240,-205,8 reason=wall'
    [void](Send 'door|Door 1|lock')
    [void](Send 'drop|0')
    [void](Send 'door|Door 1|lock')
    [void](Poll { Send 'journal' } '(?<!\S)verb\.door\.lock!=2(?!\S)')
    $state = Send 'state'
    Expect 'the dropped keycard left the inventory' $state 'items=\[\] '
    Expect 'and lies in the world' $state 'ground=\[Keycard\]'
    Expect 'without the keycard the door stayed unlocked' $state 'Door 1:open=False,locked=False.*no key that fits'

    Write-Host '==> Holders: the ground, a stocked crate' -ForegroundColor Cyan
    [void](Send 'pickup|Keycard')
    Expect 'picking it up is the same transfer in reverse' { Send 'state' } "Keycard#$cityCut@0,0.*ground=\[\]"
    [void](Send 'open|Crate')
    Expect 'the crate holds what its author stocked it with, placed the first time it is opened' { Send 'state' } 'open=\[Crate: Rationx4, Waterx2, Bandagex2, Tokenx40\]'
    foreach ($pile in 1..4) { [void](Send 'take|0') }
    Expect 'whole piles are taken whole, money with the rest' { Send 'state' } "items=\[Keycard#$cityCut@0,0, Rationx4@1,0, Waterx2@2,0, Bandagex2@3,0, Tokenx40@4,0\] .*open=\[Crate: \]"
    [void](Send 'put|0')
    [void](Send 'door|Door 1|lock')
    [void](Poll { Send 'journal' } '(?<!\S)verb\.door\.lock!=3(?!\S)')
    $state = Send 'state'
    Expect 'the crate shows its contents to whoever opened it' $state "open=\[Crate: Keycard#$cityCut\]"
    Expect 'a keycard in a crate opens nothing' "$($state -notmatch 'items=\[[^\]]*Keycard') $state" '^True .*Door 1:open=False,locked=False'
    [void](Send 'use|1')
    Expect 'drinking used one water up, in view of anyone nearby' { Send 'state' } 'Water@2,0.*John Doe drinks from a can of water'
    [void](Send 'move|0|4|3')
    Expect 'an item moved to the requested cell' { Send 'state' } 'Rationx4@4,3'
    [void](Send 'close')
    Expect 'the crate closed' { Send 'state' } 'open=\[: \]'

    Write-Host '==> Harm, and being down' -ForegroundColor Cyan
    [void](Send 'console|hexagon_hurt "John Doe" 40')
    Expect 'harm lowered health' { Send 'state' } 'health=60 down=False'
    Snapshot 'hurt' 0.6
    [void](Send 'use|2')
    Expect 'a bandage treated it and was used up' { Send 'state' } 'Bandage@3,0.*health=90 .*binds a wound'
    [void](Send 'console|hexagon_hurt "John Doe" 500')
    [void](Send 'door|Door 1|use')
    [void](Send 'leave')
    [void](Poll { Send 'journal' } '(?<!\S)city\.leave!=1(?!\S)')
    $state = Send 'state'
    Expect 'at no health the character is down, not dead' $state 'has=True .*health=0 down=True'
    Expect 'someone who is down cannot leave the city' $state 'cannot leave the city like this'
    Expect 'or act on the world' (Send 'journal|verb.door.use') 'ok=False'
    Snapshot 'down' 0.6
    [void](Send 'console|hexagon_revive "John Doe"')
    Expect 'helped up, barely' { Send 'state' } 'health=25 down=False'

    Write-Host '==> Owning a door is title; its lock takes its key' -ForegroundColor Cyan
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
    Expect 'the city keycard opens the locker, stocked as its author placed it' { Send 'state' } "open=\[Locker: Pistol, Pistol roundx30, Zip tiex2, Radio, Keycard#$cityCut, Key#$flatCut\]"
    [void](Send 'take|5')
    [void](Send 'door|Door 2|lock')
    [void](Send 'door|Door 2|use')
    Expect 'the spare key from the locker turned the flat''s lock' { Send 'state' } "Key#$flatCut@1,0.*Door 2:open=True,locked=False.*owned=\[Door 2\]"
    Snapshot 'locker'
    [void](Send 'close')
    [void](Send 'console|hexagon_door "Door 2" release')
    Expect 'staff can take a title back' { Send 'state' } 'owned=\[\]'

    Write-Host '==> Staff work in game, on the record' -ForegroundColor Cyan
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

    Write-Host '==> Radio' -ForegroundColor Cyan
    [void](Send 'say|/r hello')
    Expect 'an untuned radio carries nothing' { Send 'state' } 'no radio tuned to anything'
    [void](Send 'tune|6|55')
    Expect 'a frequency outside the band is refused' { Send 'state' } 'A frequency is between'
    [void](Send 'tune|6|101.50')
    [void](Send 'say|/r testing')
    Expect 'the dial belongs to the radio, and the speaker hears themselves on it' { Send 'state' } 'Radio~101\.5@0,1.*\[101\.5\] John Doe: "testing"'

    Write-Host '==> What is left in a crate is anyone''s' -ForegroundColor Cyan
    [void](Send 'open|Crate')
    [void](Send 'put|4')
    [void](Send 'put|2')
    Expect 'he left the keycard and a bandage in the crate' { Send 'state' } "open=\[Crate: Keycard#$cityCut, Bandage\]"
    [void](Send 'close')

    Write-Host '==> A faction is people and a look' -ForegroundColor Cyan
    [void](Send 'leave')
    [void](Send "console|hexagon_whitelist $steamId civil_protection")
    [void](Send "create|Officer Kane|$description|civil_protection")
    [void](Send 'enter|Officer Kane')
    [void](Poll { Send 'state' } "has=True name='Officer Kane'")
    Teleport 'Officer Kane' 240 -150 60
    [void](Send 'door|Door 1|lock')
    [void](Poll { Send 'journal' } '(?<!\S)verb\.door\.lock!=5(?!\S)')
    $state = Send 'state'
    Expect 'the console whitelist unlocked the faction' $state "name='Officer Kane' faction='Civil Protection'"
    Expect 'he arrives with nothing, and his faction lets him lock nothing' $state 'items=\[\] .*Door 1:open=False,locked=False.*no key that fits'
    [void](Send 'open|Crate')
    [void](Send 'take|0')
    [void](Send 'open|Locker')
    foreach ($pile in 1..3) { [void](Send 'take|0') }
    [void](Send 'close')
    [void](Send 'door|Door 1|lock')
    $state = Poll { Send 'state' } 'Door 1:open=False,locked=True'
    Expect 'with the keycard from the crate he locked the door, which also shut it' $state 'Door 1:open=False,locked=True'
    Expect 'a character put in the air comes down: a driven client still has a body' { Send 'state' } 'pos=[-\d.]+,[-\d.]+,[0-4]\.'
    Expect 'a pistol and thirty rounds from the locker' $state "items=\[Keycard#$cityCut@0,0, Pistol@1,0, Pistol roundx30@3,0, Zip tiex2@4,0\]"
    [void](Send 'split|2|10')
    Expect 'part of a pile can be set aside' { Send 'state' } 'Pistol roundx20@3,0.*Pistol roundx10@5,0'
    [void](Send 'merge|4|2')
    [void](Send 'equip|1')
    Expect 'what is in hand is appearance: everyone is sent it' { Send 'state' } "held='Pistol'"
    [void](Send 'thirdperson|1')
    [void](Send 'face|330|-100|40')
    # The camera eases out behind him, then is placed where each picture wants it.
    Snapshot 'officer' 2
    [void](Send 'camera|330|-110|70|240|-150|40')
    Snapshot 'officer-front' 1
    [void](Send 'camera|310|-120|130|240|-150|120')
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

    Write-Host '==> Leaving does not end a scene' -ForegroundColor Cyan
    [void](Send 'console|hexagon_hurt "Officer Kane" 500')
    # Moved after he fell, so only the save as the city closes knows where he is now.
    Teleport 'Officer Kane' 330 -100 8
    [void](Send 'leave')
    [void](Poll { Send 'journal' } '(?<!\S)city\.leave!=2(?!\S)')
    Expect 'down, he cannot leave, so the city closes with him lying there' (Send 'state') 'has=True .*down=True .*cannot leave the city like this'

    Write-Host '==> Restart' -ForegroundColor Cyan
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
    Expect 'what he carried survived the restart, keys cut as they were' $state "items=\[Rationx4@4,3, Water@2,0, Tokenx40@4,0, Key#$flatCut@1,0, Radio~101\.5@0,1\]"
    Expect 'the title staff took back is still nobody''s' $state 'owned=\[\]'
    Expect 'whoever was down when the city closed is still lying there' $state 'sleepers=1 '
    Expect 'where he was when it closed, which only the closing save knew' (Send 'sleepers') '^3[23]\d,-(9\d|1[01]\d),down$'
    # The swinging door nudges the pawn, so this is a neighbourhood, not a point.
    Expect 'the character returned to where it stood' $state 'pos=2[2-5]\d\.?\d*,-1[2-6]\d'
    [void](Send 'open|Crate')
    Expect 'what was left in the crate survived the restart' { Send 'state' } 'open=\[Crate: Bandage\]'
    [void](Send 'close')

    Write-Host '==> Death is final' -ForegroundColor Cyan
    [void](Send 'act|person.finish')
    Expect 'someone left lying can be finished, and leaves a body' { Send 'state' } 'bodies=1 .*sleepers=0 '
    [void](Send 'act|corpse.search')
    Expect 'what he carried is on the body' { Send 'state' } "open=\[Body: Keycard#$cityCut, Pistol, Pistol roundx30, Zip tiex2\]"
    [void](Send 'take|1')
    [void](Send 'leave')
    Expect 'the dead stay on the list, as dead' { Send 'state' } 'characters=\[John Doe, Officer Kane\(dead\)\]'
    [void](Send 'enter|Officer Kane')
    Expect 'and nobody plays them again' { Send 'state' } 'Officer Kane is dead\.'

    Write-Host '==> A life is ended by retiring; the next one waits' -ForegroundColor Cyan
    [void](Send "console|hexagon_staff $steamId 1")
    [void](Send 'retire|John Doe')
    Expect 'retiring is a death' { Send 'state' } 'characters=\[John Doe\(dead\), Officer Kane\(dead\)\]'
    [void](Send "create|Max Muster|$description|citizen")
    [void](Send 'enter|Max Muster')
    $state = Poll { Send 'state' } 'Max Muster arrives in the city'
    Expect 'the next character is made at once' $state 'characters=\[John Doe\(dead\), Officer Kane\(dead\), Max Muster\(waits\)\]'
    Expect 'but enters the city a day after the death' $state 'Max Muster arrives in the city in 2[34] hours'
    [void](Send 'staff|restore "Officer Kane"')
    Expect 'staff restore nobody while the account has its living character' { Send 'state' } 'said=\[Their account already has Max Muster alive\. That character is deleted or retired first\.\]'
    [void](Send 'delete|Max Muster')
    Expect 'one that never lived is deleted, freeing its name' { Send 'state' } 'characters=\[John Doe\(dead\), Officer Kane\(dead\)\]'

    Write-Host '==> Staff undo a death that broke the rules' -ForegroundColor Cyan
    [void](Send 'staff|restore "Officer Kane"')
    Expect 'he is alive again' { Send 'state' } 'characters=\[John Doe\(dead\), Officer Kane\] .*said=\[Officer Kane is alive again'
    [void](Send "console|hexagon_staff $steamId 0")
    [void](Send 'enter|Officer Kane')
    $state = Poll { Send 'state' } 'health=25 down=False .*bodies=0 '
    Expect 'he gets up barely, with what was still on the body: what was taken stays taken' $state "items=\[Keycard#$cityCut@0,0, Pistol roundx30@3,0, Zip tiex2@4,0\] .*health=25 down=False .*bodies=0 "
    Expect 'where the body lay' $state 'pos=3[23]\d\.?\d*,-(9\d|1[01]\d)'

    Write-Host '==> The journal' -ForegroundColor Cyan
    $journal = Send 'journal'
    Expect 'every item came from a named source: staff, or the scene as built' $journal '(?<!\S)item\.issue=12(?!\S)'
    Expect 'what was used up, or went with a retired character, went through a sink' $journal '(?<!\S)item\.destroy=8(?!\S)'
    Expect 'every hand-over is on record, bodies and restores included' $journal '(?<!\S)item\.move=23(?!\S)'
    Expect 'refused locks are on record' $journal '(?<!\S)verb\.door\.lock!=5(?!\S)'
    Expect 'allowed locks too' $journal '(?<!\S)verb\.door\.lock=4(?!\S)'
    Expect 'refused uses are on record' $journal '(?<!\S)verb\.door\.use!=5(?!\S)'
    Expect 'and the locker that would not open' $journal '(?<!\S)verb\.container\.open!=1(?!\S)'
    Expect 'the operator is on record too' $journal '(?<!\S)operator\.whitelist=1(?!\S)'
    Expect 'and so is what staff did to a door' $journal '(?<!\S)door\.assign=1(?!\S).*(?<!\S)door\.release=1(?!\S)'
    Expect 'every character made, and every one refused' $journal '(?<!\S)character\.create=3(?!\S).*(?<!\S)character\.create!=3(?!\S)|(?<!\S)character\.create!=3(?!\S).*(?<!\S)character\.create=3(?!\S)'
    Expect 'the refused deletion and the allowed one' $journal '(?<!\S)character\.delete!=1(?!\S).*(?<!\S)character\.delete=1(?!\S)'
    Expect 'the death, the retirement and the restore' $journal '(?<!\S)character\.death=1(?!\S).*(?<!\S)character\.retire=1(?!\S).*(?<!\S)operator\.restore=1(?!\S)'
    Expect 'someone left lying is acted on like anyone else, on the record' $journal '(?<!\S)verb\.person\.finish=1(?!\S)'
    Expect 'the dead and the waiting were turned away at the gate' $journal '(?<!\S)city\.enter!=2(?!\S)'
    Expect 'speech is on record' $journal '(?<!\S)chat\.say=1(?!\S).*(?<!\S)chat\.me=3(?!\S).*(?<!\S)chat\.ooc=1(?!\S)'
    Expect 'and the rearranging of piles' $journal '(?<!\S)item\.split=1(?!\S).*(?<!\S)item\.merge=1(?!\S)'
    Expect 'this client was told no other name' (Send 'proxies') '^[^A-Za-z]*$'

    # The editor sometimes looks the driver's type up while the assembly is still loading, and a scene
    # played again was first played while the code was going in. That is about this harness, not the
    # game, so it is not counted.
    $notCounted = if ($script:replayed) { 'could not find Hexagon\.' } else { 'could not find Hexagon\.Dev\.DevDriver' }
    $problems = @((Invoke-Tool 'read_console' @{ limit = 500; minimumLevel = 'Warn' }) -split "`n" |
        Where-Object { $_ -match 'Hexagon|Exception|\[store\]|\[scene\]|\[death\]|\[restore\]|Whitelist violation|hexagon\.' -and $_ -notmatch "Bad texture|Error loading resource|$notCounted" })
    Expect 'the session logged no game warnings or errors' "$($problems.Count) problem lines: $($problems -join ' | ')" '^0 problem'
    [void](Invoke-Tool 'play_stop')
}
finally {
    if (-not $editor.HasExited) { Stop-Process -Id $editor.Id -Force }
    $http.Dispose()
    # A failed run keeps its data, journal included: that is the evidence of what the host decided.
    $kept = Get-ChildItem -LiteralPath (Join-Path $SboxRoot 'data') -Recurse -Directory -Filter $dataRoot -ErrorAction SilentlyContinue
    if ($script:failures -gt 0) { $kept | ForEach-Object { Write-Host "Data kept at $($_.FullName)" -ForegroundColor DarkGray } }
    else { $kept | Remove-Item -Recurse -Force }
}

if ($script:failures -gt 0) { throw "$($script:failures) play-test expectation(s) failed." }
Write-Host 'Play test passed in the real engine.' -ForegroundColor Green
