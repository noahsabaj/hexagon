# Schema authoring

Implement `IHexSchema` with a stable lowercase ID and add explicit `IHexModule` instances. Modules declare dependencies through `ModuleBuilder.DependsOn` and register fields, factions, classes, actions, items, chat channels, commands, permissions, panels, persisted types, initializers, policies, and event handlers.

Registration is fail-fast:

- IDs must be stable and unique.
- Dependencies must exist and the graph must be acyclic.
- Classes must reference a registered faction.
- Item actions and command/chat permissions must exist.
- Droppable items must declare a resolvable world model; other items must be explicitly non-droppable.
- Every concrete schema-state and trait type needs matching persisted metadata and a codec with the same ID, CLR type, and version.
- The runtime descriptor must supply a complete `SchemaPersistenceInvariantProfile`: one character-state type, an exact trait contract for every item definition, and every character-reference category and persistent scene-entity kind the schema may store.
- Persisted configuration values must use a stable supported scalar type (`string`, Boolean, 32/64-bit integer, decimal, or GUID) and a typed codec. Defaults and validators run before host construction.
- Every policy context used at runtime needs exactly one built-in policy. Additional policies can only narrow access.

Character creation accepts only core fields plus registered creation fields represented by the closed string/integer/Boolean/choice union. The host revalidates faction, class, model, whitelist, caps, balance, rank, and schema state. Initializers are pure planners; the framework creates the character, main inventory, reservations, bags, and loadout in one commit.
