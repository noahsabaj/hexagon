# Security model

The host treats every network message as intent. `RpcGuard` constructs an immutable actor from `Rpc.Caller`; replicated platform IDs are display metadata only. Command transports live on a host-owned services object. Player bodies may remain client-owned for input, but all server-authored synchronized properties use `SyncFlags.FromHost`.

Inventory access is transient and external to persisted inventories. Commands require explicit `View`, `Move`, `TransferIn`, `TransferOut`, `Use`, `Drop`, or `Sell` capabilities. The host proves source membership, destination access, session binding, policy, and capacity before one transaction. Character changes and disconnects revoke old grants and sessions.

Continuing interactions are bound to connection, active character, target, and server-issued capabilities. Distance and line of sight are reconstructed on the host, checked at start and completion, revalidated at four hertz, and expire after sixty idle seconds. Target destruction, movement, policy changes, character changes, close, and disconnect revoke them.

Unknown schemas, modules, definitions, actions, permissions, codecs, targets, and handlers fail closed. Policy exceptions deny with diagnostics; post-commit event exceptions are isolated because the mutation is already durable.

Chat is Unicode-normalized, limited to 512 Unicode scalars, and rate-limited per connection. All chat entry points share one reservation-capable global bucket; transactional entry points hold admission through commit and release it on failure. Pending reservations remain debited across refill windows, closing concurrent double-credit bypasses. Recipient calculation uses one immutable live-inventory capture and never performs persistence reads.

Transactions can register read dependencies with `RequireUnchanged`. Those dependencies are validated under the same commit gate as writes, so a membership or authority proof that changes between validation and commit aborts the operation. A dependency-only unit of work performs no write, does not advance the sequence, and creates no synthetic revision.
