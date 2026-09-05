# ADR 0004: Supervisory waiting and execution identities

Status: accepted for the provider-neutral core; durable execution amendment
pending implementation.

## Context

The implemented fixed lifecycle uses terminal `NeedsSupervisor` for
unrecoverable or current-policy escalation. The product direction also needs a
durable pause where a supervisor can leave and later return with bounded
context, decide, and continue without treating a pause as failure.

## Decision

Add `WaitingForSupervisor` as a nonterminal, resumable state. It represents an
intentional checkpoint, not an error. A host-authenticated supervisor resumes
it with an idempotent, expected-revision-fenced intervention. Notifications
and wake schedules are hints only; they cannot authorize work, alter state, or
extend a budget.

Each pause has a stable `SupervisorCheckpointId` scoped to its supervised work,
workflow run/epoch, plan revision, and structural node/checkpoint address. A
supervisory intervention targets the checkpoint ID, the expected current
revision, and a caller-scoped idempotency key. A top-level
`WaitingForSupervisor` gates workflow progress that depends on that decision;
other eligible, independent branches may continue. It is not an unconditional
global stop.

The provider-neutral checkpoint descriptor records the host-supplied Hongxian
session reference, delegation, selected plan revision identity, workflow
run/epoch, structural node, node generation, expected observable revision, and
the dependent-progress gate. The gate is always true for a waiting checkpoint;
nonblocking attention is a wake hint, not `WaitingForSupervisor`. The plan
value is not authorization or proof of Fuwen validation; a host policy/resolver
must establish that before execution.
`Queued` may enter `WaitingForSupervisor` when the first eligible node is a
supervisor checkpoint. A waiting progress update may advance its observable
revision, but all checkpoint identity fields and the dependent-gating decision
remain stable, and the expected observable revision must equal the enclosing
progress revision exactly. Batch 3 defines the descriptor only; intervention
commands and authorization policy are Batch 4.

Keep `NeedsSupervisor` terminal for escalation that cannot continue under the
current policy. No terminal execution, result, candidate revision, or audit
evidence is reopened or mutated.

Use an explicit identity hierarchy:

```text
Hongxian Session
  -> Marang SupervisedWork / Delegation
    -> Fuwen PlanRevision
      -> Zhinu WorkflowRun / ExecutionEpoch
        -> structural Node
          -> NodeGeneration
            -> provider ExecutionAttempt / handle
              -> immutable artifacts
```

Hongxian is the session/correlation authority and Zhinu remains execution
truth. A retry/reconnect stays in the same `NodeGeneration` and may create a
new provider attempt only where policy permits, with the same semantic input.
Semantic node re-execution creates a new `NodeGeneration`. Reopening completed
supervised work creates a new linked Zhinu `WorkflowRun`/`ExecutionEpoch`; it
never mutates terminal results. The current accepted generation is selected
through an idempotent, revision-fenced intervention rather than by replacing
history.

Request identity keeps the historical canonical v1 contract intact. A plan-bound
v2 fingerprint explicitly includes either a versioned built-in preset reference
or a host-approved Fuwen definition reference's canonical fingerprint and
revision. The host must verify and authorize that reference before execution.
Advanced Fuwen-plan acceptance remains gated on Fuwen P0 validation;
Marang does not define Fuwen runtime semantics.

## Consequences and non-goals

- The supervisor can schedule future attention and return without replaying an
  entire session.
- Re-entry context is bounded and demand-driven, with Cangjie and Hetu
  identities recorded when used.
- Zhinu owns durable waits, signals, recovery, leases, and selective restart;
  Marang owns who may intervene and what the intervention means.
- This amendment does not make Marang a workflow engine, add unbounded retry,
  or permit arbitrary graph execution.
