# GitHub Wiki Auto-Sync — Design

## Problem
Documentation lives in `docs/schema-guide.md` and `docs/api-reference.md` but users must navigate raw files on GitHub. A GitHub Wiki provides a better reading experience with sidebar navigation and per-topic pages — but manually maintaining wiki pages causes drift.

## Solution
A PowerShell script (`tools/wiki-sync.ps1`) splits the two doc files into individual wiki pages. A GitHub Action pushes them to the wiki repo on every docs change.

## Architecture

```
docs/schema-guide.md   ──┐
                          ├──  tools/wiki-sync.ps1  ──>  wiki-out/*  ──>  hexagon.wiki.git
docs/api-reference.md  ──┘
                                     ↑
                         .github/workflows/wiki-sync.yml (on push to main)
```

Source of truth: `docs/` files in the main repo. Wiki is a derived artifact, regenerated from scratch on every sync.

## Wiki Page Structure

### Schema Guide Pages
- `Schema-Guide.md` — Sections 1+2 (Introduction + Getting Started)
- `Characters.md` — Section 3
- `Factions-and-Classes.md` — Section 4
- `Items.md` — Section 5
- `Inventory.md` — Section 6
- `Plugins.md` — Section 7
- `Chat.md` — Section 8
- `Commands.md` — Section 9
- `Permissions.md` — Section 10
- `Currency.md` — Section 11
- `Attributes.md` — Section 12
- `World-Interaction.md` — Section 13
- `UI-Customization.md` — Section 14
- `Configuration.md` — Section 15
- `Events-and-Hooks.md` — Section 16
- `Logging.md` — Section 17
- `Complete-Example.md` — Section 18

### API Reference Pages
- `API-Hexagon.Core.md` through `API-Hexagon.Schema.md` (one per namespace)
- `API-Listener-Interfaces.md`

### Generated Pages
- `Home.md` — Landing page with project overview and links
- `_Sidebar.md` — Navigation sidebar on every page

## Sync Script (tools/wiki-sync.ps1)

1. Split `schema-guide.md` on `## N.` headers. Sections 1+2 merge into `Schema-Guide.md`, rest become individual pages.
2. Split `api-reference.md` on `## Hexagon.Namespace` headers. Each becomes `API-Hexagon.Namespace.md`. Listener Interfaces section becomes `API-Listener-Interfaces.md`.
3. Generate `Home.md` with project description and navigation links.
4. Generate `_Sidebar.md` with two groups (Schema Guide, API Reference).
5. Rewrite internal anchor links to wiki `[[Page Name]]` links.
6. Output all files to `wiki-out/` directory.

## GitHub Action (.github/workflows/wiki-sync.yml)

- Triggers on push to main when `docs/schema-guide.md` or `docs/api-reference.md` change.
- Runs `tools/wiki-sync.ps1` to generate pages.
- Clones `hexagon.wiki.git`, replaces all files, commits and pushes.
- Uses default `github.token` for auth.

## Files
- `tools/wiki-sync.ps1` — New. Splitting/generation script.
- `.github/workflows/wiki-sync.yml` — New. GitHub Action workflow.
- `docs/plans/2026-02-12-wiki-sync-design.md` — This design doc.
