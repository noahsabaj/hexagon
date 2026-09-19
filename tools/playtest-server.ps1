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

if (Get-Process -Name 'sbox', 'sbox-server', 'sbox-dev' -ErrorAction SilentlyContinue) { throw 'Close s&box, its editor and any dedicated server first.' }

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
        Start-Sleep -Milliseconds 250
    }
    return 'no reply'
}

function Expect([string] $What, [string] $State, [string] $Pattern, [switch] $Not) {
    if (($State -match $Pattern) -ne [bool] $Not) { Write-Host "  ok   $What" -ForegroundColor Green }
    else {
        $script:failures++
        Write-Host "  FAIL $What" -ForegroundColor Red
        Write-Host "       $(if ($Not) { 'did not want' } else { 'wanted' }) /$Pattern/ in: $State" -ForegroundColor DarkGray
    }
}

Write-Host "==> Starting the dedicated server (data folder '$dataRoot')" -ForegroundColor Cyan
$start = [System.Diagnostics.ProcessStartInfo]::new((Join-Path $SboxRoot 'sbox-server.exe'))
$start.WorkingDirectory = $SboxRoot
$start.Arguments = "+game `"$manifest`" +net_allow_local 1 +hexagon_data_root $dataRoot +hexagon_dev 1"
$start.RedirectStandardOutput = $true
$start.UseShellExecute = $false
$server = [System.Diagnostics.Process]::Start($start)
$null = Register-ObjectEvent -InputObject $server -EventName OutputDataReceived -MessageData $log -Action {
    if ($null -ne $EventArgs.Data) { Add-Content -LiteralPath $Event.MessageData -Value $EventArgs.Data }
}
$server.BeginOutputReadLine()
$script:deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$clients = @()
try {
    Wait-Until 'the server' { (Read-Log) -match '\[dev\] inbox at ' }
    $script:inbox = ((Read-Log) -match '\[dev\] inbox at ' | Select-Object -First 1) -replace '^.*inbox at ', ''
    Expect 'a dedicated server has no player of its own' (Send 0 'who') 'local=False .*host=True .*pawns=\[\]'

    Write-Host '==> Starting two clients (a few minutes)' -ForegroundColor Cyan
    foreach ($index in 1, 2) {
        $clients += Start-Process -FilePath (Join-Path $SboxRoot 'sbox.exe') -WorkingDirectory $SboxRoot -PassThru -WindowStyle Minimized `
            -ArgumentList "+connect local +hexagon_dev 1 -sw -720"
    }
    Wait-Until 'both clients to have a pawn' { (Send 1 'who') -match 'local=True' -and (Send 2 'who') -match 'local=True' }

    Write-Host '==> Two characters, one city' -ForegroundColor Cyan
    [void](Send 1 "create|John Doe|$description|citizen")
    [void](Send 2 "create|Jane Roe|A woman in a grey coat, watching the street.|citizen")
    [void](Send 1 'enter|John Doe')
    Start-Sleep -Seconds 1
    [void](Send 2 'enter|John Doe')
    Expect 'a character already in the city cannot be entered twice' (Send 2 'state') 'has=False .*already in the city'
    [void](Send 2 'enter|Jane Roe')
    [void](Send 0 'console|hexagon_teleport "John Doe" 240 -150 60')
    [void](Send 0 'console|hexagon_teleport "Jane Roe" 300 -150 60')
    Start-Sleep -Seconds 2

    Write-Host '==> Perception' -ForegroundColor Cyan
    $seen = Send 1 'proxies'
    Expect 'the other player is visible, with the description an onlooker would see' $seen "has=True .*seen='A woman in a grey coat"
    Expect 'her name and faction were never sent' $seen "name='' faction=''"
    Expect 'nor is the name anywhere in what the client holds' $seen 'Jane' -Not
    Expect 'each client knows its own name' (Send 2 'state') "name='Jane Roe' faction='Citizen'"

    Write-Host '==> Speech reaches who is in range, and nobody else' -ForegroundColor Cyan
    [void](Send 1 'say|Morning')
    $heard = Send 2 'state'
    Expect 'speech reached a listener 60 units away, from a stranger' $heard '\[A tired resident of the city\.\] says "Morning"'
    Expect 'and did not carry his name' $heard 'chat=\[.*John Doe' -Not
    [void](Send 1 'introduce')
    [void](Send 1 'say|I am John')
    $heard = Send 2 'state'
    Expect 'once he introduced himself, she knows him' $heard 'known=\[John Doe\].*John Doe says "I am John"'
    Expect 'telling is one way: he still sees a stranger' (Send 1 'proxies') "label='\[A woman in a grey coat"
    Expect 'the journal names who heard it' (Send 0 'journal|chat.say') "actor='John Doe' witnesses=1"
    [void](Send 0 'console|hexagon_teleport "Jane Roe" 440 -150 60')
    Start-Sleep -Seconds 2
    Write-Host "       $(Send 0 'who')" -ForegroundColor DarkGray
    [void](Send 1 'say|/w keep this quiet')
    [void](Send 1 'say|Still here')
    $heard = Send 2 'state'
    Expect 'a whisper did not reach a listener 200 units away' $heard 'keep this quiet' -Not
    Expect 'ordinary speech still did' $heard 'Still here'
    Expect 'the journal shows the whisper had no witnesses' (Send 0 'journal|chat.whisper') 'witnesses=0'
    [void](Send 0 'console|hexagon_teleport "Jane Roe" 1500 -150 60')
    Start-Sleep -Seconds 2
    [void](Send 1 'say|/y can anyone hear me')
    [void](Send 1 'say|//back in five')
    $heard = Send 2 'state'
    Expect 'a yell did not carry 1260 units' $heard 'can anyone hear me' -Not
    Expect 'out-of-character chat reached everyone' $heard '\[OOC\] [^:/]+: back in five'

    Write-Host '==> A claimed position buys nothing' -ForegroundColor Cyan
    # goto is what a cheat does: the client simply declares itself somewhere else.
    [void](Send 2 'goto|250|-150|60')
    [void](Send 1 'say|Nobody is near me')
    Expect 'claiming to stand beside a speaker did not let her listen in' (Send 2 'state') 'Nobody is near me' -Not
    Expect 'the journal agrees nobody heard' (Send 0 'journal|chat.say') 'witnesses=0'
    Start-Sleep -Seconds 2
    Expect 'the host sent her back' (Send 2 'state') 'pos=1[45]\d\d'
    Expect 'and put the claim on record' (Send 0 'journal') 'movement\.implausible!=[1-9]'

    Write-Host '==> The world is shared' -ForegroundColor Cyan
    [void](Send 1 'door|Door 1|use')
    Start-Sleep -Seconds 1
    Expect 'a door one player opened is open for the other' (Send 2 'state') 'Door 1:open=True'
    [void](Send 2 'door|Door 1|use')
    Expect 'and out of reach for a player far away' (Send 2 'state') 'Door 1:open=True.*too far away'

    Write-Host '==> Violence is slow, visible and on the record' -ForegroundColor Cyan
    [void](Send 0 'console|hexagon_teleport "Jane Roe" 300 -150 60')
    foreach ($gift in 'pistol', 'pistol_round', 'pistol_round', 'pistol_round', 'pistol_round', 'zip_tie') { [void](Send 0 "console|hexagon_give `"John Doe`" $gift") }
    Start-Sleep -Seconds 2
    [void](Send 1 'equip|2')
    Expect 'a drawn pistol is something anyone can see' (Send 2 'proxies') "held='Pistol'"
    foreach ($shot in 1..3) { [void](Send 1 'attack'); Start-Sleep -Milliseconds 300 }
    $state = Send 2 'state'
    Expect 'three shots put her down, not out' $state 'has=True .*health=0 down=True'
    Expect 'he sees her fall but not her health' (Send 1 'proxies') 'down=True .*health=0 has=True'
    $journal = Send 0 'journal'
    Expect 'each shot, and the fall, are on record' $journal 'combat\.attack=3 .*character\.downed=1'
    Expect 'each shot used up a round' "rounds=$([regex]::Matches((Send 1 'state'), 'Pistol round@').Count)" '^rounds=1$'
    [void](Send 2 'leave')
    Expect 'she cannot leave the scene' (Send 2 'state') 'has=True .*cannot leave the city like this'

    Write-Host '==> Searching is opening a holder' -ForegroundColor Cyan
    [void](Send 1 'act|person.search')
    Expect 'he sees what she carries' (Send 1 'state') 'open=\[Belongings: Ration, Water\]'
    Expect 'and she is told' (Send 2 'state') 'going through your belongings'
    [void](Send 1 'take|0')
    Expect 'what he took left her inventory' (Send 2 'state') 'items=\[Water@'
    [void](Send 1 'act|person.revive')
    Expect 'helped up, she is no longer searchable' "$(Send 2 'state') $(Send 1 'state')" 'health=25 down=False .*open=\[: \]'

    Write-Host '==> Restraints' -ForegroundColor Cyan
    [void](Send 2 'act|person.restrain')
    Expect 'she has nothing to bind him with' (Send 2 'state') 'nothing that lets you do that'
    [void](Send 1 'act|person.restrain')
    Expect 'his zip tie bound her, and was used up' "$(Send 2 'state') $(Send 1 'state')" 'restrained=True .*Your hands are bound.*items=\[(?!.*Zip tie)'
    [void](Send 2 'drop|0')
    Expect 'bound hands do nothing' (Send 2 'state') 'items=\[Water@.*cannot do that now'
    [void](Send 1 'act|person.release')
    Expect 'released' (Send 2 'state') 'restrained=False'

    Write-Host '==> Death is a deliberate act, and leaves a body' -ForegroundColor Cyan
    [void](Send 1 'act|person.finish')
    Expect 'someone standing cannot be finished: the verb is not even offered' (Send 0 'journal') 'character\.death|verb\.person\.finish' -Not
    [void](Send 1 'attack')
    Start-Sleep -Milliseconds 500
    [void](Send 1 'act|person.finish')
    Start-Sleep -Seconds 2
    Expect 'she woke elsewhere, whole, with nothing' (Send 2 'state') 'tokens=0 .*items=\[\] .*health=100 down=False'
    Expect 'the death is on record' (Send 0 'journal') 'character\.death=1'
    [void](Send 1 'act|corpse.search')
    Expect 'what she carried is on the body' (Send 1 'state') 'bodies=1 opentokens=100 .*open=\[Body: Water\]'
    [void](Send 1 'taketokens')
    [void](Send 1 'take|0')
    Expect 'emptied, the body is gone, and nothing was created or lost' (Send 1 'state') 'tokens=200 .*bodies=0'

    Write-Host '==> Leaving' -ForegroundColor Cyan
    Stop-Process -Id $clients[1].Id -Force
    Wait-Until 'the server to notice the disconnect' { (Send 0 'journal') -match 'connection\.leave=1' }
    Expect 'the player who left is gone from the other client' (Send 1 'proxies') '^[^A-Za-z]*$'
    Expect 'both joins and both entries are on record' (Send 0 'journal') 'connection\.join=2 .*city\.enter=2'

    $problems = @(Read-Log | Where-Object { $_ -match 'Exception|\[store\]|\[journal\]|Whitelist violation|Hexagon does not' -and $_ -notmatch 'Exception when loading' })
    Expect 'the server logged no game warnings or errors' "$($problems.Count) problem lines: $($problems -join ' | ')" '^0 problem'
}
finally {
    foreach ($client in $clients) { if (-not $client.HasExited) { Stop-Process -Id $client.Id -Force } }
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    Start-Sleep -Seconds 1
    Get-ChildItem -LiteralPath (Join-Path $SboxRoot 'data') -Recurse -Directory -Filter $dataRoot -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force
    if ($script:failures -gt 0) { Write-Host "Server log kept at $log" -ForegroundColor DarkGray }
    else { Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue }
}

if ($script:failures -gt 0) { throw "$($script:failures) play-test expectation(s) failed." }
Write-Host 'Two-client play test passed on a dedicated server.' -ForegroundColor Green
