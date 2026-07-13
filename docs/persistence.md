# Persistence

The shipped provider stores v2 data beneath `hexagon/v2/<schema-id>` and never interprets v1 files. `InMemoryPersistenceProvider` shares the transactional engine for deterministic tests; `FileSystemPersistenceProvider` uses an injected storage adapter and is the single-server default.

Documents are versioned envelopes with collection, key, revision, persisted-type ID/version, and concrete payload. Every type is registered explicitly. Repositories expose one canonical committed instance per address; unit-of-work editors deserialize isolated copies, and staged changes remain invisible until commit.

Commits are serialized as length-framed, SHA-256 checksummed WAL batches. The committed view changes only after the complete frame is durable. A partial final frame is truncated during recovery; checksum, framing, or JSON corruption before the tail is fatal.

Checkpoints are immutable content-addressed blobs with checksummed manifests and completion markers. The provider retains exactly two complete generations, falls back to the prior valid generation, replays required WAL batches exactly once, retries checkpoint failures, exposes health and sequence state, and drains in-flight commits before disposal.

Framework outer envelopes are validated after recovery and before schema host construction. The active schema supplies an immutable persistence-invariant profile declaring its character-state type, exact trait set for every item definition, character-reference category contracts, and scene-entity kind contracts. Validation resolves every nested type through both compiled metadata and the frozen codec registry, decodes its current version, verifies canonical document and owner-index keys, and checks reverse slot guards, reservations, locations, and reference targets. A corrupt recovered graph therefore fails without first writing defaults or running schema initialization.

Schema configuration is stored as immutable `hexagon.config` documents in the `configuration` collection. `TypedConfigurationStore` rejects unknown keys, type changes, malformed codec payloads, and stale revisions. It creates missing defaults in one commit only after recovered domain validation succeeds. Runtime consumers read and update values through their registered typed definitions; configuration snapshots expose only stable type IDs, document revisions, and canonical encoded values for deterministic recovery evidence.
