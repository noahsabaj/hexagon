# docgen.ps1 — Auto-generates docs/api-reference.md from C# source files.
# Reads all .cs files under Code/, extracts public API surface and XML doc comments,
# produces a Markdown reference with code blocks + description tables.
#
# Usage: powershell -ExecutionPolicy Bypass -File tools/docgen.ps1

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
$CodeDir = Join-Path $RootDir "Code"
$OutputFile = Join-Path (Join-Path $RootDir "docs") "api-reference.md"

# Namespace order and listener categories are discovered dynamically from parsed source.
# No hardcoded namespace lists — adding/removing namespaces in Code/ is automatically reflected.

# ---- Data Model ----

class TypeInfo {
    [string]$Namespace
    [string]$Name
    [string]$Kind          # class, interface, enum, struct
    [string]$Modifiers     # static, sealed, abstract
    [string]$Inheritance   # ": BaseClass, IFace"
    [string]$Summary
    [System.Collections.ArrayList]$Members = @()
    [bool]$IsListener      # true if matches I*Listener or ICan* pattern
}

class MemberInfo {
    [string]$Name
    [string]$Signature     # full signature for code block
    [string]$Summary
    [string]$Kind          # property, method, field, event
    [string]$Attributes    # [Sync], [Rpc.Host], etc.
}

# ---- Parser ----

function Parse-CSharpFile([string]$FilePath) {
    $lines = Get-Content $FilePath -Encoding UTF8
    $currentNamespace = ""
    $summaryBuffer = @()
    $inSummary = $false
    $attributeBuffer = @()
    $currentType = $null
    $braceDepth = 0
    $typeStartDepth = -1
    $types = @()

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $trimmed = $line.Trim()

        # Track brace depth
        $openBraces = ([regex]::Matches($trimmed, '\{')).Count
        $closeBraces = ([regex]::Matches($trimmed, '\}')).Count

        # Namespace (file-scoped)
        if ($trimmed -match '^namespace\s+([\w.]+)\s*;') {
            $currentNamespace = $Matches[1]
            continue
        }

        # XML doc comments
        if ($trimmed -match '^///\s*<summary>\s*$') {
            $inSummary = $true
            $summaryBuffer = @()
            continue
        }
        if ($trimmed -match '^///\s*</summary>\s*$') {
            $inSummary = $false
            continue
        }
        if ($inSummary -and $trimmed -match '^///\s*(.*)$') {
            $text = $Matches[1].Trim()
            if ($text -ne "") {
                $summaryBuffer += $text
            }
            continue
        }
        # Skip other XML doc tags (param, returns, etc.)
        if ($trimmed -match '^///') {
            continue
        }

        # Attributes — may be on same line as declaration (e.g. "[Property] public int X")
        if ($trimmed -match '^\[') {
            # Extract just the attribute part
            if ($trimmed -match '^(\[.+?\])\s+(public\s+.+)$') {
                # Attribute + declaration on same line
                $attributeBuffer += $Matches[1]
                $trimmed = $Matches[2]
                # Fall through to process the declaration below
            } else {
                $attributeBuffer += $trimmed
                continue
            }
        }

        # Type declaration
        if ($trimmed -match '^public\s+(static\s+|sealed\s+|abstract\s+)?(class|interface|enum|struct)\s+(\w+)(.*)') {
            $modifiers = if ($Matches[1]) { $Matches[1].Trim() } else { "" }
            $kind = $Matches[2]
            $typeName = $Matches[3]
            $rest = $Matches[4].Trim()

            # Parse inheritance
            $inheritance = ""
            if ($rest -match '^:\s*(.+?)(\{|$)') {
                $inheritance = $Matches[1].Trim().TrimEnd('{').Trim()
            }

            $summary = ($summaryBuffer -join " ")
            $summaryBuffer = @()
            $attributeBuffer = @()

            $isListener = ($kind -eq "interface") -and (
                ($typeName -match '^I\w+Listener$') -or
                ($typeName -match '^ICan\w+')
            )

            $currentType = [TypeInfo]@{
                Namespace   = $currentNamespace
                Name        = $typeName
                Kind        = $kind
                Modifiers   = $modifiers
                Inheritance = $inheritance
                Summary     = $summary
                IsListener  = $isListener
            }
            $typeStartDepth = $braceDepth
            $types += $currentType

            $braceDepth += $openBraces
            $braceDepth -= $closeBraces
            continue
        }

        # Member detection (only inside a type)
        # For interfaces, members are implicitly public (no 'public' keyword)
        $isMemberLine = $false
        if ($currentType -ne $null) {
            if ($trimmed -match '^public\s+') {
                # Skip nested type declarations
                if ($trimmed -match '^public\s+(static\s+|sealed\s+|abstract\s+)?(class|interface|enum|struct)\s+') {
                    $braceDepth += $openBraces
                    $braceDepth -= $closeBraces
                    continue
                }
                $isMemberLine = $true
            }
            elseif ($currentType.Kind -eq "interface" -and $trimmed -match '^\w' -and $trimmed -notmatch '^(namespace|using|\/\/|#|public\s+(class|interface|enum|struct))') {
                # Interface member without public keyword
                $isMemberLine = $true
                # Prepend "public" so the same regexes work
                $trimmed = "public $trimmed"
            }
        }

        if ($isMemberLine) {

            $memberSummary = ($summaryBuffer -join " ")
            $summaryBuffer = @()

            # Detect notable attributes
            $attrs = ""
            foreach ($a in $attributeBuffer) {
                if ($a -match '\[Sync\]') { $attrs += "[Sync] " }
                if ($a -match '\[Rpc\.Host\]') { $attrs += "[Rpc.Host] " }
                if ($a -match '\[Rpc\.Owner\]') { $attrs += "[Rpc.Owner] " }
                if ($a -match '\[Rpc\.Broadcast\]') { $attrs += "[Rpc.Broadcast] " }
                if ($a -match '\[Property\]') { $attrs += "[Property] " }
            }
            $attrs = $attrs.Trim()
            $attributeBuffer = @()

            # Parse the member
            $memberKind = ""
            $memberName = ""
            $memberSig = ""

            # Event
            if ($trimmed -match '^public\s+(static\s+)?event\s+(\S+)\s+(\w+)') {
                $memberKind = "event"
                $memberName = $Matches[3]
                $static = if ($Matches[1]) { "static " } else { "" }
                $memberSig = "${static}event $($Matches[2]) $($Matches[3])"
            }
            # Property with { get; set; } or { get; }
            elseif ($trimmed -match '^public\s+(static\s+|override\s+|virtual\s+)*([\w<>\[\],\s\?]+?)\s+(\w+)\s*\{') {
                $memberKind = "property"
                $mods = if ($Matches[1]) { $Matches[1].Trim() + " " } else { "" }
                $type = $Matches[2].Trim()
                $memberName = $Matches[3]

                # Determine accessor
                $accessor = ""
                if ($trimmed -match '\{\s*get;\s*set;\s*\}') { $accessor = "{ get; set; }" }
                elseif ($trimmed -match '\{\s*get;\s*\}') { $accessor = "{ get; }" }
                elseif ($trimmed -match '\{\s*get;') { $accessor = "{ get; set; }" }
                else { $accessor = "{ get; set; }" }

                $memberSig = "${mods}${type} ${memberName} ${accessor}"
            }
            # Method (including generic methods like Fire<T>, GetData<T>)
            elseif ($trimmed -match '^public\s+(static\s+|virtual\s+|override\s+|async\s+)*([\w<>\[\],\s\?]+?)\s+(\w+(?:<[^>]+>)?)\s*\((.*)') {
                $memberKind = "method"
                $mods = if ($Matches[1]) { $Matches[1].Trim() + " " } else { "" }
                $returnType = $Matches[2].Trim()
                $memberName = $Matches[3]
                $args = $Matches[4]

                # Handle multi-line signatures - collect until we find the closing paren
                $fullArgs = $args
                while ($fullArgs -notmatch '\)' -and $i -lt $lines.Count - 1) {
                    $i++
                    $fullArgs += " " + $lines[$i].Trim()
                }

                # Clean up args
                if ($fullArgs -match '(.*?)\)') {
                    $cleanArgs = $Matches[1].Trim()
                    # Remove default values for cleaner display
                    $cleanArgs = $cleanArgs -replace '\s*=\s*[^,\)]+', ''
                    # Remove where constraints
                    $cleanArgs = $cleanArgs -replace '\s+where\s+\w+\s*:.*$', ''
                    $memberSig = "${mods}${returnType} ${memberName}( ${cleanArgs} )"
                    $memberSig = $memberSig -replace '\(\s+\)', '()'
                } else {
                    $memberSig = "${mods}${returnType} ${memberName}( ${fullArgs} )"
                }
            }
            # Field
            elseif ($trimmed -match '^public\s+(static\s+|readonly\s+)*([\w<>\[\],\s\?]+?)\s+(\w+)\s*[=;]') {
                $memberKind = "field"
                $mods = if ($Matches[1]) { $Matches[1].Trim() + " " } else { "" }
                $type = $Matches[2].Trim()
                $memberName = $Matches[3]
                $memberSig = "${mods}${type} ${memberName}"
            }

            if ($memberName -ne "" -and $memberKind -ne "") {
                # Add attribute prefix to signature if present
                $displaySig = $memberSig
                if ($attrs -ne "") {
                    $displaySig = "${attrs} ${memberSig}"
                }

                # For interface members, strip "public " from the signature display
                if ($currentType.Kind -eq "interface") {
                    $displaySig = $displaySig -replace '^public\s+', ''
                    $memberSig = $memberSig -replace '^public\s+', ''
                }

                # Clean name for table (strip generic params)
                $tableName = $memberName -replace '<.+>$', ''

                $member = [MemberInfo]@{
                    Name       = $tableName
                    Signature  = $displaySig.Trim()
                    Summary    = $memberSummary
                    Kind       = $memberKind
                    Attributes = $attrs
                }
                $currentType.Members.Add($member) | Out-Null
            } else {
                $attributeBuffer = @()
            }
        } else {
            # Non-public line or outside type — clear buffers if not doc comment
            if (-not ($trimmed -match '^///') -and -not ($trimmed -match '^\[') -and $trimmed -ne "") {
                if (-not $inSummary) {
                    $summaryBuffer = @()
                }
                $attributeBuffer = @()
            }
        }

        # Update brace depth
        $braceDepth += $openBraces
        $braceDepth -= $closeBraces

        # Check if we've exited the current type
        if ($currentType -ne $null -and $braceDepth -le $typeStartDepth -and $typeStartDepth -ge 0) {
            $currentType = $null
            $typeStartDepth = -1
        }
    }

    return $types
}

# ---- Main ----

Write-Host "docgen: Scanning $CodeDir ..."

# Collect all .cs files, excluding obj/
$files = Get-ChildItem -Path $CodeDir -Filter "*.cs" -Recurse |
    Where-Object { $_.FullName -notmatch '\\obj\\' }

Write-Host "docgen: Found $($files.Count) source files."

# Parse all files
$allTypes = @()
foreach ($file in $files) {
    $types = Parse-CSharpFile $file.FullName
    $allTypes += $types
}

Write-Host "docgen: Extracted $($allTypes.Count) public types."

# Group by namespace
$byNamespace = @{}
foreach ($type in $allTypes) {
    $ns = $type.Namespace
    if (-not $ns) { continue }
    if (-not $byNamespace.ContainsKey($ns)) {
        $byNamespace[$ns] = @()
    }
    $byNamespace[$ns] += $type
}

# Separate listener interfaces
$listeners = @{}
$regularTypes = @{}
foreach ($ns in $byNamespace.Keys) {
    $regularTypes[$ns] = @()
    foreach ($type in $byNamespace[$ns]) {
        if ($type.IsListener) {
            $cat = if ($ns -match '^Hexagon\.(.+)$') { $Matches[1] } else { $ns }
            if (-not $listeners.ContainsKey($cat)) {
                $listeners[$cat] = @()
            }
            $listeners[$cat] += $type
        } else {
            $regularTypes[$ns] += $type
        }
    }
}

# Build namespace order dynamically: Hexagon.Core first, then alphabetical
$NamespaceOrder = @("Hexagon.Core") + @($regularTypes.Keys | Where-Object { $_ -ne "Hexagon.Core" } | Sort-Object)

# ---- Generate Markdown ----

$sb = [System.Text.StringBuilder]::new()

# Header
[void]$sb.AppendLine("<!-- AUTO-GENERATED by tools/docgen.ps1 - do not edit manually -->")
[void]$sb.AppendLine("<!-- Regenerate with: powershell -ExecutionPolicy Bypass -File tools/docgen.ps1 -->")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("# Hexagon API Reference")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Complete API reference for the Hexagon roleplay framework for s&box (Source 2, C#).")
[void]$sb.AppendLine("")

# Table of contents
[void]$sb.AppendLine("## Table of Contents")
[void]$sb.AppendLine("")
foreach ($ns in $NamespaceOrder) {
    if ($regularTypes.ContainsKey($ns) -and $regularTypes[$ns].Count -gt 0) {
        $anchor = $ns.ToLower().Replace(".", "")
        [void]$sb.AppendLine("- [$ns](#$anchor)")
    }
}
[void]$sb.AppendLine("- [Listener Interfaces](#listener-interfaces)")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")

# Namespace sections
foreach ($ns in $NamespaceOrder) {
    if (-not $regularTypes.ContainsKey($ns) -or $regularTypes[$ns].Count -eq 0) { continue }

    [void]$sb.AppendLine("## $ns")
    [void]$sb.AppendLine("")

    foreach ($type in $regularTypes[$ns]) {
        # Type header
        $header = "### $($type.Name)"
        $modParts = @()
        if ($type.Modifiers) { $modParts += $type.Modifiers }
        if ($type.Kind -ne "class") { $modParts += $type.Kind }
        if ($modParts.Count -gt 0) {
            $header += " ($($modParts -join ' '))"
        }
        if ($type.Inheritance) {
            $header += " : $($type.Inheritance)"
        }
        [void]$sb.AppendLine($header)
        [void]$sb.AppendLine("")

        # Type summary
        if ($type.Summary) {
            [void]$sb.AppendLine($type.Summary)
            [void]$sb.AppendLine("")
        }

        # Members
        if ($type.Members.Count -gt 0) {
            # Code block with signatures
            [void]$sb.AppendLine('```csharp')
            foreach ($member in $type.Members) {
                [void]$sb.AppendLine($member.Signature)
            }
            [void]$sb.AppendLine('```')
            [void]$sb.AppendLine("")

            # Description table
            $hasDescriptions = $false
            foreach ($member in $type.Members) {
                if ($member.Summary) { $hasDescriptions = $true; break }
            }

            if ($hasDescriptions) {
                [void]$sb.AppendLine("| Member | Description |")
                [void]$sb.AppendLine("|--------|-------------|")
                foreach ($member in $type.Members) {
                    $desc = if ($member.Summary) { $member.Summary } else { "" }
                    # Escape pipe characters in description
                    $desc = $desc.Replace("|", "\|")
                    [void]$sb.AppendLine("| ``$($member.Name)`` | $desc |")
                }
                [void]$sb.AppendLine("")
            }
        }
    }

    [void]$sb.AppendLine("---")
    [void]$sb.AppendLine("")
}

# Listener interfaces section
[void]$sb.AppendLine("## Listener Interfaces")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("All listener interfaces for the Hexagon event system. Implement on a Component for auto-discovery via ``Scene.GetAll<T>()``.")
[void]$sb.AppendLine("")

# Sort categories by the namespace order
# Build listener category order dynamically: Core first, then alphabetical
$listenerCategoryOrder = @("Core") + @($listeners.Keys | Where-Object { $_ -ne "Core" } | Sort-Object)

foreach ($cat in $listenerCategoryOrder) {
    if (-not $listeners.ContainsKey($cat) -or $listeners[$cat].Count -eq 0) { continue }

    [void]$sb.AppendLine("### $cat")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine('```csharp')

    foreach ($type in $listeners[$cat]) {
        # Interface summary as comment
        if ($type.Summary) {
            [void]$sb.AppendLine("// $($type.Summary)")
        }
        $inh = if ($type.Inheritance) { " : $($type.Inheritance)" } else { "" }
        [void]$sb.AppendLine("interface $($type.Name)$inh")
        [void]$sb.AppendLine("{")
        foreach ($member in $type.Members) {
            $desc = if ($member.Summary) { " // $($member.Summary)" } else { "" }
            [void]$sb.AppendLine("    $($member.Signature);$desc")
        }
        [void]$sb.AppendLine("}")
        [void]$sb.AppendLine("")
    }

    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine("")
}

# Write output
$outputDir = Split-Path -Parent $OutputFile
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$sb.ToString() | Out-File -FilePath $OutputFile -Encoding UTF8 -NoNewline

$typeCount = $allTypes.Count
$memberCount = ($allTypes | ForEach-Object { $_.Members.Count } | Measure-Object -Sum).Sum
$listenerCount = ($listeners.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum

Write-Host "docgen: Generated $OutputFile"
Write-Host "docgen: $typeCount types, $memberCount members, $listenerCount listener interfaces."
