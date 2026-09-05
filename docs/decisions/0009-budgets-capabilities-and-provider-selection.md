# ADR 0009: Bounded budgets, capabilities, and provider selection

## Status

Accepted for Batch 6A. These contracts are immutable policy and evidence
boundaries; durable storage and provider invocation remain adapter concerns.

## Decision

Marang represents budget quantities as bounded integers. Counts and tokens use
integer units, elapsed time uses milliseconds, and monetary values use declared
integer currency units. Floating-point values and locale-dependent decimal
strings are not part of the budget contract. A `BudgetDefinition` is versioned
and contains a bounded set of uniquely named `BudgetLimit`s. A
`BudgetConsumptionReceipt` is an immutable, ordered, idempotency-keyed record
of positive charges. `BudgetAccounting` applies receipts to an immutable
snapshot with checked addition and returns a `BudgetExceededOutcome` when a
ceiling is crossed.

The existing `BudgetExceeded` lifecycle state is a durable terminal outcome.
Its result must carry the delegation-owned definition version, triggering
receipt, exact limit, exact accumulated consumption, charged dimension, and a
bounded reason. Terminal replay equality includes this outcome; it cannot be
added, removed, or rewritten after publication. Budget exhaustion does not
roll back workspace effects or evidence.

Provider and model identities remain open strings. `ProviderHints` carries
bounded provider/model/profile preferences without a closed enum. Providers
advertise open, integer-versioned `CapabilityDescriptor`s. A
`CapabilityRequirement` matches only an equal name, a sufficient version, and
all requested attributes. `ProviderSelection` filters disabled or incomplete
providers and orders matches deterministically by hint score, configured
priority, and ordinal provider identity. The caller cannot turn a hint into
authorization; the host supplies the authorized provider set.

## Security and integrity consequences

- All collections are copied and bounded at construction; callers cannot
  mutate receipts, definitions, descriptors, or selection results afterward.
- Receipt sequence ordering and definition-version equality make replay and
  accidental cross-definition accounting explicit failures.
- Budget snapshots retain a bounded set of receipt ids. A receipt id is
  rejected even when replayed with a newer sequence, and every charge
  dimension is validated against the definition before aggregation begins.
- Checked integer addition rejects overflow before a durable snapshot can be
  published.
- Capability claims are discovery input only. Host authorization and endpoint
  policy remain outside these contracts.
- Provider/model names are open and versioned by data rather than code enums,
  so adding a provider does not require a Marang contract release.

## Non-goals

This ADR does not define pricing conversion, provider authentication,
external-operation start/reconnect/cancel, persistence schema, or a policy for
which budget dimension a workflow must charge. Those belong to the host,
provider adapter, or durable execution integration.
