# ADR 0016: Use Siming fingerprints and an explicit in-memory pump

## Status

Accepted for the first Qingniao preview.

## Context

External work needs an idempotency identity that is reproducible from its
persisted semantic inputs. Qingniao also needs to prove start, reconnect,
observation, and result behavior before binding that policy to Zhinu durability
or exposing a public coordinator.

## Decision

`Penghou.Qingniao` depends on Penghou.Siming `0.1.0-preview.4` for canonical
JSON v2 SHA-256 computation and verification. Qingniao projects its external
start envelope into an explicit private DTO so CLR refactors do not silently
change the hash contract. The frozen representation uses lowercase D-format
GUIDs, UTC round-trip deadlines, exact duration ticks, explicit nulls, and
ordered artifact references.

The initial coordinator remains internal. `AcceptAsync` publishes `Queued` and
`PumpAsync` performs one bounded provider interaction at a caller-supplied
observable revision. It captures external handles before acknowledging starts,
reconnects after ambiguous acceptance, preserves the same start identity across
retry, counts every provider call, enforces call/retry/duration budgets, and
publishes terminal progress and result atomically through the execution store.

Providers classify adapter failures with `ExternalOperationProviderException`.
Only explicitly retryable failures are retried. Raw exception messages are not
copied into ordinary results. Unclassified exceptions fail closed with a stable
summary.

The first built-in node generation is derived deterministically from the
delegation ID. Reconstructing queued private state therefore cannot invent a
different provider identity.

## Limits before a public coordinator

- Acceptance and execution-state creation are not one atomic operation across
  the two current in-memory stores.
- The coordinator retains private state for its lifetime and has no eviction
  contract.
- The fake slice does not yet publish the objective and constraints as an
  immutable provider input artifact.
- Cancellation/resume, supervisor checkpoints, candidate sealing, independent
  test/review, and bounded correction remain later Milestone 2 batches.
- Zhinu must own durable execution and recovery; this pump is not a replacement
  workflow runtime.

These limits keep the coordinator internal. They must be resolved or mapped to
durable adapters before an application-facing coordinator API is frozen.
