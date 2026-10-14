# 0022. Add experimental durable provider recovery

Status: Accepted for AAAW M1

## Context

DelegationRuntime keeps coordinator state in memory. Its existing handle
witness persists early provider handles, but that witness alone cannot tell a
host whether a provider start was never issued, issued without a captured
handle, or completed. Retrying a start after a restart can therefore duplicate
external work.

AAAW M1 needs a narrow recovery boundary for provider operations. The durable
host store belongs to Marang, while Qingniao owns the recovery semantics and
the bounded records that store must retain.

## Decision

Add an experimental IExternalOperationRecoveryJournal contract and a
DurableExternalOperationProvider decorator. Hosts register the decorator in
the existing InMemoryExternalOperationProviderCatalog, preserving the
DelegationRuntime constructor and its binary signature.

The decorator verifies the existing semantic fingerprint contract before it
touches the journal. It creates an immutable start intent before provider
contact. The provider's handle callback writes each handle to the journal
before forwarding it to the runtime's capture sink. Provider observations and
terminal results are recorded before the decorator returns them.

Recovery is deliberately narrow:

| Journal state | Recovery behavior |
| --- | --- |
| Start intent with no handle | Report UnknownAfterRecovery; never start the provider again |
| Captured handle, provider supports observation recovery | Return an existing start receipt and replay the persisted capture |
| Captured handle, provider cannot reconnect safely | Persist KnownNotReconnectable; require operator resolution |
| Persisted terminal result | Return the existing receipt/result without provider execution |

ResumeAsync fails closed. The decorator does not claim provider-independent
exactly-once execution, cancellation recovery, or universal reconnectability.
The host journal must atomically compare the exact attempt identity and
semantic fingerprint, reject conflicting correlation, and enforce global
uniqueness of (provider, handle value). Opaque handles belong in protected
host storage and must never appear in exception messages or user-facing
evidence.

## Consequences

Marang can implement the journal with its existing durable store without
bringing a persistence framework or SQLite dependency into Qingniao. The
runtime continues to consume a normal IExternalOperationProvider, so hosts
opt in by registering the decorator.

Coordinator state itself is still in memory. This decision only makes the
external provider start/handle/observation/result binding durable; a complete
workflow recovery path requires the host to restore and fence the broader
execution state.
