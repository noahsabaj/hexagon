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

# The engine gives a dedicated server ten seconds to log on to Steam. When Steam is slower, the server
# says so once and never listens, so no client can join it.
function Test-Unheard { [bool] @(Read-Log | Select-Object -Skip $script:logFrom | Where-Object { $_ -match 'Unable to connect to Steam in a reasonable time' }) }

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

# Waits until a client's character has stopped falling: two looks in a row put it at the same height.
function Wait-Landed([int] $Client) {
    $last = $null
    $until = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $height = if ((Send $Client 'state') -match 'pos=[-\d.]+,[-\d.]+,(-?[\d.]+)') { [double] $Matches[1] }
        if ($null -ne $last -and $null -ne $height -and [Math]::Abs($height - $last) -lt 0.5) { return }
        $last = $height
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $until)
}

# Client 1 shoots at a part of someone, from his own eyes, at where the host has it, as a player aims at
# what they see. Each machine animates a body for itself, a few units apart, so aiming at his own copy
# of an arm could miss hers on the host. A body still falling is waited for, until two looks agree.
function Shoot([string] $Name, [string] $Bone) {
    $last = $null
    $until = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $reply = Send 0 "part|$Name|$Bone"
        if ($reply -notmatch '^(-?[\d.]+) (-?[\d.]+) (-?[\d.]+)') { throw "No part '$Bone' of $Name to shoot at: $reply" }
        $at = @($Matches[1], $Matches[2], $Matches[3])
        if ($null -ne $last -and ((0..2 | ForEach-Object { [Math]::Abs([double] $at[$_] - [double] $last[$_]) }) | Measure-Object -Sum).Sum -lt 2) { break }
        $last = $at
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $until)
    [void](Send 1 "attack|$($at -join '|')")
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
    # What an earlier server on this log said is not this one's.
    $script:logFrom = @(Read-Log).Count
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
    Wait-Until 'the server' { @(Read-Log | Select-Object -Skip $script:logFrom) -match $announced }
    $script:inbox = (@(Read-Log | Select-Object -Skip $script:logFrom) -match $announced | Select-Object -Last 1) -replace '^.*inbox at ', ''
}

Write-Host "==> Starting the dedicated server (data folder '$dataRoot')" -ForegroundColor Cyan
$script:deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$script:server = $null
$clients = @()
try {
    # A server Steam did not hear from in time is started again, with its clients.
    foreach ($attempt in 1..3) {
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
        foreach ($number in 1, 2) { Wait-Until "client $number to have a pawn" { (Test-Unheard) -or (Send $number 'who') -match 'local=True' } }
        if (-not (Test-Unheard)) { break }
        if ($attempt -eq 3) { throw 'Steam did not hear from the server in time, three times running.' }
        Write-Host '==> Steam did not hear from the server in time, so it never listened; starting it and the clients again' -ForegroundColor Yellow
        foreach ($process in @($clients) + @($script:server)) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force; [void]$process.WaitForExit(10000) } }
        $clients = @()
    }

    # tools/test.ps1 starts the editor play test when this line appears.
    Write-Host '==> Two characters, one city' -ForegroundColor Cyan
    [void](Send 1 "create|John Doe|$description|citizen|flat_cap,shirt_and_waistcoat,trousers,boots")
    [void](Send 2 "create|Jane Roe|A woman in a grey coat, watching the street.|human02|hair_bobcut,flannel_shirt,cardigan,jeans,boots")
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
    Expect 'what she wears, on the body she has, is there for anyone to see' $seen 'look=\[human02,hair_bobcut,flannel_shirt,cardigan_dark,jeans,black_boots\]'
    Expect 'each client knows its own name, and that she belongs to no faction' (Send 2 'state') "name='Jane Roe' faction='' "

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
    foreach ($client in 1, 2) { [void](Poll { Send $client 'state' } 'items=\[Identification card@0,0, Radio') }
    [void](Send 1 'tune|Radio|101.5')
    [void](Send 2 'tune|Radio|101.5')
    foreach ($client in 1, 2) { [void](Poll { Send $client 'state' } 'Radio~101\.5@') }
    [void](Send 1 'say|/r unit one to control')
    Expect 'a radio carries 1260 units to one tuned the same, by a name she was given' { Send 2 'state' } '\[101\.5\] John Doe: "unit one to control"'
    [void](Send 2 'say|/r control here')
    $heard = Poll { Send 1 'state' } 'control here'
    Expect 'he was never given her name, and a radio shows no face' $heard '\[101\.5\] A voice: "control here"'
    Expect 'so nothing of what she looks like came with it' $heard 'grey coat' -Not
    Expect 'the journal names who was listening' { Send 0 'journal|chat.radio' } "actor='Jane Roe' witnesses=1"
    [void](Send 2 'tune|Radio|102.0')
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
    [void](Send 2 'drop|Radio')
    [void](Poll { Send 2 'state' } 'items=\[Identification card@0,0\] ')
    Start-Sleep -Seconds 4
    Expect 'what she dropped out of his sight never reached him' (Send 1 'state') 'ground=\[\] '
    Expect 'once she is in sight' (Send 0 "sight|$sight") "sight=$sight"
    Expect 'he is sent what lies there' { Send 1 'state' } 'ground=\[Radio\] '

    Write-Host '==> Handing over takes two' -ForegroundColor Cyan
    Teleport 2 'Jane Roe' $beside
    foreach ($gift in 'pistol', 'pistol_round 6', 'zip_tie', 'token 50') { [void](Send 0 "console|hexagon_give `"John Doe`" $gift") }
    [void](Send 0 'console|hexagon_give "Jane Roe" water')
    Expect 'what staff gave him is in his hands, money with the rest' { Send 1 'state' } 'items=\[Identification card@0,0, Radio~101\.5@1,0, Pistol@3,0, Pistol roundx6@5,0, Zip tie@0,1, Tokenx50@1,1\]'
    [void](Poll { Send 2 'state' } 'items=\[Identification card@0,0, Water@1,0\]')
    [void](Send 1 'offer|Token|30')
    Expect 'she is asked, by the name she was given' { Send 2 'state' } "offer='John Doe offers you 30 Token\.'"
    Expect 'and nothing has moved yet' (Send 1 'state') 'Tokenx50@1,1'
    [void](Send 2 'answer|1')
    Expect 'she took it' { Send 2 'state' } "items=\[Identification card@0,0, Water@1,0, Tokenx30@2,0\] .*offer='' .*You take 30 Token\."
    Expect 'and it left his hands' { Send 1 'state' } 'Tokenx20@1,1.*Your offer is taken\.'
    [void](Send 1 'offer|Zip tie|1')
    [void](Poll { Send 2 'state' } "offer='John Doe offers you [^']*Zip tie")
    [void](Send 2 'answer|0')
    Expect 'an offer can be refused, and then nothing moves' { Send 1 'state' } 'Zip tie@0,1.*Your offer is refused\.'
    [void](Send 1 'offer|Zip tie|1')
    [void](Poll { Send 2 'state' } "offer='John Doe offers you [^']*Zip tie")
    Teleport 2 'Jane Roe' $apart
    Expect 'an offer lapses once the two have parted' { "$(Send 2 'state') $(Send 1 'state')" } "offer='' .*The offer lapses\..*Zip tie@0,1.*Your offer lapses\."
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

    Write-Host '==> A paper is held up to read, not handed over' -ForegroundColor Cyan
    [void](Send 2 'show|Identification card')
    $state = Poll { Send 1 'state' } "paper='Identification card'"
    Expect 'she holds up her card and he reads it, held by the stranger he takes her for' $state "paper='Identification card' shownby='\[A woman in a grey coat[^']*' lines=\[\] fields=\[Name=Jane Roe\] stamps=\[Combine Civil Authority@0\] "
    Expect 'he is told who holds it up' $state 'A woman in a grey coat[^/]* holds up the identification card for you to read\.'
    Expect 'it stays in her hand' { Send 2 'state' } 'items=\[Identification card@0,0, .*You hold up the identification card for them to read\.'
    Expect 'on the record, with what was shown' { Send 0 'journal|paper.show' } "ok=True actor='Jane Roe' .*data=\[item=\w+ definition=items/cid_card\.item\]"
    Teleport 2 'Jane Roe' $apart
    Expect 'once they part, he can no longer read it' { Send 1 'state' } "paper='' shownby='' lines=\[\] "
    Teleport 2 'Jane Roe' $beside

    Write-Host '==> Violence is slow, visible and on the record' -ForegroundColor Cyan
    # Out on the open floor past the crate, where she has room to fall. Places are measured from the
    # crate, which never moves: a door turns as it opens, and its back once faced a wall. He stands in
    # front of her, the way her body faces, so nothing of her is in the way of the part he shoots at.
    $target = Spot 'Crate' front 150
    Teleport 2 'Jane Roe' $target
    Wait-Landed 2
    Teleport 1 'John Doe' (Spot 'Jane Roe' front 60)
    Wait-Landed 1
    Expect 'six rounds are one pile' (Send 1 'state') 'Pistol roundx6@'
    [void](Send 1 'equip|Pistol')
    Expect 'a drawn pistol is something anyone can see' { Send 2 'proxies' } "held='Pistol'"
    [void](Send 2 'equip|Water')
    Expect 'and so is what she holds' { Send 1 'proxies' } "held='Water'"
    # The host judges where each shot lands, on the body's own hitboxes. A pistol fires at most every
    # half second, and the host ignores a shot sooner than that, so each shot waits for the last to
    # have used its round, and then for the pistol to be ready again.
    Shoot 'Jane Roe' leg_lower_L
    [void](Poll { Send 1 'state' } 'Pistol roundx5@')
    $state = Poll { Send 2 'state' } 'wounds=\[left leg\]'
    Expect 'a shot to the leg wounds it, and she walks on it slowly' $state 'health=60 down=False .*wounds=\[left leg\] limping=True breathing=False speed=60 .*You are hit in the left leg\.'
    Expect 'a limp anyone can see' { Send 1 'proxies' } 'limping=True breathing=False'
    Start-Sleep -Milliseconds 550
    Shoot 'Jane Roe' arm_lower_R
    [void](Poll { Send 1 'state' } 'Pistol roundx4@')
    $state = Poll { Send 2 'state' } 'right arm\]'
    Expect 'a shot to the arm drops what she held' $state "items=\[Identification card@0,0, Tokenx30@2,0\] .*health=20 down=False restrained=False held='' .*wounds=\[left leg, right arm\] .*ground=\[[^\]]*Water"
    Expect 'badly hurt, she breathes hard, and he hears it' { Send 1 'proxies' } "held='' .*breathing=True"
    [void](Send 2 'equip|Token')
    Expect 'that hand holds nothing until it is treated' { Send 2 'state' } 'Your arm will not hold anything until the wound is treated\.'
    [void](Send 2 'pickup|Water')
    Expect 'but she can pick up what fell' { Send 2 'state' } 'items=\[Identification card@0,0, Tokenx30@2,0, Water@1,0\] '
    Start-Sleep -Milliseconds 550
    Shoot 'Jane Roe' spine_2
    [void](Poll { Send 1 'state' } 'Pistol roundx3@')
    Expect 'three shots put her down, not out' { Send 2 'state' } 'has=True .*health=0 down=True'
    Expect 'he sees her fall but not her health' { Send 1 'proxies' } 'down=True .*health=0 has=True'
    $lying = Poll { Send 1 'sleepers' } ',down,limp'
    Expect 'she lies limp where she fell' $lying '^-?\d+,-?\d+,down,limp$'
    Expect 'and sees from her body, on the ground' { Send 2 'state' } 'view=-?(\d|[12]\d) '
    $journal = Poll { Send 0 'journal' } '(?<!\S)character\.downed=1(?!\S)'
    Expect 'each shot, and the fall, are on record' $journal 'combat\.attack=3 .*character\.downed=1'
    Expect 'and so is each wound' $journal '(?<!\S)character\.wounded=2(?!\S)'
    Expect 'with where the shot landed' (Send 0 'journal|character.wounded') 'data=\[place=right arm cause=Pistol\]'
    Expect 'each shot used up a round' (Send 1 'state') 'Pistol roundx3@'
    [void](Send 2 'leave')
    Expect 'she cannot leave the scene' { Send 2 'state' } 'has=True .*cannot leave the city like this'

    Write-Host '==> Searching is opening a holder' -ForegroundColor Cyan
    [void](Send 1 'act|person.search')
    Expect 'he sees what she carries' { Send 1 'state' } 'open=\[Belongings: Identification card, Tokenx30, Water\] openworn=\[Flannel shirt, Cardigan, Jeans, Black boots\] '
    Expect 'and she is told' { Send 2 'state' } 'going through your belongings'
    [void](Send 1 'take|Token')
    Expect 'what he took left her inventory' { Send 2 'state' } 'items=\[Identification card@0,0, Water@1,0\] '
    Expect 'and joined his own pile' { Send 1 'state' } 'Tokenx50@1,1'
    [void](Send 1 'strip|Cardigan')
    Expect 'what she wore can be taken off her too' { Send 2 'state' } 'items=\[Identification card@0,0, Water@1,0\] wearing=\[Flannel shirt, Jeans, Black boots\] '
    Expect 'and he sees it gone from her' { Send 1 'proxies' } 'look=\[human02,hair_bobcut,flannel_shirt,jeans,black_boots\]'
    [void](Send 1 'wear|Cardigan')
    Expect 'once he puts it on, she sees him in it' { Send 2 'proxies' } 'look=\[flatcap,waistcoat_and_shirt,trousers\.smart,black_boots,cardigan_dark\]'
    [void](Send 1 'act|person.revive')
    Expect 'helped up, she is no longer searchable' { "$(Send 2 'state') $(Send 1 'state')" } 'health=25 down=False .*open=\[: \]'
    $state = Send 2 'state'
    Expect 'she stands where her body lay, her wounds still on her' "$(Near $state ([int[]] (($lying -split ',')[0..1] + 0))) $state" "^True .*held='' .*wounds=\[left leg, right arm\] limping=True breathing=True "

    Write-Host '==> Wounds are treated by hand' -ForegroundColor Cyan
    [void](Send 0 'console|hexagon_give "John Doe" bandage 2')
    [void](Poll { Send 1 'state' } 'items=\[Identification card@0,0, Radio~101\.5@1,0, Pistol@3,0, Pistol roundx3@5,0, Zip tie@0,1, Tokenx50@1,1, Bandagex2@')
    [void](Send 1 'act|person.treat')
    Expect 'what treats her must be in his hand' { Send 1 'state' } 'Hold something to treat them with\.'
    [void](Send 1 'equip|Bandage')
    [void](Poll { Send 1 'state' } "held='Bandage'")
    [void](Send 1 'act|person.treat')
    $state = Poll { Send 2 'state' } 'wounds=\[right arm\]'
    Expect 'a bandage from his hand closes her oldest wound and heals a little' $state 'health=55 .*wounds=\[right arm\] limping=False .*Your left leg is treated\.'
    Expect 'in front of her' $state 'John Doe binds a wound'
    [void](Send 1 'act|person.treat')
    $state = Poll { Send 2 'state' } 'wounds=\[\]'
    Expect 'the second closes the other' $state 'health=85 .*wounds=\[\] limping=False breathing=False .*Your right arm is treated\.'
    Expect 'and the bandages are used up' { Send 1 'state' } "Zip tie@0,1, Tokenx50@1,1\] .*held='' "
    [void](Send 2 'equip|Water')
    Expect 'her hand holds again' { Send 2 'state' } "held='Water'"
    Expect 'each is on record, with what it closed' (Send 0 'journal|character.treated') "actor='John Doe' .*data=\[item=bandage wound=right arm healed=30\]"
    [void](Send 1 'equip|Pistol')
    [void](Poll { Send 1 'state' } "held='Pistol'")

    Write-Host '==> Restraints' -ForegroundColor Cyan
    [void](Send 2 'act|person.restrain')
    Expect 'she has nothing to bind him with' { Send 2 'state' } 'nothing that lets you do that'
    [void](Send 1 'act|person.restrain')
    Expect 'his zip tie bound her, and was used up' { "$(Send 2 'state') $(Send 1 'state')" } 'restrained=True .*Your hands are bound.*items=\[(?![^\]]*Zip tie)'
    [void](Send 2 'drop|Water')
    Expect 'bound hands do nothing' { Send 2 'state' } 'items=\[Identification card@0,0, Water@.*cannot do that now'
    [void](Send 1 'act|person.release')
    Expect 'released' { Send 2 'state' } 'restrained=False'

    Write-Host '==> Death is a deliberate act, and it is final' -ForegroundColor Cyan
    [void](Send 1 'act|person.finish')
    Expect 'someone standing cannot be finished: the verb is not even offered' (Send 0 'journal') 'character\.death|verb\.person\.finish' -Not
    [void](Send 0 'console|hexagon_hurt "Jane Roe" 60')
    [void](Poll { Send 2 'state' } 'health=25 ')
    Shoot 'Jane Roe' spine_2
    [void](Poll { Send 1 'state' } 'Pistol roundx2@')
    # A pistol is made to kill: a shot to the head kills outright, someone lying down as much as anyone.
    [void](Poll { Send 1 'sleepers' } ',down,limp')
    Start-Sleep -Milliseconds 550
    Shoot 'Jane Roe' head
    Expect 'a shot to her head as she lies there kills her, and her player is back at the list, where the dead are kept' { Send 2 'state' } 'has=False .*characters=\[John Doe, Jane Roe\(dead\)\].*Jane Roe has died\.'
    $journal = Poll { Send 0 'journal' } '(?<!\S)character\.death=1(?!\S)'
    Expect 'the death is on record' $journal '(?<!\S)character\.death=1(?!\S)'
    Expect 'with where the shot landed' (Send 0 'journal|character.death') 'place=head'
    Expect 'and the title she held went with her' $journal '(?<!\S)door\.release=1(?!\S)'
    # He is beside her, so he sees her body as she falls, not once everyone else may be sent it.
    Expect 'he sees her body at once' (Poll { Send 1 'state' } 'bodies=1 ' 2) 'bodies=1 '
    [void](Send 1 'act|corpse.search')
    Expect 'what she carried is on her body, and what she wore' { Send 1 'state' } 'bodies=1 .*open=\[Body: Identification card, Water\] openworn=\[Flannel shirt, Jeans, Black boots\] '
    Expect 'and the body is seen in it' (Send 1 'state') 'bodylooks=\[human02,hair_bobcut,flannel_shirt,jeans,black_boots\] '
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
    Expect 'she gets up where she fell, barely, with what was still on her' { Send 2 'state' } 'items=\[Identification card@0,0, Water@1,0\] wearing=\[Flannel shirt, Jeans, Black boots\] .*owned=\[\] .*health=25 down=False .*bodies=0 '
    Expect 'and the body is gone for him too' { Send 1 'state' } 'bodies=0 '
    Expect 'the restore is on record' { Send 0 'journal' } '(?<!\S)operator\.restore=1(?!\S)'

    Write-Host '==> Leaving does not end a scene' -ForegroundColor Cyan
    Teleport 2 'Jane Roe' $target
    [void](Send 0 'console|hexagon_give "John Doe" zip_tie')
    [void](Poll { Send 1 'state' } 'Zip tie@')
    [void](Poll { Send 1 'proxies' } 'down=False restrained=False .*has=True')
    [void](Send 1 'act|person.restrain')
    Expect 'he binds her again' { Send 2 'state' } 'restrained=True'
    Stop-Process -Id $clients[1].Id -Force
    Wait-Until 'the server to notice the disconnect' { (Send 0 'journal') -match 'connection\.leave=1' }
    Expect 'the player who left is gone from the other client' { Send 1 'proxies' } '^[^A-Za-z]*$'
    # What someone left behind looks like is sent once the network has stopped sending it to those too far away.
    Expect 'but the character she left bound is still standing there, and he is sent it' { Send 1 'sleepers' } '^-?\d+,-?\d+,bound,standing$'
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
catch {
    # A run that gave up waiting failed too, and keeps what explains it.
    $script:failures++
    throw
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
