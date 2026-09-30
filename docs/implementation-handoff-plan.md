# Implementation and model handoff plan

Date: 2026-09-28. Status: **QH-00–QH-07 complete; QH-08–QH-10 planned**.
Inputs: [review](architecture-boundary-review-2026-09-28.md),
[roadmap](roadmap.md), [dependency plan](dependency-release-plan.md),
[ADR 0017](decisions/0017-optional-workflow-composition.md).
Companion: [Guihua plan](../../Penghou.Guihua/docs/implementation-handoff-plan.md)
(sibling checkout).

## Objective and scope

Close adapter and public-runtime guarantees before relying on them in durable
execution. Extend the completed semantic proof through real input materialization,
operation identity, cancellation, receipts and host integration.

Preserve the completed extraction and M0/M1/M2 work. **M3 already records a
successful live proof on 2026-09-26 using codex-cli 0.157.0**, including success
and interrupt/resume. This plan does not reset that milestone or require a new
provider feasibility project. Q05 in the review should be read as a repeatable
protocol/concurrency/recovery coverage gap, not absence of all live evidence.
The optional SDK/app-server comparison remains an open, non-blocking M3 follow-up.

The main roadmap also says preview.2 includes the public DelegationRuntime
façade, while some README/dependency-plan text describes earlier status or
still says “before the coordinator becomes public”. Reconcile these descriptions
and track the outstanding guarantees as hardening work; do not infer that every
old public-surface gate was closed by exposure of the façade.

Qingniao owns one delegation, not workflow topology, provider ranking or product
acceptance criteria. Preserve exact-provider resolution, host policy, immutable
terminal results and lightweight direct use. Fuwen/Zhinu/Hongxian integrations
remain outside core; ADR 0017 requires demonstrated reuse in Marang and Guyabano
before creating optional Qingniao integration packages.

QH-00 through QH-07 are **complete** (QH-00–QH-06 on 2026-09-28, QH-07 audit on
2026-09-30, see records below); QH-08–QH-10 remain **not started**. Review IDs Q01–Q09 identify findings;
QH-00–QH-10 below identify implementation packages.

## Task status

| Task | Status | Evidence / next |
| --- | --- | --- |
| QH-00 | **complete** (2026-09-28) | Baseline record below; build 0 warnings, 395 core + 19 Codex tests per framework on net8/net10 (828 pass); README/CHANGELOG/roadmap/dependency-plan wording reconciled; no code or API change, nothing published |
| QH-01 | **complete** (2026-09-28) | Record below; concurrent stdout/stderr drains, 262144-byte per-line cap, kill/settle/complete/dispose pump lifecycle; 395 core + 33 Codex tests per framework pass; Codex coverage 92.4% line / 82.6% branch; preview API change documented; one found regression for QH-03 |
| QH-02 | **complete** (2026-09-28) | Record below; physical containment, strict candidate rule, launch re-validation seam; 395 core + 45 Codex pass, 2 privileged skips reported; Codex coverage 90.9% line / 79.7% branch; adapter launch hook left for QH-03 |
| QH-03 | **complete** (2026-09-28) | Record below; atomic terminal outcome, keyed cancel receipts, correlation fencing, start/resume single-flight; 395 core + 54 Codex pass, 3 privileged skips reported; Codex coverage 89.6% line / 81.3% branch; no public API change |
| QH-04 | **complete** (2026-09-28) | Record below; materialization seam, fingerprinted inputs, virgin-only replay, retention model; 404 core + 54 Codex pass; abstractions 91.2%/75.5%, runtime 83.3%/69.9% coverage |
| QH-05 | **complete** (2026-09-28) | Record below; gate-free provider I/O with reserve/reconcile, intent-first cancel, observable deadlines; 409 core + 54 Codex pass; runtime 83.4%/69.9% coverage |
| QH-06 | **complete** (2026-09-28) | Record below; verified completion requires `turn.completed`, frozen receipts, acknowledge-before-evict retention, usage parsing, protocol doc; 409 core + 63 Codex pass; Codex 90.5%/81.9% coverage |
| QH-07 | **complete** (2026-09-30) | Upstream audit record below; capability/version/evidence matrix and QH-08 entry gate in ADR 0018; no code change |
| QH-08 | **prep complete (2026-09-30); implementation blocked** | Witness seam + fail-closed tests + handoff below; full integration waits on the ADR 0018 entry gate; do not launch unconditionally |
| QH-09 | not started | Blocked on QH-06/QH-08 |
| QH-10 | not started | Blocked on relevant prior packages; full dogfood needs M4/M5 |

## Roadmap reconciliation

| Existing roadmap item | Planned delivery | Completion boundary |
| --- | --- | --- |
| Extraction / M0–M2 complete | QH-00; regressions throughout | Keep delivered behavior; reconcile stale status text |
| Post-M2 public-surface gates | QH-04/QH-05 | Atomic acceptance/state, bounded retention, immutable objective and trusted protocol metadata |
| M3 live Codex proof complete | QH-01/QH-02/QH-03/QH-06 | Harden the implemented adapter and preserve recorded proof |
| M3 SDK/app-server comparison | Complete 2026-09-30 (ADR 0019) | Decision note: stay on CLI; not a migration or M4 blocker |
| M4 durable execution | QH-07/QH-08 | Verified released upstream fencing plus host-layer implementation and crash tests |
| M5 evidence/validation/repair | QH-06/QH-09 | Production evidence path over existing M2 semantics; do not reimplement completed fake proofs |
| Gate 0.5 optional-code-stack independence | QH-07/QH-10 | Packed non-code consumer; session/recovery work without Hetu/code-analysis dependencies |
| M6 Marang MCP | QH-09 downstream contract handoff | Transport/auth implementation and endpoint tests belong to Marang |
| M7 dogfood/hardening | QH-10 | Measured recovery/isolation/usage evidence before graduation |
| M8 A2A | Deferred until M7 go/no-go | Explicit protocol/interoperability need, separate adapter task |
| V2.1/V2.2/V2.3 | Deferred after V1 | No experiments, history-based provider ranking or new evidence authority |

M4 planning and prerequisite audit can proceed now. Do not treat successful M3
feasibility as proof that all adversarial path, concurrency, retention or crash
cases already pass.

## Ordering and assignment

```text
QH-00 baseline -> QH-01 process I/O
              -> QH-02 workspace containment
              -> QH-03 operation lifecycle/identity
QH-01 + QH-02 + QH-03 -> QH-04 admitted execution inputs/public runtime
QH-04                 -> QH-05 interruptible provider operations
QH-01..QH-05          -> QH-06 frozen receipts/protocol regression
QH-00                 -> QH-07 upstream authority audit
QH-05 + QH-06 + QH-07 -> QH-08 durable host integration
QH-06 + QH-08         -> QH-09 evidence/composition/UX
QH-01..QH-09          -> QH-10 package/non-code/dogfood gate
```

QH-01, QH-02 and QH-07 can be independent assignments after QH-00 if their file
ownership is respected. QH-03 owns `CodexExecAdapter.cs`; any adapter call-site
changes in QH-01/QH-02 must be sequenced with it. QH-04 and QH-05 both affect the
coordinator; serialize them. Public API baseline/schema changes need one owner
at a time. Give each model one bounded package with merged prerequisites.

External host/primitive implementation is a separately scoped assignment. An
agent working only in this repository should prepare contracts, tests and the
downstream handoff, and record the external gate honestly rather than quietly
editing another project or adding a local replacement authority.

## Task packages

### QH-00 — Reconcile baseline, proof evidence and roadmap status

**Depends on:** none. **Maps to:** Q09; extraction/M2/M3 status.
**Read/change:** README, roadmap, dependency plan, sample, project/API/CI files,
review qualification and this plan.

- Record HEAD, dirty files, SDK/dependencies and baseline test results.
  Preserve earlier documentation and unrelated edits.
- Reconcile public façade availability, in-memory lifetime, provider resolution
  wording, Implement ownership and extraction publication status using evidence.
- Locate or document the existing M3 live proof and exact protocol/version.
  Distinguish observed behavior from currently automated regression coverage.
- Convert unresolved “before public” requirements into explicit hardening entries
  without marking them complete merely because the façade shipped.

**Done when:** successor agents know the real baseline and do not redo extraction
or falsely claim that no live Codex proof exists. No package publication needed.

### QH-01 — Own and bound the child-process I/O lifecycle

**Depends on:** QH-00. **Maps to:** Q01; M3/M7.
**Primary files:** `CodexProcess.cs`, narrowly scoped stream abstractions and tests.

- Drain stdout and stderr concurrently; retain bounded, separately classified
  diagnostics. Continue draining discarded data.
- Enforce line/chunk limits before allocating arbitrarily large strings.
- Coordinate exit, stream completion, kill and disposal; handle pump failures
  without leaving a live untracked child.

**Required tests:** a local fake child fills stderr beyond pipe capacity and
still completes; huge newline-free stdout stays bounded; pump failure, kill
and exit races dispose cleanly. Use local helper processes, not paid model calls.

**Done when:** no redirected stream can deadlock the adapter and the documented
capture/memory bounds are enforced in the reader.

### QH-02 — Bind execution to a physically approved candidate workspace

**Depends on:** QH-00. **Maps to:** Q02; M3 isolation/M7 adversarial paths.
**Primary files:** `CodexExecOptions.cs`, workspace resolution seam, path tests;
coordinate adapter launch changes with QH-03.

- Store canonical absolute paths, respect filesystem identity rules and resolve
  or reject escaping symlinks/junctions/reparse points.
- Validate immediately before launch; define the concurrent-path-change threat
  model and rely on OS isolation where path validation alone is insufficient.
- Distinguish host-approved disposable candidate workspace from a primary
  checkout. Do not claim that root containment itself creates isolation.
- Keep arbitrary paths and workspace policy out of core contracts.

**Required tests:** outside-root symlink/junction, case-sensitive sibling,
relative path/current-directory change, root boundary, candidate isolation and
safe refusal when containment cannot be established.

**Done when:** both supported platforms' behavior is explicit; skipped privileged
link tests are reported, not counted as verification.

### QH-03 — Make operation identity and terminal state atomic

**Depends on:** QH-00; coordinate QH-01/QH-02 merge points.
**Maps to:** Q03/Q04; M3 lifecycle.
**Primary files:** `CodexExecAdapter.cs`, adapter tests, only necessary contracts.

- Reject already-cancelled starts before spawning.
- Replace independent mutable terminal flags with an atomic transition and one
  terminal outcome. Late cancellation returns AlreadyTerminal.
- Record cancellation intent separately from confirmed exit; replay cancellation
  receipts by key with conflict detection.
- Validate provider/protocol/delegation/generation/attempt correlation on handle
  lookup. Publish a single-flight start/resume slot before any process launch.
- Do not use a side-effecting ConcurrentDictionary value factory as proof of
  at-most-once launch.

**Required tests:** pre-cancelled start spawns zero processes; late cancel preserves
success/failure; concurrent resumes launch once; mismatched correlation is
rejected; conflicting keys fail; kill/exit races yield one terminal result.

**Done when:** the public adapter enforces its own identity/lifecycle contract,
rather than relying only on the coordinator to serialize callers.

### QH-04 — Materialize admitted execution inputs and close public-runtime gaps

**Depends on:** QH-01/QH-02/QH-03. **Maps to:** Q07/Q09; post-M2 gates.
**Primary files:** runtime construction/acceptance, coordinator RuntimeState,
provider input/materialization seam, relevant abstractions and tests.

- Define a host seam that turns an admitted objective, workspace reference,
  criteria and context into immutable execution artifacts.
- Bind actual provider inputs, applicable bounds/deadline and trusted protocol
  metadata to semantic identity. Avoid deriving protocol metadata from a
  provider name or using one configured Codex prompt for unrelated delegations.
- Choose and document request-scoped resolution versus explicitly single-use
  adapters. Keep raw prompt/path access behind host policy.
- Combine acceptance and execution initialization under one coherent authority.
  Define failure/retry behavior when initialization is interrupted.
- Define bounded retained state and receipt-retention rules. Persisting acceptance
  alone must not be advertised as durable execution; reject unsupported partial
  recovery or use a coherent state owner.

**Required tests:** two different objectives/workspaces produce correct distinct
inputs; altered immutable input fails identity verification; interrupted initial
acceptance does not duplicate work; retained acceptance with missing execution
state cannot silently relaunch; trusted protocol and capability mismatch fails.

**Done when:** public runtime execution corresponds to the request it accepted.
Version any changed identity/schema explicitly; preserve historical fingerprint
semantics and update compatibility documentation.

### QH-05 — Allow cancellation and deadlines while provider I/O is pending

**Depends on:** QH-04. **Maps to:** Q06; M2 guarantee carried into M4.
**Primary files:** coordinator provider phases/cancellation, operation state,
runtime tests.

- Reserve in-flight attempt/revision under the gate, perform I/O outside it, and
  reconcile the result under the same fence.
- Record cancellation intent without waiting for a stalled provider call.
- Separate caller wait cancellation, operation deadline and cancellation of
  externally accepted work. Budget exhaustion does not prove rollback.
- Handle late handles/results and providers that ignore cancellation without
  granting permission to launch a fresh ambiguous attempt.
- Preserve evaluator/corrector behavior and immutable terminal publication.

**Required tests:** stalled start/observe/result permits recorded cancellation;
deadline becomes observable; late capture reconciles; stale callbacks cannot
overwrite newer state; no duplicate start after timeout; worker-call accounting
includes every started operation.

**Done when:** bounded execution does not depend on a caller voluntarily timing
out a blocked pump. Use controlled tasks/clocks rather than timing-sensitive sleeps.

### QH-06 — Freeze useful receipts and automate the supported Codex protocol

**Depends on:** QH-01 through QH-05. **Maps to:** Q05/Q08; M3 follow-up/M5 foundation.
**Primary files:** JSONL decoder, adapter receipt/result handling, tests, protocol
support documentation.

- Freeze terminal time, state, normalized output and provenance once.
- Model supported completion/usage events; retain explicit unknown/unsupported
  data and bounded diagnostics without converting them into verified success.
- Define result persistence/acknowledgment/retention before eviction.
- Convert existing live proof into repeatable, versioned fixtures and integration
  checks. Verify actual CLI flags and exact turn/attempt semantics for supported
  versions; do not infer cold observation from a scripted argv assertion.
- Cover missing and ephemeral sessions and a still-live original process.
  Return honest unsupported/unknown outcomes when safe reconciliation is absent.

**Required tests:** repeated reads are identical; unread receipts survive retention
policy; incomplete event streams cannot claim verified completion; usage parsing;
cold resume, response loss, missing session and concurrent resume preserve
accepted work identity. Record the precise live CLI/version evidence separately.

**Done when:** supported recovery/result behavior is documented and reproducible.
Live paid/account execution requires an explicitly scoped run; default unit/CI
tests use fixtures/local helpers. The SDK/app-server comparison can follow if
these results expose a concrete capability gap.

### QH-07 — Verify upstream durability and package-independence gates

**Depends on:** QH-00; independent read-only audit lane.
**Maps to:** Gate 0.5, dependency plan, M4 prerequisites.
**Read/change:** dependency plan, a focused integration ADR, optional test fixture
design. Inspect owning primitive APIs as available.

- Verify released Zhinu support for external-operation handle persistence,
  signal consumption, stale publication/callback fencing, waits/interventions,
  execution generation and restart/cancellation authority.
- Verify Fuwen immutable admission/revision lineage only for workflow-backed
  usage. Direct Qingniao use must remain independent.
- Verify Hongxian session/correlation/outbox contracts. Each outbox is atomic
  only with its own store; cross-store integration is idempotent reconciliation.
- Record actual minimum versions and tests; old package versions in the delivery
  plan are evidence of an earlier slice, not automatically today's requirement.
- Define the non-code packed consumer and the forbidden transitive dependency
  check (Hetu, Roslyn, ANTLR, LadybugDB and code-memory requirements).

**Done when:** a capability/version/evidence matrix identifies every prerequisite.
Missing general primitives are assigned to their owner, not implemented as
hidden Qingniao infrastructure. Existing published availability must be verified,
not inferred from local source or prior checkboxes.

### QH-08 — Prove the durable host integration

**Depends on:** QH-05/QH-06/QH-07 and required released upstream capabilities.
**Maps to:** M4. **Owner:** host/application layer plus Qingniao's neutral support
seams; separate downstream implementation assignment where necessary.

- Build the normal Zhinu activity -> Qingniao delegation mapping at the host.
  Do not add Fuwen/Zhinu references to core or invent a workflow DSL.
- Persist handle acceptance before awaiting results; map delegation, plan revision,
  run/epoch, node generation and attempt without overloading identities.
- Restore waits, interventions, cancellation, status and terminal results across
  restart. Keep Zhinu execution facts authoritative.
- Reconcile Hongxian narrative/evidence through idempotent store-local outboxes.
- Prefer existing application glue. Do not create Qingniao.Fuwen; extract a
  Qingniao.Zhinu package only after the ADR's two-consumer reuse condition holds.

**Required tests:** crash before/after acceptance, handle capture, publication,
intervention and terminal completion; competing/stale supervisors; duplicated
delivery; interrupted reconciliation; exactly one immutable terminal aggregate
per run/epoch and unchanged prior candidate evidence.

**Done when:** the real supported stores pass the crash matrix. A sample/fake
test alone does not close M4. If upstream is absent, deliver tested fail-closed
seams and a precise blocked handoff, not a “durable” in-memory substitute.

### QH-09 — Complete evidence composition and host-facing usability

**Depends on:** QH-06/QH-08 for durable completion.
**Maps to:** M5 and downstream M6; review OOP/usability.
**Primary files:** evidence/publication and focused runtime collaborators, samples,
host integration contract documentation; downstream host implementation separately.

- Publish typed artifacts and exact candidate-bound deterministic validation,
  independent review, host verdict and bounded correction evidence.
- Preserve unresolved findings, evaluator faults, first-candidate evidence,
  actual usage and optional context references. Domain acceptance stays in host.
- Provide structured stop reasons, progress/wait hints, preflight explanation and
  a supported pump/backoff/lifetime recipe.
- Refactor coordinator responsibilities around proven operation/cancellation/
  supervision/candidate transitions. Reduce shared mutable RuntimeState coupling;
  avoid creating a generic workflow framework.
- Align the cross-project sample with GH-09: optional delegation, typed evidence,
  waiting, candidate rejection/correction, and explicit plan revision.
- Give Marang an endpoint/DTO/auth/response-bound/stale-revision contract handoff.
  Actual MCP endpoint completion belongs to its roadmap, not this package.

**Required tests:** exact evidence subject/independence; deterministic failure
cannot be overridden by model prose; correction preserves lineage; waiting and
terminal escalation differ; core dependency tests; public sample recovery.
A non-delegated workflow remains valid.

**Done when:** users can understand what happened and what action is possible
without reading raw JSONL. No history-based ranking/experiments added from V2.

### QH-10 — Package verification, non-code proof and measured dogfood

**Depends on:** relevant prior packages; M4/M5 completion for full durable dogfood.
**Maps to:** Gate 0.5, M7, release discipline and M8 go/no-go.

- Run both target frameworks, formatting, API analysis, all coverage gates, pack
  and isolated package-consumer tests. Update changelog/migration notes.
- Run the non-code delegation with session history, retry, artifact evidence and
  recovery; verify no forbidden code-analysis stack is acquired. Removing an
  optional code adapter must preserve ordinary execution/session behavior.
- Dogfood one explicitly bounded disposable task and measure worker calls,
  retries, duration, reported/unknown usage, supervisor context, wakes and
  generations. Include malicious-path/prompt-injection and partial-output cases.
- Record concrete go/no-go evidence for stability/A2A, not only test counts.
  Additional providers and V2 work remain deferred until their own gates.
- Prepare a release candidate and downstream consumption checklist. Publication,
  push and stable API graduation are separate explicit actions.

**Done when:** packaging and actual use substantiate the documented guarantees.
Do not mark M7 or the durable non-code gate complete from a purely in-memory test.

## Shared integration acceptance contract

Coordinate with Guihua's plan; own one host-layer conformance example, not two
competing protocols:

1. Host fixes provider, task inputs, workspace authority, parent allocation and
   correlation before delegation; no hidden reselection.
2. Response loss reconnects the same delegation/attempt. Planner repair is not
   authorization to start fresh external work.
3. Candidate correction remains inside the bounded activity; graph/descriptor
   changes require a new admitted workflow transition outside Qingniao.
4. Run facts, evaluator assertions, host acceptance and supersession are distinct,
   with exact immutable artifact/evidence subjects.
5. Planning, delegation, evaluation and correction consume the parent allowance;
   recovery does not mint budget.
6. Terminal results remain immutable while later plans/generations carry new
   lineage. Wait/resume and unsupported recovery are visible to the user.

Core contracts can be developed independently against fakes. Full conformance
requires GH-06/GH-07 and QH-08 plus verified upstream authorities. A missing
dependency blocks that integration slice, not local adapter fixes.

## Model handoff instructions

Copy this assignment and substitute one task ID:

> Work in Penghou.Qingniao on **QH-XX** from
> docs/implementation-handoff-plan.md. Read mapped review findings, prerequisites,
> roadmap and ADR 0017. Inspect current HEAD/dirty files and preserve others'
> work. Implement only this package with its behavioral regressions; keep direct
> use lightweight and all workflow/host authority outside core. Respect existing
> fingerprint/public API compatibility and document any deliberate change.
> Verify supported provider/upstream behavior instead of assuming it. Finish with
> changed files, commands/results, migration notes, remaining gates, task status
> and next eligible package. Do not publish packages or claim downstream tests
> that were not run.

A completed handoff records task ID; starting/ending commit or uncommitted diff;
verified prerequisites; API/schema changes; test evidence; limits; external
owner/version blockers; next task IDs. Use planned, in progress, blocked on a
named prerequisite, or complete. Preserve historical milestone completion while
recording newly found regressions explicitly.

QH-00–QH-07 are complete. Next assignments: **QH-08 only after a Zhinu
release containing external-operation handle persistence, parked waits, and
execution generations plus a Hongxian release containing the reconciled
projection read** (entry gate in ADR 0018); until then, in-repo fail-closed
seams and a precise blocked handoff only. Do not launch QH-08 as
unconditional implementation work or reopen completed M3 feasibility wholesale.

## Validation commands

From repository root, after restore/build as appropriate:

```powershell
dotnet build Penghou.Qingniao.slnx --configuration Release
dotnet format Penghou.Qingniao.slnx --verify-no-changes --no-restore
dotnet test Penghou.Qingniao.slnx --configuration Release --no-build
dotnet pack Penghou.Qingniao.slnx --configuration Release --no-build --output artifacts
```

Run focused tests during each task, then affected suites. QH-10 also runs exact
coverage commands from `.github/workflows/ci.yml`: abstractions 70%, runtime 65%,
Codex 70%, line and branch, plus isolated package consumption.
Review baseline: 395 core + 19 Codex tests per framework on .NET 8 and 10.
These are historical counts, not evidence for future changes. This plan itself
changes documentation only, plus the QH-00 baseline record below.

## QH-00 baseline record — 2026-09-28

Status: **complete** (2026-09-28). Baseline captured and wording reconciled in working copy; no code or public API changed.

- HEAD: `d44d01a5961cd1252d1f7abbf17382ab853cfdbf` (`feat: close stdin at Codex spawn and record M3 live proof`, 2026-09-27).
- Working copy at record time: `M README.md`, `M docs/roadmap.md`, untracked `docs/architecture-boundary-review-2026-09-28.md` and `docs/implementation-handoff-plan.md`; QH-00 then reconciled `README.md`, `CHANGELOG.md`, `docs/roadmap.md`, `docs/dependency-release-plan.md` plus this record.
- SDK/TFMs/version: .NET SDK 10.0.401; `net8.0;net10.0`; `Version 0.1.0-preview.2`; tags `v0.1.0-preview.1`/`v0.1.0-preview.2` exist; Codex package included in publish workflow but not yet published at preview.2.
- Baseline verification (2026-09-28, Windows): `dotnet build Penghou.Qingniao.slnx -c Release` succeeded with 0 warnings/errors; `dotnet test -c Release --no-build` passed 395 core + 19 Codex tests per framework on net8.0 and net10.0 (828 executions, 0 failures). Matches the review baseline.
- M3 live proof (preserved, not redone): roadmap M3 records success plus interrupt/resume on 2026-09-26 with codex-cli 0.157.0 under a ChatGPT account; implementation commit `d44d01a` closed stdin at spawn and recorded the proof. Observed behavior (early `thread.started` handle, `exec resume <thread> --json` without prompt, disposable `hello.txt` run, usage-limit classification) is historical evidence; currently automated Codex coverage is 19 recorded/scripted-transcript tests only. Remaining repeatable protocol/concurrency/recovery work is QH-06, not a reason to reset M3.
- Wording reconciled by QH-00: public `DelegationRuntime` facade shipped in preview.2 with in-memory process-local lifetime (not durable; partial persistence unsupported); Codex row no longer says proof pending; provider language is exact authorized-provider resolution (host selects, no ranking/reselect); Implement preset graph is Marang-owned; post-M2 `before public` items are now explicit QH-04/QH-05 hardening; dependency-plan Gates 2/3 reflect published previews; changelog reflects the live proof.
- Next eligible: QH-02 with file ownership separate from QH-01, QH-07 independent audit. Do not publish packages from QH-00.

## QH-01 completion record — 2026-09-28

Status: **complete**. Child-process I/O lifecycle owned and bounded; no live paid calls used.

- Start: committed HEAD `d44d01a` plus uncommitted QH-00 wording diff. End: uncommitted working diff (no commit requested). No other agent's files touched.
- Changed: `src/Penghou.Qingniao.Codex/CodexProcess.cs` (concurrent stderr stream, byte-framed `BoundedLineReader` with 262144-byte default per-line cap, discard-while-draining, kill-safe `DisposeAsync`, `ICodexProcessDiagnostics`); `src/Penghou.Qingniao.Codex/CodexExecAdapter.cs` (dual-drain pump with fault-triggered kill, kill/settle/complete/dispose finally, first-wins `Interlocked` failure, separately classified stderr retention, internal `GetIODiagnostics`); `src/Penghou.Qingniao.Codex/PublicAPI.Unshipped.txt` (added `ReadErrorLinesAsync`, optional `maxLineBytes` ctor bound); `tests/.../CodexExecAdapterTests.cs` (seam stderr/failure/disposal tracking, 3 pump tests); `tests/.../CodexProcessIOTests.cs` (new: 8 reader units, 3 real local-process tests); `CHANGELOG.md` (Unreleased QH-01 entry).
- Prerequisites verified: QH-00 baseline record above; M3 proof preserved, not redone.
- API/schema changes: deliberate preview-only additions (`ICodexProcess.ReadErrorLinesAsync`, `ProcessCodexProcessFactory(maxLineBytes = 262144)`); baselines updated and analyzer-clean. No Abstractions change, no fingerprint change, no migration needed beyond the changelog. `CodexExecOptions.cs` untouched (QH-02 owns it); `StartAsync`/`CancelAsync` terminal semantics untouched (QH-03 owns them).
- Test evidence (Windows, .NET SDK 10.0.401): `dotnet build -c Release` 0 warnings; `dotnet test -c Release --no-build` 395 core + 33 Codex per framework on net8/net10, 0 failures; `dotnet format --verify-no-changes` clean; CI Codex coverage gate passes (92.42% line / 82.6% branch vs 70% floor). New: stderr-flood child (20000 lines) completes; 900KB newline-free stdout discarded as 1 line; kill/exit/dispose races clean; pump failure kills/disposes with `codex.pump-failed` and keeps draining the sibling stream. Caught and fixed during work: a wedged-pump deadlock (faulted drain + pending sibling) and a discard byte double-count.
- Limits: per-line cap default is a hosted constant, not host policy; stderr retention shares the `MaxOutputBytes` value as a separate account; real-process tests use OS-branched shell one-liners (`cmd.exe`/`/bin/sh`) so POSIX behavior is asserted but only Windows-executed here.
- Found regression for QH-03 (not fixed, out of scope): `ObserveAsync` on a Transport-kind terminal (e.g. `codex.pump-failed`) throws `ArgumentException` because frozen contracts accept only Remote/ResultValidation for `Failed` states. Terminal-state mapping belongs to QH-03.
- External blockers: none. Next eligible: QH-02, QH-07. Do not publish packages from QH-01.

## QH-02 completion record — 2026-09-28

Status: **complete**. Execution bound to a physically approved candidate workspace; no workflow/host authority added to core.

- Start: committed HEAD `d44d01a` plus uncommitted QH-00/QH-01 diff. End: uncommitted working diff (no commit requested). `CodexExecAdapter.cs` untouched (QH-03 owns it); `CodexProcess.cs` untouched (QH-01 owns it).
- Changed: `src/Penghou.Qingniao.Codex/WorkspacePathResolution.cs` (new: `IWorkspaceFileSystem` seam, physical resolver with final-target and dangling-link resolution, case probe with platform default, strict candidate rule, documented TOCTOU threat model); `src/Penghou.Qingniao.Codex/CodexExecOptions.cs` (canonical absolute storage, `ValidateWorkspaceBeforeLaunch`); `src/Penghou.Qingniao.Codex/PublicAPI.Unshipped.txt` (added `ValidateWorkspaceBeforeLaunch`); `tests/.../CodexWorkspaceTests.cs` (new: 8 fake-FS link/case units, 6 real temp-dir tests); `CHANGELOG.md` (Unreleased QH-02 entry).
- Prerequisites verified: QH-00 baseline and QH-01 records above; M3 proof preserved, not redone.
- API/schema changes: one deliberate preview-only addition (`ValidateWorkspaceBeforeLaunch() -> string`); baselines updated and analyzer-clean. Behavior changes (changelog): strict root rejection (root is never a candidate), canonical storage replaces original strings, physical link resolution replaces the lexical prefix check. No Abstractions change, no fingerprint change.
- Test evidence (Windows, .NET SDK 10.0.401): `dotnet build -c Release` 0 warnings; `dotnet test -c Release --no-build` 395 core + 45 Codex pass per framework on net8/net10, 0 failures, 2 skips; `dotnet format --verify-no-changes` clean; CI Codex coverage gate passes (90.92% line / 79.66% branch vs 70% floor). Skips (reported, not counted): real symlink escape and link-swap tests — link creation not permitted on this machine (`IOException`); the same escapes are proven by fake-FS units. POSIX shell branches in these tests are asserted but only Windows-executed here.
- Limits: validation is time-of-check; concurrent link swaps need host OS isolation plus launch-time re-validation. Case-variant twins coexisting on a sensitive filesystem are the host's responsibility (documented). Non-existent candidate subdirectories resolve through the nearest existing ancestor.
- Explicit hook for QH-03: call `options.ValidateWorkspaceBeforeLaunch()` at the top of `StartAsync`/`ResumeAsync` spawn paths and map `UnauthorizedAccessException` to a provider failure; do not reintroduce lexical checks in the adapter.
- External blockers: none. Next eligible: QH-03, QH-07. Do not publish packages from QH-02.

## QH-03 completion record — 2026-09-28

Status: **complete**. The public adapter enforces its own identity/lifecycle contract; merges the QH-01/QH-02 call-site hooks.

- Start: committed HEAD `d44d01a` plus uncommitted QH-00–QH-02 diff. End: uncommitted working diff (no commit requested). `CodexExecOptions.cs`/`CodexProcess.cs` untouched (QH-02/QH-01 own them); no Abstractions change.
- Changed: `src/Penghou.Qingniao.Codex/CodexExecAdapter.cs` only (plus tests/changelog/this record): one-gate atomic lifecycle (`TrackedOutcome`, intent vs confirmed outcome, first-wins terminalization owned solely by the pump); pre-cancelled starts spawn zero processes; late cancel preserves success/failure with `AlreadyTerminal`; cancellation receipts replay by key with `codex.cancel-conflict` on divergence; lookups fence provider/protocol/delegation/generation/attempt correlation; idempotency-key start bindings plus in-flight slots (duplicates attach as `Existing`, divergent semantics fail as `codex.start-conflict`, no side-effecting dictionary factories); resume slot under one lock (concurrent resumes launch once, terminal-aware receipt state); Transport-kind terminals project as `Unknown` per frozen contracts (closes the QH-01 found regression); `CancelAsync` waits for the confirmed outcome; QH-02 `ValidateWorkspaceBeforeLaunch` wired into both spawn paths (`codex.workspace-refused`); kill/exit races converge on one terminal result.
- Prerequisites verified: QH-00–QH-02 records above (including the QH-02 launch hook, now wired); M3 proof preserved, not redone.
- API/schema changes: none public (internal `TrackedOutcome`, locks, maps). Behavior changes (changelog): mid-flight owner transport-cancel no longer kills a shared launch (explicit keyed `CancelAsync` owns cancellation); attached retries report `Existing`; resume reports actual terminal state instead of always `Running`.
- Test evidence (Windows, .NET SDK 10.0.401): `dotnet build -c Release` 0 warnings; `dotnet test -c Release --no-build` 395 core + 54 Codex pass per framework on net8/net10, 0 failures, 3 reported skips (privileged link tests); `dotnet format --verify-no-changes` clean; CI Codex coverage gate passes (89.57% line / 81.32% branch vs 70% floor). New: zero-spawn pre-cancel, late-cancel success/failure preservation, 8-way concurrent resume single-spawn, generation/provider mismatch rejection, cancel-key conflict vs replay (rendezvous seam), start-key conflict, identical-start attach, race consistency, escaped-workspace refusal (priv-gated skip). Caught and fixed during work: wedged observation race (cancel now confirms), `Unknown` result-availability contract violation, thread-collision on sequential same-key retries (key→thread bindings), eviction test masking by attach (distinct keys restored).
- Limits: single-flight covers in-memory tracked launches; cross-restart/durable idempotency stays with the coordinator/Zhinu (QH-08). Bindings live only while their thread is tracked (pruned by eviction).
- External blockers: none. Next eligible: QH-04 (needs QH-01–QH-03, now unblocked), QH-07. Do not publish packages from QH-03.

## QH-04 completion record — 2026-09-28

Status: **complete**. Public runtime execution corresponds to the request it accepted; partial recovery fails closed.

- Start: committed HEAD `d44d01a` plus uncommitted QH-00–QH-03 diff. End: uncommitted working diff (no commit requested). No Guihua/Fuwen/Zhinu edits; Codex adapter behavior untouched (one class-doc note documents its explicitly single-use prompt binding).
- Changed: `src/Penghou.Qingniao.Abstractions/DelegationInputMaterialization.cs` (new: `MaterializedDelegationInputs` record, `IDelegationInputMaterializer` seam with determinism/side-effect/prompt-policy contract); `src/Penghou.Qingniao/InMemoryDelegationCoordinator.cs` (optional materializer seam, launch-path materialization, trusted-agent binding with provider-mismatch refusal, fingerprinted artifacts/budget/deadline in the start identity, virgin-only replay rule); `src/Penghou.Qingniao/DelegationRuntime.cs` (pass-through seam, documented retention model); both `PublicAPI.Unshipped.txt` baselines; `tests/.../DelegationInputMaterializationTests.cs` (new: 9 tests); `CHANGELOG.md` (Unreleased QH-04 entry).
- Prerequisites verified: QH-00–QH-03 records above; M3 proof preserved, not redone.
- API/schema changes: deliberate additive-only API (`IDelegationInputMaterializer`, `MaterializedDelegationInputs`, `DelegationRuntime` optional parameter); baselines updated and analyzer-clean. No fingerprint version change: the `external-start-semantics-v1` envelope shape is untouched, the default path computes byte-identical fingerprints (pinned by test), and materialized values flow through the existing envelope (no reinterpretation of historical bytes).
- Test evidence (Windows, .NET SDK 10.0.401): `dotnet build -c Release` 0 warnings; `dotnet test -c Release --no-build` 404 core + 54 Codex pass per framework on net8/net10, 0 failures, 3 reported privileged skips; `dotnet format --verify-no-changes` clean; CI gates pass (abstractions 91.19%/75.53%, runtime 83.25%/69.86%, Codex 89.57%/81.32% vs 70/65/70 floors). New: distinct objectives/workspaces yield distinct fingerprinted inputs; tampered inputs change the fingerprint and inconsistent identities fail verification with zero provider calls; interrupted materialization retries without duplication (2 materializations, 1 provider start, terminal run); retained acceptance with Running or Queued execution records refuses closed with zero provider calls; foreign-agent materialization refused; legacy default shape pinned.
- Limits: materializers must be deterministic and side-effect free (re-invoked on retry). Single-flight/retry across coordinator instances still resolves by identity, not by shared provider state; durable cross-restart identity stays with the coordinator/Zhinu (QH-08). The Codex adapter remains explicitly single-use per configured prompt; per-request prompt resolution stays a host-owned adapter concern.
- External blockers: none. Next eligible: QH-05 (now unblocked), QH-07. Do not publish packages from QH-04.

## QH-05 completion record — 2026-09-28

Status: **complete**. Bounded execution no longer depends on a caller timing out a blocked pump.

- Start: committed HEAD `d44d01a` plus uncommitted QH-00–QH-04 diff. End: uncommitted working diff (no commit requested). No Abstractions/Codex changes; evaluator/corrector paths untouched.
- Changed: `src/Penghou.Qingniao/InMemoryDelegationCoordinator.cs` only (plus tests/changelog/this record): `CallProviderOutsideGateAsync` runs start/observe/result/cancel/resume provider calls without the per-runtime gate after reserving attempt/revision under it; `ReconcileProviderReturnAsync` discards late outcomes against terminal state or revision movement; `InFlightProviderCall` marker stops duplicate concurrent provider work while duration/budget checks still run first; cancellation intent in `CancelAsync` records without waiting for stalled calls and post-call pending state refreshes under the fence; provider calls use a durable token so caller-wait cancellation, operation deadlines, and durable cancellation stay separate; confirmed provider results stay authoritative over late cancellation.
- Prerequisites verified: QH-00–QH-04 records above; M3 proof preserved, not redone.
- API/schema changes: none (internal only). Behavior changes (changelog): provider I/O no longer holds the runtime gate; a second pump observes deadlines and records cancellation through stalled calls; late provider outcomes that lose the fence are discarded; concurrent duplicate provider calls at one revision wait instead of launching.
- Test evidence (Windows, .NET SDK 10.0.401): `dotnet build -c Release` 0 warnings; `dotnet test -c Release --no-build` 409 core + 54 Codex pass per framework on net8/net10, 0 failures, 3 reported privileged skips; `dotnet format Penghou.Qingniao.slnx` clean after auto-fix (my blocks were one indent short); CI gates pass (abstractions 91.19%/75.53%, runtime 83.4%/69.93%, Codex 89.57%/81.32% vs 70/65/70 floors). New (`ProviderStallTests.cs`, gated stubs + controlled clocks, no sleeps): stalled start/observe/result each permit prompt recorded cancellation with terminal reconciliation; deadline expiry terminalizes through a stalled start with no duplicate start and stale late-start discard; concurrent pump waits instead of duplicating. Caught and fixed during work: Start in-flight check placed after the worker-call pre-publish (moved above it), a forgotten open gate in the result test, and `Task.WaitAsync(TimeSpan)` throwing `TimeoutException` (not `OperationCanceledException`) on stall backstops.
- Limits: a provider that never returns still parks its own pump (the gate stays free for bounds/cancel/reconcile); hung tasks rely on duration bounds or host restart. Retryable-failure sequential retries are unchanged (marker clears when the call settles). Resume keeps its receipt/handle fencing; only its provider call moved outside the gate.
- External blockers: none. Next eligible: QH-06 (now unblocked: needs QH-01–QH-05), QH-07. Do not publish packages from QH-05.

## QH-06 completion record — 2026-09-28

Status: **complete**. Supported recovery/result behavior is frozen, bounded and documented; no paid/live calls used.

- Start: committed HEAD `d44d01a` plus uncommitted QH-00–QH-05 diff. End: uncommitted working diff (no commit requested). No coordinator or Abstractions change; QH-07+ untouched.
- Changed: `src/Penghou.Qingniao.Codex/CodexJsonlEvent.cs` (new `TurnCompleted` + `CodexUsage`, tolerant nested/flat usage parsing, unknown stays unknown); `src/Penghou.Qingniao.Codex/CodexExecAdapter.cs` (verified-completion rule, `ExitCode`/`EventsObserved`/`TurnCompletedSeen`/`Usage`/`Sequence`/`ResultRetrieved` tracking, frozen `CodexTerminalReceipt`, identical terminal reads from the receipt, result read acknowledges for retention, oldest/acknowledged-first eviction with counted unread overflow, `GetTerminalReceipt`/`EvictedUnreadReceipts` diagnostics); `src/Penghou.Qingniao.Codex/PublicAPI.Unshipped.txt` (baselines); tests (`CodexJsonlEventTests`, `CodexExecAdapterTests`: versioned `0.157.0` fixture + 8 new tests, 5 existing updated to carry turn evidence); `docs/codex-protocol-support.md` (new); `README.md` docs link; `CHANGELOG.md`.
- Prerequisites verified: QH-00–QH-05 records above; the M3 live proof folded into versioned fixtures (`Cli0157SuccessWithUsage` reconstructs the recorded 2026-09-26 shapes) rather than a new live run.
- API/schema changes: deliberate preview-only additions (`CodexJsonlEvent.TurnCompleted`, `CodexJsonlEvent.CodexUsage`); baselines updated and analyzer-clean. Behavior change (changelog): a clean exit without `turn.completed` now fails closed as `codex.incomplete-evidence` instead of succeeding — the previous success test's event stream was updated to include real turn evidence.
- Test evidence (Windows, .NET SDK 10.0.401): `dotnet build -c Release` 0 warnings; `dotnet test -c Release --no-build` 409 core + 63 Codex pass per framework on net8/net10, 0 failures, 3 reported privileged skips; `dotnet format --verify-no-changes` exit 0; CI gates pass (abstractions 91.19%/75.53%, runtime 83.4%/69.93%, Codex 90.51%/81.94% vs 70/65/70 floors). New/updated: nested+flat+malformed usage parsing; clean-exit-without-turn fails closed with `codex.incomplete-evidence`; repeated terminal reads identical (summary/time/state/receipt); 128 unread receipts survive and the 129th evicts one counted unread while acknowledged reads free slots before unread survivors; cold resume preserves thread identity and fails explicitly on missing session; resume response loss reconnects the same attempt. Caught and fixed during work: raw-string interpolation syntax, worker-call counts in two QH-01 tests (`StdoutLines` 2→3 after adding the completion line), and a racy non-terminal `receipt.State` assertion replaced with a handle assertion.
- Limits: only CLI `0.157.0` observed behaviors are claimed; missing/ephemeral-session and live-process resume exact semantics remain unverified and fail closed (documented in `codex-protocol-support.md`). No live paid run was performed; characterization of the open cases is a separately scoped task.
- External blockers: none. Next eligible: QH-07 (independent audit); QH-08 only after QH-05/QH-06/QH-07 plus verified released upstream. Do not publish packages from QH-06.

## Post-review fix record — 2026-09-28

Status: **complete**. A critical review of QH-00–QH-06 found and fixed two defects; both packages remain complete (no re-scoping).

- Found **P1 (containment bypass), fixed**: `WorkspacePathResolution.ResolvePhysical` resolved a link only at the exact leaf, so a workspace under an intermediate junction/symlink that pointed outside the approved root was accepted. Verified with a real Windows directory junction (`root\a` -> outside; `Directory.Exists(root\a\sub)` true while `ResolveLinkTarget(root\a\sub)` null). Rewrote resolution to walk each path component from the filesystem root, re-queuing link targets so chains/interior links resolve; budget fails closed on link cycles. Also made `PhysicalWorkspaceFileSystem.GetFinalTarget` return null for non-existent paths (it threw `FileNotFoundException`), which had briefly regressed non-existent candidate paths. Removed the now-dead `IWorkspaceFileSystem.GetParentDirectory` seam member.
- Found **P2 (state projection), fixed**: `StartAsync` built its receipt state from `tracked.Failure is null ? Running : Failed`, which reported `Running` for an already-succeeded run and `Failed` for a Transport-kind terminal that `ObserveAsync` reports as `Unknown`. Now uses `tracked.Completed ? tracked.ProjectedState : Running`.
- Tests: added `Rejects_escaping_intermediate_junction` and `Accepts_contained_intermediate_junction` (real Windows junctions, skip if not permitted); updated the fake FS to model ancestor directories for the down-walk; relaxed the racy start-receipt assertion in `StartAsync_completes_verified_lifecycle_with_turn_evidence`.
- Evidence (Windows, .NET SDK 10.0.401): `dotnet build -c Release` 0 warnings; `dotnet test -c Release --no-build` 409 core + 65 Codex pass per framework, 3 reported skips; `dotnet format --verify-no-changes` exit 0; Codex coverage 90.43% line / 82.25% branch.
- Remaining review findings deliberately **not** fixed here (tracked for later packages): exposing frozen usage/receipt provenance publicly (QH-06/QH-09), `CodexExecOptions` constructor doing filesystem I/O (consider deferring to launch validation), `EnsureWorkspaceForLaunch` running under the launch lock in `ResumeAsync` and before the attach check in `StartAsync`, and the still-large `CodexExecAdapter`/`TrackedOperation` structure (QH-09).

## Second post-review fix record — 2026-09-28

Status: **complete**. A second cross-layer review found and fixed one coordinator/provider seam defect and one adapter liveness gap; both packages remain complete.

- Found **P1 (seam defect), fixed**: the Codex adapter projects a Transport-kind terminal as `ExternalOperationState.Unknown` (QH-03), but the coordinator treats `Unknown` as non-terminal and re-observes, so a local failure (`codex.pump-failed`) surfaced as an unrelated `BudgetExceeded` after exhausting the worker-call budget. Reproduced with a probe (provider returning a non-retryable `Unknown` observation → `BudgetExceeded`). Fix: `InMemoryDelegationCoordinator.PumpObserveAsync` now escalates a non-retryable `Unknown` observation to terminal `NeedsSupervisor` with an unresolved concern, without spending the worker-call budget. Added `ProviderObservationConformanceTests` (the recommended provider-contract ↔ coordinator conformance check).
- Found **P2 (liveness), fixed**: the adapter's `WaitForThreadAsync` used `CancellationToken.None` and ignored `ExternalOperationStartRequest.Deadline`, so a child that never reports a thread identity and never exits parked the start forever. Fix: the adapter now bounds the start by the request deadline and fails closed as `codex.start-timeout` (killing the child). Added past-deadline and future-deadline regressions. `docs/codex-protocol-support.md` records the start deadline.
- Evidence (Windows, .NET SDK 10.0.401): `dotnet build -c Release` 0 warnings; `dotnet test -c Release --no-build` 410 core + 67 Codex pass per framework, 3 reported skips; `dotnet format --verify-no-changes` exit 0; gates runtime 83.45% line / 70.01% branch, Codex 90.43% line / 82.36% branch.
- Still tracked (not fixed): Codex results carry no artifacts/candidate so the adapter cannot drive the candidate/test/review lifecycle; stderr diagnostics are captured but not exposed; usage/exit/receipt provenance remain internal-only; `CodexExecOptions` constructor does filesystem I/O; `EnsureWorkspaceForLaunch` placement; `EvictCompleted` cost and non-hard capacity; god-class structure. Cross-layer reviews catch this class of seam bug, so repeat after each 2–3 packages.

## QH-07 completion record — 2026-09-30

Status: **complete**. Read-only upstream durability and package-independence
audit; no Qingniao code changed. Full matrix, version pins, decisions, QH-08
entry gate, and non-code packed-consumer design are in
[ADR 0018](decisions/0018-qh07-upstream-durability-audit.md).

- Method: NuGet index checks for published versions; Zhinu publish content
  established by symbol-presence checks at the `preview.14` release commit
  `98ad00d` (Zhinu has no tags/CHANGELOG); Fuwen by `git diff
  v0.1.0-preview.11..HEAD`; Hongxian by `git diff v0.1.0-preview.4..HEAD`.
- Pins: Zhinu `0.1.0-preview.14` (published; handle persistence, parked waits,
  and generations are source-only); Fuwen `0.1.0-preview.11` (published;
  admission, lineage, gates, context requirements, fingerprints, Zhinu port —
  no supervisor node, no FI-04 grants, coordinated inference unreleased);
  Hongxian `0.1.0-preview.4` (published; reconciled projection read is
  post-tag); Siming `0.1.0-preview.7`, Baize `0.3.0-preview.6`, Cangjie
  `0.1.0-preview.3`, Hetu `0.2.0-preview.6`.
- Published and usable for QH-08 planning: Zhinu signal receipts/fenced
  delivery/signal waits/selective restart (+receipts)/artifact fencing/leases/
  terminal cancel; Fuwen admission/lineage/gates; Hongxian append + ExpectedHead,
  projections, leases, store-local outbox reconciliation.
- Blocking QH-08 implementation: a Zhinu release with external-operation
  handles, parked waits, and generations; a Hongxian release with the
  reconciled projection read; plus Fuwen supervisor-node, FI-04/Hufu, and
  fenced-admin work in their owning projects.
- Doc corrections made: ADR 0016's "depends on Siming preview.4" superseded
  (core vendors its own pinned envelope; boundary test forbids Siming types);
  dependency-plan Gate 1 repinned; Gate 3 records QH-04/QH-05 complete.
- Test evidence (Windows, .NET SDK 10.0.401): `dotnet build -c Release` 0 warnings;
  `dotnet test -c Release --no-build` 410 core + 69 Codex pass per framework on
  net8/net10, 0 failures, 3 reported privileged skips; `dotnet format
  --verify-no-changes` exit 0 (pre-existing workspace-load warnings only).
  Docs-only change; code untouched by this package.
- Next eligible: QH-08 prep (fail-closed seams + blocked handoff) only; full
QH-08 after the entry gate. Do not publish packages from QH-07.

## QH-08 prep record — 2026-09-30

Status: **prep complete; full QH-08 implementation remains blocked** on the
ADR 0018 entry gate (Zhinu release with external-operation handles, parked
waits, and generations; Hongxian release with the reconciled projection
read). No workflow dependencies added to core; no new package references.

- Changed: `src/Penghou.Qingniao/InMemoryDelegationCoordinator.cs` (optional
  `durableHandleWitness` ctor sink; `WitnessingHandleCaptureSink` fan-out to
  the live registry plus the witness, in that order; start, early-capture,
  post-receipt, and resume-rotation captures all flow through it before any
  observation); `src/Penghou.Qingniao/DelegationRuntime.cs` (optional public
  `durableHandleWitness` parameter with retention-model docs); `PublicAPI.Unshipped.txt`
  baseline; `tests/.../DurableHandleWitnessTests.cs` (new: witness-before-observe
  ordering, throwing-witness fail-closed with zero observe/duplicate starts and
  stable terminal replay, resume-rotation capture order); `docs/qh08-durable-host-handoff.md`
  (new: mapping table, witness contract, sink-atomicity warning, host crash
  matrix, upstream gates).
- Behavior without a witness is byte-for-byte the previous behavior: the
  fan-out collapses to the registry, all existing suites pass unmodified.
- A witness failure after provider acceptance publishes terminal `Failed` and
  drops the started handle (honest, unrecoverable inside Qingniao); the
  handoff requires atomic/idempotent witness writes plus out-of-band orphan
  reconciliation by the host.
- Test evidence (Windows, .NET SDK 10.0.401): `dotnet build -c Release`
  0 warnings; `dotnet test -c Release` 413 core + 69 Codex pass per framework
  on net8/net10 (3 new witness tests included), 0 failures, 3 reported
  privileged skips; `dotnet format --verify-no-changes` exit 0;
  `git diff --check` clean.
- Next: full QH-08 only after the entry gate. Do not publish packages from
  this prep.
