# Architecture

## Composition

`HexagonBootstrapComponent` declares a stable schema ID. Scene-scoped `IHexSchemaSource` implementations explicitly offer descriptors; duplicate or missing IDs fail startup. `SchemaCompiler` invokes `IHexSchema.Configure(SchemaBuilder)`, configures each `IHexModule`, validates registrations, and topologically orders modules by declared dependencies and stable ID.

Host and client are separate scopes even in a listen-server process. The host owns persistence, domain services, policies, interaction sessions, and one network-spawned command transport. The client owns a non-networked `HexClientStore`, controller, and Razor root. Scene disposal revokes command/session state and drains persistence.

## Layers

Domain and Application do not reference Sandbox. Networking contains closed DTOs and commands, never server aggregates. Runtime and Infrastructure adapt `Rpc.Caller`, `[Rpc.*]`, `[Sync]`, scene tracing, resources, and `FileSystem.Data` at the edge. Architectural tests enforce these boundaries.

## Mutation flow

Every authoritative operation follows the same shape:

1. Derive the actor from the authenticated connection.
2. Resolve committed aggregates and prove membership/capability.
3. Evaluate the mandatory built-in policy and every registered policy; first denial wins and exceptions deny.
4. Build changes on isolated immutable copies.
5. Stage every affected document in one unit of work.
6. Flush one complete WAL frame.
7. Publish the new canonical view.
8. Dispatch snapshots and exception-isolated post-commit events.

No UI or notification observes staged data.
