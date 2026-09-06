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

M2.4 adds revision-fenced `CancelAsync` and `ResumeAsync` operations serialized
by the runtime gate. Durable cancellation is explicit and idempotency-keyed;
caller transport tokens never become cancellation intent. Queued cancellation
publishes `Cancelled` without a provider call. Running cancellation validates
the exact captured handle and receipt key, observes the same handle after
requested, rejected, or ambiguous outcomes, and is allowed to make its safety
call even after the ordinary worker or duration budget is exhausted. Safety
observation and cancellation calls are bounded by the coordinator's internal
`CancellationSafetyCallLimit` (currently 8 calls per runtime); exhausting that
ceiling publishes the honest terminal `NeedsSupervisor` state with an unresolved
cancellation concern rather than leaving a permanently `Running` snapshot or
publishing an unrelated ordinary failure. Resume is limited to an observed
provider `Waiting` state, preserves correlation, generation, and attempt
identity, and permits only an execution-identity-fenced provider handle
rotation captured before the resume receipt. The original previous handle is
immutable for one resume request. If a rotated handle is captured and the
receipt is lost, the capture is accepted ambiguity: the next exact replay
observes the captured handle and never issues a second `ResumeAsync`. Resume
retries without a rotated capture consume the normal worker/retry/duration
budgets.

## Limits before a public coordinator

- Acceptance and execution-state creation are not one atomic operation across
  the two current in-memory stores.
- The coordinator retains private state for its lifetime and has no eviction
  contract.
- The fake slice does not yet publish the objective and constraints as an
  immutable provider input artifact.
- Supervisor checkpoints, candidate sealing, independent test/review, and
  bounded correction remain later Milestone 2 batches.
- Zhinu must own durable execution and recovery; this pump is not a replacement
  workflow runtime.

These limits keep the coordinator internal. They must be resolved or mapped to
durable adapters before an application-facing coordinator API is frozen.
