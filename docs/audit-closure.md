# v2 audit closure register

This register keeps the original seven-axis audit IDs visible through the v2
cutover. `Locally verified` means the obsolete surface was removed or replaced
and the applicable local gates passed. The current evidence set is 168 passing
Hexagon tests, 167 passing HL2RP tests, a passing static project validator, a
generated s&box rebuild with zero warnings and zero errors, and a successful
headless commit -> drain -> reopen smoke with exact recovery.

All 31 findings and all nine shared implementation root causes are closed by
that local evidence. The real dedicated-server/two-authenticated-remote-client
acceptance run is still mandatory release evidence. It remains pending for the
realm- and transport-dependent portions of NET-1, UI-2, LIF-1, and the aggregate
BLD-2 gate; this does not reopen their fixed code defects.

## Shared root causes

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
