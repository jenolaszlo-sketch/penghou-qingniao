# ADR 0003: Fixed delegation lifecycle

Status: accepted for the implemented fixed strategy. The planned
`WaitingForSupervisor` and execution-identity extension is specified
separately in [ADR 0004](0004-supervisory-waiting-and-generations.md); it is not
implemented by the current public enum.

## Decision

Marang's first strategy uses the following provider-neutral lifecycle:

```text
Queued -> Running -> Completed
                  -> Failed
                  -> Cancelled
                  -> BudgetExceeded
                  -> NeedsSupervisor
```

`Queued` may also move directly to `Failed`, `Cancelled`, `BudgetExceeded`,
or `NeedsSupervisor` when the operation cannot begin safely. Same-state
updates are allowed as idempotent observations. Every terminal state is
immutable; no transition out of `Completed`, `Failed`, `Cancelled`,
`BudgetExceeded`, or `NeedsSupervisor` is legal.

`NeedsSupervisor` is terminal for the fixed strategy. It means Marang has
stopped and requires an explicit future action. It is not an implicit retry or
continuation. A future continuation can create a linked delegation with a new
identity and explicit policy.

## Progress and results

Progress revisions are non-negative and monotonic. A changed snapshot must
have a greater revision; replaying an identical snapshot at the same revision
is idempotent. Worker-call and retry counters, as well as update timestamps,
cannot move backwards. A state change must obey the transition table.

A `DelegationResult` is available exactly when the corresponding progress
snapshot is terminal and the result has matching delegation identity and state,
valid non-negative evidence counters, a non-empty bounded summary, and a
completion timestamp that is not earlier than terminal progress. Non-terminal
progress must not expose a result, while a terminal progress snapshot must not
be published without one. Failure, cancellation, and budget exhaustion are
durable outcomes; they do not roll back workspace effects or evidence.

Terminal result publication is also immutable. The first valid result is
authoritative; an exact semantic replay (including ordinal collection contents)
is idempotent, while any changed summary, evidence, artifact reference,
concern, timestamp, identity, or state is rejected. A durable store must make
this compare-and-publish operation atomic with the terminal progress update.

The lifecycle helper is pure and provider-independent. A durable implementation
must persist the state/result transition atomically or expose an equivalent
recovery contract; this batch does not choose a storage mechanism.

## Consequences

- Clients can reason about completion without provider-specific state names.
- Retries and replay cannot regress observable progress.
- Supervisor intervention is explicit and auditable.
- Future resumability remains possible without making the current terminal
  state ambiguous.
