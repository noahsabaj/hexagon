# v2 audit closure registers

This file preserves two dated closure cohorts. Test totals are evidence snapshots,
not cumulative or current-suite claims.

- The original v2 cutover cohort contains 31 findings and nine shared root causes.
  Its 2026-07-12 evidence snapshot was 168 passing Hexagon tests, 167 passing
  HL2RP tests, static validation, a zero-warning generated build, and exact
  commit -> drain -> reopen recovery.
- The current-worktree cohort contains 19 findings and eight shared root causes.
  Its 2026-07-13 implementation, neutral-test, generated-build, and s&box smoke
  evidence is recorded below with the remaining external gates called out.

The dedicated-server/two-authenticated-client release evidence remains mandatory
and intentionally pending. That pending acceptance gate does not reopen code
defects that are locally verified, but this register separately preserves local
implementation and verification gaps found during reconciliation.

## Original cutover shared root causes (31-finding cohort)

| Root cause | Single remediation boundary | Finding IDs cleared together |
| --- | --- | --- |
| R1 | Immutable RPC actor identity and host-owned command surface | NET-1, LIF-1, UI-2, UI-3, parts of UI-1 and DOM-4 |
| R2 | Server interaction sessions and inventory capabilities | NET-2, NET-4, NET-5, DOM-3, DOM-4, DOM-5 |
| R3 | Transactional lifecycle and stable scene identity | NET-3, PER-5, PER-6, PER-8, DOM-1, DOM-2, DOM-4, DOM-6, DOM-7, UI-3 |
| R4 | Pluggable unit-of-work persistence | PER-1, PER-2, PER-3, PER-4, PER-7, PER-8 |
| R5 | Canonical aggregate identity and committed logical view | PER-2, PER-3, PER-4, PER-7, PER-8 |
| R6 | Composed aggregates and allowlisted creation transaction | NET-3, PER-5, PER-6, PER-8, DOM-1, DOM-2, DOM-6, DOM-7 |
| R7 | Realm-aware scene composition and teardown | NET-1, LIF-1, UI-1, UI-2, UI-3, DOM-4 |
| R8 | Typed schema/module/policy/event kernel | EXT-1, EXT-2, EXT-3, NET-6 |
| R9 | Package ownership and permanent verification gate | BLD-1, BLD-2 |

## Network and authority

### Critical

| ID | Finding | Replacement | Status |
| --- | --- | --- | --- |
| NET-1 | Client-owned player RPCs trusted mutable/replicated identity. | `RpcActor` is derived from `Rpc.Caller`; authoritative commands live on the host-owned services object. | Locally verified; dedicated/two-remote-client acceptance pending |
| NET-2 | Client-facing press/use paths performed authoritative mutations without a server proof. | Client adapters submit targets only; the host reconstructs distance, LOS, policy, and capability. | Locally verified in neutral interaction tests |
| NET-3 | Character creation accepted authority-bearing values and could publish partial state. | Allowlisted `CreationValue` input and one host-computed character/inventory/loadout transaction. | Locally verified |
| NET-4 | Inventory/item requests did not prove membership, destination access, or continuing authority. | Session-bound `InventoryCapability` grants and atomic mutation services. | Locally verified in neutral access/inventory/action tests |
| NET-5 | Vendor, pickup, scanner, search, and timed-action targets could be forged or become stale. | Targets and ownership derive from live placement/session state and revalidate at start/completion and 4 Hz. | Locally verified |

### High

| ID | Finding | Replacement | Status |
| --- | --- | --- | --- |
| NET-6 | Parallel event/permission mechanisms produced inconsistent fail-open behavior. | Typed policy pipeline is mandatory, first denial wins, exceptions deny; post-commit events are isolated. | Locally verified in neutral kernel tests |
| NET-7 | Chat accepted oversized/unnormalized spam and radio performed persistence reads. | NFC normalization, 512-scalar ceiling, token buckets, fail-closed channels, one canonical live inventory capture. | Locally verified in neutral chat tests |

## Persistence and consistency

### Critical

| ID | Finding | Replacement | Status |
| --- | --- | --- | --- |
| PER-1 | Write-behind persistence could acknowledge before durable publication. | Length-framed checksummed WAL flushes before publishing repository state/events. | Locally verified in provider failure/recovery tests |
| PER-2 | Queued writes, serialized caches, and repositories exposed divergent logical views. | One committed in-memory view with indexes and tombstones. | Locally verified in provider tests |
| PER-3 | Recovery could silently accept corruption or incomplete snapshots. | Partial final frames truncate; earlier corruption fails; checkpoints fall back by valid generation. | Locally verified in recovery tests |
| PER-4 | Shutdown did not guarantee a full durable drain. | Provider drain/disposal plus scene-lifetime shutdown barrier. | Locally verified, including headless drain/reopen smoke |
| PER-5 | Character creation/loadout spanned independent writes. | One unit of work validates slot, CID, inventory, items, traits, reservations, and invariants. | Locally verified in character transaction tests |
| PER-6 | Delete/drop/pickup lifecycle could leave orphaned or multiply located records. | Recursive transactional deletion and a unique inventory-or-world location index. | Locally verified in domain/application tests |

### High

| ID | Finding | Replacement | Status |
| --- | --- | --- | --- |
| PER-7 | Persisted polymorphism/config lacked explicit stable type and codec registration. | Versioned envelopes and typed config codecs reject unknown, abstract, duplicate, and incompatible types. | Locally verified in persistence/schema tests |
| PER-8 | Mutable DTOs/manual dirty tracking broke canonical identity and commit isolation. | Canonical records plus isolated unit-of-work editors publish only after commit. | Locally verified in identity/transaction tests |

## Domain and gameplay invariants

### Critical

| ID | Finding | Replacement | Status |
| --- | --- | --- | --- |
| DOM-1 | Character framework/schema state was inheritance-coupled and client-shapeable. | `CharacterRecord` composes one registered typed schema-state envelope. | Locally verified in domain/character tests |
| DOM-2 | Item ownership existed in multiple mutable fields. | Inventory placements and world records are the only item locations. | Locally verified in domain/inventory tests |
| DOM-3 | Inventory access was persisted as ownership/receivers instead of transient authority. | Active-character and interaction-session grants are external and revocable. | Locally verified in access tests |
| DOM-4 | Character/world/entity lifecycle did not revoke dependent state consistently. | Transactional lifecycle plus session/grant/reference cleanup. | Locally verified |

### High

| ID | Finding | Replacement | Status |
| --- | --- | --- | --- |
| DOM-5 | Economy/actions derived price, stock, ownership, or target from client input. | Live placement/session/state is resolved inside one transaction. | Locally verified |
| DOM-6 | Bags allowed invalid ownership/nesting and item creation without a destination. | Typed bag-owned inventories, graph validation, capacity checks, mandatory destination. | Locally verified in invariant tests |
| DOM-7 | Scene entities lacked one stable persistent identity. | One editor-authored `PersistentSceneEntity`; runtime ambiguity disables the object. | Locally verified by tests and static scene validation |

## UI and composition

### High

| ID | Finding | Replacement | Status |
| --- | --- | --- | --- |
| UI-1 | Panels and callbacks accessed mutable domain/server state. | Immutable client snapshots, `HexClientStore`, typed controllers, registered panel descriptors. | Locally verified, including zero-warning generated Razor build |
| UI-2 | Listen-host process state substituted for a true client scope. | Independent host scope and non-networked client UI/store scope. | Locally verified; dedicated/two-remote-client acceptance pending |
| UI-3 | Load/unload/death/switch paths leaked private or stale UI/body state. | Epoch-tagged atomic client state and complete unload/session/grant/body clearing. | Locally verified |

## Extension kernel

### High

| ID | Finding | Replacement | Status |
| --- | --- | --- | --- |
| EXT-1 | Reflected/static manager registration had unstable order and weak validation. | Explicit schema/module builders and deterministic topological compilation. | Locally verified in schema compiler tests |
| EXT-2 | Boolean/static hooks did not define denial, exception, or commit semantics. | Typed `IPolicy<T>` and `IEventHandler<T>` contracts. | Locally verified in policy/event tests |
| EXT-3 | Item/creation extension contracts were registered but not reliably dispatched. | Explicit actions, fields, initializers, codecs, and fail-closed startup/runtime lookup. | Locally verified |

## Build and package ownership

### High

| ID | Finding | Replacement | Status |
| --- | --- | --- | --- |
| BLD-1 | Library-owned generic scene/startup resources collided with game ownership. | Hexagon owns no scene; HL2RP alone owns `scenes/main.scene`; other assets are namespaced. | Locally verified by static project validator |

### Moderate

| ID | Finding | Replacement | Status |
| --- | --- | --- | --- |
| BLD-2 | Generated projects, Razor/XML warnings, assets, and runtime readiness lacked one durable gate. | Tracked .NET 10 tests and `tools/verify.ps1` with warnings-as-errors, asset checks, readiness, recovery smoke, plus an explicitly manual, source-bound remote-attestation integrity check. | Locally verified; dedicated/two-remote-client manual acceptance pending |

## Runtime lifecycle

### Critical

| ID | Finding | Replacement | Status |
| --- | --- | --- | --- |
| LIF-1 | Dedicated/listen initialization, disconnect, scene reload, and teardown had split ownership and stale async work. | Scene-scoped host/client composition, epochs/cancellation, teardown barrier, and deterministic disposal. | Locally verified; dedicated/two-remote-client acceptance pending |

## 2026-07-13 current-worktree closure register (19-finding cohort)

This cohort intentionally breaks the draft-v2 API and starts corrected durable
state at `hexagon/persistence/v3/{schemaId}`. Existing `hexagon/v2/...` data is
neither opened nor migrated and remains available for rollback. “Locally
verified” below is scoped to the evidence named in each row. The last complete
verifier snapshot reported 230 Hexagon and 184 HL2RP tests plus the generated
s&box build and commit -> clean drain -> exact recovery smoke. Later source and
test additions mean that snapshot is historical rather than the final evidence;
the exact-current full verifier and fullscreen editor rerun are recorded below.
Coordinated draft PRs are now published, both hosted contexts have passed, and
strict `main` protection requires them. The two-authenticated-client release gate
remains intentionally pending.

No Critical findings belonged to this cohort.

| ID | Severity / axis | Exact implementation and affected path | Closure evidence | Status / remaining gate |
| --- | --- | --- | --- | --- |
| DI-01 | High / data integrity and concurrency | Hexagon `Code/V2/Persistence/PersistenceStorage.cs`, `FileSystemPersistenceProvider.cs`, `TransactionalPersistenceProvider.cs`, and `Code/V2/Infrastructure/RuntimeContracts.cs` define the lease-aware immutable protocol and composition contract. HL2RP `Code/Runtime/HL2RPPhysicalPersistenceStorage.cs`, wired by `HL2RPSchemaSourceSystem.cs`, owns the production OS adapter and holds `lease.lock` through a write-through `FileStream` with `FileShare.None`. | Same-process and child-process contention/crash-release cases are covered by `tests/Persistence/FileSystemPersistenceProviderTests.cs` and `ImmutableWalPersistenceProviderTests.cs`; the second writer fails before mutation. The recorded engine smoke exercised the production adapter through commit, clean drain, and exact recovery. | Closed in implementation and locally verified. |
| DI-02 | High / data integrity and concurrency | `Code/V2/Domain/Inventories.cs` defines `InventoryGridPosition`, `InventoryGridSize`, and overflow-safe `InventoryRectangle`; live mutation, RPC ingress, and recovery invariants use the same geometry path. | `tests/Domain/InventoryGeometryTests.cs` and inventory/invariant suites exercise integer extrema and live/recovery parity. | Closed in implementation and locally verified. |
| DI-03 | Medium / data integrity and concurrency | `CharacterLifecycleGuardRecord`, `Application/CharacterReferenceMutationService.cs`, `CharacterService.cs`, and `DomainInvariantValidator.cs` make reference changes increment the target guard and make deletion atomically remove the guard, character graph, and matching references. | Forced reference/create/delete interleavings and bypass rejection are covered by `CharacterReferenceMutationServiceTests.cs` and `DomainInvariantValidatorTests.cs`. | Closed in implementation and locally verified. |
| DI-04 | Medium / data integrity and concurrency | `PersistencePrimitives.cs` supplies `IPersistenceInvariantSet` and a candidate-state `PersistenceInvariantContext`; schema-aware exact payload, trait, reference, scene-kind, relationship, and codec checks run before frame creation and again during recovery. | Commit-time invalid-payload and recovery tests use the same invariant implementation and reject states before acknowledgement/publication. | Closed in implementation and locally verified. |
| DI-05 | Medium / data integrity and concurrency | `Application/InventoryAccess.cs`, `Interactions.cs`, and `ItemActionService.cs` return revisioned access/session/action proofs, attach every consulted character, guard, inventory, item, and placement as a commit dependency, and recheck under `commit gate -> access/session lock`, including zero-mutation actions. HL2RP special item, vendor, door, restraint, pistol, and scanner mutations require those proofs. | Deterministic commit interleavings revoke request, token, vendor-session, permit, restraint, pistol, and scanner authority. They conflict or compensate without a post-commit event or stale world effect; scanner tests force revocation after durable entry/spotlight commits. | Closed in implementation and locally verified. |
| C-01 | Medium / correctness | HL2RP `Domain/SceneEntityStates.cs` and `Runtime/HL2RPRuntimeLogic.cs` define one `CityObjectiveContract` and typed `CityObjectiveUpdate`; `HL2RPObjectiveCommandRouter.cs` carries separate title/detail fields through route, commit, and projection with host-generated new IDs. | `ObjectiveCommandRouterTests.cs`, civic service tests, persistence codec tests, and projection tests cover multiline LF detail, limits, controls, duplicate IDs, timestamps, update/delete, and unknown supplied IDs. | Closed in implementation and locally verified. |
| C-02 | Medium / correctness | `Networking/SessionEpoch.cs`, `Runtime/RuntimePlayerSession.cs`, `HexHostServicesComponent.cs`, and `Client/HexClientStore.cs` bind every hello, command, result, state, list, and chat payload to a client nonce plus host `ConnectionEpoch`; application connection waits for successful authenticated binding. | Connection-boundary, application-latch, client-store, and delayed-old-packet tests cover clear/reconnect and all packet categories. | Closed in implementation and locally verified; real two-client transport evidence remains pending. |
| S-01 | Medium / security and authority | `Application/InteractionAuthorityService.cs` permits `WorldPoint` only for finite coordinates, computes squared distance in `double`, and replaces raw range with finite-positive `InteractionRange`; invalid geometry exits before LOS, policy, or action code. | Interaction authority tests exercise NaN, infinity, huge finite values, distance overflow, and collaborator non-invocation. | Closed in implementation and locally verified. |
| S-02 | Medium / security, performance, and resource bounds | Framework `Networking/CommandAdmission.cs` and `ClientPayloadLimits.cs` charge weighted authenticated attempts before payload construction with bounded burst/refill/active requests and UTF-8/map/string limits. HL2RP `HL2RPPresentationPlanning.cs`, `HL2RPHostApplication.cs`, and `HL2RPProjectionIndex.cs` combine typed change sets with receipt-keyed, order-independent projection updates; incremental live inventory/chat/authority/combat directories update only named rows and publication includes both losing and gaining inventory viewers. Whole-connection enumeration remains only in explicit startup/maintenance position sampling, not the inventory mutation path. | Admission/payload tests cover refill, costs, malformed charging, active cap, Unicode, and aggregate bytes. `LiveProjectionIncrementalTests` covers destination-before-source receipts, deterministic sequence/serialized snapshot equivalence, nested-bag owner propagation, deletion, old/new viewers, unaffected visibility-cache reuse, a 10,000-inventory update touching two inventories/one item, and 1-of-64 connection updates; existing runtime cases cover no-op/continuation silence, one objective broadcast, dependency-keyed caching, and invalidation. The exact-current HL2RP suite passed 216/216, its generated Release build passed with 0 warnings/0 errors, and the fullscreen editor smoke reached ready/session-ready and clean drain. | Closed in implementation and locally verified; real two-client load/transport evidence remains pending coverage. |
| R-01 | Medium / reliability and recovery | `FileSystemPersistenceProvider.cs` stores verified immutable `wal/frames/{sequence}-{hash}.frame` and `wal/acks/{sequence}.ack` records chained by the previous acknowledgement hash; acknowledged gaps/corruption are fatal and only unacknowledged orphan frames are removable. | Frame/ack/head failure matrices, hash gaps, acknowledged corruption, orphan cleanup, metadata repair, and checkpoint fault points are covered by both persistence provider suites. | Closed in implementation and locally verified. |
| R-02 | Medium / reliability and resource bounds | `InteractionAuthorityService.cs` indexes timed actions by ID and actor, permits one per connection/character, caps duration at five minutes, applies a 15-second completion grace, and sweeps through an expiry queue on lifecycle and maintenance paths. | Interaction tests cover concurrent begin, readiness, expiry, caps, disconnect/character/target cleanup, and queue/index consistency. | Closed in implementation and locally verified. |
| R-03 | Medium / reliability | HL2RP `Runtime/HL2RPMaintenanceSupervisor.cs` owns a single in-flight tick, coalesced pending signal, observed exceptions, `finally` cleanup, cancellation-aware disposal, and 250 ms-to-30 s exponential retry with immutable status. `HL2RPHostApplication.cs` initiates verification shutdown without awaiting its own supervised tick, avoiding self-shutdown deadlock. | Runtime composition tests exercise throw/retry, coalescing, backoff reset, disposal, and observed status; both the isolated verifier and fullscreen current-project smoke reached clean shutdown. | Closed in implementation and locally verified. |
| R-04 | Low / reliability and recovery | `TransactionalPersistenceProvider.cs` recovers and validates into a private candidate `ProviderState`, atomically publishes only on success, and gates repositories/reads on `Ready` in the explicit lifecycle state machine. | Recovery-failure tests prove no partial repository state becomes observable and the provider enters terminal `Faulted`. | Closed in implementation and locally verified. |
| R-05 | Low / reliability and operations | `PersistenceShutdownResult` and idempotent `ShutdownAsync` report durable/checkpoint sequences, clean/recoverable state, checkpoint result, and lease release; runtime logs `HEXAGON_DRAINED` only when clean and `HEXAGON_DRAIN_DEGRADED` when recovery remains possible. HL2RP's retryable physical lease retains its `FileStream`/handle until release actually succeeds. | Provider tests inject final checkpoint/prune failures and reopen from durable WAL. `RetryablePersistenceLeaseTests` injects a first physical release failure, proves a competing `FileShare.None` writer remains rejected, then proves retry releases idempotently and a successor acquires; the recorded engine smoke observed a clean drain followed by exact recovery. | Closed in implementation and locally verified. |
| PR-01 | Medium / performance and resource bounds | `FileSystemPersistenceOptions` defaults to 128 commits/checkpoint, two checkpoints, 16 MiB soft checkpoint pressure, 64 MiB retained WAL, 8 MiB frames, and 1,024 commits; verified checkpoints precede pruning and writes fail with `StorageLimitExceeded` before hard bounds. | Persistence stress/fault tests assert retained frame/checkpoint bounds and fallback replay safety. | Closed in implementation and locally verified; long-duration production I/O characteristics remain unmeasured. |
| PR-02 | Medium / performance and resource bounds | Checkpoint compaction reclaims tombstones, advances `CompactionGeneration`, and rejects units of work opened in an older generation with `StaleTransaction`; the older fallback checkpoint retains its required frame chain. | Churn, stale-transaction, compaction/fallback, and crash-point tests cover bounded tombstones without weakening ABA protection. | Closed in implementation and locally verified. |
| PR-03 | Low / performance | `Application/PersistentSceneIdentityIndex.cs` builds ID/path lookups in one O(N) pass; the runtime system in `Runtime/PersistentSceneEntity.cs` coalesces same-frame editor invalidations and remains fail-closed for missing/duplicate identities. | Scene identity tests exercise 10,000 candidates plus dynamic duplicate and rebuild behavior; final current-source isolated and fullscreen editor startups exercised the indexed runtime path. | Closed in implementation and locally verified; production-scale editor timing remains unmeasured. |
| T-01 | Medium / tests and architecture | Objective routing, maintenance supervision, presentation planning, and projection indexing are engine-neutral executable components with direct tests for the audit-critical regressions. | `ObjectiveCommandRouterTests.cs` and executable `RuntimeCompositionTests.cs` cases exercise the former host/lifecycle seams. Source-text wiring/compatibility assertions remain in runtime and architecture test files; they are supplemental structural checks, not behavioral evidence. | Original critical-seam finding closed with this explicit coverage limitation. |
| T-02 | Low / CI, configuration, and deployment | Hexagon `.github/workflows/neutral.yml` and HL2RP `.github/workflows/integration.yml` use .NET 10, locked restore, audit, Release tests, portable validation, warnings-as-errors, clean-diff checks, and exact lowercase 40-character `hexagon.lock.json` pinning. Full local verification and source-bound evidence publication remain separate. | Draft PRs [Hexagon #2](https://github.com/noahsabaj/hexagon/pull/2) and [HL2RP #1](https://github.com/noahsabaj/hl2rp-hexagon/pull/1) are published. `hexagon / neutral` and `hl2rp / integration` emitted and passed; strict `main` protection requires each hosted context plus `sbox / release-evidence`, covers administrators, and disables force-push and deletion. The release-evidence status is published as pending on both coordinated heads. | Closed operationally. The protected `sbox / release-evidence` context remains intentionally pending until the two-authenticated-client acceptance run exists. |

### Shared root causes and smallest coherent fixes

| Root cause | Smallest coherent fix now implemented | Findings fully or partially cleared |
| --- | --- | --- |
| RC-1: persistence had no single ownership/commit/retention protocol | Hexagon owns one versioned immutable single-writer protocol and storage abstraction; standalone HL2RP owns the raw OS adapter implementing the lifetime lease, write-through publication, and atomic rename. The protocol adds the frame/ack hash chain, candidate recovery, bounded verified checkpoints, compaction generations, and structured shutdown. | Fully clears DI-01, R-01, R-04, R-05, PR-01, and PR-02. |
| RC-2: commit authorization and durable invariants were fragmented | One schema-aware candidate-state invariant engine plus versioned lifecycle guards and explicit commit preconditions/proofs. | Fully clears DI-03, DI-04, and DI-05. |
| RC-3: geometry was represented by unchecked primitive values | Canonical finite world/range and overflow-safe inventory geometry value objects reused at ingress, mutation, action, and recovery boundaries. | Fully clears DI-02 and S-01. |
| RC-4: client traffic lacked a session fence and costed admission boundary | Nonce-plus-epoch scoping for all directions, authenticated weighted admission, active-request limits, and aggregate payload validation before construction. | Fully clears C-02 and the framework half of S-02. |
| RC-5: runtime-owned collections and scene discovery were unindexed/unbounded | Actor/ID/expiry indexes for timed actions and a coalesced O(N) persistent-scene identity index. | Fully clears R-02 and PR-03. |
| RC-6: objectives used split string contracts across layers | One typed objective contract and engine-neutral route carrying title/detail directly through persistence and projection. | Fully clears C-01. |
| RC-7: engine-bound maintenance and blanket publication hid behavior and cost | Supervised neutral maintenance, typed command outcomes/change sets, real commit receipts, dependency-keyed slices, order-independent incremental inventory state, targeted live chat/authority/combat directories, and old/new-viewer-aware deduplicated publication. | Fully clears R-03, T-01, and the HL2RP half of S-02. |
| RC-8: verification existed only as an optional local convention | Locked portable hosted workflows plus a distinct source-bound full verifier and protected `sbox / release-evidence` context. | Implements the fix for T-02; remote enforcement is completed only when the workflows/status protection are published. |

### Evidence boundary for this register

- PowerShell 7.6.3 previously ran `pwsh -NoLogo -NoProfile -File .\tools\verify.ps1 -SchemaRoot ..\hl2rp-hexagon -SkipRemoteAcceptance` against the then-current remediation worktrees. It reported Hexagon **230 passed, 0 failed**, HL2RP **184 passed, 0 failed**, exact sibling/source binding, and a generated s&box warnings-as-errors build with **0 warnings, 0 errors**.
- That earlier engine smoke passed transactional commit -> `HEXAGON_DRAINED` -> exact-sequence/digest recovery after correcting JSON escape-insensitive canonical payload comparison and a maintenance self-shutdown deadlock. It did not reuse the stale temp launcher entry from the original audit; the later source-bound rerun below supersedes its code-evidence snapshot.
- Reconciliation rebuilt the later neutral sources with `dotnet test ... --configuration Release --no-restore --nologo --warnaserror`: Hexagon passed **253/253** and HL2RP passed **198/198**. An intermediate failure in `SnapshotWireCodecTests.ClientStateRoundTripsFullImmutableGraph` was a test-order assumption over canonically sorted random GUIDs, not codec loss; selecting the captured inventory ID passed the exact case 20/20 and the rebuilt full suite. The commands refreshed ignored `bin/`/`obj/` outputs; that test-source correction is the only repository-visible source change attributed to this verification cycle.
- After the incremental S-02 closure and final access/session work, exact-current warnings-as-errors suites passed **Hexagon 259/259** and **HL2RP 216/216**. Focused projection cases passed **7/7**, scanner cases passed **13/13**, and Hexagon interaction-session cases passed **6/6**. `tools/verify-portable.ps1 -SkipRestore` passed both suites, static integration, and the exact-SHA evidence contract; a fresh generated s&box build produced both assemblies with **0 warnings, 0 errors**.
- The final source-bound `tools/verify.ps1 -SchemaRoot ..\hl2rp-hexagon -SkipRemoteAcceptance` rerun passed locked restores/audits, those same suites, exact sibling/source binding, generated warnings-as-errors compilation with **0 warnings, 0 errors**, transactional commit, clean drain, and exact recovery. Its first attempt stopped intentionally at the editor-open guard; the complete rerun passed after closing the stopped editor. The final visible-editor pass remains separate.
- The exact current HL2RP project was then reopened in a maximized 2560x1399 s&box editor. Auto-host recovered sequence/checkpoint **11/11** under `hexagon/persistence/v3/hl2rp`, reached ready/session-ready, rendered chooser and registration, and survived four transitions without a render-tree error. Stop emitted `HEXAGON_DRAINED host`; the editor reported 0 errors, the log contained no new application error after readiness, and an independent `FileShare.None` open of `lease.lock` succeeded. Nine engine/resource lookup warnings remain an environmental coverage note, not a project compile/runtime failure.
- A final adversarial pass found and closed a post-commit scanner continuation race: revocation could otherwise fall between acknowledged pilot/spotlight state and its world effect. Scanner entry now preregisters before commit, every post-commit effect rechecks `InteractionSessionProof.IsCurrent()` while serialized under the cleanup lock, and a stale continuation unconditionally starts compensating cleanup. Forced after-success entry/spotlight revocations prove no body/effect resurrection and no durable pilot ghost. The caller sweep found no remaining `_access.Has` to durable-commit path or inverse session/cleanup lock order.
- A release-operability pass then closed the last locally actionable acceptance gaps. HL2RP now exposes a tested return-to-character-selection route, clears all character-scoped presentation state across the epoch change, emits strict pre-shutdown/post-restart recovery snapshots from the production lifecycle, and includes lifecycle guards in the canonical recovery digest. Hexagon's format-v3 verifier requires two distinct remote accounts, strict JSON booleans, two distinct canonical server logs, matching sequence/digest snapshots, exact artifact references, and non-future timestamps. Its executable contract suite passes under both Windows PowerShell 5.1 and PowerShell 7.6.3; current neutral suites pass **Hexagon 256/256** and **HL2RP 226/226**, and the generated s&box build passes with **0 warnings, 0 errors**. This makes the external two-client run executable and falsifiable; it does not substitute for that still-pending run.
- Draft PRs [Hexagon #2](https://github.com/noahsabaj/hexagon/pull/2) and [HL2RP #1](https://github.com/noahsabaj/hl2rp-hexagon/pull/1) were opened and retained as drafts. HL2RP's first hosted integration run passed. Hexagon's first neutral run exposed a deterministic CI-boundary bug: five package-layout tests unconditionally required an absent sibling checkout. The fix keeps two Hex-only controls in neutral, marks the three real paired checks `CrossRepository`, excludes only that category in Hex neutral, and leaves HL integration unfiltered. Local neutral **256/256**, paired **3/3**, full Hexagon **259/259**, and the corrected hosted reruns passed.
- Both `main` branches now have strict required-status protection. Administrators are covered; force-push and deletion are disabled; Hexagon requires `hexagon / neutral` plus `sbox / release-evidence`, and HL2RP requires `hl2rp / integration` plus `sbox / release-evidence`. The configuration was read back from GitHub after publication.
- `sbox / release-evidence` is published as pending on both coordinated heads with the description `Awaiting authenticated two-client release evidence`.
- `sbox / release-evidence` must remain pending until two authenticated clients validate both exact source SHAs. This PC is not to be registered as a self-hosted runner.
- The Hexagon library is platform-whitelisted and owns the persistence protocol abstraction. The standalone-only HL2RP game supplies the raw OS `FileStream` lease, write-through, and atomic-rename adapter; this boundary now passes exact-current generated compilation, isolated persistence commit/drain/recovery, fullscreen editor startup/UI cycling, clean drain, and physical lease-release verification.
