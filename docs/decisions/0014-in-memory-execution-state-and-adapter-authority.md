# ADR 0014: In-memory execution state and adapter authority

## Status

Accepted for the Milestone 2 proof.

## Decision

Marang separates public provider metadata from executable authority. The public
provider registry supplies bounded immutable descriptors and deterministic
selection. An internal adapter catalog is populated only by trusted host
composition and resolves the exact `ProviderMatch` selected from a captured
metadata snapshot. Disabled or changed descriptors are unauthorized; missing
adapters are explicit. Replacing the in-memory catalog is the revocation
mechanism for this proof. Durable generation fencing remains a later host/Zhinu
concern.

The in-memory delegation execution store owns observable progress and terminal
results, but not private coordinator state. Creation publishes `Queued` revision
zero exactly once. Every changed progress snapshot requires the expected current
revision and must satisfy `DelegationLifecycle`. Exact replay is idempotent;
stale or conflicting publication fails.

Terminal progress and its matching immutable `DelegationResult` are installed
under one lock as one snapshot. A reader therefore cannot observe a terminal
state without its result, or a result before terminal state. Result replacement
and terminal reopening are rejected.

## Consequences

The deterministic coordinator can return immediately after queued acceptance,
then advance one explicit operation at a time without background-task races.
Its private runtime state will retain the bound request, captured registry and
catalog revisions, selected adapter, execution identities, handle, and last
observation; those values are not reconstructed from public progress.

These stores prove semantics only. They do not claim crash durability, and they
do not replace Zhinu execution state or Hongxian session evidence.
