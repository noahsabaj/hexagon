# wiki-sync.ps1
# Splits docs/schema-guide.md and docs/api-reference.md into individual GitHub Wiki pages.
# Outputs to wiki-out/ directory. Run by the wiki-sync GitHub Action.

param(
    [string]$RootDir = (Split-Path -Parent $PSScriptRoot)
)

$SchemaGuide = Join-Path (Join-Path $RootDir "docs") "schema-guide.md"
$ApiReference = Join-Path (Join-Path $RootDir "docs") "api-reference.md"
$OutDir = Join-Path $RootDir "wiki-out"

# Clean and create output directory
if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Path $OutDir | Out-Null

# --- Schema guide section mapping ---
# Section number -> (filename without .md, display title for sidebar)
# Sections 1+2 merge into Schema-Guide
$SchemaSectionMap = [ordered]@{
    "1"  = @{ File = "Schema-Guide"; Title = "Getting Started"; Desc = "Project setup, scene configuration, your first schema" }
    "2"  = @{ File = "Schema-Guide"; Title = $null; Desc = $null }  # merged with 1
    "3"  = @{ File = "Characters"; Title = "Characters"; Desc = "Multi-character system with custom [CharVar] data fields" }
    "4"  = @{ File = "Factions-and-Classes"; Title = "Factions & Classes"; Desc = "Data-driven faction/class definitions as GameResource assets" }
    "5"  = @{ File = "Items"; Title = "Items"; Desc = "Item definitions (weapons, bags, outfits, ammo, currency) with virtual methods" }
    "6"  = @{ File = "Inventory"; Title = "Inventory"; Desc = "Grid-based inventory with drag-drop and container nesting" }
    "7"  = @{ File = "Plugins"; Title = "Plugins"; Desc = "Extend the framework with [HexPlugin] classes or s&box addon packages" }
    "8"  = @{ File = "Chat"; Title = "Chat"; Desc = "Pluggable chat classes (IC, OOC, whisper, yell, /me, /it, /roll)" }
    "9"  = @{ File = "Commands"; Title = "Commands"; Desc = "Typed argument parsing with permission checks" }
    "10" = @{ File = "Permissions"; Title = "Permissions"; Desc = "Flag-based character permissions with extensible hooks" }
    "11" = @{ File = "Currency"; Title = "Currency"; Desc = "Configurable money system with physical currency items" }
    "12" = @{ File = "Attributes"; Title = "Attributes"; Desc = "Character stats with boost/debuff system" }
    "13" = @{ File = "World-Interaction"; Title = "World Interaction"; Desc = "Doors, storage containers, vendors (IPressable interface)" }
    "14" = @{ File = "UI-Customization"; Title = "UI Customization"; Desc = "Override default panels or add your own" }
    "15" = @{ File = "Configuration"; Title = "Configuration"; Desc = "Server-side config options" }
    "16" = @{ File = "Events-and-Hooks"; Title = "Events & Hooks"; Desc = "Fire, permission-gate, and value-reduce event patterns" }
    "17" = @{ File = "Logging"; Title = "Logging"; Desc = "Date-partitioned server logs for admin review" }
    "18" = @{ File = "Complete-Example"; Title = "Complete Example"; Desc = "Full HL2RP-style schema reference" }
}

# API reference namespace mapping is discovered dynamically from api-reference.md headers.
# No hardcoded namespace list --adding/removing namespaces in Code/ is automatically reflected.

# --- Helper functions ---

function Trim-TrailingLines($list, [switch]$IncludeSeparators) {
    if ($IncludeSeparators) {
        while ($list.Count -gt 0 -and ($list[$list.Count - 1].Trim() -eq '' -or $list[$list.Count - 1].Trim() -eq '---')) {
            $list.RemoveAt($list.Count - 1)
        }
    } else {
        while ($list.Count -gt 0 -and $list[$list.Count - 1].Trim() -eq '') {
            $list.RemoveAt($list.Count - 1)
        }
    }
}

function Get-ApiFileName($ns) {
    if ($ns -eq "Listener Interfaces") { return "API-Listener-Interfaces" }
    return "API-$ns"
}

function Add-ApiLinks($lines, $namespaces) {
    foreach ($ns in $namespaces) {
        $file = Get-ApiFileName $ns
        $title = if ($ns -eq "Listener Interfaces") { "Listener Interfaces" } else { $ns }
        $lines.Add("- [[$title|$file]]")
    }
}

# ============================================================
# Split schema guide
# ============================================================

$schemaLines = Get-Content $SchemaGuide -Encoding UTF8
$schemaChunks = [ordered]@{}  # section number -> lines
$currentSection = $null
$inToc = $false

foreach ($line in $schemaLines) {
    # Skip the file title and ToC block
    if ($line -match '^\s*#{1}\s+Hexagon Schema Developer Guide') { continue }
    if ($line -match '^## Table of Contents') { $inToc = $true; continue }
    if ($inToc) {
        # ToC ends at the next --- or ## header
        if ($line -match '^---' -or ($line -match '^## \d+\.' -and $currentSection -eq $null)) {
            if ($line -match '^## (\d+)\.') {
                $inToc = $false
                $currentSection = $Matches[1]
                $schemaChunks[$currentSection] = [System.Collections.Generic.List[string]]::new()
                # Don't add the header for merged sections (1+2 go into Schema-Guide)
                # Promote ## to # for wiki page title
                $title = ($line -replace '^## \d+\.\s*', '')
                $schemaChunks[$currentSection].Add("# $title")
                continue
            }
            $inToc = $false
            continue
        }
        continue
    }

    # Detect section headers: ## N. Title
    if ($line -match '^## (\d+)\.\s+(.+)$') {
        $currentSection = $Matches[1]
        $title = $Matches[2]
        $schemaChunks[$currentSection] = [System.Collections.Generic.List[string]]::new()
        $schemaChunks[$currentSection].Add("# $title")
        continue
    }

    # Accumulate lines into current section
    if ($null -ne $currentSection -and $schemaChunks.Contains($currentSection)) {
        $schemaChunks[$currentSection].Add($line)
    }
}

# Write schema pages (merge sections 1+2 into Schema-Guide)
$mergedFiles = @{}  # filename -> content lines

foreach ($sectionNum in $schemaChunks.Keys) {
    $mapping = $SchemaSectionMap[$sectionNum]
    if ($null -eq $mapping) { continue }
    $fileName = $mapping.File

    # Trim trailing blank lines and --- from this chunk before merging
    $chunk = $schemaChunks[$sectionNum]
    Trim-TrailingLines $chunk -IncludeSeparators

    if (-not $mergedFiles.ContainsKey($fileName)) {
        $mergedFiles[$fileName] = [System.Collections.Generic.List[string]]::new()
    }
    else {
        # Add separator between merged sections
        $mergedFiles[$fileName].Add("")
        $mergedFiles[$fileName].Add("---")
        $mergedFiles[$fileName].Add("")
    }

    foreach ($l in $chunk) {
        $mergedFiles[$fileName].Add($l)
    }
}

# Trim trailing blank lines and write schema pages
foreach ($fileName in $mergedFiles.Keys) {
    $content = $mergedFiles[$fileName]
    Trim-TrailingLines $content
    $outPath = Join-Path $OutDir "$fileName.md"
    $content | Out-File -FilePath $outPath -Encoding UTF8
    Write-Host "  Schema: $fileName.md ($($content.Count) lines)"
}

# ============================================================
# Split API reference
# ============================================================

$apiLines = Get-Content $ApiReference -Encoding UTF8
$apiChunks = [ordered]@{}  # namespace -> lines
$currentNs = $null
$inApiToc = $false

foreach ($line in $apiLines) {
    # Skip the auto-generated header comments and title
    if ($line -match '^<!--') { continue }
    if ($line -match '^\s*# Hexagon API Reference') { continue }
    if ($line -match '^Complete API reference') { continue }

    # Skip ToC
    if ($line -match '^## Table of Contents') { $inApiToc = $true; continue }
    if ($inApiToc) {
        if ($line -match '^## ' -and $line -notmatch '^## Table') {
            $inApiToc = $false
            # Fall through to process this header
        }
        else {
            continue
        }
    }

    # Detect namespace headers: ## Hexagon.Something or ## Listener Interfaces
    if ($line -match '^## (Hexagon\.\S+|Listener Interfaces)\s*$') {
        $currentNs = $Matches[1]
        $apiChunks[$currentNs] = [System.Collections.Generic.List[string]]::new()
        # Promote to # for wiki page title
        $apiChunks[$currentNs].Add("# $currentNs")
        continue
    }

    # Accumulate lines into current namespace
    if ($null -ne $currentNs -and $apiChunks.Contains($currentNs)) {
        $apiChunks[$currentNs].Add($line)
    }
}

# Write API pages (filenames derived dynamically from namespace)
foreach ($ns in $apiChunks.Keys) {
    $fileName = Get-ApiFileName $ns
    $content = $apiChunks[$ns]
    Trim-TrailingLines $content

    $outPath = Join-Path $OutDir "$fileName.md"
    $content | Out-File -FilePath $outPath -Encoding UTF8
    Write-Host "  API: $fileName.md ($($content.Count) lines)"
}

# ============================================================
# Generate Home.md
# ============================================================

# Build Home.md --landing page with quick start, described guide links, and API summary
$homeLines = [System.Collections.Generic.List[string]]::new()
$homeLines.Add("# Hexagon")
$homeLines.Add("")
$homeLines.Add("**Hexagon** is a roleplay framework library for s&box (Source 2, C#). It is the successor to [NutScript](https://github.com/NutScript/NutScript) and [Helix](https://github.com/NebulousCloud/helix) from Garry's Mod, rebuilt from the ground up for s&box's Scene/Component architecture.")
$homeLines.Add("")
$homeLines.Add("A **schema** is a separate s&box Game project that references the Hexagon library to create a specific RP gamemode. Hexagon provides the underlying systems; your schema defines the content, rules, and flavor of your server.")
$homeLines.Add("")
$homeLines.Add("## Quick Start")
$homeLines.Add("")
$homeLines.Add("Add ``HexagonFramework`` to any GameObject in your scene. That's it -- one component, everything auto-initializes.")
$homeLines.Add("")
$homeLines.Add("``````")
$homeLines.Add("Scene")
$homeLines.Add("  +-- HexagonFramework (Component)")
$homeLines.Add("  +-- Main Camera")
$homeLines.Add("  +-- Directional Light")
$homeLines.Add("  +-- Map")
$homeLines.Add("``````")
$homeLines.Add("")
$homeLines.Add("Then define your character data, factions, items, and plugins. See **[[Getting Started|Schema-Guide]]** for the full walkthrough.")
$homeLines.Add("")
$homeLines.Add("## Schema Developer Guide")
$homeLines.Add("")

# Schema guide links with descriptions
foreach ($sectionNum in $SchemaSectionMap.Keys) {
    $mapping = $SchemaSectionMap[$sectionNum]
    if ($null -eq $mapping.Title) { continue }
    $homeLines.Add("- **[[$($mapping.Title)|$($mapping.File)]]** -- $($mapping.Desc)")
}

$homeLines.Add("")
$homeLines.Add("## API Reference")
$homeLines.Add("")
$homeLines.Add("Full public API surface, auto-generated from source code.")
$homeLines.Add("")

# API links (dynamically from discovered namespaces)
Add-ApiLinks $homeLines $apiChunks.Keys

$homeContent = $homeLines -join "`n"
$homeContent | Out-File -FilePath (Join-Path $OutDir "Home.md") -Encoding UTF8
Write-Host "  Generated: Home.md"

# ============================================================
# Generate _Sidebar.md
# ============================================================

$sidebarLines = [System.Collections.Generic.List[string]]::new()
$sidebarLines.Add("### [[Home]]")
$sidebarLines.Add("")
$sidebarLines.Add("**Schema Guide**")
$sidebarLines.Add("")

# Schema guide sidebar entries (skip section 2, merged with 1)
foreach ($sectionNum in $SchemaSectionMap.Keys) {
    $mapping = $SchemaSectionMap[$sectionNum]
    if ($null -eq $mapping.Title) { continue }  # Skip merged sections
    $file = $mapping.File
    $title = $mapping.Title
    $sidebarLines.Add("- [[$title|$file]]")
}

$sidebarLines.Add("")
$sidebarLines.Add("**API Reference**")
$sidebarLines.Add("")

# API reference sidebar entries (dynamically from discovered namespaces)
Add-ApiLinks $sidebarLines $apiChunks.Keys

$sidebarContent = $sidebarLines -join "`n"
$sidebarContent | Out-File -FilePath (Join-Path $OutDir "_Sidebar.md") -Encoding UTF8
Write-Host "  Generated: _Sidebar.md"

# ============================================================
# Generate _Footer.md
# ============================================================

$footerContent = @"
<p align="center">
<a href="https://github.com/noahsabaj/hexagon">Hexagon</a> | <a href="https://github.com/noahsabaj/hexagon/issues">Report Issue</a> | API docs auto-generated from source
</p>
"@

$footerContent | Out-File -FilePath (Join-Path $OutDir "_Footer.md") -Encoding UTF8
Write-Host "  Generated: _Footer.md"

# ============================================================
# Summary
# ============================================================

$totalFiles = (Get-ChildItem $OutDir -Filter "*.md").Count
Write-Host ""
Write-Host "Wiki sync complete: $totalFiles pages in wiki-out/" -ForegroundColor Green
