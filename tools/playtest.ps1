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

if (Get-Process -Name 'sbox-dev' -ErrorAction SilentlyContinue) { throw 'Close the s&box editor first.' }

$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(60)
$http.DefaultRequestHeaders.Accept.ParseAdd('application/json, text/event-stream')
$script:requestId = 0
$script:failures = 0
$script:nonce = 0
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
function Snapshot([string] $Name) {
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
    # The editor skips a property write that does not change the value, so a repeated command would
    # never run. A trailing nonce makes every write distinct; the driver ignores extra arguments.
    $script:nonce++
    $write = [ordered]@{ id = $hud; type = 'DevDriver'; properties = @{ Command = "$Command|#$($script:nonce)" } }
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
    Start-Sleep -Milliseconds 700
    $lines = (Invoke-Tool 'read_console' @{ limit = 40; filter = '[dev]' }) -split "`n" | Where-Object { $_ -match '\[dev\]' }
    # The editor ends each console line with CRLF, and only the LF is split on.
    return "$(($lines | Select-Object -Last 1) -replace '^.*?=> ', '')".TrimEnd()
}

function Expect([string] $What, [string] $State, [string] $Pattern) {
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
$editor = Start-Process -FilePath $sboxDev -WorkingDirectory $SboxRoot -PassThru -WindowStyle Hidden `
    -ArgumentList "-project `"$manifest`" +hexagon_data_root $dataRoot"
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
    [void](Send "create|Officer Kane|$description|civil_protection")
    $state = Send 'state'
    Expect 'the character was created, its look-alike was not' $state 'characters=\[John Doe\]'
    Expect 'the look-alike name was refused with a reason' $state 'reads like it'
    Expect 'a whitelisted faction refused an unlisted account' $state 'not whitelisted for Civil Protection'

    Write-Host '==> Entering the city, chat, inventory' -ForegroundColor Cyan
    [void](Send 'enter|John Doe')
    [void](Send 'delete|John Doe')
    Expect 'a character in the city cannot be deleted' (Send 'state') 'characters=\[John Doe\].*Leave the city with that character before deleting it'
    [void](Send 'say|Hello there')
    [void](Send 'say|/me waves')
    [void](Send 'say|//brb')
    [void](Send 'move|0|4|3')
    $state = Send 'state'
    Expect 'the owner was told its own name and faction' $state "has=True name='John Doe' faction='Citizen' tokens=100"
    Expect 'the faction asset granted starting items' $state 'Water@'
    Expect 'an item moved to the requested cell' $state 'Ration@4,3'
    Expect 'speech, emote and out-of-character lines were delivered' $state 'John Doe says "Hello there" / \*\* John Doe waves / \[OOC\] [^:/]+: brb'

    Write-Host '==> Doors' -ForegroundColor Cyan
    [void](Send 'door|Door 1|use')
    Expect 'a door out of reach was refused' (Send 'state') 'Door 1:open=False.*too far away'
    # goto is what a cheat does: the client simply declares itself somewhere else.
    [void](Send 'goto|240|-150|60')
    [void](Send 'door|Door 1|use')
    Expect 'claiming to stand at the door did not open it' (Send 'state') 'Door 1:open=False.*too far away'
    Start-Sleep -Seconds 2
    Expect 'the host sent the claimant back' (Send 'state') 'pos=(?!2[2-5]\d\.?\d*,-1[2-6]\d)'
    Expect 'and put the claim on record' (Send 'journal') 'movement\.implausible!=[1-9]'
    # The map has a roof right above the doors. Falling is believed; passing through a floor is not.
    [void](Send 'console|hexagon_teleport "John Doe" 272 -240 400')
    Start-Sleep -Seconds 3
    [void](Send 'goto|240|-150|60')
    [void](Send 'door|Door 1|use')
    Expect 'dropping through the roof to the door below did not open it' (Send 'state') 'Door 1:open=False.*too far away'
    Start-Sleep -Seconds 2
    [void](Send 'console|hexagon_teleport "John Doe" 240 -150 60')
    Start-Sleep -Seconds 1
    [void](Send 'door|Door 1|use')
    [void](Send 'door|Door 2|use')
    [void](Send 'door|Door 1|lock')
    $state = Send 'state'
    Expect 'a door in reach opened' $state 'Door 1:open=True,locked=False'
    Expect 'a door authored as locked stayed shut' $state 'Door 2:open=False,locked=True.*The door is locked'
    Expect 'a citizen could not lock a door' $state 'nothing that lets you do that'

    Write-Host '==> A key is an item' -ForegroundColor Cyan
    [void](Send 'console|hexagon_give "John Doe" keycard')
    [void](Send 'door|Door 1|lock')
    $state = Send 'state'
    Expect 'an operator issued a keycard' $state 'Keycard@'
    Expect 'holding the keycard let a citizen lock the door' $state 'Door 1:open=False,locked=True'
    # A closed door is a wall. One step through it is no faster than walking, and is still refused.
    Start-Sleep -Seconds 2
    [void](Send 'goto|240|-205|8')
    Start-Sleep -Seconds 2
    Expect 'stepping through the locked door did not get past it' (Send 'state') 'pos=2[2-5]\d\.?\d*,-1[2-6]\d'
    Expect 'and the host recorded it as a wall' (Send 'journal|movement.implausible') 'ok=False .*reason=wall'
    [void](Send 'door|Door 1|lock')
    [void](Send 'drop|2')
    [void](Send 'door|Door 1|lock')
    $state = Send 'state'
    Expect 'the dropped keycard left the inventory' "$($state -notmatch 'Keycard@')" 'True'
    Expect 'and lies in the world' $state 'ground=\[Keycard\]'
    Expect 'without the keycard the door stayed unlocked' $state 'Door 1:open=False,locked=False.*nothing that lets you do that'

    Write-Host '==> Holders: the ground, a crate' -ForegroundColor Cyan
    [void](Send 'pickup|Keycard')
    $state = Send 'state'
    Expect 'picking it up is the same transfer in reverse' $state 'Keycard@.*ground=\[\]'
    [void](Send 'open|Crate')
    [void](Send 'put|2')
    [void](Send 'door|Door 1|lock')
    $state = Send 'state'
    Expect 'the crate shows its contents to whoever opened it' $state 'open=\[Crate: Keycard\]'
    Expect 'a keycard in a crate grants nothing' "$($state -notmatch 'Keycard@') $state" '^True .*Door 1:open=False,locked=False'
    [void](Send 'use|1')
    $state = Send 'state'
    Expect 'drinking used the water up, in view of anyone nearby' "$($state -notmatch 'Water@') $state" '^True .*John Doe drinks from a can of water'
    [void](Send 'close')
    Expect 'the crate closed' (Send 'state') 'open=\[: \]'

    Write-Host '==> Harm, and being down' -ForegroundColor Cyan
    [void](Send 'console|hexagon_hurt "John Doe" 40')
    [void](Send 'console|hexagon_give "John Doe" bandage')
    Expect 'harm lowered health' (Send 'state') 'health=60 down=False'
    [void](Send 'use|1')
    Expect 'a bandage treated it and was used up' (Send 'state') 'health=90 .*binds a wound'
    [void](Send 'console|hexagon_hurt "John Doe" 500')
    [void](Send 'door|Door 1|use')
    [void](Send 'leave')
    $state = Send 'state'
    Expect 'at no health the character is down, not dead' $state 'has=True .*health=0 down=True'
    Expect 'someone who is down cannot leave the city' $state 'cannot leave the city like this'
    Expect 'or act on the world' (Send 'journal|verb.door.use') 'ok=False'
    [void](Send 'console|hexagon_revive "John Doe"')
    Expect 'helped up, barely' (Send 'state') 'health=25 down=False'

    Write-Host '==> Commerce: tokens and goods only move' -ForegroundColor Cyan
    [void](Send 'door|Door 2|buy')
    [void](Send 'door|Door 2|lock')
    $state = Send 'state'
    Expect 'buying a door paid its price into a sink' $state 'tokens=50 .*Door 2:open=False,locked=False,owned'
    [void](Send 'console|hexagon_door "Door 2" release')
    Expect 'staff can take a door back from its owner' (Send 'state') 'Door 2:open=False,locked=False\]'
    [void](Send 'open|Ration Vendor')
    Expect 'the vendor stocked its shelves from its declared source, and has a float' (Send 'state') 'opentokens=200 .*open=\[Ration Vendor: Rationx3, Waterx3, Bandagex3\]'
    Snapshot 'vendor'
    [void](Send 'take|0')
    Expect 'taking from a vendor is buying one, which joins the pile' (Send 'state') 'tokens=40 .*Rationx2@4,3.*opentokens=210 .*open=\[Ration Vendor: Rationx2,'
    [void](Send 'put|0')
    Expect 'putting is selling one, at the vendor''s price' (Send 'state') 'tokens=45 .*Ration@4,3.*opentokens=205'
    [void](Send 'taketokens')
    Expect 'the till is not a body to be looted' (Send 'state') 'tokens=45 .*The till is not yours'
    [void](Send 'close')

    Write-Host '==> Staff work in game, on the record' -ForegroundColor Cyan
    $steamId = Send 'steamid'
    [void](Send 'staff|who')
    Expect 'someone who is not staff is refused' (Send 'state') 'staff=False said=\[You are not staff\.\]'
    [void](Send "console|hexagon_staff $steamId 1")
    [void](Send 'staff|who')
    Expect 'staff status comes only from the console, and then who is who is visible' (Send 'state') 'staff=True said=\[[^/]* is John Doe at '
    [void](Send 'panel|staff')
    Snapshot 'staff'
    [void](Send 'panel|none')
    [void](Send 'staff|give "John Doe" radio')
    Expect 'a staff command does what the console command does' (Send 'state') 'Radio@.*said=\[Gave Radio to John Doe\.\]'
    [void](Send 'staff|journal door.buy')
    Expect 'staff can read the journal' (Send 'state') 'said=\[\d\d:\d\d:\d\d verb\.door\.buy by John Doe on Door:'
    [void](Send 'staff|journal "REFUSED by John Doe (line=who)"')
    Expect 'including what staff themselves did, refusals too' (Send 'state') 'staff\.command REFUSED by John Doe \(line=who\)'
    [void](Send "console|hexagon_staff $steamId 0")
    [void](Send 'staff|who')
    Expect 'and it can be taken away' (Send 'state') 'staff=False said=\[You are not staff\.\]'

    Write-Host '==> Radio' -ForegroundColor Cyan
    [void](Send 'say|/r hello')
    Expect 'an untuned radio carries nothing' (Send 'state') 'no radio tuned to anything'
    [void](Send 'tune|1|55')
    Expect 'a frequency outside the band is refused' (Send 'state') 'A frequency is between'
    [void](Send 'tune|1|101.50')
    [void](Send 'say|/r testing')
    Expect 'the dial belongs to the radio, and the speaker hears themselves on it' (Send 'state') 'Radio~101\.5@.*\[101\.5\] John Doe: "testing"'

    Write-Host '==> Faction gate' -ForegroundColor Cyan
    $steamId = Send 'steamid'
    [void](Send 'leave')
    [void](Send "console|hexagon_whitelist $steamId civil_protection")
    [void](Send "create|Officer Kane|$description|civil_protection")
    [void](Send 'enter|Officer Kane')
    [void](Send 'console|hexagon_teleport "Officer Kane" 240 -150 60')
    Start-Sleep -Seconds 1
    [void](Send 'door|Door 1|lock')
    $state = Send 'state'
    [void](Send 'console|hexagon_payday')
    Expect 'a wage is a declared faucet, paid to those at work' (Send 'state') 'tokens=120 .*You are paid 20 tokens'
    Expect 'the console whitelist unlocked the faction' $state "name='Officer Kane' faction='Civil Protection'"
    Expect 'Civil Protection locked the door, which also shut it' $state 'Door 1:open=False,locked=True'
    Expect 'a character put in the air comes down: a driven client still has a body' (Send 'state') 'pos=[-\d.]+,[-\d.]+,[0-4]\.'
    Expect 'a pistol and thirty rounds fit in a starting inventory' $state 'Pistol@.*Pistol roundx30@.*Zip tiex2@'
    [void](Send 'split|3|10')
    Expect 'part of a pile can be set aside' (Send 'state') 'Pistol roundx20@.*Pistol roundx10@'
    [void](Send 'merge|5|3')
    [void](Send 'equip|2')
    Expect 'what is in hand is appearance: everyone is sent it' (Send 'state') "held='Pistol'"
    [void](Send 'thirdperson|1')
    [void](Send 'face|330|-100|40')
    Start-Sleep -Seconds 2
    Snapshot 'officer'
    [void](Send 'camera|330|-110|70|240|-150|40')
    Start-Sleep -Seconds 1
    Snapshot 'officer-front'
    [void](Send 'camera|310|-120|130|240|-150|120')
    Start-Sleep -Seconds 1
    Snapshot 'officer-front-high'
    [void](Send 'camera')
    [void](Send 'thirdperson|0')
    [void](Send 'menu|Door 2')
    Snapshot 'verb-menu'
    [void](Send 'menu')
    [void](Send 'panel|inventory')
    Snapshot 'inventory'
    [void](Send 'panel|none')
    Snapshot 'crate-and-vendor'
    Expect 'and put back' "$((Send 'state') -notmatch 'roundx10') $(Send 'state')" '^True .*Pistol roundx30@'

    Write-Host '==> Leaving does not end a scene' -ForegroundColor Cyan
    [void](Send 'console|hexagon_hurt "Officer Kane" 500')
    # Moved after he fell, so only the save as the city closes knows where he is now.
    [void](Send 'console|hexagon_teleport "Officer Kane" 330 -100 8')
    Start-Sleep -Seconds 2
    [void](Send 'leave')
    Expect 'down, he cannot leave, so the city closes with him lying there' (Send 'state') 'has=True .*down=True .*cannot leave the city like this'

    Write-Host '==> Restart' -ForegroundColor Cyan
    [void](Invoke-Tool 'play_stop')
    Start-Sleep -Seconds 3
    Start-Play
    $state = Send 'state'
    Expect 'both characters survived the restart' $state 'characters=\[John Doe, Officer Kane\]'
    Expect 'the locked door survived the restart' $state 'Door 1:open=False,locked=True'
    Expect 'the released door is still nobody''s' $state 'Door 2:open=False,locked=False\]'
    Expect 'a new session starts with an empty chat log' $state 'chat=\[\]'
    [void](Send 'enter|John Doe')
    # What someone left lying looks like is sent only once the network has stopped sending it to those too far away.
    Start-Sleep -Seconds 3
    $state = Send 'state'
    Expect 'the moved item survived the restart' $state 'Ration@4,3'
    Expect 'whoever was down when the city closed is still lying there' $state 'sleepers=1 '
    Expect 'where he was when it closed, which only the closing save knew' (Send 'sleepers') '^3[23]\d,-(9\d|1[01]\d),down$'
    [void](Send 'act|person.revive')
    Expect 'and someone else can help them up, after which nobody is lying there' (Send 'state') 'sleepers=0 '
    [void](Send 'open|Crate')
    Expect 'what was put in the crate survived the restart' (Send 'state') 'open=\[Crate: Keycard\]'
    # The swinging door nudges the pawn, so this is a neighbourhood, not a point.
    Expect 'the character returned to where it stood' $state 'pos=2[2-5]\d\.?\d*,-1[2-6]\d'

    Write-Host '==> The journal' -ForegroundColor Cyan
    $journal = Send 'journal'
    Expect 'tokens entered the world four times: two characters, a float, a wage' $journal 'tokens\.issue=4(\s|$)'
    Expect 'every item came from a named source' $journal 'item\.issue=13(\s|$)'
    Expect 'the drink went through a sink' $journal 'item\.destroy=2(\s|$)'
    Expect 'refused locks are on record' $journal 'verb\.door\.lock!=3(\s|$)'
    Expect 'refused uses are on record' $journal 'verb\.door\.use!=5(\s|$)'
    Expect 'allowed acts are on record' $journal 'verb\.door\.lock=4(\s|$)'
    Expect 'the money supply is what the journal says it is' $journal 'tokens\.destroy=1(\s|$)'
    Expect 'the operator is on record too' $journal 'operator\.whitelist=1(\s|$)'
    Expect 'and so is what staff did to a door' $journal 'door\.release=1(\s|$)'
    Expect 'a refused deletion is on record' $journal 'character\.delete!=1(\s|$)'
    Expect 'someone left lying is acted on like anyone else, on the record' $journal 'verb\.person\.revive=1(\s|$)'
    Expect 'speech is on record' $journal 'chat\.say=1 .*chat\.me=3 .*chat\.ooc=1.*item\.move=5 .*item\.split=1 .*item\.merge=1'
    Expect 'this client was told no other name' (Send 'proxies') '^[^A-Za-z]*$'

    # The editor sometimes looks the driver's type up while the assembly is still loading, and a scene
    # played again was first played while the code was going in. That is about this harness, not the
    # game, so it is not counted.
    $notCounted = if ($script:replayed) { 'could not find Hexagon\.' } else { 'could not find Hexagon\.Dev\.DevDriver' }
    $problems = @((Invoke-Tool 'read_console' @{ limit = 500; minimumLevel = 'Warn' }) -split "`n" |
        Where-Object { $_ -match 'Hexagon|Exception|\[store\]|Whitelist violation|hexagon\.' -and $_ -notmatch "Bad texture|Error loading resource|$notCounted" })
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
