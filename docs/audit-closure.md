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
- After the incremental S-02 closure and final access/session work, exact-current warnings-as-errors suites passed **Hexagon 259/259** and **HL2RP 216/216**. Focused projection cases passed **7/7**, scanner cases passed **13/13**, and Hexagon interaction-session cases passed **6/6**. `tools/verify-portable.ps1 -SkipRestore` passed both suites, static integration, and the exact-SHA evidence contract; a fresh generated s&box build produced both assemblies with **0 warnings, 0 errors**. *(2026-07-17 annotation, DEPE-01: at the time of that run the `-SkipRestore` switch left the substring vulnerability probe as the only dependency gate, and that probe was later shown vacuous against sources without vulnerability data. The switch has since been removed, the audited restore is unconditional, sources are pinned with `<auditSources>` in a tracked `NuGet.config`, and the list gate parses the pinned JSON schema. The historical record above is retained unmodified.)*
- The final source-bound `tools/verify.ps1 -SchemaRoot ..\hl2rp-hexagon -SkipRemoteAcceptance` rerun passed locked restores/audits, those same suites, exact sibling/source binding, generated warnings-as-errors compilation with **0 warnings, 0 errors**, transactional commit, clean drain, and exact recovery. Its first attempt stopped intentionally at the editor-open guard; the complete rerun passed after closing the stopped editor. The final visible-editor pass remains separate.
- The exact current HL2RP project was then reopened in a maximized 2560x1399 s&box editor. Auto-host recovered sequence/checkpoint **11/11** under `hexagon/persistence/v3/hl2rp`, reached ready/session-ready, rendered chooser and registration, and survived four transitions without a render-tree error. Stop emitted `HEXAGON_DRAINED host`; the editor reported 0 errors, the log contained no new application error after readiness, and an independent `FileShare.None` open of `lease.lock` succeeded. Nine engine/resource lookup warnings remain an environmental coverage note, not a project compile/runtime failure.
- A final adversarial pass found and closed a post-commit scanner continuation race: revocation could otherwise fall between acknowledged pilot/spotlight state and its world effect. Scanner entry now preregisters before commit, every post-commit effect rechecks `InteractionSessionProof.IsCurrent()` while serialized under the cleanup lock, and a stale continuation unconditionally starts compensating cleanup. Forced after-success entry/spotlight revocations prove no body/effect resurrection and no durable pilot ghost. The caller sweep found no remaining `_access.Has` to durable-commit path or inverse session/cleanup lock order.
- A release-operability pass then closed the last locally actionable acceptance gaps. HL2RP now exposes a tested return-to-character-selection route, clears all character-scoped presentation state across the epoch change, emits strict pre-shutdown/post-restart recovery snapshots from the production lifecycle, and includes lifecycle guards in the canonical recovery digest. Hexagon's format-v3 verifier requires two distinct remote accounts, strict JSON booleans, two distinct canonical server logs, matching sequence/digest snapshots, exact artifact references, and non-future timestamps. Its executable contract suite passes under both Windows PowerShell 5.1 and PowerShell 7.6.3; current neutral suites pass **Hexagon 256/256** and **HL2RP 226/226**, and the generated s&box build passes with **0 warnings, 0 errors**. This makes the external two-client run executable and falsifiable; it does not substitute for that still-pending run.
- Draft PRs [Hexagon #2](https://github.com/noahsabaj/hexagon/pull/2) and [HL2RP #1](https://github.com/noahsabaj/hl2rp-hexagon/pull/1) were opened and retained as drafts. HL2RP's first hosted integration run passed. Hexagon's first neutral run exposed a deterministic CI-boundary bug: five package-layout tests unconditionally required an absent sibling checkout. The fix keeps two Hex-only controls in neutral, marks the three real paired checks `CrossRepository`, excludes only that category in Hex neutral, and leaves HL integration unfiltered. Local neutral **256/256**, paired **3/3**, full Hexagon **259/259**, and the corrected hosted reruns passed.
- Both `main` branches now have strict required-status protection. Administrators are covered; force-push and deletion are disabled; Hexagon requires `hexagon / neutral` plus `sbox / release-evidence`, and HL2RP requires `hl2rp / integration` plus `sbox / release-evidence`. The configuration was read back from GitHub after publication.
- `sbox / release-evidence` is published as pending on both coordinated heads with the description `Awaiting authenticated two-client release evidence`.
- `sbox / release-evidence` must remain pending until two authenticated clients validate both exact source SHAs. This PC is not to be registered as a self-hosted runner.
- The Hexagon library is platform-whitelisted and owns the persistence protocol abstraction. The standalone-only HL2RP game supplies the raw OS `FileStream` lease, write-through, and atomic-rename adapter; this boundary now passes exact-current generated compilation, isolated persistence commit/drain/recovery, fullscreen editor startup/UI cycling, clean drain, and physical lease-release verification.

### Post-closure client archive regression and observability evidence (2026-07-14)

- **Confirmed regression and root cause:** an authenticated remote editor client could join the host but its streamed `package.local.hl2rp` assembly failed s&box access control. Hexagon then reported the downstream symptom `Schema runtime 'hl2rp' is not mounted.` The installed `Sandbox.AccessControl` verifier identified raw operating-system persistence APIs, `Volatile`, and compiler-lowered `ExceptionDispatchInfo` from an awaited `finally` in the client-visible assembly.
- **Smallest coherent fix:** the durable physical adapter now lives only in `HL2RPPhysicalPersistenceStorage.Server.cs`; a compile-time factory selects it under `SERVER` and selects the whitelist-safe `BaseFileSystem` adapter for editor hosting and client-visible compilation. The busy guard uses `Interlocked`, and pistol cleanup no longer awaits in `finally`. Dedicated-server durability remains on the physical write-through/FileShare.None implementation; the editor adapter is explicitly not the production durability proof.
- **Prevention and diagnostics:** `tools/verify-client-assembly-access.ps1` builds the same non-`.Server.cs` source set a remote client receives and runs the installed s&box access verifier. The full verifier invokes that gate through PowerShell 7. Hexagon also accepts only three allowlisted, bounded bootstrap diagnostic phase/code pairs before session binding, charges every attempt, deduplicates reports, derives identity from `Rpc.Caller`, and emits `HEXAGON_CLIENT_FAILED source=remote ...` on the host. Successful bindings emit `HEXAGON_SESSION_BOUND account=... connection=... epoch=...`. This is intentionally not an unrestricted client-console forwarding channel.
- **Verification:** exact-current Release suites passed Hexagon **267/267** and HL2RP **231/231**; the portable integration profile passed; generated client/editor and `SERVER` builds completed with 0 warnings and 0 errors; the client-equivalent assembly passed installed `Sandbox.AccessControl`. In the maximized 2560x1399 editor, the host recovered sequence/checkpoint **11/11**. Authenticated account `76561199767940150` connected twice and each connection reached `HEXAGON_SESSION_BOUND`; no new `HEXAGON_CLIENT_FAILED` marker appeared. Shutdown emitted the canonical pre-shutdown snapshot and `HEXAGON_DRAINED host`.
- **Coverage boundary:** this was an editor host with its local authenticated client plus one distinct remote authenticated client. It verifies the repaired archive/mount/session path, not the protected dedicated-server plus two-remote-client release gate. Raw client logs are still platform-local; complete release evidence still requires the documented two remote client logs and two dedicated-server logs. Two non-blocking s&box engine warnings reported that the remote Steam inventory proof was not OK after deserialization; the engine continued both connections and Hexagon bound both sessions, so this is retained as an environmental concern rather than attributed to the schema-mount fix.

## 2026-07-16 audit remediation register (28-finding cohort)

The 2026-07-16 64-agent adversarial audit retained 28 findings (its candidate ledger disproved DATA-02, DEPL-01, DEPL-06, and DEPL-07; those required and received no work). All 28 are remediated with structural fixes on `agent/v2-audit-remediation` in both repositories. The manual two-client dedicated-server acceptance gate remains deliberately open and `sbox / release-evidence` remains pending; nothing below substitutes for it.

| Finding | Resolution |
|---|---|
| CORR-01 (merges RELI-05, ARCH-06, REME-01) | The orphan-frame discard signal is live end to end: recovery counts discarded unacknowledged frames into `RecoveryState.DiscardedUnacknowledgedFrames`/`PersistenceHealth`, and the runtime logs `orphan_frames_discarded=`. The dead `WalFrameCodec` was deleted. |
| CORR-02 | `CompleteTouchLastPlayed` validates and completes from the commit receipt alone; the racy live re-read is gone, and an empty receipt is rejected as caller misuse. |
| RELI-01 (High) | Scanner cleanup no longer borrows the caller's cancellation token — `BeginCleanup` runs under `CancellationToken.None`, so an exiting pilot's command cancellation cannot wedge the durable cleanup. `ScannerRecoveryRetryPolicy` re-drives failed recovery handles with 5 s→2 min exponential backoff from the host tick (`HL2RP_SCANNER_RECOVERY_HEALED`/`_RETRY_FAILED`), skipping only `StorageLimitExceeded`. |
| RELI-02 | `CanRetryCleanup` treats `StaleTransaction` (compaction-generation bump) as retryable alongside `Conflict`. |
| RELI-03 | Game-supplied interaction code failing now fails closed: `Validate` throws map to `InternalError`, `AuthorizeFailClosed` centralizes the policy, and a throwing scheduled revalidation revokes that session (`scheduled_validation_threw`) instead of aborting the sweep. |
| RELI-04 | `HexClientStore` change fan-out invokes each subscriber via `GetInvocationList` with per-subscriber guards; failures surface through `SubscriberFailureDiagnostic`/`SubscriberFailureCount` and are logged by the runtime. |
| DATA-01 | `HL2RPCharacterExitCleanup` is the single canonical exit set (access revocation, presentation change, chat/lifecycle/combat-health clearing) wired at all four exit sites. Removing combat health dooms outstanding reservations: doomed commits and releases no-op with a diagnostic (`HL2RP_COMBAT_RESERVATION_DOOMED`) instead of resurrecting state. |
| DATA-03 | `SendClientState` applies the host-side public snapshot only after scope capture and encode can no longer abort; the order is pinned by an architecture test. |
| PERF-01 | Container deletion resolves the contained inventory through the keyed `OwnerInventories` index instead of scanning every inventory. |
| PERF-02 | Automatic checkpoints moved off the commit path and off the main thread: commits schedule a background checkpoint through an `Interlocked` latch, and the runtime executes it under `TryStartHostOperation` after `GameTask.WorkerThread()`. `Degraded` health is keyed to checkpoint-fallback recovery only. |
| PERF-03 | Character listing is bounded: `CharacterRules.MaximumSlots = 64`, `ListForAccount` issues 64 keyed slot probes instead of scanning the store, slot allocation returns Conflict at the cap, and slot-range invariants are enforced by the domain validator. Sorted `All()` snapshots are cached per collection and invalidated on publish. |
| PERF-04 | `HL2RPMaintenanceSupervisor` paces ticks to a 100 ms minimum interval with fully fault-swallowing pacing, and the projection directories skip identical-row and absent-null deltas so steady state stops allocating and invalidating snapshots every frame. Faction permission arrays are static. |
| PERF-05 | Inventory mutation cycle detection early-returns for non-item destinations and walks downward through the keyed owner-inventory index (explicit stack DFS) instead of scanning the store. |
| ARCH-02 | The production-dead `CanonicalChatAuthorityDirectory` twin was deleted; the live `LiveChatAuthority` gained structural equality and is the tested implementation. |
| ARCH-03 (absorbs TEST-02) | The command dispatch pipeline is an engine-neutral `CommandDispatchOrchestrator` (costs, admission, completion) with the runtime reduced to a thin `ICommandDispatchHost` adapter, unit-tested directly. The CI half adds `SourceSyntaxTests` in both repositories: every product source — including engine-bound files excluded from test compilation — must parse error-free under both the neutral and `SERVER` symbol sets. |
| ARCH-05 | Recovery tolerates exactly one torn tail artifact per WAL metadata class (unbacked ack at `durableTail+1`, torn commit head backed by a matching ack, torn prune intent), each logged with a dedicated token and bounded to what one interrupted non-atomic write can produce; anything beyond that still fails closed. The s&box adapter half stages `TryWriteImmutableAsync` through a `.staging` write→verify→copy sequence. |
| ARCH-07 | The three duplicated exception-capture wrappers collapsed into one Kernel `AsyncOperation`/`OperationOutcome` implementation with a synchronous-completion fast path. |
| ARCH-08 | `HL2RPSceneEntityBehaviorService.cs` was renamed to match its type, and hexagon's `SceneEntityStateService` documents why it has no in-repo runtime consumer. |
| TEST-01 (High, absorbs ARCH-01) | The untested ~30-route command surface moved out of `HL2RPHostApplication` into engine-neutral `HL2RPClientCommandSink`/`HL2RPSchemaCommandSink` cores (admission, routing, entitlement-before-requirement, permission gating) compiled into the test project and covered behaviorally; the host delegates through explicit interface implementations. |
| TEST-03 | Closure-evidence source scans strip comments first (`HL2RPTestSource.WithoutComments`), the drain-order pin is bounded to the actual method body, and the vacuous suppress-assertion is a real regex pin. Wiring-lint scans are categorized `WiringLint`. |
| TEST-04 | The physical persistence storage logic lives in `HL2RPPhysicalPersistenceStorageCore.Server.cs`, executed by real unit tests over a temp directory; the Sandbox shim only binds `GetFullPath`. Hexagon pins the `FileShare.None`/write-through lease block cross-repository. |
| DEPE-01 (absorbs TEST-05, DEPL-03) | Restores are unconditionally audited against sources pinned in tracked `NuGet.config` files (`packageSources` + `auditSources` cleared and pinned to api.nuget.org); the vulnerability gate parses `dotnet list package --vulnerable --output-version 1` JSON instead of substring-probing, and the `-SkipRestore` escape hatch is gone. |
| DEPE-03 (absorbs TEST-06, DEPL-04) | Cross-HEAD drift is now observable: an advisory `cross-head-canary` job (continue-on-error, never in branch protection) exercises hl2rp@main against the hexagon head, and `-AllowUnlockedHexagon` provides the equivalent local probe with an explicit cross-HEAD banner. |
| CONF-01 (High) | Host migration is refused in depth: `ProjectSettings/Networking.config` in both repositories sets `DestroyLobbyWhenHostLeaves: true` / `AutoSwitchToBestHost: false`, `OnBecameHost` on a non-original host logs `HEXAGON_HOST_MIGRATION_REFUSED` and disconnects rather than fail-opening into an unloaded runtime, and architecture tests pin both the config values and the handler. |
| CONF-02 (absorbs ARCH-04) | Host bootstrap resolution is extracted (`HostBootstrapResolution`) and tested: a non-blank probe with a blank root is `ConfigurationInvalid`. The hl2rp verification probe runs only as the synthetic verification actor against an isolated persistence root, guards its machine lookups, and fails closed with `HL2RP_PROBE_FAILED` diagnostics instead of poking live connected state. |
| CONF-04 | The auto-imported `hexagon.csproj.user` injection point is gated to emptiness by tests in both repositories and carries a MUST-STAY-EMPTY comment. |
| DEPL-02 (absorbs DEPE-02, CONF-03) | Portable verification fails closed on dirty worktrees: `worktree-gate.ps1` (porcelain, untracked included) fronts both portable profiles with its own contract suite; `-AllowDirty` is an explicit, banner-rewriting escape hatch that also skips SHA attribution, and the remote-acceptance fingerprint requires a clean tree unconditionally. |
| DEPL-05 | The release publisher re-reads and re-binds the evidence bundle after its verification window and compares canonical digests, throwing `Release evidence changed during verification.` on any mutation; a fake-verify mutation contract case proves no release is created. |

### Evidence boundary for this register

- Neutral Release suites at the remediation heads pass **Hexagon 381/381** and **HL2RP 286/286** with warnings as errors.
- `tools/verify-portable.ps1` passes clean in hexagon with SHA attribution; the hl2rp portable profile passes as a cross-HEAD canary under `-AllowUnlockedHexagon` pending the lock bump that immediately follows this register. Negative probes were exercised for the record: an untracked scratch file fails the clean gate (exit 1), and `-AllowDirty` passes with the UNCOMMITTED-sources banner and no attribution.
- The final source-bound `tools/verify.ps1 -SchemaRoot ../hl2rp-hexagon -SkipRemoteAcceptance` run (editor closed) passed end to end: audited locked restores, both suites, three generated s&box warnings-as-errors builds with **0 warnings, 0 errors** (covering the engine-bound files no test project compiles), the client-assembly access gate, and the headless transactional commit → drain → exact recovery smoke. This was the fifth passing full run of the campaign; the four earlier runs bracketed every engine-facing change as it landed.
- `hl2rp-hexagon/hexagon.lock.json` is bumped to the final hexagon head in the commit after this one, after which the plain hl2rp portable profile passes with the default locked banner.
- The two-client acceptance evidence and the pending `sbox / release-evidence` contexts are unchanged by this register.
- Post-push, the hosted `hexagon / neutral` job exposed a runner-only divergence that predated this campaign (the identical failure occurred on the pre-remediation head): the ignored-release-material walk's glob pathspecs (`Code/**`, `*.cs`) matched nothing on the runner while literal pathspecs matched, so its contract cases saw the gate pass vacuously. Local git 2.55 (Windows), 2.43, and a from-source 2.54.0 build (Linux) all matched correctly, so the walk no longer depends on pathspec semantics at all: it enumerates bare `ls-files --others --ignored --exclude-standard` output and filters relevance in-process, and a per-process probe repository must prove git reports a planted ignored file before the gate is trusted — an enumeration that goes quiet now fails loudly instead of passing silently. The hl2rp lock advanced to the repaired head.

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
