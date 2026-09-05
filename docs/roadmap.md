# Qingniao delegated-execution runtime roadmap

> Batches 1–7 and the completed Milestone 2 sub-batches were implemented under
> the original `Marang` project names. Their decisions and progress now belong
> to `Penghou.Qingniao.Abstractions` and `Penghou.Qingniao`. See
> [ADR 0015](decisions/0015-qingniao-extraction-and-marang-service-boundary.md).

## Goal

Provide a reusable delegated-execution runtime for coding agents and other
bounded artifact-producing work. Qingniao supervises one delegated activity and
its execution actor; it may participate in a Zhinu workflow but is not a general
workflow runtime. Supervisors can leave, return at defined checkpoints, inspect
bounded re-entry context, intervene, and selectively re-execute that delegated
activity.

Progress is marked here so development can resume without relying on chat
history.

The cross-project order and primitive release gates are recorded in the
[dependency release plan](dependency-release-plan.md).

## Current direction

Qingniao is the reusable delegated-execution runtime beneath Marang and other
hosts. A supervisor may leave, return at a checkpoint, inspect bounded context,
intervene, and selectively re-execute the delegated activity. Workflow
selection, durable multi-step scheduling, and remote transport belong to the
host, Fuwen, and Zhinu rather than Qingniao.

The implementation sequence is:

1. Freeze identity, lifecycle, evidence, budget, provider, and intervention
   contracts.
2. Prove the preset and advanced Fuwen workflow seam in memory, including
   waiting, wake hints, bounded re-entry, and one new `NodeGeneration`.
3. Prove a bounded Codex provider in an isolated workspace.
4. Map the proven semantics to Zhinu durable execution and recovery, then
   integrate context, evidence, MCP, and dogfooding.

Fuwen owns workflow semantics/compiler; Zhinu owns durable execution;
Hongxian owns session continuity and audit for the real durable slice; Hetu,
Cangjie, and Baize own code context, retrieval, and model execution. Qingniao
coordinates one delegation's execution actor, budgets, capability selection,
supervision, evidence, and result aggregation.

### Optional workflow composition

Direct delegation remains a first-class Qingniao use case and requires neither
Fuwen nor Zhinu. Applications that need programmable durable multi-step work
compose the peer capabilities in this direction:

```text
Fuwen -> WorkflowPlan -> Zhinu -> Qingniao -> execution actor
```

Fuwen parses, validates, and compiles workflow semantics. Zhinu schedules and
recovers the workflow and invokes Qingniao from a normal activity. Qingniao
receives an ordinary bounded delegation request; it does not parse Fuwen syntax
or own workflow topology. Qingniao provider retry is limited to safely
classified failures within one delegation. Zhinu owns workflow/activity retry,
restart, graph invalidation, compensation, loops, fan-out, signals, and durable
recovery.

Workflow, node, session, application-request, and parent-delegation lineage is
represented through neutral correlation values and immutable references rather
than Fuwen-, Zhinu-, Marang-, or Guyabano-specific fields in core abstractions.
Do not create `Penghou.Qingniao.Fuwen` or `Penghou.Qingniao.Zhinu` until both
Marang and Guyabano demonstrate substantial identical adapter code. See
[ADR 0017](decisions/0017-optional-workflow-composition.md).

## Extraction

Status: **extracted and committed; first preview publication pending**

- [x] Create the independent repository and solution layout.
- [x] Copy the reviewed abstractions, runtime, tests, ADRs, and design notes.
- [x] Rename namespaces, project identities, packages, and public API baselines.
- [x] Verify build, formatting, all tests, package contents, and API analysis.
- [x] Record the extraction commit.
- [ ] Publish `0.1.0-preview.1` after the current Milestone 2 package gate.
- [ ] Replace Marang's duplicate runtime sources with its service boundary after
      the Qingniao package is available.

## Gate 0.5 — Primitive capability audit

Status: **complete — all six primitive capability rows audited**

Audit date: **2026-09-04**. The audit covered the local source repositories
under `C:\Users\Laszlos\source\repos` and the released dependency state known
to this repository: Zhinu `0.1.0-preview.12`, Hongxian `0.1.0-preview.2`, and
Siming SQLite `0.1.0-preview.3`; Fuwen `0.1.0-preview.1`, Hetu
`0.2.0-preview.3`, Cangjie `0.1.0-preview.3`, and Baize `0.3.0-preview.5`
were inspected as local source states, not assumed to be published releases.

This is not a claim that the audit is complete. Before adding a Qingniao
abstraction or workaround, inspect the released/current APIs and roadmaps
below. Record each result in the owning primitive's roadmap and in the
[dependency release plan](dependency-release-plan.md). If a missing semantic is
generally reusable, implement, test, and release it in the owning primitive
first; Qingniao then consumes the clean public contract.

| Needed capability | Authority | Evidence to inspect or test | Outcome |
| --- | --- | --- | --- |
| Durable waiting, idempotent signals, selective restart, artifact mechanics | [Zhinu](https://github.com/jenolaszlo-sketch/penghou-zhinu) | Released API/package, restart and signal tests, artifact/replay behavior | **Audited:** use existing waits, idempotent send receipts, selective restart, artifacts/identities, and cancellation. **Upstream before M4:** signal-consumption fencing, stale artifact-publication fencing, and generic external-operation handle persistence. |
| Plan fingerprints, immutable revisions, structured supervisor nodes, context requirements | [Fuwen](https://github.com/jenolaszlo-sketch/penghou-fuwen) | Compiler/IR contracts, canonical serialization, definition-store verification, validation and cycle/termination tests | **Audited:** existing canonical plan fingerprints, structural IDs, and verified content-addressed definition storage are usable. **Fuwen P0 before advanced-plan acceptance:** deep immutability, authoritative reference/binding/type/acyclicity/resource validation, explicit revision lineage, supervisor/checkpoint external-input nodes, and typed context-snapshot requirements. Fuwen execution ports and a Fuwen-to-Zhinu adapter are P1 before advanced-plan execution; the adapter may be a separate package, while Qingniao does not define Fuwen runtime semantics. |
| Session/correlation identity and append-only reconciliation outbox | [Hongxian](https://github.com/jenolaszlo-sketch/penghou-hongxian) | Session append/concurrency API, ExpectedHead/idempotency, projections, decisions/incidents/recovery, lease fencing, outbox/reconciliation tests | **Audited:** existing session/event identity, ExpectedHead/idempotency, projections, decisions/incidents/recovery, lease fencing, and outbox/reconciliation are usable. Qingniao owns typed supervised-work/checkpoint/intervention mapping. Indexed/as-of/re-entry queries are later upstream; cross-DB integration is an idempotent saga/reconciliation, not a distributed transaction. |
| Context snapshots and code-graph revision identities | Cangjie / Hetu | Snapshot/revision APIs, impact/ownership queries, restart reproducibility tests | **Audited:** Qingniao can use reference-only adapters for current snapshot, repository, and index-publication identities. Their P1 upstream gaps remain in the owning roadmaps and are required before richer impact/re-entry integration; they do not block the provider-neutral context seam. |
| Model execution, tool usage, structured output, and provenance | Baize | Provider/tool contracts, malformed-output handling, usage/provenance tests | **Audited:** reusable for bounded in-memory model work and provenance. Two Baize P0 tool-integrity gaps block authoritative complex-tool integration; durable ordinary completion also requires provider-native external-operation handles. |

No downstream batch may silently work around an unaudited primitive capability.
Gate 0.5 is complete for the current package surfaces; future just-in-time
integration audits still record any newly discovered release blocker in the
owning roadmap and dependency plan.

## Milestone 0 — Project and boundary scaffold

Status: **complete**

- [x] Create multi-target .NET solution and package boundaries.
- [x] Add initial provider-neutral delegation contracts.
- [x] Add request validation and contract tests.
- [x] Add CI build, format, test, and pack checks.
- [x] Document architecture, security posture, non-goals, and design review.
- [x] Correct Hongxian's role to temporal session/evidence rather than worker
      execution.
- [x] Accept agent runtimes as opaque execution providers rather than rebuilding
      their native delegation and repository loops.
- [x] Verify that the official Codex SDK and non-interactive surfaces can support
      an initial Codex execution adapter.
- [x] Establish MCP northbound, A2A-preferred southbound, and provider adapters
      as external protocol boundaries.
- [x] Record the invariant that protocols are adapters while Qingniao owns the
      durable outcome model.

## Milestone 1 — Contract and identity freeze

Status: **complete — Batches 1–7 implemented and contract surface frozen**

Batches 1 and 2 are implemented and tested: canonical request identity,
caller-scoped idempotent acceptance, immutable public inputs, fixed lifecycle
transitions, monotonic progress, and immutable terminal-result publication.
See [ADR 0002](decisions/0002-request-identity.md) and
[ADR 0003](decisions/0003-delegation-lifecycle.md).

The remaining work is intentionally ordered. Each batch ends with its own
review and does not smuggle provider execution or persistence into the core.

### Batch 3 — Supervisory lifecycle and identities — complete

- [x] Define planned nonterminal `WaitingForSupervisor` and terminal
      `NeedsSupervisor` semantics.
- [x] Define `SupervisorCheckpointId`, top-level wait gating, and independent
      branch progress, including `Queued` entry and stable checkpoint identity
      across waiting revisions.
- [x] Bind request identity to a canonical workflow-plan fingerprint and
      immutable `PlanRevision` for a versioned built-in preset reference or a
      host-approved Fuwen definition reference; host policy must verify it
      before execution.
- [x] Keep acceptance of supervisor-authored/advanced Fuwen plans blocked until
      Fuwen P0 validation and binding semantics are released and audited.
- [x] Freeze the identity hierarchy: Hongxian Session, Qingniao SupervisedWork /
      Delegation, Fuwen PlanRevision, Zhinu WorkflowRun / ExecutionEpoch,
      structural Node, NodeGeneration, provider ExecutionAttempt / handle.
- [x] Define retry/reconnect versus semantic node re-execution and linked runs.

Tests: table-driven state/identity tests, exact v1/v2 fingerprint fixtures,
unknown or stale identity rejection, exact checkpoint fencing, checkpoint
consistency, and terminal immutability cases. Release tests passed for
`net8.0` and `net10.0`.

Exit: every pause, run, node generation, attempt, and accepted plan revision
has a stable non-overloaded identity and an unambiguous lifecycle meaning;
advanced Fuwen-plan acceptance is explicitly gated on Fuwen P0.

### Batch 4 — Intervention, wake, and context contracts — complete

- [x] Define revision-fenced, caller-authorized intervention acceptance and
      idempotency behavior, including async storage-ready activation.
- [x] Define wake/attention scheduling as bounded non-authorizing hints with
      an explicit `ExpiresAt` and normalized prose reason.
- [x] Define a closed typed action hierarchy (no arbitrary payload), global
      single-assignment per checkpoint, rich immutable receipts, and rejection
      taxonomy. `Respond` remains bounded normalized prose; artifact-bound
      responses and richer typed evidence are deferred to Batch 5.
- [x] Define bounded, demand-driven checkpoint re-entry context and provenance
      references to Cangjie/Hetu snapshots when used, with explicit
      truncation/omission and UTF-8 byte limits.

Tests: stale revision, duplicate/conflicting intervention, unauthorized action,
typed action bounds, competing authorized supervisors, concurrent acceptance,
activation refresh/regression, cancellation, bounded context, exact
checkpoint/delegation/revision binding, UTF-8 byte limits, explicit
truncation/omission, independent-branch, and wake-hint tests. Release tests
pass on `net8.0` and `net10.0` (188 tests per target).

Exit: host-authenticated intervention intent can be accepted atomically and
idempotently without overwriting newer decisions, extending authority, or
performing a side effect; a supervisor can request and receive a bounded,
explicitly delimited context package for the exact waiting checkpoint fence.

See [ADR 0005](decisions/0005-intervention-acceptance.md) for the acceptance
contract and [ADR 0006](decisions/0006-checkpoint-context.md) for bounded
checkpoint re-entry context.

### Batch 5 — Artifacts, evidence, and candidate identity (in progress)

#### Batch 5A — Artifact/content identity and candidate references (complete)

- [x] Define immutable artifact references with provider/repository identity,
      kind/schema version, opaque location, required versioned content identity,
      and exact SHA-256-bytes-v1 validation.
- [x] Define node-generation ownership, strong candidate/revision identities,
      aggregate result references, and an asynchronous in-memory idempotent
      publication proof.

See [ADR 0007](decisions/0007-artifact-and-candidate-identity.md).

#### Batch 5B — Canonical semantic content identity (complete)

- [x] Define the separately versioned `penghou-canonical-json-v2` contract in
      Siming without changing or reinterpreting persisted v1 identities.
- [x] Add type-independent canonicalization and SHA-256 verification from
      persisted JSON, duplicate-property rejection, exact number handling, and
      independent cross-runtime golden vectors in Siming.
- [x] Consume released Siming preview.4 for external-start semantic
      fingerprints without copying canonicalization or SHA-256 logic.
- [x] Freeze the semantic JSON projection and golden hash independently of the
      public CLR record layout.

#### Remaining Batch 5

- [x] Define normalized evidence for agent, model, deterministic execution,
      validation, and review without forcing one lowest-common-denominator API.
- [x] Define worker invocation and review-independence evidence, including
      candidate subject and reviewer identity.
- [x] Integrate the richer evidence records into immutable candidate/result
      publication without weakening existing terminal-result equality.

Tests: canonical payload/file hashes, schema compatibility, duplicate
publication, conflicting references, missing/invalid evidence, and immutable
candidate/result tests. Batch 5C additionally covers provider/model identity,
bounded extension data, ownership, deterministic validation, review subject
identity, and adversarial independence claims. Batch 5D adds bounded evidence
bundles, publication ownership checks, evidence-aware candidate conflicts, and
terminal-result replay/replacement checks.

Exit: every accepted output is verifiable, attributable to a node generation,
and safely referenced by the eventual immutable aggregate result.

### Batch 6 — Budgets, capabilities, providers, and provenance (complete)

- [x] Define budget consumption receipts, provider hints, telemetry, and
      `BudgetExceeded` as a durable outcome.
- [x] Define open capability descriptors and deterministic matching without a
      closed provider/model enum.
- [x] Define the durable external-operation protocol: idempotent start, handle
      capture, observe, cancel, result retrieval, and resume.
- [x] Define stable external agent/task identities and correlation with
      delegation, workflow step, and execution attempt.
- [x] Separate transport failure, remote failure, cancellation, timeout,
      rejection, and result-validation failure.
- [x] Define versioned capability snapshots and model/tool/usage provenance.

See [ADR 0009](decisions/0009-budgets-capabilities-and-provider-selection.md)
and [ADR 0010](decisions/0010-durable-external-operation-protocol.md).

Tests: budget accounting, capability matching, ambiguous start/reconnect,
provider result validation, cancellation, timeout, and provenance tests.

Exit: a provider can be started and recovered through a durable handle while
Qingniao preserves cost, capability, failure, and provenance semantics.

### Batch 7 — Public API baseline and contract freeze (complete)

- [x] Review all public contracts for ownership, authority, security, and
      provider neutrality.
- [x] Generate/update public API baselines for all target frameworks.
- [x] Run package/API compatibility checks and review XML documentation.
- [x] Consolidate ADRs and mark deferred semantics explicitly.

See [ADR 0011](decisions/0011-public-api-and-contract-freeze.md) and the
ownership/bounds hardening in
[ADR 0012](decisions/0012-contract-bounds-and-ownership.md).

Tests and checks: Release tests on `net8.0` and `net10.0` where applicable,
format verification, pack, API review, link/diff checks, and roadmap update.

Exit: the reviewed core contracts are frozen for the in-memory vertical slice;
future breaking changes require an explicit roadmap decision.

### Batch discipline

At every batch boundary, run the relevant test matrix on `net8.0` and
`net10.0`, format verification, pack, API review, and `git diff --check`; update
the roadmap and owning ADR. Commit and push only at an explicitly requested
checkpoint. Upstream primitive changes are separate release gates; Qingniao
pauses only when a required dependency is a real blocker.

## First usable end-to-end definition

The first usable product is a local, in-memory supervisory slice: a host
submits the simple `marang_delegate` preset or selects a validated compiled
Fuwen plan; a bounded fake/provider executes one artifact-producing node; the
workflow reaches a checkpoint; the host can leave without polling, inspect
bounded context, issue one authorized revision-fenced intervention, resume,
and retrieve immutable evidence plus one aggregate result. This proves the
product seam before real Codex, Zhinu persistence, or MCP transport.

Deliberately deferred until later milestones: durable Zhinu/Hongxian storage,
real Codex execution, repository mutation, full Cangjie/Hetu/Baize adapters,
MCP/A2A transport, unbounded workflow graphs, collaboration, archival, and
provider-specific UI.

## Milestone 2 — In-memory supervision vertical slice

Status: **in progress — M2.1 through M2.3 complete; M2.4 reconnect proof complete except cancellation/resume**

Implementation is split into dependency-ordered, independently reviewable
batches. This slice is an in-memory coordinator over the frozen contracts; it
is not a second workflow engine and does not interpret Fuwen or reproduce
Zhinu durability.

1. **M2.1 — Plan/preset resolution (complete):** resolve a planless request to the stable
   built-in `Implement` revision, represent the fixed structured preset, and
   keep advanced Fuwen definitions behind a host-verification seam.
2. **M2.2 — Provider policy and reconnect state (complete):** add an immutable provider
   registry snapshot, deterministic explicit selection outcomes, authorized
   adapter lookup, and conflict-safe in-memory external-handle capture.
3. **M2.3 — Deterministic coordinator (complete):** accept a request, publish monotonic
   progress, advance through an explicit testable pump, and publish exactly one
   immutable terminal result.
4. **M2.4 — External-operation proof (partial):** use bounded fake providers to prove
   early handle capture, ambiguous acceptance, observation, result retrieval,
   cancellation, and reconnect without duplicate work.
5. **M2.5 — Supervision proof:** reach a stable checkpoint, emit a
   non-authorizing wake hint, assemble bounded re-entry context, and apply one
   authorized revision-fenced intervention exactly once.
6. **M2.6 — Candidate and evidence proof:** seal one candidate before parallel
   deterministic Test and independent Review, both against the exact immutable
   candidate revision.
7. **M2.7 — Bounded correction:** evaluate the independent outcomes and allow
   at most one semantic fix using a new `NodeGeneration` and candidate revision.
8. **M2.8 — Outcome matrix:** aggregate immutable evidence and cover success,
   rejection, cancellation, budget exhaustion, provider error,
   `NeedsSupervisor`, and planned waiting.

Fuwen definition references are never interpreted by this slice: the host must
authorize and verify the exact identifier, revision, and canonical fingerprint.
The M2 seam proves only neutral reference and admission boundaries; it does not
execute a Fuwen plan. Advanced execution waits for Fuwen's authoritative P0
validation and immutable revision lineage, a Fuwen-to-Zhinu execution port, and
Zhinu's durable external-operation fencing.
Provider capability claims are selection input, not authorization. Wake hints
never resume work or extend authority, reconnect never creates a new semantic
generation, and deterministic validation cannot be overridden by model review.

M2.1 through M2.3 are implemented and verified. Planless requests are bound to the same v2 identity as explicit
`Implement/1` requests; the fixed definition encodes one conditional repair
without becoming a caller-authored graph. Provider registration is bounded,
immutable, and host-authorized; selection uses one revisioned snapshot and an
explicit no-match outcome. Early external handles are captured idempotently and
can be recovered from task-less pre-acceptance correlation without exposing
opaque handle values in conflict diagnostics. See
[ADR 0013](decisions/0013-in-memory-plan-and-provider-resolution.md).

The atomic in-memory execution store required by M2.3 is also complete. It
publishes monotonic progress through expected-revision fences and makes terminal
progress plus its immutable result visible as one operation. The executable
adapter catalog remains internal: only trusted host composition registers
authority, and the coordinator must resolve the exact match from its captured
provider snapshot. See
[ADR 0014](decisions/0014-in-memory-execution-state-and-adapter-authority.md).

The internal explicit-pump coordinator now proves Siming-backed semantic
fingerprints, stable start identities, revision fencing, early handle capture,
ambiguous-start reconnect without duplicate work, classified retry policy,
provider-call accounting, duration/call/retry budgets, validated observation
and result progression, and immutable terminal replay. Cancellation/resume
remain in M2.4. See
[ADR 0016](decisions/0016-siming-fingerprints-and-explicit-pump.md).

Before the coordinator becomes public, combine acceptance and execution-state
initialization under one atomic authority, bound its retained state, publish the
actual delegated objective as an immutable input artifact, and introduce
trusted external-agent/protocol metadata rather than deriving it from a
provider name.

- [x] Keep `marang_delegate` as a simple predefined `Implement` preset.
- [x] Add the advanced workflow-selection seam for compiled Fuwen plans.
- [x] Define provider registry, capability selection, and policy decision
      contracts.
- [ ] Implement the fixed strategy with fake agent, model, deterministic, and
      context providers.
- [ ] Exercise wake, bounded re-entry context, intervention, and one selective
      `NodeGeneration` re-execution.
- [x] Simulate ambiguous provider acceptance and reconnect through its external
      handle.
- [ ] Seal a candidate revision before parallel Test and Review.
- [ ] Evaluate deterministic test results separately from reviewer judgment.
- [ ] Support one bounded fix cycle.
- [x] Aggregate a concise immutable result for the external-operation proof;
      richer candidate/test/review aggregation remains M2.6–M2.8.
- [ ] Cover success, rejection, cancellation, budget exhaustion, worker error,
      `NeedsSupervisor`, and planned `WaitingForSupervisor` paths.

Exit: the complete policy can be tested without MCP, real models, shells, or a
workflow database, including waiting, intervention, and selective
re-execution.

Go/no-go: proceed to Codex feasibility only if duplicate acceptance, bounded
context, checkpoint intervention, terminal immutability, and all outcome paths
pass with deterministic fakes. Otherwise stop and revise the contracts.

## Milestone 3 — Codex execution capability slice

Status: **planned**

- [ ] Implement an approved-workspace resolver and disposable candidate
      provider for a sample repository.
- [ ] Implement a process-isolated adapter around `codex exec --json` with an
      output schema, explicit sandbox, bounded output, and early thread-ID
      capture.
- [ ] Prove that a known Codex thread can be observed or resumed without
      starting duplicate work.
- [ ] Prove bounded implement-code execution without exposing Marang MCP or
      credentials to the subordinate task.
- [ ] Record resolved model/tool/usage provenance.
- [ ] Compare the CLI spike with an SDK/app-server bridge for cancellation,
      progress, approvals, and long-lived support.
- [ ] Verify local authentication and subscription/API-key behavior without
      claiming unavailable cost precision.

Exit: one economical Codex agent can reliably change a tiny disposable project,
return structured evidence, reconnect after interruption, and remain inside
host policy.

Go/no-go: continue only if the provider exposes a durable handle early,
reconnects without duplicate work, stays inside workspace/credential policy,
and produces verifiable evidence. Stop and hold a product-design review if any
of those conditions fails; do not add more workflow infrastructure to hide the
failure.

## Milestone 4 — Durable Zhinu execution and supervision

Status: **planned**

- [ ] Inspect and pin the minimum supported Zhinu API/version.
- [ ] Map the fixed and amended lifecycle to durable workflow steps, signals,
      waits, and selective restart.
- [ ] Implement the application-layer Zhinu activity adapter that constructs a
      normal Qingniao delegation request and returns structured result/evidence;
      keep Fuwen and Zhinu references out of Qingniao's core dependency graph.
- [ ] Persist Qingniao `SupervisedWork` / `Delegation` and link each Fuwen
      `PlanRevision` to a Zhinu `WorkflowRun` / `ExecutionEpoch`.
- [ ] Integrate Hongxian as the session/correlation authority for the durable
      slice; keep Zhinu authoritative for execution state and reconcile
      cross-authority evidence through idempotent saga/forward-reconciliation
      steps. Each outbox is atomic only with its owning store; no outbox spans
      the Zhinu and Hongxian SQLite databases.
- [ ] Persist external provider handles before awaiting their terminal results.
- [ ] Map Zhinu cancellation without pretending it rolls back effects.
- [ ] Restore waiting, intervention, status, and result after process restart.
- [ ] Test crash windows around acceptance, publication, intervention, and
      terminal result completion.

Exit: a delegation can wait for supervision, resume by fenced intervention,
survive host restart, and return exactly one immutable aggregate result per
terminal Zhinu `WorkflowRun` / `ExecutionEpoch`; individual `NodeGeneration`s
retain their own immutable artifacts and evidence.

Go/no-go: advance to evidence/context work only when Zhinu remains authoritative
for execution state, Hongxian preserves session/correlation narrative through
crash windows, and reconciliation/outbox tests cover ambiguous outcomes.

## Milestone 5 — Evidence, validation, and bounded repair

Status: **planned**

- [ ] Publish typed inspection, plan, implementation, test, review, and result
      artifacts.
- [ ] Run Test and Review against the same immutable candidate revision.
- [ ] Use a deterministic executor for tests and diff inspection; agent claims
      never override its receipts.
- [ ] Record the review-independence dimensions.
- [ ] Implement evaluator policy and one repair revision.
- [ ] Return unresolved findings rather than suppressing them.
- [ ] Record session-linked decisions, incidents, recovery, and
      unusual-but-recovered events in the evidence model.
- [ ] Record Cangjie context snapshots, Hetu graph revisions, Baize provenance,
      and wake/re-entry rationale only when demanded by the activity.

Exit: the supervisor can judge the result without raw worker transcripts, with
session-linked evidence and demand-driven context references.

Go/no-go: advance to MCP only when artifacts, evidence, Cangjie/Hetu context
identities, Baize provenance, and immutable aggregate results are independently
verifiable.

## Milestone 6 — Marang MCP vertical slice (downstream validation)

Status: **planned in the Marang repository**

- [ ] Expose `marang_delegate`, `marang_status`, `marang_result`, and
      `marang_cancel`.
- [ ] Expose `marang_execute_workflow`, `marang_wait`, `marang_intervene`, and
      `marang_inspect` with bounded context and revision-fenced semantics.
- [ ] Expose bounded `marang_get_artifact` metadata/content retrieval with
      authorization, retention, and response-size policy.
- [ ] Add workflow selection, future-attention hints, bounded re-entry, and
      revision-fenced intervention without exposing arbitrary low-level graphs.
- [ ] Keep transport DTOs separate from domain contracts.
- [ ] Define host authentication and workspace authorization.
- [ ] Add compact status revision/cursor behavior.
- [ ] Bound MCP response and artifact payload sizes.
- [ ] Add transport contract and ambiguous-response retry tests.
- [ ] Ensure subordinate agent environments cannot recursively call Qingniao.

Exit: Codex can submit or select a validated workflow, leave while work waits
without polling, receive bounded checkpoint context, make a revision-fenced
intervention, resume, inspect evidence, and retrieve one immutable result.

Go/no-go: expose the public MCP surface only after authentication,
authorization, response bounds, stale-revision handling, and ambiguous-request
retries pass transport tests.

## Milestone 7 — Dogfood and hardening

Status: **planned**

- [ ] Delegate one small Qingniao improvement to Qingniao.
- [ ] Measure supervisor context avoided, worker calls, retries, duration,
      model usage, wake frequency, and `NodeGeneration`s.
- [ ] Add adversarial repository prompt-injection and malicious-path tests.
- [ ] Add restart, cancellation, stale-revision, and partial-artifact tests.
- [ ] Review package boundaries and graduate only packages with a stable use.

Go/no-go: continue to A2A or additional providers only if dogfood confirms
recoverable failures, auditable unusual events, bounded supervisor context,
and no hidden dependency-specific workaround is required.

## Milestone 8 — A2A execution adapter

Status: **deferred until Milestone 7 go/no-go**

- [ ] Select and pin a tested A2A protocol version and conforming client.
- [ ] Discover capabilities from host-authorized Agent Cards; never accept
      arbitrary caller-supplied endpoints.
- [ ] Map one execution attempt to one durable external A2A Task.
- [ ] Persist agent, task, protocol-version, and capability-snapshot identities.
- [ ] Map working, completed, failed, rejected, cancelled, and
      input/authorization-required states without leaking protocol types into
      core.
- [ ] Normalize A2A Artifacts and Parts into Qingniao artifacts with provenance
      and integrity checks.
- [ ] Support reconnect through Task observation/subscription after restart.
- [ ] Add conformance, malformed-response, SSRF, remote-artifact, cancellation,
      version-negotiation, and data-disclosure tests.
- [ ] Keep push notifications disabled until authenticated callback and replay
      protection are available.

Exit: the fixed workflow can replace its process agent with a conforming A2A
agent without changing Qingniao workflow semantics or its MCP contract.

Go/no-go: pursue A2A only after dogfood demonstrates a stable provider-neutral
contract and a concrete interoperability need; otherwise keep it deferred.

## Later, evidence-driven integrations

- Cross-session collaboration, branching, and long-lived session archival after
  the durable Hongxian integration is proven.
- Richer projection/query surfaces for pending decisions, incidents, and
  supervisor attention history.
- Deeper Hetu impact-driven selective re-execution and ownership analysis after
  the first graph-revision integration.
- Advanced Cangjie retrieval ranking, context budgeting, and snapshot
  compaction after demand-driven retrieval is operational.
- Fuwen compiler diagnostics and plan optimization after the validated plan
  boundary is stable.
- More predefined strategies: Investigate, Review, and Fix.
- Additional bounded-execution providers.
- Deeper visibility into opaque provider subagent trees only when cancellation,
  cost, traceability, or policy demonstrates a need.
- Optional A2A bridges for valuable non-A2A agents, only where the bridge adds
  real interoperability value.

## Non-goals

- Replacing Codex or becoming a general autonomous coding agent.
- A workflow DSL or caller-supplied arbitrary workflow graph.
- Parsing Fuwen source or owning general workflow nodes, branches, loops,
  fan-out, compensation graphs, durable timers, or workflow persistence.
- Requiring Fuwen or Zhinu for direct Qingniao delegation.
- Creating `Penghou.Qingniao.Fuwen` or `Penghou.Qingniao.Zhinu` before repeated
  integration code demonstrates a genuine reusable adapter boundary.
- Unbounded self-reflection, retries, fan-out, or recursive delegation.
- Recreating native Codex/Claude Code/OpenCode subagent scheduling, repository
  exploration, or prompt iteration.
- Owning workflow durability, model-provider clients, memory, code graphs,
  session history, or JSON repair.
- Letting model output grant itself tools, filesystem scope, budgets, or model
  escalation.
- Direct commits, pushes, publication, credential access, or primary-workspace
  mutation by default.
- Implementing a generic A2A framework, registry, gateway, or protocol stack.
- Treating Agent Card capability claims as authorization or automatically
  transmitting repository contents to remote agents.
- Returning complete worker transcripts in normal MCP results.
