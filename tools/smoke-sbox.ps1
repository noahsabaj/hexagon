[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SchemaManifest,

    [Parameter()]
    [string] $SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox',

    [Parameter()]
    [ValidateRange(15, 300)]
    [int] $TimeoutSeconds = 120,

    [Parameter()]
    [string] $DataRoot = ("hexagon-verification/" + [Guid]::NewGuid().ToString('N'))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$manifestPath = (Resolve-Path -LiteralPath $SchemaManifest).Path
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$sboxDev = Join-Path $SboxRoot 'sbox-dev.exe'
$logPath = Join-Path $SboxRoot 'logs\sbox-dev.log'
$mcpEndpoint = 'http://127.0.0.1:7269/mcp'

if (-not (Test-Path -LiteralPath $sboxDev -PathType Leaf)) {
    throw "sbox-dev.exe was not found at '$sboxDev'."
}
if (@(Get-Process -Name 'sbox-dev' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Close existing sbox-dev editor processes before running the isolated smoke gate.'
}

# The editor process can exit before HTTP.sys releases its MCP listener. Starting
# the smoke editor during that narrow window connects the probe to a stale editor
# and prevents the new package from hotloading. Require both process and endpoint
# ownership to be gone before launching the isolated run.
$listenerDeadline = [DateTime]::UtcNow.AddSeconds([Math]::Min(30, [Math]::Max(5, $TimeoutSeconds / 3)))
do {
    $activeListeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    $mcpListener = $activeListeners |
        Where-Object { $_.Port -eq 7269 } |
        Select-Object -First 1
    if ($null -eq $mcpListener) { break }
    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $listenerDeadline)
if ($null -ne $mcpListener) {
    throw 'MCP port 7269 remained owned after the previous editor exited; refusing to connect the smoke probe to a stale editor.'
}
if ($DataRoot -notmatch '^[a-z0-9][a-z0-9._/-]*$' -or
    $DataRoot.Split('/') -contains '..') {
    throw "Verification data root '$DataRoot' is not a normalized relative path."
}

function Read-LogDelta {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][long] $Offset
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{ Text = ''; Offset = 0L }
    }

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite)
    try {
        if ($stream.Length -lt $Offset) {
            $Offset = 0
        }
        [void]$stream.Seek($Offset, [System.IO.SeekOrigin]::Begin)
        $reader = [System.IO.StreamReader]::new($stream)
        try {
            $text = $reader.ReadToEnd()
            return [pscustomobject]@{ Text = $text; Offset = $stream.Position }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-ProbeRun {
    param(
        [Parameter(Mandatory)][ValidateSet('commit', 'recover')][string] $Probe,
        [Parameter(Mandatory)][string] $ExpectedSentinel
    )

    $logOffset = if (Test-Path -LiteralPath $logPath -PathType Leaf) {
        (Get-Item -LiteralPath $logPath).Length
    }
    else {
        0L
    }
    $captured = [System.Text.StringBuilder]::new()
    $fatalPattern = '(?i)Whitelist violation|Compile of .* Failed|Resource Compile Failed|ERROR recompiling|Invalid full path|problem opening the StartupScene|Broken Reference:|Error opening (stylesheet|resource).*\b(hexagon|hl2rp)|Error when trying to network (serialize|deserialize) object|\]\s+(Error|Fatal)\s*(\||:)|\b[A-Za-z0-9_.]+Exception:\s|HEXAGON_(HOST|CLIENT)_FAILED|HEXAGON_SNAPSHOT_WIRE_(ENCODE|DECODE)_FAILED|HEXAGON_DRAIN_(FAILED|DEGRADED)|HL2RP_PROBE_FAILED'
    $escapedManifest = $manifestPath.Replace('"', '\"')
    $arguments = "-project `"$escapedManifest`" +hexagon-data-root `"$DataRoot`" +hexagon-verification-probe `"$Probe`""

    Write-Host "==> Running s&box v2 readiness and '$Probe' persistence probe" -ForegroundColor Cyan
    $editor = Start-Process `
        -FilePath $sboxDev `
        -ArgumentList $arguments `
        -WorkingDirectory $SboxRoot `
        -PassThru `
        -WindowStyle Hidden

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $hostReady = $false
    $clientReady = $false
    $clientSessionReady = $false
    $drained = $false
    $probeMatch = $null

    try {
        $http = [System.Net.Http.HttpClient]::new()
		# Tool discovery can legitimately pause while the editor imports assets or
		# hotloads the freshly generated assemblies. Keep each request bounded, but
		# give it a share of the probe's overall deadline instead of failing the
		# entire smoke gate at a fixed five-second transport timeout.
		$requestTimeoutSeconds = [Math]::Min( 30, [Math]::Max( 5, [int]($TimeoutSeconds / 6) ) )
		$http.Timeout = [TimeSpan]::FromSeconds( $requestTimeoutSeconds )
        $http.DefaultRequestHeaders.Accept.ParseAdd('application/json, text/event-stream')
        try {
            $initializeBody = [ordered]@{
                jsonrpc = '2.0'
                id = 1
                method = 'initialize'
                params = [ordered]@{
                    protocolVersion = '2025-03-26'
                    capabilities = [ordered]@{}
                    clientInfo = [ordered]@{ name = 'hexagon-smoke'; version = '2' }
                }
            } | ConvertTo-Json -Depth 8 -Compress
            $initialized = $null
            while ([DateTime]::UtcNow -lt $deadline -and $null -eq $initialized) {
                if ($editor.HasExited) {
                    throw "s&box exited with code $($editor.ExitCode) before its editor control endpoint became ready."
                }
                try {
                    $content = [System.Net.Http.StringContent]::new(
                        $initializeBody,
                        [System.Text.Encoding]::UTF8,
                        'application/json')
                    try {
                        $response = $http.PostAsync($mcpEndpoint, $content).GetAwaiter().GetResult()
                        $payload = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        if (-not $response.IsSuccessStatusCode) {
                            throw "Editor control initialize returned HTTP $([int]$response.StatusCode): $payload"
                        }
                        $initialized = $payload | ConvertFrom-Json
                    }
                    finally {
                        $content.Dispose()
                    }
                }
                catch {
                    Start-Sleep -Milliseconds 250
                }
            }
            if ($null -eq $initialized -or $initialized.result.protocolVersion -ne '2025-03-26') {
                throw 'Timed out waiting for the s&box editor control protocol.'
            }

            $searchBody = [ordered]@{
				jsonrpc = '2.0'
				id = 2
				method = 'tools/call'
				params = [ordered]@{
					name = 'search_tools'
					arguments = [ordered]@{ query = 'play' }
				}
			} | ConvertTo-Json -Depth 8 -Compress
			$searchPayload = ''
			while ([DateTime]::UtcNow -lt $deadline -and $searchPayload -notmatch 'play_start') {
				if ($editor.HasExited) {
					throw "s&box exited with code $($editor.ExitCode) before its play tools became ready."
				}
				$searchContent = [System.Net.Http.StringContent]::new(
					$searchBody,
					[System.Text.Encoding]::UTF8,
					'application/json')
				try {
					$searchResponse = $http.PostAsync($mcpEndpoint, $searchContent).GetAwaiter().GetResult()
					$searchPayload = $searchResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
					if (-not $searchResponse.IsSuccessStatusCode) { $searchPayload = '' }
				}
				catch {
					$searchPayload = ''
				}
				finally {
					$searchContent.Dispose()
				}
				if ($searchPayload -notmatch 'play_start') { Start-Sleep -Milliseconds 250 }
			}
			if ($searchPayload -notmatch 'play_start') {
				throw "Timed out waiting for the editor play_start tool: $searchPayload"
			}

			$componentSearchBody = [ordered]@{
				jsonrpc = '2.0'
				id = 3
				method = 'tools/call'
				params = [ordered]@{
					name = 'search_tools'
					arguments = [ordered]@{ query = 'component type documentation' }
				}
			} | ConvertTo-Json -Depth 8 -Compress
			$componentSearchContent = [System.Net.Http.StringContent]::new(
				$componentSearchBody,
				[System.Text.Encoding]::UTF8,
				'application/json')
			try {
				$componentSearchResponse = $http.PostAsync($mcpEndpoint, $componentSearchContent).GetAwaiter().GetResult()
				$componentSearchPayload = $componentSearchResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
				if (-not $componentSearchResponse.IsSuccessStatusCode -or $componentSearchPayload -notmatch 'get_component_type') {
					throw "Editor tool discovery did not expose get_component_type: $componentSearchPayload"
				}
			}
			finally {
				$componentSearchContent.Dispose()
			}

			$typeBody = [ordered]@{
					jsonrpc = '2.0'
					id = 4
					method = 'tools/call'
					params = [ordered]@{
						name = 'call_tool'
						arguments = [ordered]@{
							name = 'get_component_type'
							arguments = [ordered]@{
								name = 'HexagonBootstrapComponent'
							}
						}
					}
				} | ConvertTo-Json -Depth 12 -Compress
			$typePayload = ''
			$typeReady = $false
			while ([DateTime]::UtcNow -lt $deadline -and -not $typeReady) {
				if ($editor.HasExited) {
					throw "s&box exited with code $($editor.ExitCode) before Hexagon's component types hotloaded."
				}
				$typeContent = [System.Net.Http.StringContent]::new(
					$typeBody,
					[System.Text.Encoding]::UTF8,
					'application/json')
				try {
					$typeResponse = $http.PostAsync($mcpEndpoint, $typeContent).GetAwaiter().GetResult()
					$typePayload = $typeResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
					$typeReady = $typeResponse.IsSuccessStatusCode -and
						$typePayload -notmatch '"isError"\s*:\s*true' -and
						$typePayload -match 'HexagonBootstrapComponent' -and
						$typePayload -match 'Hexagon\.V2\.Runtime'
				}
				catch {
					$typePayload = ''
					$typeReady = $false
				}
				finally {
					$typeContent.Dispose()
				}
				$compileDelta = Read-LogDelta -Path $logPath -Offset $logOffset
				$logOffset = $compileDelta.Offset
				if (-not [string]::IsNullOrEmpty($compileDelta.Text)) {
					[void]$captured.Append($compileDelta.Text)
				}
				$compileFatal = @($captured.ToString() -split "`r?`n" | Where-Object { $_ -match $fatalPattern })
				if ($compileFatal.Count -gt 0) {
					$summary = ($compileFatal | Select-Object -Unique | Select-Object -First 30) -join "`n"
					throw "s&box '$Probe' failed before component hotload:`n$summary"
				}
				if (-not $typeReady) {
					Start-Sleep -Milliseconds 250
				}
			}
			if (-not $typeReady) {
				throw "Timed out waiting for Hexagon's component types to hotload: $typePayload"
			}

			$consoleSearchBody = [ordered]@{
				jsonrpc = '2.0'
				id = 5
				method = 'tools/call'
				params = [ordered]@{
					name = 'search_tools'
					arguments = [ordered]@{ query = 'console command' }
				}
			} | ConvertTo-Json -Depth 8 -Compress
			$consoleSearchContent = [System.Net.Http.StringContent]::new(
				$consoleSearchBody,
				[System.Text.Encoding]::UTF8,
				'application/json')
			try {
				$consoleSearchResponse = $http.PostAsync($mcpEndpoint, $consoleSearchContent).GetAwaiter().GetResult()
				$consoleSearchPayload = $consoleSearchResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
				if (-not $consoleSearchResponse.IsSuccessStatusCode -or $consoleSearchPayload -notmatch 'console_command') {
					throw "Editor tool discovery did not expose console_command: $consoleSearchPayload"
				}
			}
			finally {
				$consoleSearchContent.Dispose()
			}

			$consoleOverrides = @(
				[pscustomobject]@{ Name = 'hexagon-data-root'; Value = $DataRoot },
				[pscustomobject]@{ Name = 'hexagon-verification-probe'; Value = $Probe }
			)
			$commandId = 6
			foreach ($override in $consoleOverrides) {
				$consoleBody = [ordered]@{
					jsonrpc = '2.0'
					id = $commandId
					method = 'tools/call'
					params = [ordered]@{
						name = 'call_tool'
						arguments = [ordered]@{
							name = 'console_command'
							arguments = [ordered]@{
								command = "$($override.Name) `"$($override.Value)`""
							}
						}
					}
				} | ConvertTo-Json -Depth 12 -Compress
				$consoleContent = [System.Net.Http.StringContent]::new(
					$consoleBody,
					[System.Text.Encoding]::UTF8,
					'application/json')
				try {
					$consoleResponse = $http.PostAsync($mcpEndpoint, $consoleContent).GetAwaiter().GetResult()
					$consolePayload = $consoleResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
					if (-not $consoleResponse.IsSuccessStatusCode -or
						$consolePayload -match '"isError"\s*:\s*true' -or
						$consolePayload -match '(?i)Unknown Command') {
						throw "Editor rejected $($override.Name) override '$($override.Value)': $consolePayload"
					}
				}
				finally {
					$consoleContent.Dispose()
				}
				$commandId++
			}

			$playBody = [ordered]@{
				jsonrpc = '2.0'
				id = $commandId
				method = 'tools/call'
				params = [ordered]@{
					name = 'call_tool'
					arguments = [ordered]@{ name = 'play_start'; arguments = [ordered]@{} }
				}
            } | ConvertTo-Json -Depth 8 -Compress
			$playPayload = ''
			$structured = $null
			while ([DateTime]::UtcNow -lt $deadline -and
				($null -eq $structured -or $structured.IsPlaying -ne $true -or $structured.Scene -cne 'main')) {
				if ($editor.HasExited) {
					throw "s&box exited with code $($editor.ExitCode) before the main scene entered play mode."
				}
				$playContent = [System.Net.Http.StringContent]::new(
					$playBody,
					[System.Text.Encoding]::UTF8,
					'application/json')
				try {
					$playResponse = $http.PostAsync($mcpEndpoint, $playContent).GetAwaiter().GetResult()
					$playPayload = $playResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
					if (-not $playResponse.IsSuccessStatusCode) {
						throw "Editor play_start returned HTTP $([int]$playResponse.StatusCode): $playPayload"
					}
					$play = $playPayload | ConvertFrom-Json
				}
				finally {
					$playContent.Dispose()
				}
				$structuredProperty = if ($null -eq $play.result) { $null } else {
					$play.result.PSObject.Properties['structuredContent']
				}
				$structured = if ($null -eq $structuredProperty) { $null } else { $structuredProperty.Value }
				if ($null -eq $structured -and $null -ne $play.result) {
					$contentProperty = $play.result.PSObject.Properties['content']
					if ($null -ne $contentProperty -and @($contentProperty.Value).Count -gt 0) {
						$textProperty = @($contentProperty.Value)[0].PSObject.Properties['text']
						if ($null -ne $textProperty) {
							try { $structured = $textProperty.Value | ConvertFrom-Json } catch { }
						}
					}
				}
				if ($null -eq $structured -or $structured.IsPlaying -ne $true -or $structured.Scene -cne 'main') {
					Start-Sleep -Milliseconds 250
				}
			}
			if ($null -eq $structured -or $structured.IsPlaying -ne $true -or
				$structured.Scene -cne 'main') {
				throw "Timed out starting the game-owned main scene: $playPayload"
			}
        }
        finally {
            $http.Dispose()
        }

        while ([DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 500

            $delta = Read-LogDelta -Path $logPath -Offset $logOffset
            $logOffset = $delta.Offset
            if (-not [string]::IsNullOrEmpty($delta.Text)) {
                [void]$captured.Append($delta.Text)
            }

            $logText = $captured.ToString()
            $fatalLines = @($logText -split "`r?`n" | Where-Object { $_ -match $fatalPattern })
            if ($fatalLines.Count -gt 0) {
                $summary = ($fatalLines | Select-Object -Unique | Select-Object -First 30) -join "`n"
                throw "s&box '$Probe' run reported fatal compiler/resource/runtime errors:`n$summary"
            }

            $hostReady = $logText -match '(?m)HEXAGON_READY host\b'
            $clientReady = $logText -match '(?m)HEXAGON_READY client\b'
            $clientSessionReady = $logText -match '(?m)HEXAGON_SESSION_READY client\b'
            $drained = $logText -match '(?m)HEXAGON_DRAINED host\b'
            $probeMatch = [regex]::Match(
                $logText,
                "(?m)$ExpectedSentinel sequence=(?<sequence>[1-9][0-9]*) digest=(?<digest>[a-f0-9]{64})" )
            if ($hostReady -and $clientReady -and $clientSessionReady -and $probeMatch.Success -and $drained) {
                break
            }

            if ($editor.HasExited) {
                throw "s&box exited with code $($editor.ExitCode) before '$Probe' readiness, probe, and drain sentinels."
            }
        }

        if (-not ($hostReady -and $clientReady -and $clientSessionReady -and $probeMatch.Success -and $drained)) {
            $tail = ($captured.ToString() -split "`r?`n" | Select-Object -Last 50) -join "`n"
            throw "Timed out after $TimeoutSeconds seconds waiting for '$Probe' sentinels " +
                "(host=$hostReady, client=$clientReady, clientSession=$clientSessionReady, probe=$($probeMatch.Success), drained=$drained). " +
                "Recent log output:`n$tail"
        }

        return [pscustomobject]@{
            Sequence = [long]$probeMatch.Groups['sequence'].Value
            Digest = $probeMatch.Groups['digest'].Value
        }
    }
    finally {
        if (-not $editor.HasExited) {
            Stop-Process -Id $editor.Id -Force
            $editor.WaitForExit()
        }
    }
}

$packageDataRoot = Join-Path $SboxRoot ("data\{0}\{1}#{0}" -f $manifest.Org, $manifest.Ident)
$verificationRoot = Join-Path $packageDataRoot $DataRoot.Replace('/', [System.IO.Path]::DirectorySeparatorChar)

try {
    $committed = Invoke-ProbeRun -Probe 'commit' -ExpectedSentinel 'HL2RP_PROBE_COMMITTED'
    $recovered = Invoke-ProbeRun -Probe 'recover' -ExpectedSentinel 'HL2RP_PROBE_RECOVERED'
    if ($recovered.Sequence -ne $committed.Sequence -or $recovered.Digest -cne $committed.Digest) {
        throw "Recovery mismatch: committed sequence/digest $($committed.Sequence)/$($committed.Digest), " +
            "recovered $($recovered.Sequence)/$($recovered.Digest)."
    }
}
finally {
    if (Test-Path -LiteralPath $verificationRoot -PathType Container) {
        $packagePath = [System.IO.Path]::GetFullPath($packageDataRoot).TrimEnd('\')
        $targetPath = [System.IO.Path]::GetFullPath($verificationRoot).TrimEnd('\')
        if (-not $targetPath.StartsWith($packagePath + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove verification data outside '$packagePath': '$targetPath'."
        }
        # Windows PowerShell's FileSystem provider can fail part-way through a
        # recursive delete when a content-addressed checkpoint pushes a leaf over
        # MAX_PATH. The extended path keeps the already-verified target intact.
        $extendedTargetPath = if ($targetPath.StartsWith('\\')) {
            '\\?\UNC\' + $targetPath.TrimStart('\')
        }
        else {
            '\\?\' + $targetPath
        }
        [System.IO.Directory]::Delete($extendedTargetPath, $true)
        if ([System.IO.Directory]::Exists($extendedTargetPath)) {
            throw "Verification data cleanup did not remove '$targetPath'."
        }
    }
}

Write-Host 's&box host/client readiness, transactional probe, drain, and exact recovery passed.' -ForegroundColor Green
