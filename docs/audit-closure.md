# v2 audit closure registers

Dated registers of what each review found and how it was closed. Test totals inside a register
are evidence snapshots for that date, not current-suite claims. The newest register is last.

## Before 2026-07-17 (condensed 2026-09-18)

Three early cohorts were recorded here as finding-by-finding tables: the original v2 cutover
(31 findings, 2026-07-12), the current-worktree cohort (19 findings, 2026-07-13), and the
64-agent audit remediation (28 findings, 2026-07-16). Every row was a closed finding, so the
tables were removed on 2026-09-18. They remain in git history, and the commits they describe
are the authoritative record.

What from those cohorts is still in force:

- The dedicated-server, two-authenticated-client release evidence is mandatory and still
  pending. `sbox / release-evidence` stays pending on both repositories until two
  authenticated clients validate both exact source SHAs. This PC is not to be registered as a
  self-hosted runner.
- Both `main` branches carry strict required-status protection: `hexagon / neutral` and
  `hl2rp / integration`, each with `sbox / release-evidence`. Administrators are covered, and
  force-push and deletion are disabled.
- The advisory `cross-head-canary` job tests the HL2RP branch named by `HL2RP_CONSUMER_REF`,
  not `hl2rp@main`, which has no tests. It must never enter branch protection.
- Restores are audited against sources pinned in tracked `NuGet.config` files, and the
  vulnerability gate parses the pinned JSON schema rather than probing for a substring.
- Portable verification fails closed on a dirty worktree. `-AllowDirty` is an explicit escape
  hatch that rewrites the banner and skips SHA attribution.
- Host migration is refused in depth by `Networking.config` in both repositories and by the
  `OnBecameHost` handler.
- The 2026-07-14 client-archive regression is why game code streamed to clients must pass
  `tools/verify-client-assembly-access.ps1`: raw OS persistence APIs fail s&box access control
  on a remote client. The 2026-07-17 register below supersedes the storage design from that fix.

## 2026-07-17 whitelist-safe storage unification (publish-route blocker)

Pre-flight for the two-client acceptance run surfaced an engine constraint the prior
registers did not account for: publishing to sbox.game requires `IsStandaloneOnly: false`,
and once that flag is off, s&box access control applies to the streamed game assembly on
**every** host — including the dedicated server loading its own package (`PackageLoader`
enforces the whitelist for all non-local packages, with no server-specific rule set in
`Sandbox.Access/Rules`). The raw-OS dedicated-server storage
(`HL2RPPhysicalPersistenceStorageCore.Server.cs`: `File`/`Directory`/`SafeFileHandle`/
`FileOptions.WriteThrough`) is not whitelisted, so the posture TEST-04 and RC-1 recorded —
standalone-only game owning a raw OS adapter — was structurally incompatible with
publication. Those historical entries stand as written; this register supersedes the
posture they describe.

Resolution — one whitelist-safe storage path for every host shape:

- `HL2RPDurableStorageCore` (engine-neutral, test-compiled) now carries the whole durable
  storage protocol over `IHL2RPStorageFileSystem`, the exact whitelist-safe
  `Sandbox.BaseFileSystem` surface. The bodies are the verbatim staged-verify-copy and
  exclusive-lease implementations previously proven in the editor adapter, whose behavior
  the `IPersistenceStorage` contract and the ARCH-05 torn-tail recovery closure already
  cover. `HL2RPPersistenceStorageFactory` binds it to the engine filesystem with no
  compilation-boundary fork; the retired `HL2RPPhysicalPersistenceStorage*.Server.cs` and
  `HL2RPSandboxPersistenceStorage.cs` twins are deleted.
- The lease remains process-exclusive: `BaseFileSystem.OpenWrite` opens the physical
  backing file with no shared writer (Zio `PhysicalFileSystem` default share), and the
  held stream is the lease resource.
- **Durability trade, stated plainly:** whitelist-safe code cannot flush past the
  operating-system cache and has no atomic rename. Process-crash durability, the
  single-writer lease, staged+verified publication, and fail-closed recovery are
  unchanged; durability against power loss is now bounded by the OS cache, and a crash in
  the short final-copy window can leave one torn artifact at the final name — exactly the
  per-metadata-class torn-tail case ARCH-05 closed with dedicated recovery tokens.
- `hl2rp.sbproj` flips `IsStandaloneOnly` to `false` and `Metadata.Compiler.Whitelist` to
  `true`, so the engine compiler now enforces the whitelist at build time in the editor,
  in `verify.ps1`'s generated builds, and in the smoke boot. `validate-project.ps1`,
  `validate-framework-portable.ps1`, and the hexagon layout pins
  (`GameUsesOneWhitelistSafeStoragePathForEveryHost`,
  `GameOwnsAResolvableStartupScene`) now fail closed on any return of raw OS access or a
  silent whitelist downgrade.
- `docs/testing.md`'s immutable-release enablement command is corrected from `PATCH` with
  a body to a bare `PUT` (the PATCH form 404s; PUT verified against the live repository
  on 2026-07-17).

### Evidence boundary for this register

- Neutral Release suites at these heads: **Hexagon 381/381**, **HL2RP 298/298** (the four
  physical-core tests were ported, not dropped: `DurableStorageCoreTests` executes the
  production core — lease conflict, absent-or-complete publication with no visible
  staging, traversal rejection, and the full provider commit→shutdown→recovery round
  trip — through a physical boundary implementation encoding the no-shared-writer
  contract).
- The source-bound `tools/verify.ps1 -SchemaRoot ../hl2rp-hexagon -SkipRemoteAcceptance`
  run (editor closed) passed end to end at these heads, including three generated
  warnings-as-errors builds (0/0), and — decisive for this register — the
  client-equivalent assembly passed the installed `Sandbox.Access` verifier with the
  unified storage compiled in. Because the server source shape now equals the client
  shape, that gate is authoritative for every host. The headless commit → drain → exact
  recovery smoke ran through the new core.
- Still open, unchanged: organization identities in both `.sbproj` manifests (`local` →
  the owner's sbox.game org), package publication, and the two-client acceptance run with
  its `sbox / release-evidence` contexts.

## 2026-07-20 persistence self-heal (scene-handoff barrier + operator quarantine)

The paused acceptance run's editor iteration surfaced a latent operational wedge: after any
ungraceful stop (an editor `play_stop` that did not await the async drain, a killed editor, a
force power-off) the next Play hung, and only an editor-process recycle plus a hand-moved store
folder recovered it. Root cause was `SceneShutdownBarrier`, a per-process static that carries the
previous host's drain `Task` across an in-process scene replacement so the successor awaits it
before opening storage. That await is correct, but `InitializeHostAsync` also **failed the host
closed** whenever the awaited drain reported failure — a purely in-memory signal that conflates a
benign engine-teardown fault (for example a destroyed `GameObject` during disembody) with a real
durability fault, and that a refused Play then re-published on its own teardown, so every
subsequent Play in the process failed closed. The genuine authorities on whether a store may be
opened — the exclusive lease (`LeaseUnavailable` for a live owner) and WAL recovery
(`PersistenceCorruptionException` for real corruption) — run immediately afterward, so the barrier
was a redundant, coarser, self-poisoning gate.

Resolution — the barrier becomes an ordering primitive, and genuine corruption gains a deliberate
recovery:

- `InitializeHostAsync` awaits the predecessor drain (time-boxed at 10s so a stranded drain cannot
  hang init — on timeout it proceeds under lease protection) and then **always proceeds**,
  deferring ownership and integrity to the lease and recovery. The classification is extracted to
  the engine-free, test-compiled `SceneHandoffPolicy` seam, which pins the invariant that no
  predecessor outcome fails the successor closed; a non-clean predecessor is a
  `HEXAGON_HANDOFF_DEGRADED` warning, never a `FailHost`. The old `did not drain cleanly` fatal is
  removed. Genuine corruption still fails closed, precisely, at the recovery gate.
- Fatal corruption stays fail-closed with no automatic discard. The server ConVar
  `hexagon-persistence-quarantine`, armed by an operator for one host start, makes recovery — only
  on a genuine `PersistenceCorruptionException`, never a tolerated torn tail or an intact store —
  archive the unreadable WAL and checkpoint artifacts under `quarantine/<writer-epoch>/` (holding
  the lease it already acquired) and rebuild a clean store at sequence zero
  (`HEXAGON_PERSISTENCE_QUARANTINED`, `PersistenceHealth.RecoveredByQuarantine`), then reset the
  switch. `format.json` and the lease are left in place so the store keeps its slot. This accepts
  the resulting data loss as the recovery action.
- Position authority and every other lifecycle path are unchanged; this register touches only the
  in-process store handoff and the corruption-recovery affordance.

### Evidence boundary for this register

- Neutral suites at this head: **Hexagon 379/379** (371 + a four-case `SceneHandoffPolicy` seam
  test, three provider quarantine tests spanning armed-heal / intact-untouched / torn-tail-not-
  quarantined, and a `LayerBoundaryTests` source pin that the handoff defers to recovery and the
  quarantine is ConVar-gated), **HL2RP 298/298** (links the same persistence source).
- Source-bound `verify.ps1` sub-steps ran with the editor closed: the generated warnings-as-errors
  s&box build compiled **0/0** (authoritative whitelist gate — the bounded wait built from
  `CancellationTokenSource.CancelAfter` + `TaskCompletionSource` + `ContinueWith`, the ConVar, and
  the provider quarantine all pass the whitelist), the client-equivalent assembly passed
  `Sandbox.Access`, and the headless commit → drain → exact-recovery smoke passed.
- **Live editor verification (MCP-driven, 2026-07-20)** on an isolated data root
  (`hexagon-data-root`), the real store untouched: a corrupted WAL frame failed the host closed
  with the precise `Persistence recovery failed … frame … failed verification`; that failed drain
  poisoned the barrier, and the next Play emitted `HEXAGON_HANDOFF_DEGRADED
  state=DrainReportedFailure … deferring … to the lease and recovery` and reached the authoritative
  recovery gate rather than the old fatal `did not drain cleanly` — the self-heal, observed live.
  Arming `hexagon-persistence-quarantine` then archived 36 artifacts, rebuilt a clean store
  (`sequence=0 quarantined=True`), reached `HEXAGON_READY host`, and consumed the one-shot; the
  editor's real store still recovered at `sequence=19`. The editor's own compile reported
  `kbj.hexagon` 0/0.
- Still open, unchanged: the two-client acceptance run and its `sbox / release-evidence` contexts;
  the branch is not yet re-frozen or re-published.

## 2026-07-20 scoped re-audit remediation (movement authority + quarantine hardening)

A scoped re-audit of the two deliberate authority changes (movement server-simulated →
server-validated; persistence store-open no longer fail-closing on a predecessor drain) — two
adversarial agents plus direct verification against the engine source — found the movement change
under-delivered and turned up three lower findings in the self-heal's quarantine.

**MOVE-RE-01 (High, confirmed against `sbox-public`).** "Server-validated" position was
detection-only, not enforcing, so a client could still mint spatial authority — contradicting the
audited `ClientInputCannotBecomeSpatialAuthority` invariant (which was a source-text pin that
verified the structure existed, not that it enforced). The player is a connection-owned object;
the engine authors a networked transform only on the owner (`NetworkObject.WriteSnapshotState`,
`if ( !IsProxy )`), so on the host a remote player is a proxy whose transform the host cannot
write. `HexPlayerBody.IssueCorrection` only sets `[Sync(FromHost)]` fields; the actual snap-back
(`OwnerApplyCorrection`) runs on the owner, which a cheat client ignores — and nothing escalated.
Meanwhile every gameplay decision (interaction reach, LOS, combat trace, proximity chat, encounter,
pickup) read the raw client `body.WorldPosition`, plus the validator's `StepSkin=16` allowed ~3.75×
speed / straight-up flight even for a compliant client, and the frozen check leaked the same skin.

Remediation — keep the client-owned idiom, make the host authoritative for *gameplay* and enforce:
- **Tightened validator** (`HexMovementValidator`): the flat `StepSkin=16` per-tick grant is split
  into a small `PositionSkin=4` jitter/impulse epsilon plus a `StepRise=16` vertical step allowed
  only alongside horizontal motion (no straight-up flight), a tighter `TeleportGuard` (384), and a
  dedicated tight `FrozenSkin=2` for locked/dead players. Constants are conservative framework
  defaults, tunable once the two-client run exercises real remote movement.
- **Host-authoritative gameplay position** (`HexPlayerBody.AuthoritativeWorldPosition` +
  `AuthoritativeWorldPositionOf(GameObject)`): on the host a remote proxy resolves to the last
  position that passed validation — a client cannot teleport it. All ten player-position reads in
  the gamemode (interaction context/LOS, pistol origin, chat, nearest-target, encounter, drop
  transform) were migrated to it; the host's own non-proxy body keeps the live transform.
- **Enforcement**: a leaky-bucket violation score (`+1` corrected / `-1` accepted) kicks a client
  (`Connection.Kick`) after a sustained run of out-of-envelope reports (`HEXAGON_MOVEMENT_KICK`).
- **Honest residual (documented, tracked):** *bounded* within-envelope speed/fly (≈1.5× horizontal;
  vertical bounded to jump/run) remains possible without host-side ground-truth movement simulation
  — the very thing that caused the origin-pin wedge. This is a movement-quality residual, not a
  spatial-authority breach: teleport/instant-reposition/interact-from-arbitrary-point is closed, and
  sustained gross violation is kicked. `ClientInputCannotBecomeSpatialAuthority` now pins the
  enforcement (`AuthoritativeWorldPosition` + `Connection.Kick`), not just the structure.

**Quarantine hardening (from the self-heal re-audit).** QUAR-01 (Medium): the one-shot ConVar was
reset only when quarantine actually consumed, so an armed start that did not fire left it armed for
a later store — now the arming is consumed at the start of every host attempt regardless of outcome.
QUAR-02/03 (Low, documented as expected in `persistence.md`): whole-store rebuild archives a
readable-but-chain-broken checkpoint along with the corruption (recoverable under `quarantine/`),
and `format.json` corruption is preserved rather than self-healed. The re-audit confirmed the
self-heal's hard guarantees hold — the exclusive OS-file lease, not the barrier, gates ownership;
no double-writer, no data loss by deferral, genuine corruption still fatal when unarmed.

### Evidence boundary for this register

- Neutral suites: **Hexagon 384/384** (five new `HexMovementValidator` tightening cases; the
  `ClientInputCannotBecomeSpatialAuthority` and quarantine pins updated to the enforcing model),
  **HL2RP 298/298** (the migrated gameplay files are engine-coupled, exercised by the editor compile
  and the two-client run, not the neutral suite).
- Editor whitelist compile clean: `kbj.hexagon` 0/0, `kbj.hl2rp` 0 errors (`Connection.Kick`,
  `AuthoritativeWorldPosition`, and the migrations all pass s&box access control). Single-editor Play
  reached `HEXAGON_READY host`/`client` with no regression (the host body is non-proxy, so gameplay
  uses the live transform exactly as before).
- NOT yet exercised: the validator envelope and host-authoritative reads against a real *remote*
  proxy — that lands in the two-client acceptance run, where the validator constants are tuned and
  the kick threshold confirmed against live latency.

## Register 2026-07-29 (round 3) — the movement envelope was a rate, not a bound

Round 3 re-examined the round-1 movement remediation above rather than sweeping for new defects, on
the principle that a passing source-text pin can be vacuous. It found that the tightened validator
closed the wrong half of the problem, and that three artifacts asserted protections the code did not
provide.

**M1 (High) — `HexMovementValidator` was memoryless.** `Evaluate` clamped each per-tick delta and kept
no state, so every allowance was re-granted every tick and became a rate a client could sustain
forever. Measured by compiling the real source into a probe at engine ground truth (`RunSpeed=320`,
`JumpSpeed=300`, `FixedUpdateFrequency=50`, all read from `sbox-public`):

| Motion | Sustained, zero corrections |
|---|---|
| Horizontal | 600 u/s vs 320 legit (1.88x) |
| ...of which `PositionSkin` alone | 200 u/s, independent of `dt` |
| Vertical climb (6 u/tick horizontal) | 1450 u/s, 78-degree ascent |
| Vertical climb, zero horizontal | 650 u/s |
| Frozen (dead / locked) | 100 u/s |

**Annotation to the round-1 register above.** Its residual claim — "≈1.5x horizontal; vertical bounded
to jump/run" and "no straight-up flight" — was wrong on both counts, and is left in place per the
convention that historical entries are annotated rather than rewritten. The measured figures are
1.88x horizontal and 650 u/s of sustained straight-up flight. The claim was not tested because every
validator test asserted a single tick, which structurally cannot express a rate.

**The guard that blessed the exploit.** `RejectsStraightUpFlightWithoutHorizontalMotion` rejected one
30-unit tick and was read as proving flight impossible. Meanwhile the *passing*
`AllowsAStepUpWhileMovingHorizontally` blessed `(6, 0, 18)` — repeated every tick that is 900 u/s,
180 m of climb in ten seconds. The test suite did not merely miss the defect; it certified its
vector.

**Root cause under the root cause.** The host validated step-ups against a hand-picked `StepRise=16`
while clients actually stepped by `MoveModeWalk.StepUpHeight`, which the engine defaults to **18**,
and the host modelled no ground-angle limit at all. Two numbers that must agree, declared
independently in two repositories, disagreeing.

Remediation:
- **Stateful validator.** `Evaluate` now takes and returns `MovementState`. Horizontal is bounded as
  a rate over any window, with the jitter reservoir refilled only from unused budget so a burst is
  absorbed without raising the sustained ceiling; it explicitly cannot fund vertical rise, because it
  refills from unused *horizontal* budget and standing still would otherwise pay for a climb.
  Vertical is integrated: rise is paid from an inferred vertical speed that only a host-observed
  ground contact re-arms and gravity decays each airborne tick. A grounded client's rise is bounded
  by horizontal travel through the ground angle.
- **Host ground truth.** `HexPlayerBody.HostObservesGround` uses the controller's own public
  `PlayerController.TraceBody`, so the host's notion of ground is computed by the engine's code with
  the engine's body dimensions rather than an approximation that drifts from client physics. A short
  probe box is traced so a ducked player — whose `CurrentHeight` the host cannot observe — measures
  the same as a standing one. A trace failure fails closed to "airborne", denying the allowance.
- **One envelope, both ends.** `HexMovementEnvelope` is operator-tunable (`hexagon-movement-*`
  ConVars) and bounds-checked, falling back to the framework default when a value is out of range.
  The geometric values are published to the owner over `[Sync(FromHost)]` and applied by
  `HexMoveModeWalk : MoveModeWalk`, so client physics and host validation configure from one source.
  Composition, not subclassing: `PlayerController` is `sealed`, `MoveModeWalk` is not.
- **Enforcement in seconds.** The violation counter is now sustained-violation *seconds* rather than a
  tick count, which silently changed meaning whenever the tick rate or correction cooldown moved.
  Every violation is logged (`HEXAGON_MOVEMENT_VIOLATION`), not only the kick, so the two-client run
  can produce a false-positive rate without shipping a degraded posture.
- **M2 (Low) — `RpcAdmissionTests.ChargesAdmission`** substring-matched the method body, which retains
  interior trivia, so a comment mentioning the admission call satisfied the guard. It now matches
  `InvocationExpressionSyntax` nodes.

**Guarding the class, not the instance.** `tests/Foundation/SustainedEnvelopeHarness.cs` drives any
envelope or budget rule over many ticks and reports accumulated work, so a per-tick allowance that is
secretly a rate fails on arrival. It is pointed at the movement validator and at the command token
bucket, and `TheSecurityDocumentPublishesTheBudgetThatIsActuallyEnforced` parses the published numbers
out of `docs/security.md` and asserts them against the enforcing constants — making the prose a claim
the suite owns rather than a description that can drift.

The command bucket was audited with the same harness and found sound: `16`/`8`/`16` match the document
exactly, and it already integrated over time correctly. No change was needed there.

### Evidence boundary for this register

- Every sustained guard was **watched to fail before it passed**: six failures against the pre-fix
  validator at 4.7x, 87.5x, 62.5x, and 1.5x their bounds, with the greedy adversary extracting 14,500
  units of climb in ten seconds. The assertions did not change when the validator gained state; only
  the driver did.
- Neutral suites: **Hexagon 423/423** (up from 412: five sustained movement guards, four command-budget
  guards, and two envelope/priming cases), **HL2RP 298/298**.
- Both Runtime layers compiled directly — `dotnet build Code/hexagon.csproj` and `Code/hl2rp.csproj`,
  0 warnings, 0 errors — because the neutral suite structurally cannot compile the Sandbox-bound
  layer where `HexPlayerBody`, `HexMoveModeWalk`, and the ground probe live.
- **NOT yet exercised:** `TraceBody` called by the host on a *client-owned proxy*. It was verified by
  source reading to depend only on `BodyRadius`, `CurrentHeight`, `GameObject`, and `Tags` — all
  present on a proxy — and to run `Scene.Trace` against the host's physics world, but a proxy requires
  a second authenticated client and lands in the two-client acceptance run. The envelope constants
  and the kick threshold are still tuned there.

## Register 2026-07-29 (round 3, part 2) — the movement envelope was auditing the wrong signal

Round 3's windowed-envelope fix above was live-verified on a dedicated server with one remote client
and **failed**: honest sprinting and jumping rubber-banded, logging 85 violations during normal play.
Two further fixes were needed, and the second was architectural.

**M3 (High) — the host cannot observe the client's reported position at all.** For a proxy,
`GameObject.WorldPosition` is the INTERPOLATED transform; the raw received value lives in
`GameTransform.TargetLocal`, which is `internal` and used only by `NetworkObject`'s own send/receive
paths (`NetworkObject.cs` 636/709/774/849). A per-tick delta from that signal is an artifact of buffer
depth and packet timing. Measured live: an honest sprint produced deltas quantised at **19.2 units,
twelve times identically**, against a per-tick budget of about 15.

Per-tick validation with snap-back IS the industry idiom, but every engine that uses it validates the
client's CLAIMED position out of a movement packet. s&box exposes no such input to addons. **We copied
the idiom without the input it assumes, and that is the whole defect** — no choice of constants fixes a
signal that does not carry per-tick truth. A first attempt to accumulate `dt` until the position moved
cut violations 85 to 33 but could not converge, because interpolation moves the position slightly on
most ticks.

A second-order defect made it self-sustaining: a correction makes the owner snap
(`OwnerApplyCorrection` -> `ClearInterpolation`), the snap returns to the host as another large discrete
delta, which trips another correction. **The corrective loop fed itself**, which is what players
experience as rubber-banding, and it is why walking escaped — its round-trip distance stayed inside the
envelope.

Remediation — audit over a window instead (`HexMovementValidator.Observe`, model `windowed-audit-v1`):
- **Per sample:** a teleport guard only, whose margin no interpolation artifact approaches, corrected
  immediately through the host-authored channel.
- **Per window (1s):** horizontal PATH against the run envelope plus a fixed interpolation slack, and
  NET RISE against horizontal path through the ground angle plus one jump apex.
- **Soundness rests on one property**, pinned by `AnIdenticalPathIsMeasuredTheSameHoweverItIsChunked`:
  summing deltas over a window is invariant to how the interpolator chunks the travel. Per-tick
  checking fails that by construction.
- **No per-window snap-back.** A failed window escalates the violation clock toward a kick. Reversing a
  window of travel is worse for an honest player than the abuse, and per-tick snapping fed the loop.
- Deleted outright: the skin reservoir, the step budget, per-tick vertical integration, and the
  `TraceBody` ground probe — the windowed rise check needs no per-tick grounded state, which also
  retires the unverified `TraceBody`-on-a-proxy dependency the previous register recorded.

**Idiomaticity was investigated before committing to this** (user-directed). s&box ships no movement
validation, so there is no engine idiom to conform to. Per-tick is the wider industry convention and
windowing is not — that is recorded as a real mark against it. Two idiom-informed corrections were
adopted anyway: the teleport clamp stays per-sample because that check IS conventional and robust here,
and corrections are no longer issued on every trip.

### Evidence boundary for this register

- **Live-verified 2026-07-29 on a dedicated server with one authenticated remote client**, at STOCK
  settings (`angle=45 step=18`, no ConVar overrides): **0 violations, 0 teleport corrections, 0 kicks**
  across a full sprint/jump/stairs/fall session. Prior runs on the per-tick model logged 85, 33 and 55.
- The live build is now self-identifying: `HEXAGON_MOVEMENT_MODEL=windowed-audit-v1 ...` is logged at
  player spawn. **This was added because three earlier playtest sessions unknowingly ran code that was
  never deployed** — a publish that created no revision, then a stale package — and nothing in the logs
  said which build was live. The old log format was the only tell.
- Neutral suites: **Hexagon 415/415**, **HL2RP 298/298**; both Runtime layers compile clean.
- **NOT yet exercised: the detection path firing.** Zero violations proves no false positives, not that
  the audit catches anything. The offline tests cover a 2x speed run and an impossible climb, but no
  guard has been watched to fire live. Tightening `hexagon-movement-horizontal-tolerance` to 1.0 with
  `slack 0` would make ordinary sprinting exceed the window and exercise detection and kick end to end
  without building a cheat client.
- **NOT yet answered: the ground-angle sync check.** `HexMoveModeWalk` reads host-published values but
  bails when they are zero, and the defaults equal the engine's, so a sync failure stays invisible at
  stock settings. Needs steep geometry or a deliberately distinctive value.

## Register 2026-07-30 (round 2) — the identity boundary, and three guards nobody had watched

Round 2 swept the risk-weighted half of the codebase, then the ~9,000 lines round 1 never examined
(the four large HL2RP files, UI/Razor, Editor, release tooling). It produced one finding of substance
and three observability items. This register is written after the fact, from the commits and from the
live sessions that closed the items; the round itself predates it.

**F1 (High) — character `Name` and `Description` were the only player-authored strings with no
control-character, bidi or normalisation check**, while every other string in the codebase had one,
and a name reaches other players verbatim through recognition records and `[Sync(FromHost)]`.
Remediated at the framework identity boundary: NFKC names, NFC descriptions, enforced as a persistence
invariant so no writer can bypass them. Hardened twice more at the owner's direction — the UTS #39
*highly restrictive* script profile (one writing system per name), then the full Unicode confusables
table (16.0.0, 6,355 entries, generated by `tools/generate-confusables.ps1`, pinned by version **and**
source SHA-256). Names are unique on their confusable skeleton, strictness operator-tunable. Published
in `docs/security.md` and pinned by a test that reads the numbers back out of the document (`2af3998`).

**O1, O2, O3 — three guards that are silent by construction when they work.** One spawn-placement path
behind `IHexSpawnSelector` with `HostPlaceAt` as the single authoritative move; a scoped commit gate;
handshake metering with a Roslyn test tying every `[Rpc.Host]` method to admission.

### What watching them actually found

All four were watched executing in live sessions on 2026-07-30, and two of the four watches found
defects that had survived the project's entire life.

- **O1's watch found that respawn had never worked.** `HostDisembody` disables the `PlayerController`
  and `HostEmbody` looked it up enabled-only, so re-embodying threw *"An embodied player requires a
  PlayerController"*. Nothing could die on demand until an administrative `/kill` existed, so the death
  cycle had never been exercised. Fixed in `fa06996`. The marker inside `SceneTagSpawnSelector.Select`
  is what made the run evidence rather than inference: with one connection the spawn lease is reused,
  so position alone cannot distinguish "one rule answered both paths" from a replayed connect-time
  copy. The scene is adversarial in a useful way — 190 map `info_player_start` points against 1 tagged
  `hexagon_spawn` — so namespaced-tag precedence is proven rather than assumed.
- **F1's watch found that the uniqueness index could not be trusted.** A second character whose
  skeleton matched an existing one was *accepted*. A name reservation is written only at character
  creation, so it records the scheme in force at that moment: characters predating the setting hold
  none, and an `Exact` to `Skeleton` switch leaves keys computed the other way. The index therefore
  reports a name free while a character is visibly using it, and the names it leaves unprotected are
  the oldest on the server. Fixed in `1d1a5c0` by deciding against the characters — the source of
  truth — with the reservation demoted to what the code always called it, the atomic tiebreak for two
  racing creations. Costs a character-table scan per creation where the lookup was O(1); creation is
  rare and stores are small, but the trade is deliberate and contestable.
- **O2 and O3 were confirmed sound.** `HEXAGON_COMMIT_GATE_TAKEN scoped=true reentrant=false`, and
  `HEXAGON_ADMISSION_CHARGED cost=4 burst=16 refill=8/s` on real client commands. O3 does **not** fire
  on connect: metering charges commands, not the handshake, so a session that only connects proves
  nothing about it.

### Evidence boundary for this register

- Markers retained deliberately (`6ad53e9`): `HEXAGON_NAME_DECISION`, `HEXAGON_COMMIT_GATE_TAKEN`,
  `HEXAGON_ADMISSION_CHARGED`. A guard nobody has seen hold proves nothing, and all three are silent
  when they work.
- F1's fix has **two regression tests watched failing** against the index-based check before it landed
  (2 failed / 426 passed): enabling uniqueness on a populated store, and tightening `Exact` to
  `Skeleton`.
- The `Test-Citizen` / `TestCitizen` sequence is the live proof: the first was accepted against a
  legacy character holding no reservation, the second was refused by the UI once a reservation
  existed. A refusal, not a log line, is what shows the folding is load-bearing.

## Register 2026-07-30 (idiomaticity sweep) — what the engine already offered

A user-directed sweep of both packages for s&box idiom, run as two workflows over 38 agents. Verdict:
**the codebase is broadly idiomatic** — it uses `GameObjectSystem`, `PanelComponent`, `[Sync]`,
`[Rpc.*]` and the scene/prefab model the way s&box intends. The sweep's own tally was 31 candidates,
12 confirmed and 15 refuted; the confirmed fixes below are the ones identifiable from the commits.

| Finding | Resolution |
| --- | --- |
| Eye height and model validity were guessed rather than asked of the engine. | `Model.Load(...) is { IsError: false }`; eye height from `CurrentHeight`/`EyeDistanceFromTop`. (hl2rp `f006187`) |
| Interaction reach was measured differently from the client gate that displays it. | Reach uses `ActorEyePoint` and `ClosestSurfacePoint` for scene entities. (hl2rp `512814c`) |
| Markup s&box silently ignores. | Removed. (hl2rp `ba34880`) |
| Disabled controls were dimmed but still live. | Disarmed as well as dimmed. (hl2rp `1d3e187`) |
| The HUD did not rebuild when its clock-derived values changed. | Hash quantised to whole percent/second/unexpired count. (hl2rp `9ff5f0a`) |
| Expired notifications were pruned only by an unrelated rebuild. | Pruned on time. (hl2rp `779bf28`) |
| The client composition root was reachable on the wire. | Kept off it. (hexagon `fa51231`) |
| **F5** — a GameObject existing only to be an RPC endpoint. | Quarantined; see below. |

**Two findings from the sweep matter more than any individual fix.**

1. **The failure class is silence at the engine boundary.** Every confirmed finding compiles clean,
   logs nothing and throws nothing, which is exactly why none had been noticed. Everything else this
   project has found the hard way — the frozen chat fade, the respawn throw, the luminance mask, the
   stale uniqueness index — is the same shape.
2. **A shape-matching sweep generates roughly 40% false positives against this codebase on
   engine-neutrality alone.** Eight of the fourteen refutations were proposals to break the
   zero-Sandbox test build that `LayerBoundaryTests`, `PackageLayoutTests` and the csproj
   `<Compile Include>` lists enforce. The defence exists in code and a sweep cannot see it. **Do not
   re-run a naive sweep over Domain/Application/Kernel/Persistence expecting signal.**

**F5, and why it is quarantined.** Hexagon spawned a "Hexagon v2 Host Services" GameObject whose only
purpose was to *be* an RPC endpoint — with a publication guard, three scene scans, six static `Current`
reach-backs, and a non-networked back-pointer that was null after a host migration, silently no-opping
every RPC. s&box supports `[Rpc.*]` directly on a `GameObjectSystem`, addressed by an Id the join
snapshot already carries, and `HexagonRuntimeSystem` is one — so the apparatus solved a problem the
engine does not have. The rewrite's **engine compile is clean, which is the real proof that the RPC
source generator emits wrappers for a system**; `dotnet build` cannot answer that. It changes the wire
surface and is therefore BREAKING for library consumers, so it sits on `agent/f5-runtime-system-rpc`
awaiting the two-client run that would prove the client/host handshake.

### Evidence boundary for this register

- Reconstructed from commit history, not from a contemporaneous finding list. The per-finding table
  covers what the commits identify; the sweep's own 12/15 split is recorded as its tally, not as
  something this register re-derived.
- Every listed fix is on `agent/v2-audit-remediation` and CI-green on both required contexts.
- **The sweep did not run the code.** Each fix is a static-idiom correction verified by compile and
  suite; the ones with live confirmation say so in their own commits.

## Register 2026-07-30 (release gate) — two guards that could only fail off CI

Running `verify.ps1` for the first time since the chat rework found three defects that no CI run could
have surfaced, because each depends on state a fresh clone does not have.

- **The generated-asset classification was restated in two places and had gone stale.**
  `validate-project.ps1` allow-listed `scenes/main.scene_c` by literal path and `release-inputs.ps1`
  listed `_c`, `.vpk`, `.los`. When the editor first wrote `main.scene_d` and `/Assets/**/*_d` was
  added to hl2rp's `.gitignore`, neither guard learned, and both failed — but only on a machine that
  had run the editor, which is every machine that would cut a release. Both now derive the set from
  the repository's own `.gitignore`, where the classification is actually maintained (`548c7f5`).
  hexagon's `.gitignore` was itself missing `*_d`.
- **`verify.ps1` builds the generated s&box project with warnings as errors; CI does not.** Nine
  warnings the editor tolerates were nine hard errors at the release gate: a `Panel.Opacity` shadow,
  and eight partial-`<param>` records (`497532f`).
- **A dead field was the visible half of a live defect.** `CS0414` on `_lastLineCount` implied a
  mechanism nobody wrote — re-stamp the fade clock when the line set changes. Without it `_lineClock`
  was assigned only at HUD construction, so every chat line shared one timestamp and, once the HUD
  outlived a fade window, **no chat line was visible while chat was closed**. A transient entry over a
  fading log is the whole design. `HexChatLine` now carries a stable `Key` and `HexChatPanel` stamps
  arrival itself, so a consumer cannot supply a timestamp it has no way to compute.

### Evidence boundary for this register

- Full `verify.ps1 -SkipRemoteAcceptance` passes: layout, both neutral suites in Release, generated
  s&box build with warnings as errors, client access control, and the engine readiness/commit/recover/
  drain probes.
- The `.gitignore` derivation has a test in `test-release-inputs.ps1` proving both directions, watched
  failing against the old hardcoded list.
- **The chat fade fix is NOT watched running.** It compiles and the suites pass, but the fade itself
  needs one in-game look: a line must linger then fade with chat closed, and a later line must fade
  independently rather than resetting the others.

## Register 2026-09-18 (full review) — green suites were hiding broken features

A whole-codebase review after a two-month gap. Both neutral suites passed before it
started, yet four gameplay features could not work in production. Every one was masked the
same way: the test substituted an always-allow fake or a hand-typed key for the exact
production piece that was wrong. Evidence after both passes: 440 Hexagon and 337 HL2RP tests, and both portable profiles.

Fixed, with a behavioural test for each unless noted:

| Finding | Closure |
| --- | --- |
| Restraining was impossible | The character interactable demanded an already-restrained target, and it also fronts the timed restrain action. An unrestrained target now authorizes proximity only, with no session and no grant. A new test drives restrain, search and release through the production interactable. |
| Sessions outlived their offer | `InteractionAuthorityService` now revokes a session, on `Continue` and on scheduled revalidation, once its interactable stops offering that session kind. |
| Scanner pilot ejected after ~130 units | Reach is measured from the drone while its pilot session is live. Engine-side edit; parse-checked only. |
| Search workspace never opened | Host and client each spelled the session field name. Both now derive it from `HL2RPPresentationFields.InteractionSession`. |
| Free infinite ammunition | The `replenish` action is removed. Ammunition is bought. |
| Restraint gated one route only | `HL2RPFeatureRuntimePolicy` denies hands-on operations for a restrained actor, and health vials check restraint. |
| Restraint was permanent | Death releases it inside the death transaction, and `RestraintService.ReleaseAsync` exists for system callers. Operator kill is therefore the operator escape hatch. |
| Unkillable holder of an undroppable pistol | Death keeps the pistol in the inventory, unequipped, instead of refusing the transition. |
| Unreadable vest made its holder immune | The vest is ignored with a warning. |
| Every pistol shot re-snapshotted every client | Item refresh deadlines are connection-scoped. |
| Failed application connect left a zombie player | The connection is kicked. A missing spawn point also kicks rather than retrying silently. Engine-side; parse-checked only. |
| Scene disposal could skip `Disconnected` | The coordinated shutdown captures and owns its sessions. Engine-side; parse-checked only. |
| Persistence liveness | A permanently undeletable WAL file no longer forces a checkpoint per commit or blocks reopen; torn checkpoint artifacts are swept; a corrupt `format.json` is never quarantined around. |
| Name skeleton single pass | Table outputs are re-folded, so `Ƽara` collides with `Sara`. |
| Dead and banned chat senders | Enforced host-side through `IChatLivenessSource`; previously client-only. |
| Chat channel definitions | The compiler validates range and prefixes; the definition is the single source of range. |
| Smaller framework items | Throwing creation hooks fail closed; a failed grant revokes its session; retained async failures are capped; definition lists are copied. |
| Three drifted persistence error maps | All game helpers route through the framework's `PersistenceResultMapping`, now public. Only the scanner copy had mapped lease, storage-limit and stale-transaction errors. |
| Silent decode failures | Reported once per distinct message through one warning sink. |
| UI | Drafts reset only on a real host change; slot, name and price limits come from host constants; the create button has a real submitting guard; the invented map name is gone. |
| Dead code | `RpcGuard`, `ProtectiveVestService`, `IssuePermitAsync`, `IRuntimePlayer`, `ProceedsToRecovery`, the misnamed bootstrap-operators file, and an assertion-free architecture test (now asserting). |
| Docs | The operator runbook, canary branch, whitelist posture and actor-resolution claims now match the code. |

Second pass, same day, closing what the first pass had left:

| Item | Closure |
| --- | --- |
| Pistol raise state | Both collections moved behind one locked holder. Restraint tickets are a concurrent dictionary. |
| Verify scripts | The three helpers that were byte-identical across profiles live in `tools/verify-common.ps1`, which HL2RP dot-sources from its sibling checkout as it already does `worktree-gate.ps1`. Both portable profiles pass. `capture-release-environment.ps1` keeps its own `Get-RepositoryHead`, which uses a different git invoker on purpose. |
| Respawn guard | The unobservable `_respawningCharacters` set is gone; its block was dedented mechanically. Engine-side; parse-checked only. |
| Note authorship | Writing still claims authorship, which stops a later holder forging the text. Saving a note blank now releases it. |
| Name uniqueness cost | Name keys are memoized per service. Characters remain the authority on which names are in use. |
| Source-text pins | Kept, because they are the only guard on engine-bound files that no test project compiles. They now compare through `SourcePin`, which ignores whitespace, so a reformat no longer breaks them. |
| This register | The three pre-2026-07-17 cohorts were condensed to the facts still in force. |
| Client commands | Discarded command tasks run through one guard that turns a throw into a reported failure, and a failed search command now clears its pending state. Scanner intents no longer raise a toast per input. |
| Panel hashes and view models | The scoreboard hashes every row and the window hashes its fragments. Fields that never varied (`Visible`, `IsPiloting`, the always-empty `Alerts`) are removed. |
| Shared item proof | `ItemProof.Require` is adopted by the note, radio and request-device services. |
| Zip-tie bookkeeping | The executor reports the consumed tie; the host no longer scans the inventory before the command. |
| Audit command | `/audit` takes a note, so the fact records something beyond its own invocation. |
| Restraint target search | Runs only for Civil Protection and Overwatch viewers. The reported per-tick trace storm was overstated: distance was already checked before any trace. |
| Degraded-boundary log lines | Worded once and shared by the host and the executor. |
| Deny paths | Feature tests now cover denied civic and note operations, the infractions cap, and the separate audit operation for a record rewrite. |

Closed without a change:

- The `SERVER` symbol lives in `hl2rp.server.csproj`, which s&box generates and git ignores. It is
  the engine's convention for `.Server.cs` files, not dead configuration.
- A forcefield toggle passes no session proof because it is a one-shot interaction, authorized
  for range and line of sight immediately before the call. Doors hold a session. The call site
  now says so.
- Character deletion still scans reservations, references and inventories. Deletion is rare, and
  removing the scans needs persisted indexes.
- The Combine lock service keeps its own faction check next to the policy's. Feature tests run
  with an allow-all policy, so the service check is what those tests exercise.

Engine evidence, same day: `tools/verify.ps1 -SchemaRoot ../hl2rp-hexagon -SkipRemoteAcceptance`
passed end to end on engine 26.09.15. That covers three generated s&box warnings-as-errors builds
with 0 warnings and 0 errors, which compile every engine-bound file and Razor panel this register
touched; the client-assembly access gate; and the headless host/client readiness, transactional
commit, drain and exact-recovery probe. The access gate needed one repair first: it hard-required
`Base Library.dll`, which engine builds from 26.09 no longer emit, so it refused to start. It now
loads that assembly only where it exists.

NOT yet exercised: interactive play. Restrain, scanner flight and the search panel have not been
driven by a player in a running session, and the two-client acceptance gate is still pending.
