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
    "1"  = @{ File = "Schema-Guide"; Title = "Getting Started" }
    "2"  = @{ File = "Schema-Guide"; Title = $null }  # merged with 1
    "3"  = @{ File = "Characters"; Title = "Characters" }
    "4"  = @{ File = "Factions-and-Classes"; Title = "Factions & Classes" }
    "5"  = @{ File = "Items"; Title = "Items" }
    "6"  = @{ File = "Inventory"; Title = "Inventory" }
    "7"  = @{ File = "Plugins"; Title = "Plugins" }
    "8"  = @{ File = "Chat"; Title = "Chat" }
    "9"  = @{ File = "Commands"; Title = "Commands" }
    "10" = @{ File = "Permissions"; Title = "Permissions" }
    "11" = @{ File = "Currency"; Title = "Currency" }
    "12" = @{ File = "Attributes"; Title = "Attributes" }
    "13" = @{ File = "World-Interaction"; Title = "World Interaction" }
    "14" = @{ File = "UI-Customization"; Title = "UI Customization" }
    "15" = @{ File = "Configuration"; Title = "Configuration" }
    "16" = @{ File = "Events-and-Hooks"; Title = "Events & Hooks" }
    "17" = @{ File = "Logging"; Title = "Logging" }
    "18" = @{ File = "Complete-Example"; Title = "Complete Example" }
}

# --- API reference namespace mapping ---
# Namespace -> (filename without .md, display title for sidebar)
$ApiSectionMap = [ordered]@{
    "Hexagon.Core"         = @{ File = "API-Hexagon.Core"; Title = "Hexagon.Core" }
    "Hexagon.Characters"   = @{ File = "API-Hexagon.Characters"; Title = "Hexagon.Characters" }
    "Hexagon.Factions"     = @{ File = "API-Hexagon.Factions"; Title = "Hexagon.Factions" }
    "Hexagon.Items"        = @{ File = "API-Hexagon.Items"; Title = "Hexagon.Items" }
    "Hexagon.Items.Bases"  = @{ File = "API-Hexagon.Items.Bases"; Title = "Hexagon.Items.Bases" }
    "Hexagon.Inventory"    = @{ File = "API-Hexagon.Inventory"; Title = "Hexagon.Inventory" }
    "Hexagon.Chat"         = @{ File = "API-Hexagon.Chat"; Title = "Hexagon.Chat" }
    "Hexagon.Commands"     = @{ File = "API-Hexagon.Commands"; Title = "Hexagon.Commands" }
    "Hexagon.Permissions"  = @{ File = "API-Hexagon.Permissions"; Title = "Hexagon.Permissions" }
    "Hexagon.Currency"     = @{ File = "API-Hexagon.Currency"; Title = "Hexagon.Currency" }
    "Hexagon.Attributes"   = @{ File = "API-Hexagon.Attributes"; Title = "Hexagon.Attributes" }
    "Hexagon.Config"       = @{ File = "API-Hexagon.Config"; Title = "Hexagon.Config" }
    "Hexagon.Logging"      = @{ File = "API-Hexagon.Logging"; Title = "Hexagon.Logging" }
    "Hexagon.Doors"        = @{ File = "API-Hexagon.Doors"; Title = "Hexagon.Doors" }
    "Hexagon.Storage"      = @{ File = "API-Hexagon.Storage"; Title = "Hexagon.Storage" }
    "Hexagon.Vendors"      = @{ File = "API-Hexagon.Vendors"; Title = "Hexagon.Vendors" }
    "Hexagon.UI"           = @{ File = "API-Hexagon.UI"; Title = "Hexagon.UI" }
    "Hexagon.Persistence"  = @{ File = "API-Hexagon.Persistence"; Title = "Hexagon.Persistence" }
    "Hexagon.Schema"       = @{ File = "API-Hexagon.Schema"; Title = "Hexagon.Schema" }
    "Listener Interfaces"  = @{ File = "API-Listener-Interfaces"; Title = "Listener Interfaces" }
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
    while ($chunk.Count -gt 0 -and ($chunk[$chunk.Count - 1].Trim() -eq '' -or $chunk[$chunk.Count - 1].Trim() -eq '---')) {
        $chunk.RemoveAt($chunk.Count - 1)
    }

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
    # Trim trailing empty lines
    while ($content.Count -gt 0 -and $content[$content.Count - 1].Trim() -eq '') {
        $content.RemoveAt($content.Count - 1)
    }
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

# Write API pages
foreach ($ns in $apiChunks.Keys) {
    $mapping = $ApiSectionMap[$ns]
    if ($null -eq $mapping) {
        Write-Host "  WARNING: No mapping for namespace '$ns'" -ForegroundColor Yellow
        continue
    }
    $fileName = $mapping.File
    $content = $apiChunks[$ns]

    # Trim trailing empty lines
    while ($content.Count -gt 0 -and $content[$content.Count - 1].Trim() -eq '') {
        $content.RemoveAt($content.Count - 1)
    }

    $outPath = Join-Path $OutDir "$fileName.md"
    $content | Out-File -FilePath $outPath -Encoding UTF8
    Write-Host "  API: $fileName.md ($($content.Count) lines)"
}

# ============================================================
# Generate Home.md
# ============================================================

$homeContent = @"
# Hexagon

**Hexagon** is a roleplay framework library for s&box (Source 2, C#). It is the successor to [NutScript](https://github.com/NutScript/NutScript) and [Helix](https://github.com/NebulousCloud/helix) from Garry's Mod, rebuilt from the ground up for s&box's Scene/Component architecture.

A **schema** is a separate s&box Game project that references the Hexagon library to create a specific RP gamemode. Hexagon provides the underlying systems; your schema defines the content, rules, and flavor of your server.

## Schema Developer Guide

Start here if you're building a schema.

- [[Getting Started|Schema-Guide]] -- Project setup and first steps
- [[Characters]] -- Multi-character system with custom data fields
- [[Factions and Classes|Factions-and-Classes]] -- Organizational groups with per-faction classes
- [[Items]] -- Item definitions and custom item types
- [[Inventory]] -- Grid-based inventory system
- [[Plugins]] -- Plugin architecture for modular features
- [[Chat]] -- Class-based chat routing (IC, OOC, yell, whisper)
- [[Commands]] -- Typed argument parsing, permission-gated commands
- [[Permissions]] -- Flag-based character permissions
- [[Currency]] -- Money management with hooks
- [[Attributes]] -- Character stats with boost/debuff system
- [[World Interaction|World-Interaction]] -- Doors, storage, vendors
- [[UI Customization|UI-Customization]] -- Default Razor panels (all overridable)
- [[Configuration]] -- Server and schema configuration
- [[Events and Hooks|Events-and-Hooks]] -- Event system and listener interfaces
- [[Logging]] -- Date-partitioned server logs
- [[Complete Example|Complete-Example]] -- Full working schema

## API Reference

Full public API surface, auto-generated from source code.

- [[Hexagon.Core|API-Hexagon.Core]] -- Framework entry point, events, plugins
- [[Hexagon.Characters|API-Hexagon.Characters]] -- Character data, runtime, networking
- [[Hexagon.Factions|API-Hexagon.Factions]] -- Factions and classes
- [[Hexagon.Items|API-Hexagon.Items]] -- Item definitions and instances
- [[Hexagon.Items.Bases|API-Hexagon.Items.Bases]] -- Built-in item base classes
- [[Hexagon.Inventory|API-Hexagon.Inventory]] -- Inventory management
- [[Hexagon.Chat|API-Hexagon.Chat]] -- Chat classes and routing
- [[Hexagon.Commands|API-Hexagon.Commands]] -- Command definitions and parsing
- [[Hexagon.Permissions|API-Hexagon.Permissions]] -- Permission checks
- [[Hexagon.Currency|API-Hexagon.Currency]] -- Currency operations
- [[Hexagon.Attributes|API-Hexagon.Attributes]] -- Attribute definitions and boosts
- [[Hexagon.Config|API-Hexagon.Config]] -- Configuration system
- [[Hexagon.Logging|API-Hexagon.Logging]] -- Log manager
- [[Hexagon.Doors|API-Hexagon.Doors]] -- Door system
- [[Hexagon.Storage|API-Hexagon.Storage]] -- World storage containers
- [[Hexagon.Vendors|API-Hexagon.Vendors]] -- Vendor buy/sell system
- [[Hexagon.UI|API-Hexagon.UI]] -- UI manager and panels
- [[Hexagon.Persistence|API-Hexagon.Persistence]] -- Database manager
- [[Hexagon.Schema|API-Hexagon.Schema]] -- Skeleton schema reference
- [[Listener Interfaces|API-Listener-Interfaces]] -- All hook/event interfaces
"@

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

# API reference sidebar entries
foreach ($ns in $ApiSectionMap.Keys) {
    $mapping = $ApiSectionMap[$ns]
    $file = $mapping.File
    $title = $mapping.Title
    $sidebarLines.Add("- [[$title|$file]]")
}

$sidebarContent = $sidebarLines -join "`n"
$sidebarContent | Out-File -FilePath (Join-Path $OutDir "_Sidebar.md") -Encoding UTF8
Write-Host "  Generated: _Sidebar.md"

# ============================================================
# Summary
# ============================================================

$totalFiles = (Get-ChildItem $OutDir -Filter "*.md").Count
Write-Host ""
Write-Host "Wiki sync complete: $totalFiles pages in wiki-out/" -ForegroundColor Green
