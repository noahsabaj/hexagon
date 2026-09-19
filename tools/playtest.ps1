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

function Wait-Until([string] $What, [scriptblock] $Condition) {
    while ([DateTime]::UtcNow -lt $script:deadline) {
        if ($editor.HasExited) { throw "s&box exited with code $($editor.ExitCode) while waiting for $What." }
        try { if (& $Condition) { return } } catch { $script:lastProblem = $_.Exception.Message }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for $What. Last problem: $($script:lastProblem)"
}

# Sends one driver command and returns what the game reported back through the console.
function Send([string] $Command) {
    # The editor skips a property write that does not change the value, so a repeated command would
    # never run. A trailing nonce makes every write distinct; the driver ignores extra arguments.
    $script:nonce++
    [void](Invoke-Tool 'set_component' ([ordered]@{ id = $hud; type = 'DevDriver'; properties = @{ Command = "$Command|#$($script:nonce)" } }))
    Start-Sleep -Milliseconds 700
    $lines = (Invoke-Tool 'read_console' @{ limit = 40; filter = '[dev]' }) -split "`n" | Where-Object { $_ -match '\[dev\]' }
    return (($lines | Select-Object -Last 1) -replace '^.*?=> ', '')
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
    Wait-Until 'the local player' { $reply = Send 'state'; $script:lastProblem = "driver said: $reply"; $reply -match '^has=' }
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
    [void](Send "create|John Doe|$description|citizen")
    [void](Send "create|J0hn Doe|$description|citizen")
    [void](Send "create|Officer Kane|$description|civil_protection")
    $state = Send 'state'
    Expect 'the character was created, its look-alike was not' $state 'characters=\[John Doe\]'
    Expect 'the look-alike name was refused with a reason' $state 'reads like it'
    Expect 'a whitelisted faction refused an unlisted account' $state 'not whitelisted for Civil Protection'

    Write-Host '==> Entering the city, chat, inventory' -ForegroundColor Cyan
    [void](Send 'enter|John Doe')
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
    Expect 'the console whitelist unlocked the faction' $state "name='Officer Kane' faction='Civil Protection'"
    Expect 'Civil Protection locked the door, which also shut it' $state 'Door 1:open=False,locked=True'

    Write-Host '==> Restart' -ForegroundColor Cyan
    [void](Send 'leave')
    [void](Invoke-Tool 'play_stop')
    Start-Sleep -Seconds 3
    Start-Play
    $state = Send 'state'
    Expect 'both characters survived the restart' $state 'characters=\[John Doe, Officer Kane\]'
    Expect 'the locked door survived the restart' $state 'Door 1:open=False,locked=True'
    Expect 'a new session starts with an empty chat log' $state 'chat=\[\]'
    [void](Send 'enter|John Doe')
    $state = Send 'state'
    Expect 'the moved item survived the restart' $state 'Ration@4,3'
    [void](Send 'open|Crate')
    Expect 'what was put in the crate survived the restart' (Send 'state') 'open=\[Crate: Keycard\]'
    # The swinging door nudges the pawn, so this is a neighbourhood, not a point.
    Expect 'the character returned to where it stood' $state 'pos=2[2-5]\d\.?\d*,-1[2-6]\d'

    Write-Host '==> The journal' -ForegroundColor Cyan
    $journal = Send 'journal'
    Expect 'starting tokens were issued once per character' $journal 'tokens\.issue=2(\s|$)'
    Expect 'every item came from a named source' $journal 'item\.issue=6(\s|$)'
    Expect 'the drink went through a sink' $journal 'item\.destroy=2(\s|$)'
    Expect 'refused locks are on record' $journal 'verb\.door\.lock!=3(\s|$)'
    Expect 'refused uses are on record' $journal 'verb\.door\.use!=4(\s|$)'
    Expect 'allowed acts are on record' $journal 'verb\.door\.lock=3(\s|$)'
    Expect 'the operator is on record too' $journal 'operator\.whitelist=1(\s|$)'
    Expect 'speech is on record' $journal 'chat\.say=1 .*chat\.me=3 .*chat\.ooc=1.*item\.move=3'
    Expect 'this client was told no other name' (Send 'proxies') '^[^A-Za-z]*$'

    # The editor sometimes looks the driver's type up while the assembly is still loading. That is
    # about this harness, not the game, so it is not counted.
    $problems = @((Invoke-Tool 'read_console' @{ limit = 500; minimumLevel = 'Warn' }) -split "`n" |
        Where-Object { $_ -match 'Hexagon|Exception|\[store\]|Whitelist violation|hexagon\.' -and $_ -notmatch 'Bad texture|Error loading resource|could not find Hexagon\.Dev\.DevDriver' })
    Expect 'the session logged no game warnings or errors' "$($problems.Count) problem lines: $($problems -join ' | ')" '^0 problem'
    [void](Invoke-Tool 'play_stop')
}
finally {
    if (-not $editor.HasExited) { Stop-Process -Id $editor.Id -Force }
    $http.Dispose()
    Get-ChildItem -LiteralPath (Join-Path $SboxRoot 'data') -Recurse -Directory -Filter $dataRoot -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force
}

if ($script:failures -gt 0) { throw "$($script:failures) play-test expectation(s) failed." }
Write-Host 'Play test passed in the real engine.' -ForegroundColor Green
