# llms-gen.ps1
# Generates llms.txt (index) and llms-full.txt (full docs) at repo root.
# Run automatically by the pre-commit hook alongside docgen.ps1.

param(
    [string]$RootDir = (Split-Path -Parent $PSScriptRoot)
)

$SchemaGuide = Join-Path (Join-Path $RootDir "docs") "schema-guide.md"
$ApiReference = Join-Path (Join-Path $RootDir "docs") "api-reference.md"
$LlmsTxt = Join-Path $RootDir "llms.txt"
$LlmsFullTxt = Join-Path $RootDir "llms-full.txt"

$RepoBase = "https://raw.githubusercontent.com/noahsabaj/hexagon/main"

# ============================================================
# llms.txt — lightweight index
# ============================================================

$index = @"
# Hexagon

> Roleplay framework library for s&box (Source 2, C#). Successor to NutScript and Helix from Garry's Mod, rebuilt for s&box's Scene/Component architecture. A "schema" is a separate s&box Game project that references the Hexagon library to create a specific RP gamemode.

## Docs

- [Schema Developer Guide]($RepoBase/docs/schema-guide.md): How to build schemas -- characters, items, inventory, chat, commands, permissions, UI, and more
- [API Reference]($RepoBase/docs/api-reference.md): Complete public API surface, auto-generated from source code XML doc comments

## Full Documentation

- [llms-full.txt]($RepoBase/llms-full.txt): Complete documentation in a single file for LLM context
"@

$index | Out-File -FilePath $LlmsTxt -Encoding UTF8 -NoNewline
Write-Host "  Generated: llms.txt"

# ============================================================
# llms-full.txt — everything concatenated
# ============================================================

$schemaContent = Get-Content $SchemaGuide -Raw -Encoding UTF8
$apiContent = Get-Content $ApiReference -Raw -Encoding UTF8

# Strip the auto-generated comment header from api-reference
$apiContent = $apiContent -replace '(?s)^<!--.*?-->\s*\n', ''

$full = @"
# Hexagon -- Complete Documentation

> This file contains the full Hexagon framework documentation for LLM consumption.
> Hexagon is a roleplay framework library for s&box (Source 2, C#).
> Source: https://github.com/noahsabaj/hexagon

---

$schemaContent
---

$apiContent
"@

$full | Out-File -FilePath $LlmsFullTxt -Encoding UTF8 -NoNewline
Write-Host "  Generated: llms-full.txt"
