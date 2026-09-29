# Architecture, boundaries, and usability review

Reviewed: 2026-09-28. Source baseline: `d44d01a5961cd1252d1f7abbf17382ab853cfdbf`.
Companion review: [Guihua](../../Penghou.Guihua/docs/architecture-boundary-review-2026-09-28.md)
(sibling-checkout link).

This document records findings and proposed work, not implemented fixes or an
approved ADR. The review covers the public contracts, in-memory runtime, Codex
adapter, tests, samples and architecture documents.

## Assessment

The separation from Guihua is appropriate and worth preserving. Guihua changes
the structure of a workflow; Qingniao supervises one selected delegation and its
candidate/evaluation/correction lifecycle. Similar loops do not make these the
same responsibility.

The existing separation of caller idempotency, provider identity, semantic node
generation, immutable candidates, deterministic validation and model review is
a strong foundation. Host-supplied verification policy and fail-closed defaults
also keep domain acceptance outside core. Dependency-boundary tests already
prevent other Penghou packages from entering core and abstractions.

The main risks are in carrying those guarantees through real adapters and
recovery. The in-memory runtime is now publicly reachable through
`DelegationRuntime`, while the README still describes a narrower public surface.
The Codex adapter has concrete lifecycle, process I/O and isolation gaps that
scripted happy-path tests do not exercise.

## Boundary contract with Guihua

| Concern | Qingniao owns | Guihua / host owns |
| --- | --- | --- |
| Delegation | Lifecycle of one bounded selected activity | Where it belongs in a larger workflow |
| Provider | Resolve the exact authorized provider and capabilities | Provider choice; Baize handles model routing |
| Correction | New candidate/generation inside the admitted activity and budget | Change graph, descriptors, dependencies or task objective through a new admitted revision |
| Recovery | Reconcile the accepted operation/handle before new work | Zhinu durable scheduling and transition authority |
| Verification | Preserve exact evidence and execute a host verdict | Domain criteria, evaluator trust and acceptance policy |
| Supervision | Checkpoint/revision-fenced local intervention | Authentication, workspace policy, product UX and promotion |
| Artifacts | Candidate/attempt identity and immutable result references | Planning revision/provenance and storage implementation |
| Budgets | Delegated execution allocation and local enforcement | Parent end-to-end allocation and planning expenditure |

Recommended flow:

```text
host -> Guihua proposal -> Fuwen admission -> Zhinu
                                         -> Qingniao activity -> executor
          Guihua <- bounded evidence projection <- execution/candidate receipts
```

Direct Qingniao use remains first-class. Do not introduce a Guihua dependency in
core or require a workflow engine for a simple delegation.

Keep four repair operations distinct: proposal repair, transport reconnect,
candidate correction, and workflow revision. A planner receiving a timeout must
not infer that nothing ran and submit a fresh delegation. A candidate failure
must not silently let Qingniao rewrite the surrounding workflow. Allocate a
shared parent allowance so nested planning and correction do not multiply the
user's intended limits.

The integration adapter should preserve plan revision, run/epoch, structural
node, node generation, attempt, delegation, candidate digest and evidence
identity where present. “Version 2” is not an adequate mapping among those
different identities.

## Findings

Priority: P1 = address before relying on the affected guarantee; P2 = important
design or usability improvement. Reproduced findings ran in isolated probes;
other findings are source-confirmed control-flow observations or explicitly
identified design limitations.

### Q01 — P1: redirected stderr is not drained, and stdout line allocation is unbounded

**Evidence:** [CodexProcess.cs](../src/Penghou.Qingniao.Codex/CodexProcess.cs),
`ProcessCodexProcessFactory.Start` (42–44), `ReadLinesAsync` (63–70);
[CodexExecAdapter.cs](../src/Penghou.Qingniao.Codex/CodexExecAdapter.cs),
`PumpAsync`, `TrackedOperation.NoteLine` (456–469).

Both stdout and stderr are redirected, but only stdout is consumed. A child
that fills its stderr pipe can block while the adapter waits for more stdout or
exit. The capture limit is applied after `ReadLineAsync` has allocated an entire
line; one very large JSON line can exceed the intended memory bound.

**Recommendation:** drain both streams concurrently, retain separate bounded
diagnostics, and cap line/chunk size before building large strings. Continue
draining discarded output so the child cannot block. Make process exit, stream
drain and disposal part of one owned lifecycle.

**Acceptance:** a real local child emitting more than a pipe buffer on stderr
finishes; an oversized newline-free stdout record stays within a documented
memory bound. No model call is needed for either test.

### Q02 — P1: the approved-workspace check is lexical rather than physical

**Evidence:** [CodexExecOptions.cs](../src/Penghou.Qingniao.Codex/CodexExecOptions.cs),
constructor (23–25), `IsUnderRoot` (89–94);
[architecture.md](architecture.md), “Workspace and mutation boundary”.

`Path.GetFullPath` plus an ordinal-ignore-case prefix check does not resolve
symlinks/junctions. A child directory under the approved root can resolve outside
it. Case-insensitive comparison is also inappropriate on case-sensitive
filesystems. Validation computes full paths but stores the original strings,
leaving relative paths dependent on later current-directory interpretation.

The same check also accepts the root itself; it does not establish that a
workspace is an isolated candidate rather than a primary checkout. That
isolation remains a host obligation.

**Recommendation:** resolve and retain absolute approved paths, enforce the
platform's path identity rules, and reject escaping links/reparse points.
Bind execution to the host-approved candidate workspace and validate immediately
before launch. Explicitly define the threat model for concurrent path changes;
a prefix check is not an OS sandbox.

**Acceptance:** outside-root junction/symlink and case-variant sibling cases
are rejected; changing current directory cannot change the approved target;
the composition sample uses an isolated candidate workspace.

### Q03 — P1: cancellation can rewrite terminal success and spawn already-cancelled work

**Evidence:** [CodexExecAdapter.cs](../src/Penghou.Qingniao.Codex/CodexExecAdapter.cs),
`StartAsync` (58–80), `CancelAsync` (149–170),
`TrackedOperation.TerminalState` (443–446).

`StartAsync` spawns before checking the caller token. `CancelAsync` sets
`Cancelled = true` even after success, then marks completion without waiting
for confirmed process exit. A late cancellation changes the observed terminal
state and makes a previously readable successful result throw.

**Reproduced:** an already-cancelled start spawned **one process**. Separately,
reading a successful result and then cancelling changed observation from
`Succeeded` to `Cancelled`.

**Recommendation:** check cancellation before any launch side effect. Implement
atomic operation transitions with a single terminal result. Return
`AlreadyTerminal` for terminal work; distinguish cancellation intent from
confirmed exit. Store/replay cancellation-key receipts rather than deriving a
new outcome from mutable flags.

**Acceptance:** pre-cancelled start spawns nothing; late cancellation preserves
success/failure; racing exit/cancel yields one immutable outcome.

### Q04 — P1: adapter lookup and resume do not fence the full operation identity

**Evidence:** [CodexExecAdapter.cs](../src/Penghou.Qingniao.Codex/CodexExecAdapter.cs),
`ResumeAsync` (185–207), `RequireTracked` (373–381),
`StartAsync` (65–87).

Tracking is keyed by thread string. Observe/get-result/cancel lookup does not
compare provider, protocol or full delegation/attempt correlation with the
stored handle. Resume uses a check-then-spawn sequence; two concurrent resumes
can both start a process and overwrite tracking. Start also has no adapter-level
deduplication by start identity. The core coordinator supplies some protection,
but the adapter is public and its own contract must state and enforce its
requirements.

**Recommendation:** validate the complete handle binding and publish a
single-flight operation slot before spawning. Use stable start/resume/cancel
keys with semantic conflict checks. Avoid a side-effecting `GetOrAdd` value
factory: it may run more than once.

**Acceptance:** the same thread value with different correlation is rejected;
simultaneous equivalent resumes launch at most one process; conflicting
requests return typed outcomes and never affect another tracked operation.

### Q05 — P1 release gate: cold resume needs repeatable protocol and recovery coverage

**Roadmap qualification added during handoff planning:** M3 records a completed
live success and interrupt/resume proof on 2026-09-26 with codex-cli 0.157.0.
That historical proof remains complete. This finding concerns repeatable
versioned tests and the additional concurrency, exact-turn, missing-session and
crash scenarios below; it must not be interpreted as absence of all live proof.

**Evidence:** [CodexExecAdapter.cs](../src/Penghou.Qingniao.Codex/CodexExecAdapter.cs),
`ResumeAsync` documentation and `BuildInvocation` (217–260);
[CodexExecAdapterTests.cs](../tests/Penghou.Qingniao.Codex.Tests/CodexExecAdapterTests.cs),
`ResumeAsync_unknown_thread_spawns_resume_without_prompt_or_bypass`.

After loss of tracking, resume launches another CLI process, pre-populates the
expected thread ID, and returns `Running` without waiting for a validated
reconnect. The test checks argv against a scripted process; it does not establish
that a supported real CLI version accepts those resume flags or that the command
only observes previously accepted work. Ephemeral sessions further require an
explicit non-recoverable policy.

This review did not execute a live CLI resume and does not claim a specific
current CLI behavior. The finding is the gap between the promised
non-duplicating recovery contract and the evidence that currently proves it.

**Recommendation:** version and test the adapter protocol against actual CLI
fixtures/integration behavior. Establish whether thread identity is sufficient
to identify the exact accepted turn. If safe observation/reconciliation is
unavailable, return an explicit unsupported/unknown/supervisor-required outcome
rather than presenting a new process as verified recovery.

**Acceptance:** restart, response loss, live original process, completed turn,
missing session and ephemeral mode have explicit tested outcomes. Recovery must
not submit an additional task or silently report a new turn as the old one.

### Q06 — P1: a blocked provider call prevents cancellation and duration enforcement

**Evidence:** [InMemoryDelegationCoordinator.cs](../src/Penghou.Qingniao/InMemoryDelegationCoordinator.cs),
`PumpAsync` (860–928), `PumpStartAsync` (1043–1050),
`CancelAsync` (261–273), `EnforceDurationAsync` (2032–2061).

The delegation gate stays held while start/observe/result provider calls are
awaited. Cancellation needs the same gate. Duration checks happen between
operations; they cannot interrupt a provider that never returns. A Codex child
that does not emit a thread ID exposes this directly unless the caller supplies
an independent timeout.

Candidate evaluation already releases the gate, demonstrating a useful local
pattern, but provider calls need equally explicit ownership and reconciliation.

**Recommendation:** reserve an in-flight operation and revision under the gate,
perform I/O outside it, then reconcile under the same attempt fence. Record
cancellation intent independently. Use operation deadlines and distinguish
cancelling a caller's wait from cancelling externally accepted work. A timeout
must not authorize an unclassified new start.

**Acceptance:** a stalled start/observe/result cannot prevent cancellation
intent from being recorded; duration expiry becomes observable; a late handle
is reconciled without a duplicate launch.

### Q07 — P1 integration gap: accepted task semantics do not reach the provider through the runtime

**Evidence:** [InMemoryDelegationCoordinator.cs](../src/Penghou.Qingniao/InMemoryDelegationCoordinator.cs),
`RuntimeState` construction (2168–2207);
[CodexExecAdapter.cs](../src/Penghou.Qingniao.Codex/CodexExecAdapter.cs),
`BuildInvocation`;
[ExternalOperationContracts.cs](../src/Penghou.Qingniao.Abstractions/ExternalOperationContracts.cs),
`ExternalOperationStartRequest`.

The runtime creates an `agent.execute` start with empty input artifacts and no
budget/deadline hints. The accepted objective, workspace and acceptance criteria
do not get materialized into an input artifact by this path. Codex instead
uses the prompt/workspace configured on its adapter instance. Registering one
such adapter and submitting different objectives can therefore launch the same
configured prompt for both.

The contract deliberately excludes raw prompts and paths; that is appropriate.
The missing piece is a trustworthy materialization/resolution seam, not adding
unrestricted strings to the provider protocol.

**Recommendation:** let the host materialize immutable execution inputs and
resolve approved workspace/prompt data for the exact delegation. Include those
input identities and applicable bounds in start semantics. Either bind a
single-use adapter explicitly to one admitted request or make the adapter
resolve request-scoped inputs through a narrow host port.

**Acceptance:** two delegations with different objectives/workspaces resolve
different correct inputs; request semantics and recorded fingerprints cover the
actual invocation; mismatched adapter configuration fails before launch.

### Q08 — P2: successful adapter results are not stable, useful evidence

**Evidence:** [CodexExecAdapter.cs](../src/Penghou.Qingniao.Codex/CodexExecAdapter.cs),
`GetResultAsync` (126–145), `PumpAsync`, `EvictCompleted` (384–405),
`TrackedOperation.Summary` (471–480).

Every result read assigns a fresh completion timestamp. Success can be inferred
from exit code zero after minimal events; the current success test supplies
only thread-started and turn-started events. Results contain a tail of JSONL,
no artifacts, no candidate, and no resolved invocation/usage provenance. Tracking
eviction can remove a completed operation before a consumer retrieves it.

**Recommendation:** freeze a terminal receipt once, including completion time,
supported completion evidence, normalized output and retention policy. Publish
sealed candidate/artifact references where available, and typed “unavailable”
fields where not. Persist or acknowledge results before eviction. Keep raw
transcripts in a bounded, access-controlled diagnostic sink.

**Acceptance:** repeated result reads are identical; incomplete event streams
do not masquerade as verified task success; more than 128 operations cannot
silently discard unread receipts; Guihua can consume exact evidence references.

### Q09 — P2: public runtime availability and durability are described inconsistently

**Evidence:** [DelegationRuntime.cs](../src/Penghou.Qingniao/DelegationRuntime.cs),
constructor (21–49); [README.md](../README.md), “Current status”;
[sample](../samples/Penghou.Qingniao.Sample/Program.cs).

The public façade now runs the internal coordinator, but the README says the
preview only exposes contracts and reusable policy/registry components.
The façade constructs in-memory execution/handle state even though acceptance
and intervention registries can be supplied separately. Persisting one registry
does not make the combined runtime durable.

**Recommendation:** document the actual public entry point and its process-local
lifetime. Make partial persistence unsupported or explicitly fail closed on
restart; define one coherent durable recovery seam before advertising durable
execution. Update stale provider “selection” wording to exact-provider
resolution and reconcile the README's built-in Implement description with the
architecture's ownership in Marang.

**Acceptance:** a consumer can identify which APIs run work, which state survives
restart, and what external host owns. A restart test with a retained acceptance
record must not silently launch an already-accepted operation anew.

## Structure and OOP improvements

- The 2,000-plus-line coordinator is a useful semantic reference but difficult
  to extend safely. Extract operation reconciliation, cancellation, supervision
  and candidate progression around their invariants; keep orchestration thin.
- `CandidateEvaluationRunner` is already extracted, but shares the coordinator's
  large mutable `RuntimeState`. Prefer focused state transitions or narrow
  snapshots/commands over dozens of flags, nullable tasks and writable fields.
- Model Codex operation state as an owned object with atomic terminal transition,
  exact identity binding, stream/process lifetime and one frozen receipt.
  Separate invocation construction from execution tracking and event decoding.
- Keep public contracts immutable and constructor-validated. Split very large
  contract files by cohesive concepts without introducing a shared “workflow
  base class”. Remove redundant compatibility aliases only through an explicit
  preview API migration.
- Group construction dependencies by role through validated options/builders,
  while keeping real policy ports explicit. A convenience factory should
  clearly identify in-memory operation and admission defaults.
- Preserve dependency tests, but supplement name-based architectural tripwires
  with behavior: unchanged selected provider, no graph rewrite during candidate
  correction, exact evidence subject, and correct replay under ambiguity.

## Usability and usefulness

1. Publish a supported host recipe that materializes request inputs, pumps work
   with backoff, surfaces waiting checkpoints, applies an intervention and
   retrieves evidence. Include the relationship between polling frequency,
   observation calls and worker-call limits.
2. Add structured progress events/receipts and actionable stop reasons so hosts
   can present “waiting for approval”, “recovery uncertain”, “budget exhausted”,
   and “candidate ready for review” consistently.
3. Add a Guihua/Zhinu composition sample at the adapter or application layer.
   Map a delegation failure into a planning observation without losing its
   candidate or attempt identity. Demonstrate that transport retry reconnects
   while a deliberate plan change creates new lineage.
4. Offer a preflight explanation of resolved provider/capabilities, approved
   workspace, budget allocation and verification policy before launch. Keep
   candidate promotion a separate explicit host action.
5. Clearly label the Codex adapter's currently demonstrated capabilities and
   unsupported recovery/result cases. A caller needs capability diagnostics
   before discovering a missing guarantee halfway through execution.

## Recommended delivery order and verification

1. Fix process I/O, containment and cancellation defects (Q01–Q03).
2. Establish per-operation identity, input materialization and non-duplicating
   recovery (Q04/Q05/Q07); test provider stalls and cancellation (Q06).
3. Freeze result receipts and document retention/durability (Q08/Q09).
4. Refactor around the proven transitions and add the cross-project sample.
   Defer general experiments, ranking and workflow evolution to Guihua/host work.

Executed on Windows with .NET SDK 10.0.401:

```powershell
dotnet test Penghou.Qingniao.slnx --configuration Release --no-restore --verbosity minimal
```

Existing tests passed: **395 core + 19 Codex adapter tests per framework** on
both .NET 8 and .NET 10 (**828 test executions**). This did not run the complete
CI formatting, coverage-threshold or packaging workflow.

Two isolated .NET 10 probes reused temporary copies of
`CodexExecAdapterTests` helpers, without modifying repository tests:

| Probe | Required invariant | Observed |
| --- | --- | --- |
| Start with already-cancelled token | Zero process spawns | One spawn |
| Read success, then cancel and observe | Success remains terminal | State becomes Cancelled |

These probes asserted the required behavior and failed. Other findings are
source analysis and proposed validation scenarios; no live Codex task,
external model call, symlink exploit, or production workflow run was performed.

## Follow-up implementation plan

[Implementation and model handoff plan](implementation-handoff-plan.md) aligns these findings with the roadmap and defines bounded assignments, prerequisites and acceptance tests.
