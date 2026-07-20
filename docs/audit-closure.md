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
