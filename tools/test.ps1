# Runs every check there is: verify.ps1, then the two-client play test, and the editor play test
# beside it once the two clients have joined. Each play test's lines are shown as they come, marked
# with the test that wrote them. Steam must be running.
[CmdletBinding()]
param(
    # A game that uses Hexagon. Defaults to the sibling HL2RP checkout.
    [string] $GameRoot,
    [string] $SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# A server and clients the two-client test kept for its next run are its own, and everything here starts afresh.
& (Join-Path $PSScriptRoot 'playtest-server.ps1') -Close
if (@(Get-Process -Name 'sbox', 'sbox-server', 'sbox-dev' -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited }).Count -gt 0) { throw 'Close s&box, its editor and any dedicated server first.' }

$clock = [System.Diagnostics.Stopwatch]::StartNew()
$passOn = @{ SboxRoot = $SboxRoot }
if ($GameRoot) { $passOn.GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path }

& (Join-Path $PSScriptRoot 'verify.ps1') @passOn
$verified = $clock.Elapsed

# The play tests share only the machine: each has its own data folder and stops only what it started,
# and the dedicated server listens on a port of its own, away from the editor's. Starting s&box is
# what the machine finds hardest, and four at once took three times as long as one, so the editor is
# started only once the server and both clients are up.
$server = [pscustomobject] @{ Label = 'server'; Script = 'playtest-server.ps1'; Title = 'Two-client play test' }
$editor = [pscustomobject] @{ Label = 'editor'; Script = 'playtest.ps1'; Title = 'Editor play test' }
$tests = $server, $editor
foreach ($test in $tests) {
    $test | Add-Member -NotePropertyMembers @{ Job = $null; Started = $null; Took = $null; Problem = $null; Shown = 0; Errors = 0; Passed = 0; Failed = 0 }
}

function Start-Test($Test) {
    $Test.Started = $clock.Elapsed
    $Test.Job = Start-Job -Name $Test.Label -ScriptBlock { param($Script, $Arguments) & $Script @Arguments } `
        -ArgumentList (Join-Path $PSScriptRoot $Test.Script), $passOn
}

# Shows what a test has written since last time, marked with its name, in its own colours. The job's
# streams are read directly: receiving them would also echo each line unmarked.
function Show-New($Test) {
    $job = $Test.Job.ChildJobs[0]
    while ($Test.Shown -lt $job.Information.Count) {
        $message = $job.Information[$Test.Shown++].MessageData
        $text = if ($message.PSObject.Properties['Message']) { $message.Message } else { "$message" }
        $colour = @{}
        if ($message.PSObject.Properties['ForegroundColor'] -and $null -ne $message.ForegroundColor) { $colour.ForegroundColor = "$($message.ForegroundColor)" }
        if ($text -match '^\s+ok\s') { $Test.Passed++ } elseif ($text -match '^\s+FAIL\s') { $Test.Failed++ }
        # The two-client test's first section after both clients have joined.
        if ($Test.Label -eq 'server' -and $text -match '^==> Two characters, one city') { $script:joined = $true }
        Write-Host "[$($Test.Label)] " -NoNewline -ForegroundColor DarkGray
        Write-Host $text @colour
    }
    while ($Test.Errors -lt $job.Error.Count) { Write-Host "[$($Test.Label)] $($job.Error[$Test.Errors++])" -ForegroundColor Red }
}

$script:joined = $false
Start-Test $server
try {
    while ($tests | Where-Object { $null -eq $_.Took }) {
        foreach ($test in $tests | Where-Object { $_.Job -and $null -eq $_.Took }) {
            # Read after the state, so that nothing a finished test wrote is missed.
            $finished = $test.Job.State -notin 'NotStarted', 'Running'
            Show-New $test
            if (-not $finished) { continue }
            $test.Took = $clock.Elapsed - $test.Started
            # What stopped a test is its job's reason, not a line it wrote.
            if ($test.Job.State -ne 'Completed') {
                $reason = $test.Job.ChildJobs[0].JobStateInfo.Reason
                $test.Problem = if ($reason) { $reason.Message } else { "the job ended $($test.Job.State)" }
                Write-Host "[$($test.Label)] $($test.Problem)" -ForegroundColor Red
            }
        }
        if (-not $editor.Job -and ($script:joined -or $null -ne $server.Took)) { Start-Test $editor }
        Start-Sleep -Milliseconds 250
    }
}
finally {
    # Stopping a test still runs its clean-up, which stops the s&box processes it started.
    foreach ($test in $tests | Where-Object { $_.Job }) {
        if ($test.Job.State -in 'NotStarted', 'Running') { Stop-Job -Job $test.Job }
        Remove-Job -Job $test.Job -Force
    }
}

Write-Host ''
Write-Host ('verify.ps1 passed in {0:m\:ss}.' -f $verified) -ForegroundColor Green
foreach ($test in $tests) {
    $outcome = if ($test.Problem) { 'failed' } else { 'passed' }
    Write-Host ('{0} {1}: {2} checks passed, {3} failed; started at {4:m\:ss}, took {5:m\:ss}.' -f $test.Title, $outcome, $test.Passed, $test.Failed, $test.Started, $test.Took) `
        -ForegroundColor $(if ($test.Problem) { 'Red' } else { 'Green' })
}
$failed = @($tests | Where-Object { $_.Problem })
if ($failed) { throw "$(($failed | ForEach-Object { $_.Title }) -join ' and ') failed." }
Write-Host ('Everything passed in {0:m\:ss}.' -f $clock.Elapsed) -ForegroundColor Green
