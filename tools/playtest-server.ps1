# Plays a Hexagon game as two real clients on a real dedicated server, and checks what each was told.
#
# The editor play test has one client, which is also the host and so knows everything. What one
# player may learn about another can only be checked here: a dedicated server, two game clients
# joined over the loopback, and each client asked what it actually received.
#
# The server does not read a redirected console, so commands are left as files in its data folder
# (see DevCommands.PollInbox) and replies are read from its output. Both ends run with +hexagon_dev 1.
# Steam must be running. Both clients are the same Steam account, so they share one character list.
[CmdletBinding()]
param(
    [string] $GameRoot,
    [string] $SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox',
    [int] $TimeoutSeconds = 900
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $GameRoot) { $GameRoot = Join-Path (Split-Path -Parent $root) 'hl2rp-hexagon' }
$manifest = (Get-ChildItem -LiteralPath (Resolve-Path -LiteralPath $GameRoot).Path -Filter '*.sbproj' | Select-Object -First 1).FullName
$dataRoot = "playtest-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$log = Join-Path ([System.IO.Path]::GetTempPath()) "$dataRoot-server.log"
$description = 'A tired resident of the city.'
$script:failures = 0
$script:sent = 0

# An open editor is no obstacle, so the editor play test can run at the same time (tools/test.ps1).
if (Get-Process -Name 'sbox', 'sbox-server' -ErrorAction SilentlyContinue) { throw 'Close s&box and any dedicated server first.' }

# RenderDoc, when it is on the PATH, is loaded into each game client, and with it every native
# library the engine loads while starting is slow: a client took two to four minutes to reach its
# menu, and 13 seconds without it. The clients are started without it.
$pathWithoutRenderDoc = ($env:Path -split ';' | Where-Object { $_ -and -not (Test-Path -LiteralPath (Join-Path $_ 'renderdoc.dll')) }) -join ';'

# The editor, while it plays a scene, listens for local players on the engine's default port, so the
# server and its clients meet on another. A port the system hands out is free, and outside the ranges
# Windows keeps for itself.
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$localPort = $listener.LocalEndpoint.Port
$listener.Stop()

function Read-Log { if (Test-Path -LiteralPath $log) { @(Get-Content -LiteralPath $log) } else { @() } }

function Wait-Until([string] $What, [scriptblock] $Condition) {
    while ([DateTime]::UtcNow -lt $script:deadline) {
        if ($server.HasExited) { throw "The server exited with code $($server.ExitCode) while waiting for $What." }
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for $What."
}

# Sends one command to a client (0 is the server itself) and returns its reply.
function Send([int] $Client, [string] $Command) {
    $script:sent++
    # The tag makes each reply findable, and the driver ignores arguments it does not use.
    $tagged = "$Command|#$($script:sent)"
    Set-Content -LiteralPath (Join-Path $script:inbox ('{0:d5}.txt' -f $script:sent)) -Value "$Client|$tagged"
    $marker = "[dev] $Client $tagged => "
    $until = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $until) {
        $line = Read-Log | Where-Object { $_.Contains($marker) } | Select-Object -Last 1
        if ($line) { return $line.Substring($line.IndexOf($marker) + $marker.Length) }
        Start-Sleep -Milliseconds 100
    }
    return 'no reply'
}

# Asks until the reply matches, and returns the last reply. What one player does takes a moment to
# reach the host and the other player, and longer on a busy machine, so the test waits for what it
# expects rather than for a fixed time. A reply that never matches is returned for a check to fail on.
function Poll([scriptblock] $Ask, [string] $Pattern, [int] $Seconds = 10) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ($true) {
        $reply = & $Ask
        if ($reply -match $Pattern -or [DateTime]::UtcNow -ge $until) { return $reply }
        Start-Sleep -Milliseconds 250
    }
}

# Where a place beside something named in the scene is, as the server has it: x, y and z. The side is
# the object's own front, back, left or right; the distance is from its face on that side; the height
# is above the floor there. Places are found by what is there, so a test survives the map being moved.
function Spot([string] $Name, [string] $Side, [double] $Distance, [double] $Height = 60) {
    $reply = Send 0 "spot|$Name|$Side|$Distance|$Height"
    if ($reply -notmatch '^(-?\d+) (-?\d+) (-?\d+)') { throw "No place beside '$Name': $reply" }
    return [int[]] @($Matches[1], $Matches[2], $Matches[3])
}

# Whether a reply puts someone within reach of a place, on the ground: the first x,y in it, after pos=
# or at= where there is one, against the place's.
function Near([string] $Reply, [int[]] $At, [double] $Range = 40) {
    if ($Reply -notmatch '(?:pos=|at=|^)(-?[\d.]+),(-?[\d.]+)') { return $false }
    return [Math]::Abs([double] $Matches[1] - $At[0]) -lt $Range -and [Math]::Abs([double] $Matches[2] - $At[1]) -lt $Range
}

# Moves a character as staff would, and waits until the client playing it has it there. Until then,
# what that client claims about where it stands is judged against the old place.
function Teleport([int] $Client, [string] $Name, [int[]] $At) {
    [void](Send 0 "console|hexagon_teleport `"$Name`" $($At -join ' ')")
    $until = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if (Near (Send $Client 'state') $At 32) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $until)
}

# Checks a reply. Given the question instead, in braces, it asks until the answer is the one wanted.
# That something did not happen is checked once, after something sent later has arrived.
function Expect([string] $What, $State, [string] $Pattern, [switch] $Not) {
    if ($State -is [scriptblock]) { $State = if ($Not) { & $State } else { Poll $State $Pattern } }
    if (($State -match $Pattern) -ne [bool] $Not) { Write-Host "  ok   $What" -ForegroundColor Green }
    else {
        $script:failures++
        Write-Host "  FAIL $What" -ForegroundColor Red
        Write-Host "       $(if ($Not) { 'did not want' } else { 'wanted' }) /$Pattern/ in: $State" -ForegroundColor DarkGray
    }
}

# Starts a dedicated server on a data folder and waits until it is taking commands.
function Start-Server([string] $DataFolder) {
    $start = [System.Diagnostics.ProcessStartInfo]::new((Join-Path $SboxRoot 'sbox-server.exe'))
    $start.WorkingDirectory = $SboxRoot
    $start.Arguments = "+game `"$manifest`" +net_allow_local 1 +net_local_port $localPort +hexagon_data_root $DataFolder +hexagon_dev 1"
    $start.RedirectStandardOutput = $true
    # Errors are written to the other stream, and are what the last check most needs to see.
    $start.RedirectStandardError = $true
    $start.UseShellExecute = $false
    $script:server = [System.Diagnostics.Process]::Start($start)
    foreach ($stream in 'OutputDataReceived', 'ErrorDataReceived') {
        $null = Register-ObjectEvent -InputObject $script:server -EventName $stream -MessageData $log -Action {
            if ($null -ne $EventArgs.Data) { Add-Content -LiteralPath $Event.MessageData -Value $EventArgs.Data }
        }
    }
    $script:server.BeginOutputReadLine()
    $script:server.BeginErrorReadLine()
    $announced = "\[dev\] inbox at .*$([regex]::Escape($DataFolder))[\\/]"
    Wait-Until 'the server' { (Read-Log) -match $announced }
    $script:inbox = ((Read-Log) -match $announced | Select-Object -Last 1) -replace '^.*inbox at ', ''
}

Write-Host "==> Starting the dedicated server (data folder '$dataRoot')" -ForegroundColor Cyan
$script:deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$script:server = $null
$clients = @()
try {
    Start-Server $dataRoot
    Expect 'a dedicated server has no player of its own' (Send 0 'who') 'local=False .*host=True .*pawns=\[\]'
    # Both clients are one Steam account, so this city lets that account live two lives at once.
    Expect 'a game may allow an account more than one life' (Send 0 'policy|living|2') 'death=Permanent living=2 hours=24'

    Write-Host '==> Starting two clients' -ForegroundColor Cyan
    # Each client is told its number and tells the server once it has joined, so they start
    # together, either may join first, and the second process is client 2. A minimized window never
    # presents and every frame waited for it, so a client ran at a few frames a second; -nowindow
    # gives it a window that is never shown, and it runs at full speed.
    foreach ($number in 1, 2) {
        $clients += Start-Process -FilePath (Join-Path $SboxRoot 'sbox.exe') -WorkingDirectory $SboxRoot -PassThru -WindowStyle Hidden `
            -Environment @{ Path = $pathWithoutRenderDoc } -ArgumentList "+net_local_port $localPort +connect local +hexagon_dev 1 +hexagon_dev_client $number -nowindow"
    }
    foreach ($number in 1, 2) { Wait-Until "client $number to have a pawn" { (Send $number 'who') -match 'local=True' } }

    # tools/test.ps1 starts the editor play test when this line appears.
    Write-Host '==> Two characters, one city' -ForegroundColor Cyan
    [void](Send 1 "create|John Doe|$description|citizen")
    [void](Send 2 "create|Jane Roe|A woman in a grey coat, watching the street.|citizen")
    [void](Send 1 'enter|John Doe')
    [void](Poll { Send 1 'state' } "has=True name='John Doe'")
    [void](Send 2 'enter|John Doe')
    Expect 'a character already in the city cannot be entered twice' { Send 2 'state' } 'has=False .*already in the city'
    [void](Send 2 'enter|Jane Roe')
    [void](Poll { Send 2 'state' } "has=True name='Jane Roe'")
    # Where the two stand: him beside the first door, and her beside him, behind the crate; a little
    # way off, in front of it; and well away, along the floor in front of it.
    $door = Spot 'Door 1' left 30
    $beside = Spot 'Crate' back 10
    $apart = Spot 'Crate' front 90
    $sightAway = Spot 'Crate' front 850
    $far = Spot 'Crate' front 1150
    Teleport 1 'John Doe' $door
    Teleport 2 'Jane Roe' $beside

    Write-Host '==> Perception' -ForegroundColor Cyan
    Expect 'the other player is visible, with the description an onlooker would see' { Send 1 'proxies' } "has=True .*seen='A woman in a grey coat"
    $seen = Send 1 'proxies'
    Expect 'her name and faction were never sent' $seen "name='' faction=''"
    Expect 'nor is the name anywhere in what the client holds' $seen 'Jane' -Not
    Expect 'each client knows its own name' (Send 2 'state') "name='Jane Roe' faction='Citizen'"

    Write-Host '==> Speech reaches who is in range, and nobody else' -ForegroundColor Cyan
    [void](Send 1 'say|Morning')
    $heard = Poll { Send 2 'state' } 'says "Morning"'
    Expect 'speech reached a listener 60 units away, from a stranger' $heard '\[A tired resident of the city\.\] says "Morning"'
    Expect 'and did not carry his name' $heard 'chat=\[.*John Doe' -Not
    [void](Send 1 'introduce')
    [void](Send 1 'say|I am John')
    Expect 'once he introduced himself, she knows him' { Send 2 'state' } 'known=\[John Doe\].*John Doe says "I am John"'
    Expect 'telling is one way: he still sees a stranger' (Send 1 'proxies') "label='\[A woman in a grey coat"
    Expect 'the journal names who heard it' { Send 0 'journal|chat.say' } "actor='John Doe' witnesses=1"
    Teleport 2 'Jane Roe' $apart
    Write-Host "       $(Send 0 'who')" -ForegroundColor DarkGray
    [void](Send 1 'say|/w keep this quiet')
    [void](Send 1 'say|Still here')
    # The host sends what is said in the order it was said, so once the second line has arrived the
    # first would have.
    $heard = Poll { Send 2 'state' } 'Still here'
    Expect 'a whisper did not reach a listener 200 units away' $heard 'keep this quiet' -Not
    Expect 'ordinary speech still did' $heard 'Still here'
    Expect 'the journal shows the whisper had no witnesses' { Send 0 'journal|chat.whisper' } 'witnesses=0'
    Teleport 2 'Jane Roe' $far
    [void](Send 1 'say|/y can anyone hear me')
    [void](Send 1 'say|//back in five')
    $heard = Poll { Send 2 'state' } 'back in five'
    Expect 'a yell did not carry 1260 units' $heard 'can anyone hear me' -Not
    Expect 'out-of-character chat reached everyone' $heard '\[OOC\] [^:/]+: back in five'

    Write-Host '==> A claimed position buys nothing' -ForegroundColor Cyan
    # goto is what a cheat does: the client simply declares itself somewhere else.
    [void](Send 2 'goto|250|-150|60')
    [void](Send 1 'say|Nobody is near me')
    # He hears himself once the host has handled it, and had she counted as near, she would have been sent it then.
    [void](Poll { Send 1 'state' } 'John Doe says "Nobody is near me"')
    Expect 'claiming to stand beside a speaker did not let her listen in' { Send 2 'state' } 'Nobody is near me' -Not
    Expect 'the journal agrees nobody heard' { Send 0 'journal|chat.say' } 'witnesses=0'
    Expect 'the host sent her back' { $state = Send 2 'state'; "$(Near $state $far) $state" } '^True '
    Expect 'and put the claim on record' { Send 0 'journal' } 'movement\.implausible!=[1-9]'

    Write-Host '==> Out of sight is out of the network' -ForegroundColor Cyan
    # Her client sends her movement itself, and the host passes on what one client sends another
    # unread, so only her client can keep it from those who cannot see her. Sight is narrowed so she
    # can be out of it without leaving the scene's floor; she is 1260 units from him.
    $sight = [regex]::Match((Send 0 'sight'), 'sight=([\d.]+)').Groups[1].Value
    Expect 'the host can narrow how far anyone sees' (Send 0 'sight|600') 'sight=600'
    Expect 'out of his sight, he no longer shows her' { $pawns = Send 1 'pawns'; "$(Near $pawns $far) $pawns" } '^True .*present=False'
    Teleport 2 'Jane Roe' $sightAway
    # Her own client has her there now. Were it still sending her to him, he would have her there
    # within a few of its updates, thirty a second; he is watched for a second.
    $pawns = Poll { $pawns = Send 1 'pawns'; "$(Near $pawns $sightAway) $pawns" } '^True ' 1
    Expect 'and her client no longer sends him where she goes' "$(Near $pawns $far) $pawns" '^True .*present=False'
    Expect 'restoring how far anyone sees' (Send 0 "sight|$sight") "sight=$sight"
    Expect 'back in his sight, she is sent to him again, where she now is' { $pawns = Send 1 'pawns'; "$(Near $pawns $sightAway) $pawns" } '^True .*present=True'
    Teleport 2 'Jane Roe' $far

    Write-Host '==> The world is shared' -ForegroundColor Cyan
    [void](Send 1 'door|Door 1|use')
    Expect 'a door one player opened is open for the other' { Send 2 'state' } 'Door 1:open=True'
    [void](Send 2 'door|Door 1|use')
    Expect 'and out of reach for a player far away' { Send 2 'state' } 'Door 1:open=True.*too far away'

    Write-Host '==> Radio reaches those tuned in, and nobody else' -ForegroundColor Cyan
    foreach ($name in 'John Doe', 'Jane Roe') { [void](Send 0 "console|hexagon_give `"$name`" radio") }
    foreach ($client in 1, 2) { [void](Poll { Send $client 'state' } 'items=\[Radio') }
    [void](Send 1 'tune|0|101.5')
    [void](Send 2 'tune|0|101.5')
    foreach ($client in 1, 2) { [void](Poll { Send $client 'state' } 'Radio~101\.5@') }
    [void](Send 1 'say|/r unit one to control')
    Expect 'a radio carries 1260 units to one tuned the same, by a name she was given' { Send 2 'state' } '\[101\.5\] John Doe: "unit one to control"'
    [void](Send 2 'say|/r control here')
    $heard = Poll { Send 1 'state' } 'control here'
    Expect 'he was never given her name, and a radio shows no face' $heard '\[101\.5\] A voice: "control here"'
    Expect 'so nothing of what she looks like came with it' $heard 'grey coat' -Not
    Expect 'the journal names who was listening' { Send 0 'journal|chat.radio' } "actor='Jane Roe' witnesses=1"
    [void](Send 2 'tune|0|102.0')
    [void](Poll { Send 2 'state' } 'Radio~102\.0@')
    [void](Send 1 'say|/r is this thing on')
    [void](Poll { Send 1 'state' } 'John Doe: "is this thing on"')
    Expect 'retuned, she hears nothing' { Send 2 'state' } 'is this thing on' -Not
    Expect 'and the journal agrees' { Send 0 'journal|chat.radio' } "actor='John Doe' witnesses=0"
    Teleport 2 'Jane Roe' $beside
    [void](Send 1 'say|/r standing right here')
    Expect 'beside him she hears a man talking into a radio, whatever hers is tuned to' { Send 2 'state' } 'John Doe radios "standing right here"'
    # Something put down out of sight is not sent, and nor is what it is: every client is told a new
    # object exists, so what it is waits until the network sends it only to those who can see it.
    Expect 'with sight narrowed, he cannot see her' (Send 0 'sight|30') 'sight=30'
    [void](Send 2 'drop|0')
    [void](Poll { Send 2 'state' } 'items=\[\] ')
    Start-Sleep -Seconds 4
    Expect 'what she dropped out of his sight never reached him' (Send 1 'state') 'ground=\[\] '
    Expect 'once she is in sight' (Send 0 "sight|$sight") "sight=$sight"
    Expect 'he is sent what lies there' { Send 1 'state' } 'ground=\[Radio\] '

    Write-Host '==> Handing over takes two' -ForegroundColor Cyan
    Teleport 2 'Jane Roe' $beside
    foreach ($gift in 'pistol', 'pistol_round 4', 'zip_tie', 'token 50') { [void](Send 0 "console|hexagon_give `"John Doe`" $gift") }
    [void](Send 0 'console|hexagon_give "Jane Roe" water')
    Expect 'what staff gave him is in his hands, money with the rest' { Send 1 'state' } 'items=\[Radio~101\.5@0,0, Pistol@2,0, Pistol roundx4@4,0, Zip tie@5,0, Tokenx50@0,1\]'
    [void](Poll { Send 2 'state' } 'items=\[Water@0,0\]')
    [void](Send 1 'offer|4|30')
    Expect 'she is asked, by the name she was given' { Send 2 'state' } "offer='John Doe offers you 30 Token\.'"
    Expect 'and nothing has moved yet' (Send 1 'state') 'Tokenx50@0,1'
    [void](Send 2 'answer|1')
    Expect 'she took it' { Send 2 'state' } "items=\[Water@0,0, Tokenx30@1,0\] .*offer='' .*You take 30 Token\."
    Expect 'and it left his hands' { Send 1 'state' } 'Tokenx20@0,1.*Your offer is taken\.'
    [void](Send 1 'offer|3|1')
    [void](Poll { Send 2 'state' } "offer='John Doe offers you [^']*Zip tie")
    [void](Send 2 'answer|0')
    Expect 'an offer can be refused, and then nothing moves' { Send 1 'state' } 'Zip tie@5,0.*Your offer is refused\.'
    [void](Send 1 'offer|3|1')
    [void](Poll { Send 2 'state' } "offer='John Doe offers you [^']*Zip tie")
    Teleport 2 'Jane Roe' $apart
    Expect 'an offer lapses once the two have parted' { "$(Send 2 'state') $(Send 1 'state')" } "offer='' .*The offer lapses\..*Zip tie@5,0.*Your offer lapses\."
    Expect 'on the record, with why' { Send 0 'journal|offer.lapse' } 'ok=True .*data=\[reason=apart\]'
    Teleport 2 'Jane Roe' $beside
    [void](Send 0 'console|hexagon_door "Door 2" assign "John Doe"')
    Expect 'staff assigned him a flat' { Send 1 'state' } 'owned=\[Door 2\]'
    [void](Send 1 'offerdoor|Door 2')
    Expect 'its owner offers the title like anything else' { Send 2 'state' } "offer='John Doe offers you the title to Door 2\.'"
    [void](Send 2 'answer|1')
    Expect 'once she accepts, it is hers' { Send 2 'state' } 'owned=\[Door 2\]'
    Expect 'and no longer his' { Send 1 'state' } 'owned=\[\]'
    Expect 'the hand-over is on record, from him to her' { Send 0 'journal|door.assign' } "actor='John Doe' .*data=\[owner=Jane Roe from=John Doe\]"

    Write-Host '==> Violence is slow, visible and on the record' -ForegroundColor Cyan
    Expect 'four rounds are one pile' (Send 1 'state') 'Pistol roundx4@'
    [void](Send 1 'equip|1')
    Expect 'a drawn pistol is something anyone can see' { Send 2 'proxies' } "held='Pistol'"
    # A pistol fires at most every half second, and the host ignores a shot sooner than that. So each
    # shot waits for the last to have used its round, and then for the pistol to be ready again.
    foreach ($left in 'x3', 'x2', '') {
        [void](Send 1 'attack')
        [void](Poll { Send 1 'state' } "Pistol round$left@")
        if ($left) { Start-Sleep -Milliseconds 550 }
    }
    Expect 'three shots put her down, not out' { Send 2 'state' } 'has=True .*health=0 down=True'
    Expect 'he sees her fall but not her health' { Send 1 'proxies' } 'down=True .*health=0 has=True'
    Expect 'each shot, and the fall, are on record' { Send 0 'journal' } 'combat\.attack=3 .*character\.downed=1'
    Expect 'each shot used up a round' (Send 1 'state') 'Pistol round@'
    [void](Send 2 'leave')
    Expect 'she cannot leave the scene' { Send 2 'state' } 'has=True .*cannot leave the city like this'

    Write-Host '==> Searching is opening a holder' -ForegroundColor Cyan
    [void](Send 1 'act|person.search')
    Expect 'he sees what she carries' { Send 1 'state' } 'open=\[Belongings: Water, Tokenx30\]'
    Expect 'and she is told' { Send 2 'state' } 'going through your belongings'
    [void](Send 1 'take|1')
    Expect 'what he took left her inventory' { Send 2 'state' } 'items=\[Water@0,0\] '
    Expect 'and joined his own pile' { Send 1 'state' } 'Tokenx50@0,1'
    [void](Send 1 'act|person.revive')
    Expect 'helped up, she is no longer searchable' { "$(Send 2 'state') $(Send 1 'state')" } 'health=25 down=False .*open=\[: \]'

    Write-Host '==> Restraints' -ForegroundColor Cyan
    [void](Send 2 'act|person.restrain')
    Expect 'she has nothing to bind him with' { Send 2 'state' } 'nothing that lets you do that'
    [void](Send 1 'act|person.restrain')
    Expect 'his zip tie bound her, and was used up' { "$(Send 2 'state') $(Send 1 'state')" } 'restrained=True .*Your hands are bound.*items=\[(?![^\]]*Zip tie)'
    [void](Send 2 'drop|0')
    Expect 'bound hands do nothing' { Send 2 'state' } 'items=\[Water@.*cannot do that now'
    [void](Send 1 'act|person.release')
    Expect 'released' { Send 2 'state' } 'restrained=False'

    Write-Host '==> Death is a deliberate act, and it is final' -ForegroundColor Cyan
    [void](Send 1 'act|person.finish')
    Expect 'someone standing cannot be finished: the verb is not even offered' (Send 0 'journal') 'character\.death|verb\.person\.finish' -Not
    [void](Send 1 'attack')
    # He can finish her once his own client shows her down and offers it.
    [void](Poll { Send 1 'proxies' } 'down=True')
    [void](Send 1 'act|person.finish')
    Expect 'she is dead, and her player is back at the list, where the dead are kept' { Send 2 'state' } 'has=False .*characters=\[John Doe, Jane Roe\(dead\)\].*Jane Roe has died\.'
    $journal = Poll { Send 0 'journal' } '(?<!\S)character\.death=1(?!\S)'
    Expect 'the death is on record' $journal '(?<!\S)character\.death=1(?!\S)'
    Expect 'and the title she held went with her' $journal '(?<!\S)door\.release=1(?!\S)'
    # He is beside her, so he sees her body as she falls, not once everyone else may be sent it.
    Expect 'he sees her body at once' (Poll { Send 1 'state' } 'bodies=1 ' 2) 'bodies=1 '
    [void](Send 1 'act|corpse.search')
    Expect 'what she carried is on her body' { Send 1 'state' } 'bodies=1 .*open=\[Body: Water\]'
    [void](Send 1 'close')

    Write-Host '==> The next life waits; staff can undo a death' -ForegroundColor Cyan
    [void](Send 2 "create|Max Muster|$description|citizen")
    [void](Send 2 'enter|Max Muster')
    $state = Poll { Send 2 'state' } 'Max Muster arrives in the city'
    Expect 'the next character is made at once' $state 'characters=\[John Doe, Jane Roe\(dead\), Max Muster\(waits\)\]'
    Expect 'but enters the city a day after the death' $state 'Max Muster arrives in the city in 2[34] hours'
    [void](Send 0 'console|hexagon_restore "Jane Roe"')
    Expect 'staff restore nobody while the account has all the lives it may' { "$(Read-Log)" } 'Their account already has John Doe, Max Muster alive\.'
    [void](Send 2 'delete|Max Muster')
    [void](Poll { Send 2 'state' } 'characters=\[John Doe, Jane Roe\(dead\)\]')
    [void](Send 0 'console|hexagon_restore "Jane Roe"')
    Expect 'once there is room, she is alive again' { Send 2 'state' } 'characters=\[John Doe, Jane Roe\] '
    [void](Send 2 'enter|Jane Roe')
    Expect 'she gets up where she fell, barely, with what was still on her' { Send 2 'state' } 'items=\[Water@0,0\] .*owned=\[\] .*health=25 down=False .*bodies=0 '
    Expect 'and the body is gone for him too' { Send 1 'state' } 'bodies=0 '
    Expect 'the restore is on record' { Send 0 'journal' } '(?<!\S)operator\.restore=1(?!\S)'

    Write-Host '==> Leaving does not end a scene' -ForegroundColor Cyan
    Teleport 2 'Jane Roe' $beside
    [void](Send 0 'console|hexagon_give "John Doe" zip_tie')
    [void](Poll { Send 1 'state' } 'Zip tie@')
    [void](Poll { Send 1 'proxies' } 'down=False restrained=False .*has=True')
    [void](Send 1 'act|person.restrain')
    Expect 'he binds her again' { Send 2 'state' } 'restrained=True'
    Stop-Process -Id $clients[1].Id -Force
    Wait-Until 'the server to notice the disconnect' { (Send 0 'journal') -match 'connection\.leave=1' }
    Expect 'the player who left is gone from the other client' { Send 1 'proxies' } '^[^A-Za-z]*$'
    # What someone left behind looks like is sent once the network has stopped sending it to those too far away.
    Expect 'but the character she left bound is still standing there, and he is sent it' { Send 1 'sleepers' } '^\d+,-?\d+,bound$'
    Expect 'he can free her' (Send 1 'act|person.release') '^sent person\.release$'
    Expect 'and then nobody is left there' { Send 1 'state' } 'sleepers=0 '
    Expect 'freeing someone whose player has gone is on record like any other' { Send 0 'journal|verb.person.release' } "ok=True actor='John Doe' .*target=Sleeper"
    Expect 'both joins and every entry are on record' { Send 0 'journal' } 'connection\.join=2 .*city\.enter=3'

    Write-Host '==> A copy taken while the server runs is a working backup' -ForegroundColor Cyan
    $saved = Send 0 'roster'
    $live = Split-Path -Parent $script:inbox
    $copy = "$dataRoot-copy"
    Copy-Item -LiteralPath $live -Destination (Join-Path (Split-Path -Parent $live) $copy) -Recurse
    Expect 'the running server has both characters saved' $saved 'characters=\[Jane Roe:.*John Doe:.*\] holders=[1-9]'
    foreach ($client in $clients) { if (-not $client.HasExited) { Stop-Process -Id $client.Id -Force } }
    Stop-Process -Id $script:server.Id -Force
    $script:server.WaitForExit()
    Start-Server $copy
    Expect 'a server started on the copy has every character, with all they had' (Send 0 'roster') "^$([regex]::Escape($saved))$"

    $problems = @(Read-Log | Where-Object { $_ -match 'Exception|\[store\]|\[journal\]|\[scene\]|\[death\]|\[restore\]|Whitelist violation|Hexagon does not' -and $_ -notmatch 'Exception when loading' })
    Expect 'the server logged no game warnings or errors' "$($problems.Count) problem lines: $($problems -join ' | ')" '^0 problem'
}
finally {
    foreach ($client in $clients) { if (-not $client.HasExited) { Stop-Process -Id $client.Id -Force } }
    if ($script:server -and -not $script:server.HasExited) { Stop-Process -Id $script:server.Id -Force }
    # Their files are let go once they have exited.
    foreach ($process in @($clients) + @($script:server)) { if ($process) { [void]$process.WaitForExit(10000) } }
    # A failed run keeps its data, journal included: that is the evidence of what the host decided.
    $kept = Get-ChildItem -LiteralPath (Join-Path $SboxRoot 'data') -Recurse -Directory -Filter "$dataRoot*" -ErrorAction SilentlyContinue
    if ($script:failures -gt 0) { $kept | ForEach-Object { Write-Host "Data kept at $($_.FullName)" -ForegroundColor DarkGray } }
    else { $kept | Remove-Item -Recurse -Force }
    if ($script:failures -gt 0) { Write-Host "Server log kept at $log" -ForegroundColor DarkGray }
    else { Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue }
}

if ($script:failures -gt 0) { throw "$($script:failures) play-test expectation(s) failed." }
Write-Host 'Two-client play test passed on a dedicated server.' -ForegroundColor Green
