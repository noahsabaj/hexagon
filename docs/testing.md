# Testing and acceptance

`tests/Hexagon.V2.Tests.csproj` builds the Sandbox-independent layers on .NET 10 with nullable analysis and warnings as errors. The suite covers registration, policies, events, persistence/recovery, transaction failure injection, canonical identity, inventory graphs, character lifecycle, capabilities, interactions, item actions, chat, client state, and architecture constraints.

`tools/validate-project.ps1` checks manifests, mounted paths, startup resources,
scene IDs, models, definitions, and module graphs. `tools/verify.ps1` then runs
tests, generates the actual mounted s&box solution, builds Hexagon and the
schema with warnings as errors, and launches isolated commit/recover smoke runs.
The full command also requires `-RemoteAcceptanceEvidence <manifest.json>`.
Use `-SkipRemoteAcceptance` only for an explicitly incomplete local run.

A final release requires a real dedicated server with two distinct authenticated
remote clients. That run must prove cross-account character isolation,
capability denials, local UI/body/private snapshots for both clients, revocation
on switching, remote combat/scanner/restraint/vendor/storage/world-item behavior,
and exact restart recovery. It is deliberately classified as a manual acceptance
run: this repository cannot create two distinct authenticated Steam sessions and
does not pretend that matching text sentinels independently prove gameplay.

## Manual dedicated-server two-client runbook

Create a source-bound evidence skeleton before starting the run:

```powershell
./tools/verify-remote-acceptance.ps1 `
  -SchemaRoot ../hl2rp-hexagon `
  -EvidencePath ./remote-acceptance.json `
  -WriteTemplate
```

Use the exact source tree whose fingerprint is in the template. Start one true
dedicated-server process and two remote client processes authenticated as the
two different nonzero accounts recorded as roles A and B. Capture continuous
raw logs from all three processes and record distinct process-instance labels
and UTC start, capture, completion, and attestation times.

Exercise every observation in the template. For each one, record a concrete
description of what was observed and reference at least one contemporaneous
artifact. Add screenshots, video, or traces where a raw log cannot show the UI
or isolation result. Shut down cleanly, restart the dedicated server, and record
the recovered sequence and digest after verifying characters, inventories,
world items, entity state, and typed traits.

Hash every referenced artifact, complete the named operator attestation, then
pass the manifest to `tools/verify.ps1`. The validator rejects stale source
fingerprints, duplicate accounts or process instances, missing scenarios,
unreferenced or changed artifacts, invalid timestamps, and incomplete recovery
metadata. Its success means the manual evidence bundle is complete and has not
changed since hashing; it does **not** execute or independently prove the remote
scenarios. `-SkipRemoteAcceptance` therefore remains an explicitly incomplete
local-only release check.
