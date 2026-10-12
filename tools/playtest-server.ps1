# Plays a Hexagon game as two real clients on a real dedicated server, and checks what each was told.
#
# The editor play test has one client, which is also the host and so knows everything. What one
# player may learn about another can only be checked here: a dedicated server, two game clients
# joined over the loopback, and each client asked what it actually received.
#
# The server does not read a redirected console, so commands are left as files in its data folder
# (see DevCommands.PollInbox) and replies are read from its output. Both ends run with +hexagon_dev 1.
# Steam must be running. Both clients are the same Steam account, so they share one character list.
#
# A run stops at the first check that fails. Starting the server and the clients takes about a minute
# and a half, so a run with -Keep leaves them running, and the next run with -Keep starts on them in a
# fresh city, in seconds, unless the code or an asset has changed since; -Close closes them.
[CmdletBinding()]
param(
    [string] $GameRoot,
    [string] $SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox',
    [int] $TimeoutSeconds = 900,
    # Ends the run once the section whose name starts with this has been played.
    [string] $Until,
    # Leaves the server and both clients running when the run ends, and starts on ones a run with -Keep left.
    [switch] $Keep,
    # Closes the server and clients a run with -Keep left, and does nothing else.
    [switch] $Close
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $GameRoot) { $GameRoot = Join-Path (Split-Path -Parent $root) 'hl2rp-hexagon' }
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
$manifest = (Get-ChildItem -LiteralPath $GameRoot -Filter '*.sbproj' | Select-Object -First 1).FullName
$dataRoot = "playtest-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$description = 'A tired resident of the city.'
$script:failures = 0
$script:sent = 0
$script:untilPlayed = $false
$script:inbox = $null
$script:log = $null
$script:logFrom = 0
$untilReached = 'The -Until section has been played.'
$stoppedAtFailure = 'A check failed, so the run stops there.'
# What a run with -Keep left running, for the next run to start on.
$keptFile = Join-Path ([System.IO.Path]::GetTempPath()) 'hexagon-playtest-kept.json'

if ($Until) {
    $sections = Select-String -LiteralPath $PSCommandPath -Pattern "^\s*Section '(.+)'\s*$" | ForEach-Object { $_.Matches[0].Groups[1].Value -replace "''", "'" }
    if (-not ($sections | Where-Object { $_ -like "$Until*" })) { throw "No section's name starts with '$Until'. The sections are: $($sections -join '; ')." }
}

# A failed run keeps its data, journal included, and the server's log: they are the evidence of what
# the host decided.
function Remove-RunFiles([string] $Data, [bool] $Passed, [string[]] $Logs = @()) {
    $folders = @(Get-ChildItem -LiteralPath (Join-Path $SboxRoot 'data') -Recurse -Directory -Filter "$Data*" -ErrorAction SilentlyContinue | ForEach-Object FullName)
    $files = @($Logs | Where-Object { $_ } | ForEach-Object { $_, "$_.err" } | Where-Object { Test-Path -LiteralPath $_ })
    if ($Passed) { foreach ($item in $folders + $files) { Remove-Item -LiteralPath $item -Recurse -Force } }
    else { foreach ($item in $folders + $files) { Write-Host "Kept $item" -ForegroundColor DarkGray } }
}

# Closes what a run with -Keep left: the processes it started, if they are still those, and what the
# last run on them wrote, unless it failed.
function Close-Kept($Record) {
    $processes = @(@(@($Record.Clients) + @($Record.Server) | Where-Object { $_ }) | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue } | Where-Object { $_.ProcessName -in 'sbox', 'sbox-server' })
    foreach ($process in $processes) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    # A client killed takes a while to be gone.
    $until = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $until -and @($processes | Where-Object { -not $_.HasExited }).Count -gt 0) { Start-Sleep -Milliseconds 250 }
    Remove-Item -LiteralPath $keptFile -ErrorAction SilentlyContinue
    Remove-RunFiles $Record.Data $Record.Passed @($Record.Log)
}

# The code and assets a server and its clients loaded. When any of them has changed, kept ones are
# started again.
function Get-Fingerprint {
    $places = @((Join-Path $root 'Code'), (Join-Path $GameRoot 'Assets'), (Join-Path $GameRoot 'Code'), (Join-Path $GameRoot 'ProjectSettings')) | Where-Object { Test-Path -LiteralPath $_ }
    $files = @(Get-ChildItem -LiteralPath $places -Recurse -File | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\|\.csproj$' }) + @(Get-Item -LiteralPath $manifest)
    "$($files.Count):$(($files | ForEach-Object { $_.LastWriteTimeUtc.Ticks } | Measure-Object -Maximum).Maximum)"
}

$record = if (Test-Path -LiteralPath $keptFile) { Get-Content -LiteralPath $keptFile -Raw | ConvertFrom-Json } else { $null }
if ($Close) {
    if ($record) { Close-Kept $record; Write-Host 'Closed the server and clients a run with -Keep left.' -ForegroundColor Green }
    return
}

& (Join-Path $PSScriptRoot 'compile-assets.ps1') -GameRoot $GameRoot -SboxRoot $SboxRoot
$fingerprint = Get-Fingerprint
$reuse = $null
if ($record) {
    $kept = Get-Process -Id $record.Server -ErrorAction SilentlyContinue
    $why = if (-not $Keep) { $null }
        elseif (-not $kept -or $kept.ProcessName -ne 'sbox-server') { 'its server has exited' }
        elseif ($record.Manifest -ne $manifest) { 'it plays another game' }
        elseif ($record.Fingerprint -ne $fingerprint) { 'the code or an asset has changed since' }
    if ($Keep -and -not $why) { $reuse = $record }
    else {
        if ($why) { Write-Host "==> Closing the server and clients the last run kept: $why" -ForegroundColor Cyan }
        Close-Kept $record
    }
}

# An open editor is no obstacle, so the editor play test can run at the same time (tools/test.ps1).
# A client killed lingers a while after it has exited, and is no obstacle.
if (-not $reuse -and @(Get-Process -Name 'sbox', 'sbox-server' -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited }).Count -gt 0) { throw 'Close s&box and any dedicated server first.' }

# RenderDoc, when it is on the PATH, is loaded into each game client, and with it every native
# library the engine loads while starting is slow: a client took two to four minutes to reach its
# menu, and 13 seconds without it. The clients are started without it.
$pathWithoutRenderDoc = ($env:Path -split ';' | Where-Object { $_ -and -not (Test-Path -LiteralPath (Join-Path $_ 'renderdoc.dll')) }) -join ';'

# The editor, while it plays a scene, listens for local players on the engine's default port, so the
# server and its clients meet on another. A port the system hands out is free, and outside the ranges
# Windows keeps for itself.
if ($reuse) { $localPort = [int] $reuse.Port }
else {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $localPort = $listener.LocalEndpoint.Port
    $listener.Stop()
}

# What the server has written since this run began, read as it comes, both streams: errors are
# written to the other, and are what the last check most needs to see. A server kept from an earlier
# run wrote that run's lines too, and they are skipped.
$script:logLines = [System.Collections.Generic.List[string]]::new()
$script:logs = @()
$script:readers = @()
$script:partial = @()
function Open-Log([string] $Path, [switch] $FromEnd) {
    foreach ($reader in $script:readers) { $reader.Dispose() }
    $script:log = $Path
    $script:logs += $Path
    $script:readers = @(foreach ($file in $Path, "$Path.err") {
        $stream = [System.IO.FileStream]::new($file, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
        if ($FromEnd) { [void]$stream.Seek(0, [System.IO.SeekOrigin]::End) }
        [System.IO.StreamReader]::new($stream)
    })
    $script:partial = @('', '')
}
function Update-Log {
    for ($index = 0; $index -lt $script:readers.Count; $index++) {
        $text = $script:readers[$index].ReadToEnd()
        if (-not $text) { continue }
        # A line is taken once it has ended: the server may be part way through writing it.
        $lines = ($script:partial[$index] + $text) -split "`r?`n"
        $script:partial[$index] = $lines[-1]
        for ($line = 0; $line -lt $lines.Count - 1; $line++) { $script:logLines.Add($lines[$line]) }
    }
}
function Read-Log { Update-Log; $script:logLines }

# The engine gives a dedicated server ten seconds to log on to Steam. When Steam is slower, the server
# says so once and never listens, so no client can join it.
function Test-Unheard { [bool] @(Read-Log | Select-Object -Skip $script:logFrom | Where-Object { $_ -match 'Unable to connect to Steam in a reasonable time' }) }

function Wait-Until([string] $What, [scriptblock] $Condition) {
    while ([DateTime]::UtcNow -lt $script:deadline) {
        if ($script:server.HasExited) { throw "The server exited with code $($script:server.ExitCode) while waiting for $What." }
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for $What."
}

# Sends one command to a client (0 is the server itself) and returns its reply.
function Send([int] $Client, [string] $Command) {
    $script:sent++
    # The tag makes each reply findable, and the driver ignores arguments it does not use. A kept
    # server goes on counting from where the last run left off, so no tag is used twice in its output.
    $tagged = "$Command|#$($script:sent)"
    Update-Log
    $from = $script:logLines.Count
    Set-Content -LiteralPath (Join-Path $script:inbox ('{0:d6}.txt' -f $script:sent)) -Value "$Client|$tagged"
    $marker = "[dev] $Client $tagged => "
    $until = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $until) {
        Update-Log
        for ($index = $script:logLines.Count - 1; $index -ge $from; $index--) {
            $line = $script:logLines[$index]
            if ($line.Contains($marker)) { return $line.Substring($line.IndexOf($marker) + $marker.Length) }
        }
        Start-Sleep -Milliseconds 20
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
        Start-Sleep -Milliseconds 100
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
        Start-Sleep -Milliseconds 100
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
# That something did not happen is checked once, after something sent later has arrived. The first
# check that fails ends the run: what follows it was played on a city it left wrong.
function Expect([string] $What, $State, [string] $Pattern, [switch] $Not) {
    if ($State -is [scriptblock]) { $State = if ($Not) { & $State } else { Poll $State $Pattern } }
    if (($State -match $Pattern) -ne [bool] $Not) { Write-Host "  ok   $What" -ForegroundColor Green }
    else {
        $script:failures++
        Write-Host "  FAIL $What" -ForegroundColor Red
        Write-Host "       $(if ($Not) { 'did not want' } else { 'wanted' }) /$Pattern/ in: $State" -ForegroundColor DarkGray
        throw $stoppedAtFailure
    }
}

# Whether the server wrote anything a game should not have, which only a run that played to its end, or
# to its -Until section, can say.
function Test-Problems {
    $problems = @(Read-Log | Where-Object { $_ -match 'Exception|\[store\]|\[journal\]|\[scene\]|\[death\]|\[restore\]|Whitelist violation|Hexagon does not' -and $_ -notmatch 'Exception when loading' })
    Expect 'the server logged no game warnings or errors' "$($problems.Count) problem lines: $($problems -join ' | ')" '^0 problem'
}

# Begins the next section, or ends the run once the -Until section has been played.
function Section([string] $Name) {
    if ($script:untilPlayed) { Test-Problems; throw $untilReached }
    if ($Until -and $Name -like "$Until*") { $script:untilPlayed = $true }
    Write-Host "==> $Name" -ForegroundColor Cyan
}

# Starts a dedicated server on a data folder and waits until it is taking commands. Its output goes
# to files rather than to this script, so a server kept for the next run outlives it.
function Start-Server([string] $DataFolder) {
    Update-Log
    $script:logFrom = $script:logLines.Count
    $script:starts++
    $path = Join-Path ([System.IO.Path]::GetTempPath()) "$DataFolder-server$($script:starts).log"
    $script:server = Start-Process -FilePath (Join-Path $SboxRoot 'sbox-server.exe') -WorkingDirectory $SboxRoot -WindowStyle Hidden -PassThru `
        -ArgumentList "+game `"$manifest`" +net_allow_local 1 +net_local_port $localPort +hexagon_data_root $DataFolder +hexagon_dev 1" `
        -RedirectStandardOutput $path -RedirectStandardError "$path.err"
    Open-Log $path
    Wait-Inbox $DataFolder 'the server'
}

# Waits until the server announces where it reads commands for a data folder.
function Wait-Inbox([string] $DataFolder, [string] $What) {
    $announced = "\[dev\] inbox at .*$([regex]::Escape($DataFolder))[\\/]"
    Wait-Until $What { @(Read-Log | Select-Object -Skip $script:logFrom) -match $announced }
    $script:inbox = (@(Read-Log | Select-Object -Skip $script:logFrom) -match $announced | Select-Object -Last 1) -replace '^.*inbox at ', ''
}

# Each client is told its number and tells the server once it has joined, so they start together,
# either may join first, and the second process is client 2. A minimized window never presents and
# every frame waited for it, so a client ran at a few frames a second; -nowindow gives it a window
# that is never shown, and it runs at full speed.
function Start-Client([int] $Number) {
    Start-Process -FilePath (Join-Path $SboxRoot 'sbox.exe') -WorkingDirectory $SboxRoot -PassThru -WindowStyle Hidden `
        -Environment @{ Path = $pathWithoutRenderDoc } -ArgumentList "+net_local_port $localPort +connect local +hexagon_dev 1 +hexagon_dev_client $Number -nowindow"
}

# Waits until a client has its pawn, or the server turns out never to have listened. A client sometimes
# drops its first connection and never tries again, so one that has exited, or has not joined in two
# and a half minutes, is started again.
function Wait-Client([int] $Number) {
    foreach ($try in 1..3) {
        $given = [DateTime]::UtcNow.AddSeconds(150)
        while ([DateTime]::UtcNow -lt $given) {
            if ([DateTime]::UtcNow -ge $script:deadline) { throw "Timed out waiting for client $Number to have a pawn." }
            if ($script:server.HasExited) { throw "The server exited with code $($script:server.ExitCode) while waiting for client $Number." }
            if ((Test-Unheard) -or (Send $Number 'who') -match 'local=True') { return }
            if (-not $clients[$Number - 1] -or $clients[$Number - 1].HasExited) { break }
            Start-Sleep -Milliseconds 250
        }
        if ($try -eq 3) { break }
        Write-Host "  -    client $Number has not joined; starting it again" -ForegroundColor Yellow
        if ($clients[$Number - 1] -and -not $clients[$Number - 1].HasExited) { Stop-Process -Id $clients[$Number - 1].Id -Force }
        $clients[$Number - 1] = Start-Client $Number
    }
    throw "Client $Number did not join, started three times."
}

# Waits until each client has its pawn, and the server has both.
function Wait-Joined {
    foreach ($number in 1, 2) { Wait-Client $number }
    Wait-Until 'both clients to have pawns' { ([regex]::Matches((Send 0 'who'), 'Player: ')).Count -ge 2 }
}

$script:deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$script:server = $null
$script:starts = 0
$clients = @($null, $null)
try {
    if ($reuse) {
        Write-Host "==> Starting on the server and clients the last run kept, in a fresh city (data folder '$dataRoot')" -ForegroundColor Cyan
        $script:server = Get-Process -Id $reuse.Server
        Open-Log $reuse.Log -FromEnd
        $script:logFrom = 0
        $script:inbox = $reuse.Inbox
        $script:sent = [int] $reuse.Sent
        foreach ($index in 0, 1) {
            $id = @($reuse.Clients)[$index]
            $process = if ($id) { Get-Process -Id $id -ErrorAction SilentlyContinue }
            if ($process -and $process.ProcessName -eq 'sbox') { $clients[$index] = $process }
        }
        Expect 'the kept server starts the city over' (Send 0 "fresh|$dataRoot") '^fresh$'
        Wait-Inbox $dataRoot 'the fresh city'
        # The city it left is no longer written to. What a passed run left is not needed; a failed run's is kept.
        if ($reuse.Passed) { Remove-RunFiles $reuse.Data $true }
        foreach ($number in 1, 2) { if (-not $clients[$number - 1]) { $clients[$number - 1] = Start-Client $number } }
        Wait-Joined
        Expect 'a game may allow an account more than one life' (Send 0 'policy|living|2') 'death=Permanent living=2 hours=24'
    }
    else {
        Write-Host "==> Starting the dedicated server (data folder '$dataRoot')" -ForegroundColor Cyan
        # A server Steam did not hear from in time is started again, with its clients.
        foreach ($attempt in 1..3) {
            Start-Server $dataRoot
            Expect 'a dedicated server has no player of its own' (Send 0 'who') 'local=False .*host=True .*pawns=\[\]'
            # Both clients are one Steam account, so this city lets that account live two lives at once.
            Expect 'a game may allow an account more than one life' (Send 0 'policy|living|2') 'death=Permanent living=2 hours=24'

            Write-Host '==> Starting two clients' -ForegroundColor Cyan
            $clients[0] = Start-Client 1
            $clients[1] = Start-Client 2
            foreach ($number in 1, 2) { Wait-Client $number }
            if (-not (Test-Unheard)) { break }
            if ($attempt -eq 3) { throw 'Steam did not hear from the server in time, three times running.' }
            Write-Host '==> Steam did not hear from the server in time, so it never listened; starting it and the clients again' -ForegroundColor Yellow
            foreach ($process in @($clients) + @($script:server)) { if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force; [void]$process.WaitForExit(10000) } }
            $clients[0] = $null
            $clients[1] = $null
        }
    }

    # tools/test.ps1 starts the editor play test when this line appears.
    Section 'Two characters, one city'
    # A train that came at its time of day in the middle of a run would bring goods nothing here expects.
    [void](Send 0 'console|hexagon_train "Supply train" hold')
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

    Section 'Perception'
    Expect 'the other player is visible, with the description an onlooker would see' { Send 1 'proxies' } "has=True .*seen='A woman in a grey coat"
    $seen = Send 1 'proxies'
    Expect 'her name and faction were never sent' $seen "name='' faction=''"
    Expect 'nor is the name anywhere in what the client holds' $seen 'Jane' -Not
    Expect 'what she wears, on the body she has, is there for anyone to see' $seen 'look=\[human02,hair_bobcut,flannel_shirt,cardigan_dark,jeans,black_boots\]'
    Expect 'each client knows its own name, and that she belongs to no faction' (Send 2 'state') "name='Jane Roe' faction='' "

    Section 'Speech reaches who is in range, and nobody else'
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

    Section 'A claimed position buys nothing'
    # goto is what a cheat does: the client simply declares itself somewhere else.
    [void](Send 2 'goto|250|-150|60')
    [void](Send 1 'say|Nobody is near me')
    # He hears himself once the host has handled it, and had she counted as near, she would have been sent it then.
    [void](Poll { Send 1 'state' } 'John Doe says "Nobody is near me"')
    Expect 'claiming to stand beside a speaker did not let her listen in' { Send 2 'state' } 'Nobody is near me' -Not
    Expect 'the journal agrees nobody heard' { Send 0 'journal|chat.say' } 'witnesses=0'
    Expect 'the host sent her back' { $state = Send 2 'state'; "$(Near $state $far) $state" } '^True '
    Expect 'and put the claim on record' { Send 0 'journal' } 'movement\.implausible!=[1-9]'

    Section 'Out of sight is out of the network'
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

    Section 'The world is shared'
    [void](Send 1 'door|Door 1|use')
    Expect 'a door one player opened is open for the other' { Send 2 'state' } 'Door 1:open=True'
    [void](Send 2 'door|Door 1|use')
    Expect 'and out of reach for a player far away' { Send 2 'state' } 'Door 1:open=True.*too far away'

    Section 'Radio reaches those tuned in, and nobody else'
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

    Section 'Handing over takes two'
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

    Section 'A paper is held up to read, not handed over'
    [void](Send 2 'show|Identification card')
    $state = Poll { Send 1 'state' } "paper='Identification card'"
    Expect 'she holds up her card and he reads it, held by the stranger he takes her for' $state "paper='Identification card' shownby='\[A woman in a grey coat[^']*' lines=\[\] fields=\[Name=Jane Roe\] stamps=\[Combine Civil Authority@0\] "
    Expect 'he is told who holds it up' $state 'A woman in a grey coat[^/]* holds up the identification card for you to read\.'
    Expect 'it stays in her hand' { Send 2 'state' } 'items=\[Identification card@0,0, .*You hold up the identification card for them to read\.'
    Expect 'on the record, with what was shown' { Send 0 'journal|paper.show' } "ok=True actor='Jane Roe' .*data=\[item=\w+ definition=items/cid_card\.item\]"
    Teleport 2 'Jane Roe' $apart
    Expect 'once they part, he can no longer read it' { Send 1 'state' } "paper='' shownby='' lines=\[\] "
    Teleport 2 'Jane Roe' $beside

    Section 'A map on a table is marked up by whoever stands at it'
    Teleport 1 'John Doe' (Spot 'Planning table' back 20 8)
    Teleport 2 'Jane Roe' (Spot 'Planning table' front 20 8)
    [void](Send 1 'open|Map case')
    [void](Poll { Send 1 'state' } 'open=\[Map case: City map, Grease pencilx2\]')
    [void](Send 1 'take|City map')
    [void](Send 1 'take|Grease pencil')
    [void](Send 1 'close')
    [void](Send 1 'face|Planning table|level')
    # Where he looks reaches the host with his movement, a moment after he turns, and the host lays
    # the map out where he looks.
    Start-Sleep -Milliseconds 500
    [void](Send 1 'layout|City map')
    Expect 'he lays a map out on the table, and she sees it lying there' { Send 2 'maps' } '^City map@4\d\d,1\d\d,62 leaning=False marks=\[\] counters=\[\]$'
    [void](Send 1 'lean')
    [void](Poll { Send 1 'state' } "leaning='City map'")
    [void](Send 1 'mapdraw|100,200;300,400;500,450')
    Expect 'he draws on it in grease pencil, and she sees the line' { Send 2 'maps' } 'marks=\[1:line3#B31E17\]'
    [void](Send 2 'lean')
    Expect 'she leans over it too, from across the table' { Send 2 'state' } "leaning='City map'"
    [void](Send 2 'mapmove|3|400|300')
    Expect 'she stands a counter on it, and he sees it' { Send 1 'maps' } 'counters=\[3@400,300\]'
    [void](Send 2 'mapword|100|100|Here')
    Expect 'with nothing to write with, she writes nothing on it' { Send 2 'state' } 'You have nothing to write with\.'
    Expect 'and her try is on record' { Send 0 'journal|map.write' } "ok=False actor='Jane Roe' .*reason=Denied"
    [void](Send 2 'mapwipe|1')
    Expect 'but anyone at the table can wipe grease pencil off' { Send 1 'maps' } 'marks=\[\] counters=\[3@400,300\]'
    Expect 'and whose hand wiped it is on record' { Send 0 'journal|map.wipe' } "ok=True actor='Jane Roe' .*mark=1"
    Teleport 2 'Jane Roe' $far
    Expect 'walking away from the table stands her up' { Send 2 'state' } "leaning='' "
    [void](Send 1 'standup')
    [void](Send 1 'open|Map case')
    [void](Send 1 'put|Grease pencil')
    Expect 'he puts the grease pencils back, and leaves the map out' { Send 1 'state' } "open=\[Map case: Grease pencilx2\] .*leaning='' "
    [void](Send 1 'close')

    Section 'A faction takes in whoever accepts a place, and a superior gives each rank'
    Teleport 1 'John Doe' $door
    Teleport 2 'Jane Roe' $beside
    Expect 'belonging to nothing, he can enlist nobody' (Send 1 'verbs') '^person\.introduce,(?!.*person\.(enlist|rank|discharge))'
    [void](Send 0 'console|hexagon_faction "John Doe" civil_protection')
    Expect 'staff appoint him head of Civil Protection' { Send 1 'state' } "faction='Civil Protection' rank='Divisional Leader' "
    Expect 'whose rank lets him enlist, promote and discharge' (Send 1 'verbs') 'person\.enlist,person\.rank,person\.discharge'
    Expect 'and nothing about him shows it' (Send 2 'proxies') "faction='' rank='' .*insignia=\[\]"
    [void](Send 1 'act|person.enlist')
    Expect 'she is offered a place, by the name she was given' { Send 2 'state' } "offer='John Doe offers you a place in Civil Protection, as a Recruit\.'"
    Expect 'and belongs to nothing yet' (Send 2 'state') "faction='' rank='' "
    [void](Send 2 'answer|1')
    Expect 'once she accepts, she belongs, at its lowest rank' { Send 2 'state' } "faction='Civil Protection' rank='Recruit' .*You join Civil Protection as a Recruit\."
    Expect 'he is told' { Send 1 'state' } 'They join Civil Protection as a Recruit\.'
    Expect 'a recruit can enlist, promote and discharge nobody' (Send 2 'verbs') 'person\.(enlist|rank|discharge)' -Not
    foreach ($band in 'recruit', 'unit') { [void](Send 0 "console|hexagon_give `"John Doe`" cp_armband_$band") }
    [void](Poll { Send 1 'state' } "Unit's armband@")
    [void](Send 1 "offer|Recruit's armband|1")
    [void](Poll { Send 2 'state' } "offer='John Doe offers you [^']*Recruit's armband")
    [void](Send 2 'answer|1')
    [void](Poll { Send 2 'state' } "Recruit's armband@")
    [void](Send 2 "wear|Recruit's armband")
    Expect 'she puts on the armband he handed her, and he sees it on her' { Send 1 'proxies' } "insignia=\[Recruit's armband\]"
    Expect 'but not her faction or rank, which were never sent' (Send 1 'proxies') "faction='' rank='' "
    [void](Send 1 'rank|Unit')
    Expect 'he promotes her' { Send 2 'state' } "rank='Unit' .*You are promoted to Unit\."
    Expect 'and is told what a unit wears' { Send 1 'state' } "You make them a Unit\. A Unit wears the unit's armband\."
    Expect 'her armband still says recruit: it says what she wears, not what she is' (Send 1 'proxies') "insignia=\[Recruit's armband\]"
    [void](Send 2 'rank|Recruit')
    Expect 'a unit cannot demote anyone' { Send 2 'state' } 'Your rank does not let you do that\.'
    Expect 'and her try is on record' { Send 0 'journal|verb.person.rank' } "ok=False actor='Jane Roe' .*reason=Denied"
    [void](Send 1 'rank|Divisional Leader')
    Expect 'nobody raises anyone to their own rank' { Send 1 'state' } 'You cannot give that rank\.'
    [void](Send 1 "offer|Unit's armband|1")
    [void](Poll { Send 2 'state' } "offer='John Doe offers you [^']*Unit's armband")
    [void](Send 2 'answer|1')
    [void](Poll { Send 2 'state' } "Unit's armband@")
    [void](Send 2 "wear|Recruit's armband")
    [void](Send 2 "wear|Unit's armband")
    Expect 'once she swaps armbands, she is seen as a unit' { Send 1 'proxies' } "insignia=\[Unit's armband\]"
    [void](Send 1 'act|person.discharge')
    Expect 'he discharges her' { Send 2 'state' } "faction='' rank='' .*You are discharged from Civil Protection\."
    Expect 'which takes nothing off her' (Send 1 'proxies') "insignia=\[Unit's armband\]"
    [void](Send 1 'act|person.enlist')
    [void](Poll { Send 2 'state' } "offer='John Doe offers you a place in Civil Protection")
    [void](Send 2 'answer|1')
    [void](Poll { Send 2 'state' } "rank='Recruit' ")
    [void](Send 2 'walkout')
    Expect 'enlisted again, she walks out on her own' { Send 2 'state' } "faction='' rank='' .*You walk out of Civil Protection\."
    $journal = Send 0 'journal'
    Expect 'every enlistment, promotion, discharge and walking out is on record' $journal '(?<!\S)faction\.join=2(?!\S).*(?<!\S)faction\.rank=1(?!\S).*(?<!\S)faction\.discharge=1(?!\S).*(?<!\S)faction\.leave=1(?!\S)'
    Expect 'with who did it, and from what rank to what' (Send 0 'journal|faction.rank') "ok=True actor='John Doe' .*data=\[faction=civil_protection from=Recruit to=Unit\]"
    # What she wore and what he kept are left on the floor, so neither carries them on.
    [void](Send 2 "wear|Unit's armband")
    foreach ($band in "Recruit's armband", "Unit's armband") { [void](Send 2 "drop|$band") }
    [void](Poll { Send 2 'state' } 'items=\[Identification card@0,0, Water@1,0, Tokenx30@2,0\] ')

    Section 'Violence is slow, visible and on the record'
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

    Section 'Searching is opening a holder'
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

    Section 'Wounds are treated by hand'
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

    Section 'Restraints'
    [void](Send 2 'act|person.restrain')
    Expect 'she has nothing to bind him with' { Send 2 'state' } 'nothing that lets you do that'
    [void](Send 1 'act|person.restrain')
    Expect 'his zip tie bound her, and was used up' { "$(Send 2 'state') $(Send 1 'state')" } 'restrained=True .*Your hands are bound.*items=\[(?![^\]]*Zip tie)'
    [void](Send 2 'drop|Water')
    Expect 'bound hands do nothing' { Send 2 'state' } 'items=\[Identification card@0,0, Water@.*cannot do that now'
    [void](Send 1 'act|person.release')
    Expect 'released' { Send 2 'state' } 'restrained=False'

    Section 'Death is a deliberate act, and it is final'
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

    Section 'The next life waits; staff can undo a death'
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

    Section 'Money is made by someone, from something'
    # The city's press stands locked; a forger keeps a crude one a little way off, with a die. He is put
    # back where he stands now afterwards, which is where the next section has him.
    $stood = if ((Send 1 'state') -match 'pos=(-?[\d.]+),(-?[\d.]+),(-?[\d.]+)') { [int[]] @([double] $Matches[1], [double] $Matches[2], [double] $Matches[3] + 8) }
    $press = Spot 'Token press' front 20
    Teleport 1 'John Doe' $press
    [void](Send 1 'work|Token press')
    Expect 'the city''s press is locked to whoever has no key' { Send 1 'state' } 'It is locked, and you have no key that fits\.'
    [void](Send 0 'console|hexagon_give "John Doe" keycard 1 city')
    [void](Poll { Send 1 'state' } 'Keycard#')
    [void](Send 1 'work|Token press')
    Expect 'with a key it works, but makes nothing from nothing' { Send 1 'state' } 'There is nothing here to make anything from\.'
    [void](Send 0 'console|hexagon_give "John Doe" token_blank 10')
    [void](Poll { Send 1 'state' } 'Token blankx10@')
    [void](Send 1 'open|Token press')
    [void](Send 1 'put|Token blank')
    Expect 'he loads it like a crate' { Send 1 'state' } 'open=\[Token press: Token blankx10\] '
    [void](Send 1 'close')
    [void](Send 1 'work|Token press')
    Expect 'and works it, holding Use, and sees what he is at' { Send 1 'state' } "working='Strike tokens'"
    [void](Send 1 'stopwork')
    Expect 'letting go stops it' { Send 1 'state' } "working='' "
    [void](Send 1 'work|Token press')
    [void](Poll { Send 1 'state' } "working='Strike tokens'")
    Teleport 1 'John Doe' (Spot 'Token press' front 200)
    Expect 'and so does walking away' { Send 1 'state' } "working='' .*You stop working: you walked away\."
    Teleport 1 'John Doe' $press
    [void](Send 1 'open|Token press')
    Expect 'either way, nothing was made, and nothing used up' { Send 1 'state' } 'open=\[Token press: Token blankx10\] '
    [void](Send 1 'close')
    [void](Send 1 'work|Token press')
    Expect 'kept at, it strikes ten tokens from ten blanks' (Poll { Send 1 'state' } 'Done: Token x10\.' 20) "working='' .*Done: Token x10\."
    [void](Send 1 'open|Token press')
    Expect 'and keeps them for whoever opens it' { Send 1 'state' } 'open=\[Token press: Tokenx10\] '
    [void](Send 1 'take|Token')
    Expect 'he takes them, onto the pile staff gave him' { Send 1 'state' } 'Tokenx60@1,1'
    [void](Send 1 'close')
    Teleport 2 'Jane Roe' (Spot "Forger's crate" front 20)
    [void](Send 2 "open|Forger's crate")
    Expect 'the forger keeps blanks, a die and a loupe, locked to nobody' { Send 2 'state' } "open=\[Forger's crate: Token blankx10, Token die, Loupe\] "
    foreach ($thing in 'Token blank', 'Token die', 'Loupe') { [void](Send 2 "take|$thing") }
    [void](Poll { Send 2 'state' } "open=\[Forger's crate: \] ")
    [void](Send 2 'close')
    [void](Send 2 'make|Token die')
    Expect 'a die strikes nothing unless it is in hand' { Send 2 'state' } 'Hold the token die to work with it\.'
    [void](Send 2 'equip|Token die')
    [void](Poll { Send 2 'state' } "held='Token die'")
    [void](Send 2 'make|Token die')
    Expect 'in hand, it strikes a token from a blank she carries' (Poll { Send 2 'state' } 'Done: Token x1\.' 15) "working='' .*Done: Token x1\."
    [void](Send 2 'make|Token die')
    [void](Poll { Send 2 'state' } "working='Strike a token'")
    Teleport 2 'Jane Roe' (Spot 'Crude press' back 20)
    Expect 'moving off stops a tool''s work' { Send 2 'state' } "working='' .*You stop working\."
    Expect 'and nothing more was made, or used up' (Send 2 'state') '^(?=.*Token blankx9@)(?=.*, Token@)'
    [void](Send 2 'open|Crude press')
    [void](Send 2 'put|Token blank')
    [void](Poll { Send 2 'state' } 'open=\[Crude press: Token blankx9\] ')
    [void](Send 2 'close')
    [void](Send 2 'work|Crude press')
    Expect 'a press anyone can work strikes five at a time' (Poll { Send 2 'state' } 'Done: Token x5\.' 20) "working='' .*Done: Token x5\."
    [void](Send 2 'open|Crude press')
    Expect 'from what it was loaded with' { Send 2 'state' } 'open=\[Crude press: Token blankx4, Tokenx5\] '
    [void](Send 2 'take|Token')
    Expect 'she takes them onto the token her die struck' { Send 2 'state' } ', Tokenx6@'
    [void](Send 2 'close')
    Expect 'what was made is on record, with what made it' (Send 0 'journal|item.issue') "actor='Jane Roe' .*data=\[source=made .*count=5 maker=machine:"
    Expect 'and what it was made from' (Send 0 'journal|item.destroy') "actor='Jane Roe' .*data=\[sink=made .*count=5 maker=machine:"
    [void](Send 2 'compare|Token')
    Expect 'under her loupe, she can tell what her die struck from what her press did' { Send 2 'state' } 'They were made 2 ways: 1 one way; 5 another\.'
    Teleport 2 'Jane Roe' (Spot 'Token press' left 20)
    [void](Send 2 'offer|Token|6')
    [void](Poll { Send 1 'state' } "offer='[^']*offers you 6 Token\.'")
    [void](Send 1 'answer|1')
    Expect 'he takes her tokens onto his pile' { Send 1 'state' } 'Tokenx66@1,1'
    Expect 'and nothing he was sent says how any of them was made' (Send 1 'marks') '^(?=.*Tokenx66:made=0,by=0)(?!.*by=[1-9])'
    [void](Send 1 'compare|Token')
    Expect 'to his bare eye, one token is like another' { Send 1 'state' } 'You have nothing to tell them apart with\.'
    [void](Send 0 'console|hexagon_give "John Doe" loupe')
    [void](Poll { Send 1 'state' } 'Loupe@')
    [void](Send 1 'compare|Token')
    Expect 'under a loupe he tells the four kinds apart, but not whose each is' { Send 1 'state' } 'They were made 4 ways: 50 one way; 10 another; 1 a third; 5 a fourth\.'
    Expect 'the locked press and the empty one were refused on the record' (Send 0 'journal') '(?<!\S)verb\.machine\.work!=2(?!\S)'

    Section 'A train brings goods in, and takes away what is loaded'
    Expect 'held by staff, the train is away' (Send 1 'trains') 'Supply train:phase=Away '
    [void](Send 0 'console|hexagon_train "Supply train" send')
    Expect 'sent, it rolls in to its station and stands there' (Poll { Send 1 'trains' } 'phase=Waiting at=400,-1050' 30) 'Supply train:phase=Waiting at=400,-1050'
    Expect 'its arrival is on record, with what it brought' (Send 0 'journal|train.arrive') 'data=\[train=Supply train brings=Ration x16, Water x16, Token blank x40\]'
    Teleport 1 'John Doe' (Spot 'Supply wagon' left 20)
    Teleport 2 'Jane Roe' (Spot 'Supply wagon' right 20)
    [void](Send 2 'open|Supply wagon')
    Expect 'its wagons are locked to whoever has no key' { Send 2 'state' } 'It is locked, and you have no key that fits\.'
    [void](Send 1 'open|Supply wagon')
    Expect 'with the city''s key he opens it: what it brought, and a manifest of it' { Send 1 'state' } 'open=\[Supply wagon: (Rationx4, ){4}(Waterx4, ){4}Token blankx40, Manifest\] '
    [void](Send 1 'take|Manifest')
    [void](Send 1 'take|Ration')
    [void](Send 1 'close')
    [void](Poll { Send 1 'state' } 'items=\[[^\]]*Manifest@[^\]]*Rationx4@')
    [void](Send 1 'read|Manifest')
    Expect 'the manifest says what came, under the railway''s stamp' { Send 1 'state' } "paper='Manifest' .*lines=\[Supply train, \d+ \w+, \d\d:\d\d / Ration x16 / Water x16 / Token blank x40\] fields=\[\] stamps=\[Combine Overwatch Logistics@4\] "
    Teleport 1 'John Doe' (Spot 'Outbound wagon' left 20)
    [void](Send 1 'open|Outbound wagon')
    [void](Send 1 'put|Ration')
    Expect 'he loads a ration pile on the outbound wagon' { Send 1 'state' } 'open=\[Outbound wagon: Rationx4\] '
    [void](Send 0 'console|hexagon_train "Supply train" away')
    Expect 'sent away, it shuts him out and rolls on' { Send 1 'state' } "open=\[: \] "
    Expect 'taking what was loaded, and what nobody unloaded, out of the world' (Poll { Send 0 'journal|train.depart' } 'took=' 10) 'data=\[train=Supply train took=Ration x16, Water x16, Token blank x40\]'
    Expect 'each of it on the record' (Send 0 'journal|item.destroy') 'data=\[sink=departed '
    Expect 'what he unloaded stays with him' (Send 1 'state') 'Manifest@'
    Expect 'it is gone from the line' (Poll { Send 1 'trains' } 'phase=Away' 30) 'Supply train:phase=Away '

    Section 'A site yields slowly to whoever works it'
    Teleport 2 'Jane Roe' (Spot 'Toolbox' front 20)
    [void](Send 2 'open|Toolbox')
    [void](Poll { Send 2 'state' } 'open=\[Toolbox: Crowbar\] ')
    [void](Send 2 'take|Crowbar')
    [void](Send 2 'close')
    Teleport 2 'Jane Roe' (Spot 'Scrap yard' front 20)
    [void](Send 2 'work|Scrap yard')
    Expect 'a scrap yard yields nothing to bare hands' { Send 2 'state' } 'You need the right tool in hand to work it\.'
    [void](Send 2 'equip|Crowbar')
    [void](Poll { Send 2 'state' } "held='Crowbar'")
    [void](Send 2 'work|Scrap yard')
    Expect 'with a crowbar in hand she salvages scrap' (Poll { Send 2 'state' } 'Done: Scrap metal x5\.' 15) "working='' .*Done: Scrap metal x5\."
    Expect 'which came from the site, on the record' (Send 0 'journal|item.issue') "actor='Jane Roe' .*data=\[source=site .*count=5[\] ]"
    [void](Send 2 'work|Scrap yard')
    [void](Poll { Send 2 'state' } 'Scrap metalx10@' 15)
    [void](Send 2 'work|Scrap yard')
    Expect 'until there is nothing left, which grows back in time' { Send 2 'state' } 'There is nothing left to take here\. It grows back in time\.'
    [void](Send 2 'open|Scrap cutter')
    [void](Send 2 'put|Scrap metal')
    [void](Poll { Send 2 'state' } 'open=\[Scrap cutter: Scrap metalx10\] ')
    [void](Send 2 'close')
    [void](Send 2 'work|Scrap cutter')
    Expect 'a cutter turns scrap into blanks for a press' (Poll { Send 2 'state' } 'Done: Token blank x5\.' 15) 'Done: Token blank x5\.'
    Teleport 1 'John Doe' $stood
    Wait-Landed 1

    Section 'Leaving does not end a scene'
    Teleport 2 'Jane Roe' $target
    [void](Send 0 'console|hexagon_give "John Doe" zip_tie')
    [void](Poll { Send 1 'state' } 'Zip tie@')
    [void](Poll { Send 1 'proxies' } 'down=False restrained=False .*has=True')
    [void](Send 1 'act|person.restrain')
    Expect 'he binds her again' { Send 2 'state' } 'restrained=True'
    # A client started again because its first connection dropped has left once already.
    $left = if ((Send 0 'journal') -match '(?<!\S)connection\.leave=(\d+)') { [int] $Matches[1] } else { 0 }
    Stop-Process -Id $clients[1].Id -Force
    Wait-Until 'the server to notice the disconnect' { (Send 0 'journal') -match "(?<!\S)connection\.leave=$($left + 1)(?!\S)" }
    Expect 'the player who left is gone from the other client' { Send 1 'proxies' } '^[^A-Za-z]*$'
    # What someone left behind looks like is sent once the network has stopped sending it to those too far away.
    Expect 'but the character she left bound is still standing there, and he is sent it' { Send 1 'sleepers' } '^-?\d+,-?\d+,bound,standing$'
    Expect 'he can free her' (Send 1 'act|person.release') '^sent person\.release$'
    Expect 'and then nobody is left there' { Send 1 'state' } 'sleepers=0 '
    Expect 'freeing someone whose player has gone is on record like any other' { Send 0 'journal|verb.person.release' } "ok=True actor='John Doe' .*target=Sleeper"
    Expect 'both joins and every entry are on record' { Send 0 'journal' } 'connection\.join=2 .*city\.enter=3'

    Section 'A copy taken while the server runs is a working backup'
    if ($Keep) { Write-Host '  -    left out with -Keep: it stops the server and the clients' -ForegroundColor DarkGray }
    else {
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
    }

    Test-Problems
}
catch {
    if ($_.Exception.Message -notin $untilReached, $stoppedAtFailure) {
        # A run that gave up waiting failed too, and keeps what explains it.
        $script:failures++
        throw
    }
}
finally {
    $passed = $script:failures -eq 0
    if ($Keep -and $script:server -and -not $script:server.HasExited) {
        # Kept as it stands for the next run with -Keep, which starts the city over on it.
        [pscustomobject] @{
            Server = $script:server.Id
            Clients = @($clients | ForEach-Object { if ($_ -and -not $_.HasExited) { $_.Id } else { 0 } })
            Port = $localPort; Log = $script:log; Inbox = $script:inbox; Sent = $script:sent
            # The city it plays is the one its inbox is in: a run that could not start it over left the last.
            Data = if ($script:inbox) { Split-Path -Leaf (Split-Path -Parent $script:inbox) } else { $dataRoot }; Passed = $passed; Manifest = $manifest; Fingerprint = $fingerprint
        } | ConvertTo-Json | Set-Content -LiteralPath $keptFile
        Write-Host 'The server and clients are left running for the next run with -Keep; -Close closes them.' -ForegroundColor DarkGray
        if (-not $passed) { Write-Host "Data is in $(Split-Path -Parent $script:inbox); the server's log is $($script:log)" -ForegroundColor DarkGray }
    }
    else {
        foreach ($client in $clients) { if ($client -and -not $client.HasExited) { Stop-Process -Id $client.Id -Force } }
        if ($script:server -and -not $script:server.HasExited) { Stop-Process -Id $script:server.Id -Force }
        # Their files are let go once they have exited.
        foreach ($process in @($clients) + @($script:server)) { if ($process) { [void]$process.WaitForExit(10000) } }
        foreach ($reader in $script:readers) { $reader.Dispose() }
        $script:readers = @()
        Remove-RunFiles $dataRoot $passed $script:logs
    }
    foreach ($reader in $script:readers) { $reader.Dispose() }
}

if ($script:failures -gt 0) { throw "$($script:failures) play-test expectation(s) failed." }
Write-Host "Two-client play test passed on a dedicated server$(if ($Until) { ", through '$Until'" })." -ForegroundColor Green
