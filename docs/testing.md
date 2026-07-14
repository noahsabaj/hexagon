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
  neutral suites, package validation, generated s&box builds, a client-equivalent
  archive build checked by the installed `Sandbox.Access` verifier, persistence
  commit/recovery smoke, and the remote-evidence gate. Raw operating-system server
  code is excluded exactly as s&box excludes `.Server.cs` from remote clients.
  PowerShell 7 is required for this binary access-control step. The profile is not
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

On a fresh persistence root, bootstrap authorization **before** the timed
acceptance run. An authenticated account must already be able to use HL2RP's
Access workspace so the two test accounts can receive the restricted
entitlements needed for Civil Protection, scanner, restraint/search, and City
Administration scenarios. For a local fresh-store setup, temporarily enter an
operator Steam ID64 in the scene's `HL2RP Bootstrap Operators` component, use
that authenticated editor session to grant the required entitlements, shut down
cleanly so those grants are durable, then close/reload without saving the
operator ID into the tracked scene. This is setup, not dedicated-server
acceptance evidence. Confirm both exact source worktrees are clean afterward;
do not publish a personal account ID or attest an editor-hosted setup run.

Create the evidence directory and source-bound skeleton **outside both Git worktrees**.
This is required because the release publisher rejects tracked or
untracked worktree changes, including a manifest created beside the scripts:

```powershell
$evidence = 'C:\HexagonReleaseEvidence\<run-id>'
New-Item -ItemType Directory -Path $evidence
./tools/verify-remote-acceptance.ps1 `
  -SchemaRoot ../hl2rp-hexagon `
  -EvidencePath "$evidence\remote-acceptance.json" `
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
or isolation result.

For restart evidence, preserve the **complete raw server log from each server
process** before s&box rotates or overwrites it. After the first process becomes
quiescent and immediately before its persistence shutdown, it emits exactly one:

```text
HL2RP_RECOVERY_SNAPSHOT phase=pre_shutdown sequence=<positive-integer> digest=<lowercase-64-hex>
```

Wait for that line and `HEXAGON_DRAINED host`, copy that process's untouched log
to `$evidence\server-pre-shutdown.log`, and only then restart the same dedicated
server against the same persistence root and exact source commits. Successful
recovery emits exactly one:

```text
HL2RP_RECOVERY_SNAPSHOT phase=post_restart sequence=<positive-integer> digest=<lowercase-64-hex>
```

Copy the restarted process's untouched log to
`$evidence\server-post-restart.log`. Verify the restored characters,
inventories, world items, entity state, and typed traits in both clients. The
two sequence values and two digests must match exactly; otherwise the restart
observation failed and must not be attested.

Extract the values from the raw artifacts rather than retyping or constructing
a marker file. This PowerShell fragment fails closed on missing, malformed, or
duplicate markers and prints the values to enter under
`recovery.pre_shutdown` and `recovery.post_restart`; keep each template
`artifact_id` pointed at the corresponding hashed raw server log:

```powershell
$pattern = 'HL2RP_RECOVERY_SNAPSHOT phase={0} sequence=(?<sequence>[1-9][0-9]*) digest=(?<digest>[a-f0-9]{{64}})(?=[ \t]*\r?$)'
$snapshots = @{}
foreach ($phase in 'pre_shutdown', 'post_restart') {
  $path = Join-Path $evidence "server-$($phase.Replace('_', '-')).log"
  $matches = [regex]::Matches(
    (Get-Content -LiteralPath $path -Raw),
    ($pattern -f $phase),
    [Text.RegularExpressions.RegexOptions]::Multiline)
  if ($matches.Count -ne 1) { throw "$phase marker count was $($matches.Count), expected 1" }
  $snapshots[$phase] = [pscustomobject]@{
    sequence = [long]$matches[0].Groups['sequence'].Value
    digest = $matches[0].Groups['digest'].Value
  }
}
if ($snapshots.pre_shutdown.sequence -ne $snapshots.post_restart.sequence -or
    $snapshots.pre_shutdown.digest -cne $snapshots.post_restart.digest) {
  throw 'Restart did not recover the exact pre-shutdown state.'
}
$snapshots
```

Hash every referenced artifact, complete the named operator attestation, then
pass the manifest to `tools/verify.ps1`. The validator rejects mismatched source
SHAs, stale tracked-tree fingerprints, numerically duplicate accounts or process
instances, non-Boolean attestations, missing scenarios, reused or aliased
artifact paths, unreferenced or changed artifacts, timestamps more than five
minutes in the future, ambiguous recovery markers, manifest/log disagreements,
and non-identical pre-shutdown/post-restart snapshots. Exactly two distinct
server-log artifacts are required, and the `restart_restores_all_state`
observation must reference both raw
server-log artifacts. Validator success means the manual evidence bundle is complete and has not
changed since hashing; it does **not** execute or independently prove the remote
scenarios. `-SkipRemoteAcceptance` therefore remains an explicitly incomplete
local-only release check.

After both source heads are final and the complete two-client bundle is
available, publish the protected release context with:

```powershell
./tools/publish-release-evidence.ps1 `
  -SchemaRoot ../hl2rp-hexagon `
  -RemoteAcceptanceEvidence "$evidence\remote-acceptance.json" `
  -HexagonSha <hexagon-40-character-sha> `
  -HL2RPSha <hl2rp-40-character-sha>
```
