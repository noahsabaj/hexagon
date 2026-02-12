# Auto-Generated API Reference — Design

## Problem
Hand-written API docs drift from code. When a method signature changes, the docs become stale.

## Solution
A PowerShell script (`tools/docgen.ps1`) that parses `.cs` source files and generates `docs/api-reference.md`. A git pre-commit hook runs it automatically on every commit.

## Architecture

```
Code/*.cs  →  tools/docgen.ps1  →  docs/api-reference.md
                    ↑
            .git/hooks/pre-commit (calls docgen, stages result)
```

## What the parser extracts
- Namespaces (grouping key)
- Public classes, interfaces, enums (with inheritance)
- Public methods, properties, fields (signatures)
- `///` XML doc comment summaries (one-line descriptions)
- Notable attributes: `[Sync]`, `[Rpc.Host]`, `[Rpc.Owner]`, `[Rpc.Broadcast]`, `[Property]`
- Skips: `internal`, `private`, `protected` members

## Output format
Each class gets:
1. Markdown header with class name, modifiers, inheritance
2. One-line class description from `/// <summary>`
3. Code block with all public signatures
4. Table mapping member names to descriptions

## Trigger
Git pre-commit hook:
1. Runs `tools/docgen.ps1`
2. Stages `docs/api-reference.md`
3. Commit includes updated docs automatically

## Files
- `tools/docgen.ps1` — the generator script
- `.git/hooks/pre-commit` — hook that calls the script
- `docs/api-reference.md` — generated output (has header noting it's auto-generated)
