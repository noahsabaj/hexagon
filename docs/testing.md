# Testing and acceptance

`tests/Hexagon.V2.Tests.csproj` builds the Sandbox-independent layers on .NET 10 with nullable analysis and warnings as errors. The suite covers registration, policies, events, persistence/recovery, transaction failure injection, canonical identity, inventory graphs, character lifecycle, capabilities, interactions, item actions, chat, client state, and architecture constraints.

`tools/validate-project.ps1` checks manifests, mounted paths, startup resources,
the whitelisted-library/standalone-game boundary, scene IDs, models, definitions, and
module graphs. `tools/verify.ps1` then runs
tests, generates the actual mounted s&box solution, builds Hexagon and the
schema with warnings as errors, and launches isolated commit/recover smoke runs.
The full command also requires `-RemoteAcceptanceEvidence <manifest.json>`.
Use `-SkipRemoteAcceptance` only for an explicitly incomplete local run.

The verification boundary is split deliberately:

- `tools/verify-portable.ps1` is the hosted, engine-neutral profile. It performs
  a locked restore, treats NuGet audit warnings as errors, inventories direct
  and transitive vulnerabilities, runs the Release suite with warnings as
  errors, and enforces the neutral layer boundary. The `hexagon / neutral`
  GitHub check runs this profile on Ubuntu 24.04 and requires a clean diff.
- `tools/verify.ps1` is the full source-bound local profile. It includes both
  neutral suites, package validation, generated s&box builds, persistence
  commit/recovery smoke, and the remote-evidence gate. It is intentionally not
  assigned to a self-hosted GitHub runner.

HL2RP stores its exact Hexagon dependency in `hexagon.lock.json`. Its hosted
`hl2rp / integration` check validates the lowercase 40-character SHA, checks
out that commit as a sibling, verifies `HEAD`, then runs both locked neutral
suites. A release operator may run `tools/publish-release-evidence.ps1` only
from clean checkouts whose two exact HEADs match the HL2RP lock. The publisher
runs the full verifier before setting `sbox / release-evidence` to success for
both commits.

A final release requires a real dedicated server with two distinct authenticated
remote clients. That run must prove cross-account character isolation,
capability denials, local UI/body/private snapshots for both clients, revocation
on switching, remote combat/scanner/restraint/vendor/storage/world-item behavior,
and exact restart recovery. It is deliberately classified as a manual acceptance
run: this repository cannot create two distinct authenticated Steam sessions and
does not pretend that matching text sentinels independently prove gameplay.

The release target is a local standalone project/dedicated host. This is an
intentional persistence safety boundary: the platform-whitelisted Hexagon library
defines the v3 protocol but contains no raw OS storage implementation. The HL2RP
game supplies that implementation and disables the whitelist for its standalone
build. Manifest, source-boundary, and portable checks fail if either side drifts.

## Manual dedicated-server two-client runbook

Create a source-bound evidence skeleton before starting the run:

```powershell
./tools/verify-remote-acceptance.ps1 `
  -SchemaRoot ../hl2rp-hexagon `
  -EvidencePath ./remote-acceptance.json `
  -HexagonSha <hexagon-40-character-sha> `
  -HL2RPSha <hl2rp-40-character-sha> `
  -WriteTemplate
```

Use the two exact source commits and tracked-tree fingerprint recorded in the
template. The fingerprint covers the complete non-ignored working tree in both
repositories except the generated remote-evidence manifest itself, including
all tracked workflows, project files, configuration, and runtime assets. The
publisher additionally requires both checkouts to be clean. Start one true
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
pass the manifest to `tools/verify.ps1`. The validator rejects mismatched source
SHAs, stale tracked-tree fingerprints, duplicate accounts or process instances, missing scenarios,
unreferenced or changed artifacts, invalid timestamps, and incomplete recovery
metadata. Its success means the manual evidence bundle is complete and has not
changed since hashing; it does **not** execute or independently prove the remote
scenarios. `-SkipRemoteAcceptance` therefore remains an explicitly incomplete
local-only release check.

After both source heads are final and the complete two-client bundle is
available, publish the protected release context with:

```powershell
./tools/publish-release-evidence.ps1 `
  -SchemaRoot ../hl2rp-hexagon `
  -RemoteAcceptanceEvidence ./remote-acceptance.json `
  -HexagonSha <hexagon-40-character-sha> `
  -HL2RPSha <hl2rp-40-character-sha>
```
