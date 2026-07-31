Set-StrictMode -Version Latest

function Invoke-ReleaseInputGit {
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string[]] $Arguments
    )

    $global:LASTEXITCODE = 0
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell promotes native stderr to ErrorRecord even when git
        # exits zero (for example its checkout-EOL advisory). Capture it and
        # decide success exclusively from the process exit code.
        $ErrorActionPreference = 'Continue'
        [string[]] $output = @(& git -C $Root @Arguments 2>&1)
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed in '$Root': $($output -join [Environment]::NewLine)"
    }
    return $output
}

function Get-GeneratedAssetPattern {
    <#
    .SYNOPSIS
    Derives, from a repository's own .gitignore, which suffixes mark a file under Assets as
    engine build output rather than authored content.

    .DESCRIPTION
    Reading the classification rather than restating it is deliberate. '/Assets/**/*_d' was
    added to hl2rp's .gitignore the first time the editor wrote a scene dependency file, and
    both guards that carried their own copy of the list — the release-material gate below and
    the namespace check in validate-project.ps1 — kept passing in CI, where no clone ever has
    such a file, and then failed the release pipeline on the one machine that had run the
    editor. A list maintained in three places is maintained in none of them.
    #>
    param([Parameter(Mandatory)][string] $Root)

    # Absence of information means nothing is exempt, never an error: this function answers
    # "what may be waved through", so no .gitignore and a .gitignore declaring no Assets
    # rules are the same answer - none of it - and both fall through to the pattern below.
    $ignorePath = Join-Path $Root '.gitignore'
    if (-not (Test-Path -LiteralPath $ignorePath -PathType Leaf)) { return '(?!)' }

    # Only extension-shaped rules count. A rule that names a particular file is ignored for
    # some reason of its own, and must keep failing the release gate rather than being waved
    # through as build output.
    $suffixes = @(Get-Content -LiteralPath $ignorePath |
        ForEach-Object { $_.Trim() } |
        ForEach-Object {
            if ($_ -cmatch '^/Assets/\*\*/\*(?<suffix>[_.][A-Za-z0-9_.]+)$') { $Matches['suffix'] }
        })

    # No such rules means nothing under Assets is build output, so nothing is exempt. That is
    # the STRICT end of this function, not a vacuous one: every ignored file under Assets then
    # fails the release gate, which is what a repository that declares no generated assets
    # should get. Returning a never-matching pattern says exactly that.
    if ($suffixes.Count -eq 0) { return '(?!)' }

    return '(?:' + (($suffixes | Sort-Object -Unique |
        ForEach-Object { [Regex]::Escape($_) }) -join '|') + ')$'
}

function Test-IsAllowedIgnoredReleaseInput {
    param(
        [Parameter(Mandatory)][string] $RelativePath,
        [Parameter(Mandatory)][string] $GeneratedAssetPattern
    )

    $path = $RelativePath.Replace('\', '/')
    return $path -cmatch '^(?:Code|Editor|tests)/(?:obj|bin)/' -or
        $path -ceq 'Code/Properties/launchSettings.json' -or
        $path -cmatch '^Code/[^/]+\.csproj$' -or
        ($path -cmatch '^Assets/.+' -and $path -cmatch $GeneratedAssetPattern)
}

function Test-IsReleaseRelevantIgnoredPath {
    param([Parameter(Mandatory)][string] $RelativePath)

    $path = $RelativePath.Replace('\', '/')
    return $path -cmatch '^(?:Code|Assets|ProjectSettings)/' -or $path -cmatch '\.cs$'
}

$script:IgnoredEnumerationVerified = $false

function Assert-IgnoredEnumerationContract {
    # The ignored-release-material gate is only as strong as git's willingness to
    # report ignored files. Prove once per process, against a throwaway repository,
    # that a bare 'ls-files --others --ignored --exclude-standard' surfaces a known
    # ignored file — a git build or environment that suppresses the listing must
    # fail this gate loudly instead of letting the material check pass vacuously.
    if ($script:IgnoredEnumerationVerified) { return }
    $probeRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        'hexagon-ignored-enumeration-probe-' + [Guid]::NewGuid().ToString('N'))
    try {
        [void](New-Item -ItemType Directory -Path (Join-Path $probeRoot 'Code') -Force)
        [void](Invoke-ReleaseInputGit -Root $probeRoot -Arguments @('init', '-q'))
        '*.generated.*' | Set-Content -LiteralPath (Join-Path $probeRoot '.gitignore') -Encoding utf8
        'probe' | Set-Content -LiteralPath (
            Join-Path $probeRoot 'Code/Probe.generated.cs') -Encoding utf8
        $reported = @(Invoke-ReleaseInputGit -Root $probeRoot -Arguments @(
            'ls-files', '--others', '--ignored', '--exclude-standard') |
            ForEach-Object { $_.Replace('\', '/') })
        if (@($reported | Where-Object { $_ -ceq 'Code/Probe.generated.cs' }).Count -ne 1) {
            $gitVersion = (Invoke-ReleaseInputGit -Root $probeRoot -Arguments @('--version')) -join ' '
            $reportedText = if ($reported.Count -ne 0) { $reported -join ', ' } else { '<nothing>' }
            throw ("git failed the ignored-file enumeration contract " +
                "($gitVersion reported: $reportedText); " +
                'refusing to trust the ignored-release-material gate.')
        }
        $script:IgnoredEnumerationVerified = $true
    }
    finally {
        if (Test-Path -LiteralPath $probeRoot) {
            Remove-Item -LiteralPath $probeRoot -Recurse -Force
        }
    }
}

function Add-ReleaseInputLength {
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.IncrementalHash] $Hash,
        [Parameter(Mandatory)][UInt64] $Value
    )

    [byte[]] $bytes = [BitConverter]::GetBytes($Value)
    if ([BitConverter]::IsLittleEndian) {
        [Array]::Reverse($bytes)
    }
    $Hash.AppendData($bytes)
}

function Add-ReleaseInputFileBytes {
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.IncrementalHash] $Hash,
        [Parameter(Mandatory)][string] $FullPath
    )

    $stream = [System.IO.File]::OpenRead($FullPath)
    try {
        Add-ReleaseInputLength -Hash $Hash -Value ([UInt64]$stream.Length)
        [byte[]] $buffer = [byte[]]::new(65536)
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $Hash.AppendData($buffer, 0, $read)
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Add-ReleaseInputPath {
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.IncrementalHash] $Hash,
        [Parameter(Mandatory)][object] $File
    )

    $pathBytes = [System.Text.Encoding]::UTF8.GetBytes(
        "$($File.Label)/$($File.RelativePath)")
    Add-ReleaseInputLength -Hash $Hash -Value ([UInt64]$pathBytes.Length)
    $Hash.AppendData($pathBytes)
}

function Read-GitBatchHeader {
    param([Parameter(Mandatory)][System.IO.Stream] $Stream)

    $bytes = [System.Collections.Generic.List[byte]]::new()
    while ($true) {
        $value = $Stream.ReadByte()
        if ($value -lt 0) { throw 'git cat-file --batch ended before its response header.' }
        if ($value -eq 10) { break }
        if ($bytes.Count -ge 1024) { throw 'git cat-file --batch returned an oversized response header.' }
        $bytes.Add([byte]$value)
    }
    return [System.Text.Encoding]::ASCII.GetString($bytes.ToArray())
}

function Add-ReleaseInputSetBytes {
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.IncrementalHash] $Hash,
        [Parameter(Mandatory)][object] $Set
    )

    if (-not $Set.UseGitBlobs) {
        foreach ($file in $Set.Files) {
            Add-ReleaseInputPath -Hash $Hash -File $file
            Add-ReleaseInputFileBytes -Hash $Hash -FullPath ([string]$file.FullPath)
        }
        return
    }

    $git = @(Get-Command git -CommandType Application -All -ErrorAction Stop)[0]
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $git.Path
    $startInfo.WorkingDirectory = [string]$Set.Root
    $startInfo.Arguments = 'cat-file --batch'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try {
        if (-not $process.Start()) {
            throw "Could not start git cat-file --batch in '$($Set.Root)'."
        }
        $started = $true
        $input = $process.StandardInput.BaseStream
        $output = $process.StandardOutput.BaseStream
        if ($PSVersionTable.PSEdition -ceq 'Desktop') {
            # Windows PowerShell's redirected StreamWriter emits a UTF-8 BOM
            # before the first raw pipe write. Consume it as a throwaway batch
            # request so it cannot prefix the first object ID.
            $input.WriteByte(10)
            $input.Flush()
            $discardedHeader = Read-GitBatchHeader -Stream $output
            if ($discardedHeader -notmatch ' missing$') {
                throw "Could not consume the Windows PowerShell batch preamble: $discardedHeader"
            }
        }
        [byte[]] $buffer = [byte[]]::new(65536)
        foreach ($file in $Set.Files) {
            Add-ReleaseInputPath -Hash $Hash -File $file
            $request = [System.Text.Encoding]::ASCII.GetBytes(
                "$($file.GitObjectId)`n")
            $input.Write($request, 0, $request.Length)
            $input.Flush()

            $header = Read-GitBatchHeader -Stream $output
            $match = [regex]::Match(
                $header,
                '^(?<object>[a-f0-9]{40,64}) blob (?<length>[0-9]+)$')
            [UInt64] $length = 0
            if (-not $match.Success -or
                $match.Groups['object'].Value -cne [string]$file.GitObjectId -or
                -not [UInt64]::TryParse(
                    $match.Groups['length'].Value,
                    [System.Globalization.NumberStyles]::None,
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [ref]$length)) {
                throw "Unexpected git cat-file response for '$($file.RelativePath)': $header"
            }
            Add-ReleaseInputLength -Hash $Hash -Value $length
            [UInt64] $remaining = $length
            while ($remaining -gt 0) {
                $requested = [int][Math]::Min([UInt64]$buffer.Length, $remaining)
                $read = $output.Read($buffer, 0, $requested)
                if ($read -le 0) {
                    throw "git cat-file ended inside '$($file.RelativePath)'."
                }
                $Hash.AppendData($buffer, 0, $read)
                $remaining -= [UInt64]$read
            }
            if ($output.ReadByte() -ne 10) {
                throw "git cat-file returned invalid framing for '$($file.RelativePath)'."
            }
        }
        $process.StandardInput.Close()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "git cat-file --batch failed in '$($Set.Root)': $standardError"
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.StandardInput.Close()
            if (-not $process.WaitForExit(1000)) { $process.Kill() }
        }
        $process.Dispose()
    }
}

function Get-ReleaseRelativePath {
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string] $Path
    )
    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') +
        [System.IO.Path]::DirectorySeparatorChar
    $rootUri = [Uri]::new($rootPath)
    $pathUri = [Uri]::new([System.IO.Path]::GetFullPath($Path))
    return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

function Get-RepositoryReleaseInputSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string] $Label,
        [Parameter()][switch] $RequireClean
    )

    $resolved = (Resolve-Path -LiteralPath $Root).Path.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)

    $changes = @(Invoke-ReleaseInputGit -Root $resolved -Arguments @(
        'status', '--porcelain=v1', '--untracked-files=all'))
    $isClean = $changes.Count -eq 0
    if ($RequireClean -and -not $isClean) {
        throw "Release inputs require a clean checkout: '$resolved'."
    }

    # No pathspecs here: glob pathspec matching for the ignored-file walk proved
    # environment-sensitive (a hosted runner returned nothing for 'Code/**'/'*.cs'
    # while literal pathspecs matched), so relevance is filtered in-process instead.
    Assert-IgnoredEnumerationContract
    $generatedAssetPattern = Get-GeneratedAssetPattern -Root $resolved
    $ignoredMaterial = @(
        Invoke-ReleaseInputGit -Root $resolved -Arguments @(
            'ls-files', '--others', '--ignored', '--exclude-standard') |
            ForEach-Object { $_.Replace('\', '/') } |
            Where-Object { (Test-IsReleaseRelevantIgnoredPath -RelativePath $_) -and
                -not (Test-IsAllowedIgnoredReleaseInput -RelativePath $_ `
                    -GeneratedAssetPattern $generatedAssetPattern) } |
            Sort-Object -Unique
    )
    if ($ignoredMaterial.Count -ne 0) {
        throw "Ignored release material exists in '$resolved': $($ignoredMaterial -join ', ')."
    }

    [string[]] $tracked = @(Invoke-ReleaseInputGit -Root $resolved -Arguments @(
        'ls-files', '--cached'))
    [string[]] $effectivePaths = @($tracked)
    if (-not $isClean) {
        $effectivePaths += @(Invoke-ReleaseInputGit -Root $resolved -Arguments @(
            'ls-files', '--others', '--exclude-standard'))
    }
    [Array]::Sort($effectivePaths, [System.StringComparer]::Ordinal)
    $objectIds = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::Ordinal)
    if ($isClean) {
        foreach ($row in @(Invoke-ReleaseInputGit -Root $resolved -Arguments @(
            'ls-files', '--format=%(objectname) %(path)'))) {
            $separator = $row.IndexOf(' ')
            if ($separator -lt 1) {
                throw "Could not parse a tracked-object record in '$resolved': $row"
            }
            $objectId = $row.Substring(0, $separator)
            $path = $row.Substring($separator + 1).Replace('\', '/')
            if ($objectId -cnotmatch '^[a-f0-9]{40,64}$' -or
                $objectIds.ContainsKey($path)) {
                throw "Invalid or duplicated tracked-object record for '$path' in '$resolved'."
            }
            $objectIds.Add($path, $objectId)
        }
    }
    $files = [System.Collections.Generic.List[object]]::new()
    foreach ($effectivePath in $effectivePaths) {
        $relative = $effectivePath.Replace('\', '/')

        $fullPath = Join-Path $resolved $relative.Replace(
            '/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            if ($RequireClean) {
                throw "Tracked release input '$relative' is missing from '$resolved'."
            }
            continue
        }
        if ($isClean -and -not $objectIds.ContainsKey($relative)) {
            throw "Tracked release input '$relative' has no canonical Git blob in '$resolved'."
        }
        $files.Add([pscustomobject]@{
            Label = $Label
            RelativePath = $relative
            FullPath = [System.IO.Path]::GetFullPath($fullPath)
            Sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
            GitObjectId = if ($isClean) {
                $objectIds[$relative]
            }
            else { '' }
        })
    }

    return [pscustomobject]@{
        Label = $Label
        Root = $resolved
        UseGitBlobs = $isClean
        Files = @($files)
    }
}

function Get-EffectiveReleaseInputs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]] $Repositories,
        [Parameter()][switch] $RequireClean
    )

    $orderedRepositories = [System.Collections.Generic.SortedDictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($repository in $Repositories) {
        $label = [string]$repository.Label
        if ([string]::IsNullOrWhiteSpace($label) -or $orderedRepositories.ContainsKey($label)) {
            throw "Release repository labels must be distinct non-empty strings; found '$label'."
        }
        $orderedRepositories.Add($label, $repository)
    }

    $sets = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $orderedRepositories.GetEnumerator()) {
        $repository = $entry.Value
        $sets.Add((Get-RepositoryReleaseInputSet `
            -Root ([string]$repository.Root) `
            -Label ([string]$repository.Label) `
            -RequireClean:$RequireClean))
    }

    $sha = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($set in $sets) {
            Add-ReleaseInputSetBytes -Hash $sha -Set $set
        }
        $fingerprint = ([BitConverter]::ToString($sha.GetHashAndReset()) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }

    return [pscustomobject]@{
        Fingerprint = $fingerprint
        Repositories = @($sets)
        Files = @($sets | ForEach-Object { $_.Files })
    }
}

function Assert-GeneratedCompileInputsTracked {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $ProjectPath,
        [Parameter(Mandatory)][object[]] $Repositories,
        [Parameter()][switch] $AllowNonIgnoredUntracked
    )

    $project = (Resolve-Path -LiteralPath $ProjectPath).Path
    $global:LASTEXITCODE = 0
    [string[]] $output = @(& dotnet msbuild $project -nologo -getItem:Compile 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not query Compile items for '$project': $($output -join [Environment]::NewLine)"
    }
    $jsonText = $output -join [Environment]::NewLine
    $jsonStart = $jsonText.IndexOf('{')
    if ($jsonStart -lt 0) {
        throw "MSBuild returned no Compile-item JSON for '$project'."
    }
    $items = (($jsonText.Substring($jsonStart) | ConvertFrom-Json).Items.Compile)
    foreach ($item in @($items)) {
        $candidate = if (-not [string]::IsNullOrWhiteSpace([string]$item.FullPath)) {
            [string]$item.FullPath
        }
        else {
            Join-Path (Split-Path -Parent $project) ([string]$item.Identity)
        }
        $fullPath = [System.IO.Path]::GetFullPath($candidate)

        $owner = $null
        foreach ($repository in $Repositories) {
            $root = [System.IO.Path]::GetFullPath([string]$repository.Root).TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar)
            if ($fullPath.StartsWith(
                $root + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase)) {
                $owner = [pscustomobject]@{ Root = $root; Label = [string]$repository.Label }
                break
            }
        }
        if ($null -eq $owner) {
            throw "Compile input '$fullPath' is outside the declared release repositories."
        }

        $relative = (Get-ReleaseRelativePath -Root $owner.Root -Path $fullPath).Replace('\', '/')
        if ($relative -match '^Code/(?:obj|bin)/') { continue }
        $trackedMatch = @(
            Invoke-ReleaseInputGit -Root $owner.Root -Arguments @(
                'ls-files', '--cached', '--', $relative) |
                Where-Object { $_.Replace('\', '/') -ceq $relative }
        )
        if ($trackedMatch.Count -eq 1) { continue }

        if ($AllowNonIgnoredUntracked) {
            $untrackedMatch = @(
                Invoke-ReleaseInputGit -Root $owner.Root -Arguments @(
                    'ls-files', '--others', '--exclude-standard', '--', $relative) |
                    Where-Object { $_.Replace('\', '/') -ceq $relative }
            )
            if ($untrackedMatch.Count -eq 1) { continue }
        }

        throw "Compile input '$relative' is not a tracked effective input in '$($owner.Root)'."
    }
}
