# QH-08 durable host handoff (prep)

Status: **prep complete 2026-09-30; full QH-08 implementation blocked on the
entry gate in [ADR 0018](decisions/0018-qh07-upstream-durability-audit.md).**
This note tells a host (Marang, Guyabano, or another application) exactly what
to build once the upstream releases land, and what the in-repo seams already
guarantee. It does not claim durable execution: the runtime remains
process-local until a host wires the mapping below against released upstream
stores.

## What the host builds (outside Qingniao core)

No `Penghou.Qingniao.Fuwen` or `Penghou.Qingniao.Zhinu` package (ADR 0017).
The host — a normal Zhinu activity — maps one activity execution to one
Qingniao delegation using only neutral correlation values and opaque
references:

| Qingniao concept | Host (Zhinu) concept | Hongxian concept |
| --- | --- | --- |
| `DelegationId` + `RequestKey` | WorkflowRun step key + idempotency key | Session + causation/correlation IDs |
| `NodeGeneration` | Execution generation (first Zhinu release after `preview.14`) | Recovery attempt chain |
| Provider execution attempt | Step attempt / claim revision | `ExternalOperationReference` |
| Provider durable handle | External-operation record (first Zhinu release after `preview.14`) | Opaque operation correlation |
| `WorkflowReference("qingniao", …)` | Host's opaque run/epoch token travels here; Qingniao never parses it | Cross-system refs |
| Terminal result + evidence bundle | Step completion payload + artifact publication | Session events via the host-driven evidence dispatcher |

## Seams already available (tested)

- **Durable handle witness** (`DelegationRuntime(…, durableHandleWitness: …)`):
  the witness receives every accepted capture event in order — provider start
  captures (early adapter captures and post-receipt captures), resume
  rotations, and idempotent replays — before the coordinator observes provider
  results. Proven by `DurableHandleWitnessTests` (ordering, rotation order,
  failure behavior).
- **Witness contract** (`IExternalOperationHandleCaptureSink`): persist
  atomically; treat exact replays as idempotent. The coordinator keeps the
  in-memory registry as its live read authority; the witness is write-only
  host evidence, not a replica the coordinator reads back.
- **Fail-closed rules (pinned by tests):**
  - Witness throw during start → terminal `Failed`
    ("Provider start receipt validation failed."), zero observe calls, zero
    duplicate starts, terminal replay thereafter.
  - Retained acceptance without live execution state → closed, zero provider
    calls (QH-04 virgin-only replay).
  - No witness configured → process-local only; acceptance alone is never
    durable execution (runtime retention model on `DelegationRuntime`).

## Sink-atomicity warning (read before implementing)

If the witness write fails after the provider accepted work, the coordinator
publishes terminal `Failed` **and the started handle is dropped**: the
external operation keeps running, orphaned. This is honest (no unpersisted
work is ever observed) but unrecoverable inside Qingniao. The host must
therefore:

1. make witness writes atomic and idempotent (same key ⇒ same stored bytes);
2. reconcile orphaned provider work out-of-band (provider-side listing by the
   persisted attempt identity) because Qingniao will not retry a terminal
   `Failed` delegation on its own;
3. never treat a terminal `Failed` with a persisted pre-failure capture as
   "safe to re-delegate identically" without first reconciling the orphan —
   re-delegation creates a new `NodeGeneration`, never a silent retry.

## Crash matrix the host must run (after the entry gate)

Crash before/after acceptance, handle capture, publication, intervention, and
terminal completion; competing/stale supervisors; duplicated delivery;
interrupted reconciliation; exactly one immutable terminal aggregate per
run/epoch with prior candidate evidence unchanged. Each outbox is atomic only
with its owning store; cross-store integration reconciles forward through
idempotent store-local outboxes — never a distributed transaction (ADR 0018
D5). A sample/fake test alone does not close M4.

## Upstream gates (from ADR 0018)

- Zhinu release with external-operation handle persistence, parked waits, and
  execution generations; Hongxian release with the reconciled projection read.
- Fuwen `preview.11` is already sufficient for the reference/admission part
  (no supervisor node; workflow-backed delegation only).
- Hufu M1, Fuwen FI-04 binding, Zhinu fenced admin commands, and typed
  authority waits stay in their owning projects; supervisor mapping uses
  signal waits meanwhile.
