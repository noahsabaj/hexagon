[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'release-inputs.ps1')

$publisher = Join-Path $PSScriptRoot 'publish-release-evidence.ps1'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'hexagon-release-publisher-test-' + [Guid]::NewGuid().ToString('N'))
$hexagonRoot = Join-Path $tempRoot 'hexagon'
$schemaRoot = Join-Path $tempRoot 'hl2rp-hexagon'
$evidenceRoot = Join-Path $tempRoot 'evidence'
$evidencePath = Join-Path $evidenceRoot 'remote-acceptance.json'
$artifactPath = Join-Path $evidenceRoot 'fixture-artifact.log'
$fakeGh = Join-Path $tempRoot 'fake-gh.ps1'
$fakeVerify = Join-Path $tempRoot 'fake-verify.ps1'
$statePath = Join-Path $tempRoot 'github-state.json'

function Invoke-GitFixture {
    param([Parameter(Mandatory)][string] $Root, [Parameter(Mandatory)][string[]] $Arguments)
    [void](Invoke-ReleaseInputGit -Root $Root -Arguments $Arguments)
}

function Save-Evidence {
    param([Parameter(Mandatory)][Guid] $RunId, [Parameter(Mandatory)][string] $Fingerprint)
    [ordered]@{
        format = 'hexagon-v2-manual-remote-acceptance/4'
        run_id = $RunId.ToString('D')
        hexagon_sha = $script:hexagonSha
        hl2rp_sha = $script:schemaSha
        source_fingerprint = $Fingerprint
        artifacts = @([ordered]@{
            id = 'fixture-artifact'
            path = 'fixture-artifact.log'
            sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $evidencePath -Encoding utf8
}

function Invoke-Publisher {
    & $publisher `
        -HexagonRoot $hexagonRoot `
        -SchemaRoot $schemaRoot `
        -RemoteAcceptanceEvidence $evidencePath `
        -HexagonSha $script:hexagonSha `
        -HL2RPSha $script:schemaSha `
        -GitHubCommand $fakeGh `
        -VerificationScript $fakeVerify `
        -Confirm:$false
}

function Assert-FailsLike {
    param([Parameter(Mandatory)][string] $Expected)
    try { Invoke-Publisher }
    catch {
        if ($_.Exception.Message -notlike "*$Expected*") {
            throw "Expected publisher failure containing '$Expected', got: $($_.Exception.Message)"
        }
        return
    }
    throw "Expected publisher failure containing '$Expected', but publication passed."
}

function Read-State {
    return Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
}

function Write-State {
    param([Parameter(Mandatory)][object] $State)
    $State | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $statePath -Encoding utf8
}

try {
    [void](New-Item -ItemType Directory -Path $hexagonRoot, $schemaRoot, $evidenceRoot)
    'publisher deterministic artifact fixture' |
        Set-Content -LiteralPath $artifactPath -Encoding utf8
    foreach ($repository in @($hexagonRoot, $schemaRoot)) {
        Invoke-GitFixture -Root $repository -Arguments @('init', '-q')
        Invoke-GitFixture -Root $repository -Arguments @('config', 'user.name', 'Publisher Test')
        Invoke-GitFixture -Root $repository -Arguments @('config', 'user.email', 'publisher@example.invalid')
        Invoke-GitFixture -Root $repository -Arguments @('config', 'commit.gpgsign', 'false')
    }
    'hexagon source' | Set-Content -LiteralPath (Join-Path $hexagonRoot 'source.txt') -Encoding utf8
    Invoke-GitFixture -Root $hexagonRoot -Arguments @('add', 'source.txt')
    Invoke-GitFixture -Root $hexagonRoot -Arguments @('commit', '-q', '-m', 'hexagon fixture')
    $script:hexagonSha = [string]@(Invoke-ReleaseInputGit -Root $hexagonRoot -Arguments @('rev-parse', 'HEAD'))[0]

    [ordered]@{ repository = 'noahsabaj/hexagon'; commit = $script:hexagonSha } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $schemaRoot 'hexagon.lock.json') -Encoding utf8
    'hl2rp source' | Set-Content -LiteralPath (Join-Path $schemaRoot 'source.txt') -Encoding utf8
    Invoke-GitFixture -Root $schemaRoot -Arguments @('add', 'hexagon.lock.json', 'source.txt')
    Invoke-GitFixture -Root $schemaRoot -Arguments @('commit', '-q', '-m', 'schema fixture')
    $script:schemaSha = [string]@(Invoke-ReleaseInputGit -Root $schemaRoot -Arguments @('rev-parse', 'HEAD'))[0]

    @'
param(
    [Parameter(Mandatory)][string] $SchemaRoot,
    [Parameter(Mandatory)][string] $RemoteAcceptanceEvidence,
    [Parameter(Mandatory)][string] $HexagonSha,
    [Parameter(Mandatory)][string] $HL2RPSha
)
if ($env:HEXAGON_FAKE_VERIFY_FAIL -ceq '1') { throw 'Injected verification failure.' }
if ($env:HEXAGON_FAKE_VERIFY_DIRTY -ceq '1') {
    Add-Content -LiteralPath (Join-Path $SchemaRoot 'source.txt') -Value 'verifier-side-effect'
}
'@ | Set-Content -LiteralPath $fakeVerify -Encoding utf8

    @'
param([Parameter(Position=0, ValueFromRemainingArguments=$true)][string[]] $CommandArguments)
$ErrorActionPreference = 'Stop'
$statePath = $env:HEXAGON_FAKE_GH_STATE
$state = if (Test-Path -LiteralPath $statePath) {
    Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
}
else {
    [pscustomobject]@{ commands = @(); statuses = @(); releases = @() }
}
$state.commands = @($state.commands) + ,($CommandArguments -join [char]31)
function Save-State { $state | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $statePath -Encoding utf8 }
function Get-Field([string] $Prefix) {
    $match = @($CommandArguments | Where-Object { $_ -like "$Prefix*" } | Select-Object -Last 1)
    if ($match.Count -eq 0) { return '' }
    return [string]$match[0].Substring($Prefix.Length)
}
if ($CommandArguments[0] -ceq 'auth') { Save-State; exit 0 }
if ($CommandArguments[0] -ceq 'api') {
    $endpoint = @($CommandArguments | Where-Object { $_ -like 'repos/*' } | Select-Object -Last 1)[0]
    if ($endpoint -like '*/git/ref/tags/*') {
        $tag = $endpoint.Substring($endpoint.LastIndexOf('/') + 1)
        $release = @($state.releases | Where-Object { $_.tag_name -ceq $tag })
        Save-State
        if ($release.Count -ne 1) { [Console]::Error.WriteLine('tag not found'); exit 1 }
        [pscustomobject]@{
            object = [pscustomobject]@{
                type = 'commit'
                sha = [string]$release[0].tag_commit
            }
        } | ConvertTo-Json -Depth 5
        exit 0
    }
    if ($endpoint -like '*/immutable-releases') {
        Save-State
        '{"enabled":true}'
        exit 0
    }
    if ($endpoint -like '*/statuses/*') {
        $status = [pscustomobject]@{
            endpoint = $endpoint
            state = Get-Field 'state='
            context = Get-Field 'context='
            description = Get-Field 'description='
            target_url = Get-Field 'target_url='
        }
        $state.statuses = @($state.statuses) + $status
        Save-State
        if ($status.state -ceq 'pending' -and
            -not [string]::IsNullOrWhiteSpace($env:HEXAGON_FAKE_GH_FAIL_PENDING_REPOSITORY) -and
            $endpoint -like "repos/$($env:HEXAGON_FAKE_GH_FAIL_PENDING_REPOSITORY)/statuses/*") {
            [Console]::Error.WriteLine('Injected pending-status publication failure.')
            exit 1
        }
        if ($status.state -ceq 'success' -and
            -not [string]::IsNullOrWhiteSpace($env:HEXAGON_FAKE_GH_FAIL_SUCCESS_REPOSITORY) -and
            $endpoint -like "repos/$($env:HEXAGON_FAKE_GH_FAIL_SUCCESS_REPOSITORY)/statuses/*") {
            [Console]::Error.WriteLine('Injected status publication failure.')
            exit 1
        }
        '{}'
        exit 0
    }
    if ($endpoint -like '*/releases/tags/*') {
        $tag = $endpoint.Substring($endpoint.LastIndexOf('/') + 1)
        $release = @($state.releases | Where-Object { $_.tag_name -ceq $tag })
        Save-State
        if ($release.Count -ne 1) { [Console]::Error.WriteLine('release not found'); exit 1 }
        $release[0] | ConvertTo-Json -Depth 10
        exit 0
    }
}
if ($CommandArguments[0] -ceq 'release' -and $CommandArguments[1] -ceq 'create') {
    $tag = $CommandArguments[2]
    $assetPath = $CommandArguments[3]
    $targetIndex = [Array]::IndexOf($CommandArguments, '--target')
    $notesIndex = [Array]::IndexOf($CommandArguments, '--notes')
    $release = [pscustomobject]@{
        tag_name = $tag
        immutable = $false
        draft = $true
        prerelease = $true
        target_commitish = $CommandArguments[$targetIndex + 1]
        tag_commit = $CommandArguments[$targetIndex + 1]
        body = $CommandArguments[$notesIndex + 1]
        html_url = "https://example.invalid/releases/$tag"
        assets = @([pscustomobject]@{
            name = [IO.Path]::GetFileName($assetPath)
            digest = "sha256:$((Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant())"
            size = (Get-Item -LiteralPath $assetPath).Length
        })
    }
    $state.releases = @($state.releases) + $release
    Save-State
    exit 0
}
if ($CommandArguments[0] -ceq 'release' -and $CommandArguments[1] -ceq 'edit') {
    $tag = $CommandArguments[2]
    $release = @($state.releases | Where-Object { $_.tag_name -ceq $tag })[0]
    $release.draft = $false
    $release.immutable = $true
    Save-State
    exit 0
}
Save-State
[Console]::Error.WriteLine("Unsupported fake gh command: $($CommandArguments -join ' ')")
exit 1
'@ | Set-Content -LiteralPath $fakeGh -Encoding utf8

    $env:HEXAGON_FAKE_GH_STATE = $statePath
    $fingerprint = (Get-EffectiveReleaseInputs -Repositories @(
        [pscustomobject]@{ Label = 'hexagon'; Root = $hexagonRoot },
        [pscustomobject]@{ Label = 'hl2rp-hexagon'; Root = $schemaRoot }) -RequireClean).Fingerprint

    $firstRun = [Guid]::NewGuid()
    Save-Evidence -RunId $firstRun -Fingerprint $fingerprint
    Invoke-Publisher
    $state = Read-State
    if (($state.statuses.state -join ',') -cne 'pending,pending,success,success' -or
        @($state.releases).Count -ne 1 -or -not $state.releases[0].immutable) {
        throw 'Successful publisher flow did not produce paired pending/success statuses and one immutable release.'
    }
    $successStatuses = @($state.statuses | Where-Object { $_.state -ceq 'success' })
    $expectedSuccessDescription = "Evidence $($firstRun.ToString('D')) $($state.releases[0].assets[0].digest)"
    if ($successStatuses.Count -ne 2 -or
        @($successStatuses | Where-Object {
            $_.description -cne $expectedSuccessDescription -or
            $_.target_url -cne [string]$state.releases[0].html_url
        }).Count -ne 0) {
        throw 'Successful statuses did not carry the exact full bundle digest and shared release URL.'
    }

    Invoke-Publisher
    $state = Read-State
    if (@($state.releases).Count -ne 1 -or
        @($state.commands | Where-Object { $_ -like "release$([char]31)create*" }).Count -ne 1) {
        throw 'Idempotent publication recreated or changed the existing immutable release.'
    }

    $correctDigest = [string]$state.releases[0].assets[0].digest
    $state.releases[0].assets[0].digest = "sha256:$('0' * 64)"
    Write-State -State $state
    Assert-FailsLike -Expected 'not the exact immutable evidence release'
    $state = Read-State
    if (($state.statuses | Select-Object -Last 2).state -join ',' -cne 'failure,failure') {
        throw 'An existing release with the wrong asset digest was not failure-compensated.'
    }
    $state.releases[0].assets[0].digest = $correctDigest
    Write-State -State $state

    $correctTagCommit = [string]$state.releases[0].tag_commit
    $state.releases[0].tag_commit = ('f' * 40)
    Write-State -State $state
    Assert-FailsLike -Expected 'does not resolve to the exact HL2RP source commit'
    $state = Read-State
    if (($state.statuses | Select-Object -Last 2).state -join ',' -cne 'failure,failure') {
        throw 'An existing release with the wrong tag commit was not failure-compensated.'
    }
    $state.releases[0].tag_commit = $correctTagCommit
    Write-State -State $state

    $env:HEXAGON_FAKE_GH_FAIL_PENDING_REPOSITORY = 'noahsabaj/hl2rp-hexagon'
    Save-Evidence -RunId ([Guid]::NewGuid()) -Fingerprint $fingerprint
    Assert-FailsLike -Expected 'GitHub command failed'
    $state = Read-State
    if (($state.statuses | Select-Object -Last 2).state -join ',' -cne 'failure,failure') {
        throw 'Partial pending publication did not compensate both repositories with failure statuses.'
    }
    Remove-Item Env:HEXAGON_FAKE_GH_FAIL_PENDING_REPOSITORY

    $env:HEXAGON_FAKE_VERIFY_FAIL = '1'
    Save-Evidence -RunId ([Guid]::NewGuid()) -Fingerprint $fingerprint
    Assert-FailsLike -Expected 'Injected verification failure'
    $state = Read-State
    if (($state.statuses | Select-Object -Last 2).state -join ',' -cne 'failure,failure') {
        throw 'Verification failure did not compensate both repositories with failure statuses.'
    }
    Remove-Item Env:HEXAGON_FAKE_VERIFY_FAIL

    $env:HEXAGON_FAKE_VERIFY_DIRTY = '1'
    Save-Evidence -RunId ([Guid]::NewGuid()) -Fingerprint $fingerprint
    Assert-FailsLike -Expected 'require a clean checkout'
    $state = Read-State
    if (($state.statuses | Select-Object -Last 2).state -join ',' -cne 'failure,failure' -or
        @($state.releases).Count -ne 1) {
        throw 'A verifier-created dirty worktree was not rejected and failure-compensated before release creation.'
    }
    Remove-Item Env:HEXAGON_FAKE_VERIFY_DIRTY
    Invoke-GitFixture -Root $schemaRoot -Arguments @('restore', '--worktree', '--', 'source.txt')

    $env:HEXAGON_FAKE_GH_FAIL_SUCCESS_REPOSITORY = 'noahsabaj/hl2rp-hexagon'
    Save-Evidence -RunId ([Guid]::NewGuid()) -Fingerprint $fingerprint
    Assert-FailsLike -Expected 'GitHub command failed'
    $state = Read-State
    if (($state.statuses | Select-Object -Last 2).state -join ',' -cne 'failure,failure') {
        throw 'Partial terminal publication did not compensate both repositories with failure statuses.'
    }
    Remove-Item Env:HEXAGON_FAKE_GH_FAIL_SUCCESS_REPOSITORY

    Write-Host 'Release-publisher contract tests passed.' -ForegroundColor Green
}
finally {
    Remove-Item Env:HEXAGON_FAKE_GH_STATE -ErrorAction SilentlyContinue
    Remove-Item Env:HEXAGON_FAKE_GH_FAIL_PENDING_REPOSITORY -ErrorAction SilentlyContinue
    Remove-Item Env:HEXAGON_FAKE_VERIFY_FAIL -ErrorAction SilentlyContinue
    Remove-Item Env:HEXAGON_FAKE_VERIFY_DIRTY -ErrorAction SilentlyContinue
    Remove-Item Env:HEXAGON_FAKE_GH_FAIL_SUCCESS_REPOSITORY -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $tempRoot) {
        $resolved = (Resolve-Path -LiteralPath $tempRoot).Path
        $base = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($base, [System.StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($resolved) -cnotmatch '^hexagon-release-publisher-test-[a-f0-9]{32}$') {
            throw "Refusing to remove unexpected publisher-test path '$resolved'."
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
