# ADR 0020: Host-owned pre-execution authority preflight for delegated execution

## Status

Proposed (QH-08 slice). Implementation follows this record; the seam is frozen
before code because it changes Qingniao's core execution contract, not an
adapter.

## Context

Qingniao resolves a host-supplied provider, verifies registration, availability,
and capability match, and launches an external operation. That is feature
matching, not authority: per [ADR 0009](0009-budgets-capabilities-and-provider-selection.md),
`RequiredCapabilities` selects a provider and never authorizes work.

Delegation admission (`IDelegationAdmissionVerifier`, `DelegationAdmission.cs:107`)
is the only existing host authority seam. It runs at acceptance, **before**
`DelegationId` and any generation exist (`InMemoryDelegationCoordinator.cs:144`),
is synchronous and decision-only, and returns one of
`DelegationAdmissionStatus = { Admitted, Unknown, Unauthorized, Rejected }`
(`DelegationAdmission.cs:4`). It cannot express a bounded-authority derivation,
which is inherently keyed by `(delegation, generation, parent grant, requested
authority)` — values that do not exist when admission runs. There is no
`Unavailable` value, so the current seam also cannot distinguish "denied" from
"the authority provider could not be reached".

Hufu `0.1.0-preview.5` now provides derived authority: the host derives an
attenuated child grant from a parent, with an exact-tuple derivation identity,
containment in core, and ancestor-liveness gating at use time. Qingniao must
request and carry that outcome without ever manufacturing, interpreting,
widening, or substituting authority.

No authority carry field exists today (`RuntimeState`,
`InMemoryDelegationCoordinator.cs:2425`; the observable
`DelegationExecutionSnapshot`). The generation is
`new NodeGenerationId(delegationId.Value)` (`:2471`): stable across a retry,
rotated for a new generation. That is already the right binding key.

## Decision

Qingniao gains a host-owned pre-execution authority preflight between durable
execution identity creation and adapter start. Qingniao carries the result
opaquely but never interprets authority.

### D1 — Placement (the authority start boundary)

- The preflight runs **after** `DelegationId` and `NodeGeneration` exist.
- It runs **before** `Adapter.StartAsync` (`InMemoryDelegationCoordinator.cs:1151`).
- This point is the authority start boundary for delegation execution.

### D2 — Inputs

The preflight receives Qingniao-owned execution identity plus host-supplied
labels:

- `DelegationId`
- `Generation` (`NodeGenerationId`)
- `ParentGrantId` (opaque host value; never interpreted)
- `RequestedAuthority` (opaque host value; never interpreted)
- `SupervisorIdentity` (label only)

No Hufu types appear in Qingniao core. `ParentGrantId` and `RequestedAuthority`
are opaque strings/values Qingniao passes through unchanged.

### D3 — Outcome

Do **not** reuse `DelegationAdmissionStatus`. The preflight returns its own
result with three values:

- `Permit`
- `Deny`
- `Unavailable`

This preserves the distinction Hufu already makes (explicit refusal versus an
authority provider that could not be reached) without overloading admission.

### D4 — Opaque carry

On `Permit`, the host returns an execution authorization attachment. Qingniao
carries it but must not dereference, inspect, or interpret it. Conceptually:

```text
AuthorityAttachment
    opaque host-owned value
```

It is stored with the runtime/execution snapshot and handed through to the
execution adapter. The host owns interpretation; Qingniao owns only transport.

### D5 — No serialization assumption yet

The durable representation is deliberately not frozen here. The requirement is
narrow: **the authority attachment must survive the same execution lifecycle as
the delegation generation.** Whether that durable form is a serializable opaque
identifier, a small authority-context DTO, or a host-resolvable key is decided
by the proof slice, not assumed now.

### D6 — Lifecycle identity

Bind the attachment to exactly:

```text
DelegationId + Generation
```

`RequestedAuthority` remains part of Hufu's derivation identity, not Qingniao's
delegation identity. Consequences:

- Same-generation retry: same derivation, same child issuance.
- New generation: the authority preflight runs again.

### D7 — Failure behavior

- `Deny` — the delegation must not start.
- `Unavailable` — the delegation must not start.

They remain separately observable to the supervisor/runtime, but neither may
degrade to: parent authority, no authority, default admit, or provider
capability.

### D8 — Explicit separations (negative, because these are the likely future mistakes)

- `RequiredCapabilities` != authority.
- Admission fence != authority approval.
- `SupervisorIdentity` != authentication credential.
- `RequestedAuthority` != execution permission.

### D9 — No authority request

Preserve existing behavior: the preflight is invoked **only** for delegations
that request derived authority (`ParentGrantId` / `RequestedAuthority` present).
A delegation that requests no derived authority is unaffected, so this slice
does not make Hufu mandatory for every Qingniao execution.

## Durability boundary (investigated during this record)

The preflight must not be able to leave durable state that "looks executable"
but never acquired authority. The current in-memory lifecycle has three relevant
points:

| Point | Code | Meaning |
| --- | --- | --- |
| Durable execution record created (`DelegationState.Queued`) | `executionStore.CreateAsync`, `InMemoryDelegationCoordinator.cs:268` | First durable, restart-visible record; a replayed acceptance accepts an existing `Queued` record (`:258`) |
| Generation computed (`new NodeGenerationId(delegationId.Value)`) | `RuntimeState` ctor, `:2471`, constructed `:274` | Execution identity |
| Begin transition (`Queued -> Running`) | `publisher.PublishRunningAsync`, `:1093` (and retry `:1134`) | Precedes `Adapter.StartAsync` `:1151` |

In the current order the durable record is committed (`:268`) **before** the
generation is computed (`:274`, `:2471`). There is therefore no point today at
which "generation exists and no durable record exists", and a naive preflight
placed near `:1091`–`:1151` would run after durable executable state already
exists.

**Frozen invariant:** no durable execution record from which a restart could
begin provider work may exist unless it already records an authority outcome —
either `Permit` plus its attachment, or a non-startable `Deny`/`Unavailable`.
This is achieved by one of:

1. (Recommended) Establish the generation identity before the durable execution
   record is committed; run `AuthorityPreflightAsync`; commit the execution
   record with the authority outcome atomically. This requires computing the
   correlation/generation before `CreateAsync`.
2. Treat `Queued` as non-startable and make the `Queued -> Running` transition
   atomically record the resulting attachment.

Either way the preflight outcome and the transition that makes a generation
startable are committed together.

## The seam

```text
Initialize delegation
    |
    v
DelegationId + Generation established
    |
    v
AuthorityPreflightAsync(delegation, generation, parentGrantId, requestedAuthority, supervisorIdentity)
    |
    +--> Deny        -> not startable (observable)
    +--> Unavailable -> not startable (observable)
    |
    +--> Permit + opaque AuthorityAttachment
    |
    v
persist runtime state / execution snapshot  (atomically with the startable transition)
    |
    v
Adapter.StartAsync(... attachment ...)
```

## Consequences and non-goals

- Qingniao's core gains one host-owned authority seam and one opaque carry field
  bound to `DelegationId + Generation`; it gains no Hufu dependency and no
  authority semantics.
- Admission (`IDelegationAdmissionVerifier`) is unchanged and keeps its current
  values and placement.
- The durable form of the attachment stays open until the proof slice.
- Non-goals: Qingniao does not derive, attenuate, validate, or refresh
  authority; it does not add typed authority waits; it does not cascade
  revocation; ancestor liveness remains a use-time host/Hufu concern.

## Proof slice

A parent grant P delegates to a Qingniao delegation D requesting attenuated
child C. The host derives C through Hufu, D executes carrying C, and a Marang
`fs.read` inside C succeeds while a read inside P but outside C fails. Revoking
P makes the next use fail as `authority.ancestor-revoked`. A same-generation
retry produces the same child issuance; a new generation produces a distinct
one.

## References

- [ADR 0003](0003-delegation-lifecycle.md) — fixed lifecycle and durable
  transition atomicity.
- [ADR 0004](0004-supervisory-waiting-and-generations.md) — execution
  identities and `NodeGeneration`.
- [ADR 0009](0009-budgets-capabilities-and-provider-selection.md) —
  capabilities and provider selection are not authority.
- [ADR 0014](0014-in-memory-execution-state-and-adapter-authority.md) —
  adapter authority in the in-memory slice.
- Hufu `docs/decisions/0012-derived-authority-lineage.md` and
  `0013-delegability-of-derived-authority.md` — derived-authority semantics
  (external).
