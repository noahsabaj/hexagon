# Hexagon v2

Hexagon is a strongly typed, host-authoritative roleplay framework for s&box. Version 2 is a clean break: it has no v1 data reader, compatibility manager, reflected character variables, static permission hooks, or client-owned mutation RPCs.

The framework is split into explicit layers:

- `V2/Kernel` compiles one schema and its deterministic module graph.
- `V2/Domain` contains immutable records and strong identifiers.
- `V2/Application` owns policies, transactions, inventory capabilities, interactions, item actions, chat, and lifecycle rules.
- `V2/Persistence` provides canonical repositories, isolated unit-of-work editors, WAL recovery, and checkpoints.
- `V2/Client` and `V2/Networking` expose immutable snapshots and intent commands only.
- `V2/Runtime` and `V2/Infrastructure` are the only s&box-dependent layers.

The library does not own a scene. A game package owns its startup scene, places one `HexagonBootstrapComponent`, and supplies an explicit `IHexSchemaSource` for the selected schema ID.

## Verify

From the parent workspace:

```powershell
./hexagon/tools/verify.ps1 -SkipRemoteAcceptance
```

The command runs the neutral .NET suite, validates package and asset ownership, generates and builds the mounted s&box projects with warnings as errors, and executes the hidden whitelist/asset/startup smoke check.
This is the explicitly incomplete local-only form; a release invocation must
instead provide the real two-client evidence manifest described in the testing
guide.

See [architecture](docs/architecture.md), [schema authoring](docs/schema-authoring.md), [security](docs/security.md), [persistence](docs/persistence.md), [testing](docs/testing.md), and the [v2 audit closure register](docs/audit-closure.md).
