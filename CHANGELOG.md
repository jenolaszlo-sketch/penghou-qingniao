# Changelog

Notable changes to Penghou.Qingniao are recorded here. The project follows
[Semantic Versioning](https://semver.org/) for package versions. Preview
releases may still revise public contracts; every public API is tracked in
`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` per package.

## Unreleased

- SDK/app-server comparison (M3 follow-up, docs-only): [ADR 0019](docs/decisions/0019-codex-appserver-comparison.md)
  stays on `codex exec --json` — no official .NET SDK exists, the long-lived
  server cuts against the disposable-process failure model, and in-turn
  approvals have no demanding consumer. Revisit only on a concrete gap plus a
  maintained .NET transport.

(nothing else yet)

## 0.1.0-preview.3

Release prep cut; publishes to NuGet on tag push (`v0.1.0-preview.3`).
Readiness verified 2026-09-30: Release build 0 warnings, format clean,
coverage gates pass (abstractions 91.2/75.5, runtime 83.7/70.2, Codex
90.8/84.2 line/branch vs 70/65/70 floors), all three packages pack with
symbols, and an isolated file-local consumer drives a packed delegation
Queued → Completed (including the new witness seam). Remaining release
gates: independent design/security/API review, tag, push.

- QH-08 prep (durable host seams, no new dependencies): new optional `durableHandleWitness` on `DelegationRuntime` (and the internal coordinator) receives every accepted handle capture — starts, early adapter captures, and resume rotations — before the coordinator observes provider results; a witness failure fails the start closed with zero observe calls and no duplicate starts (preview API change, baselines updated). Without a witness the runtime is unchanged and process-local only. See `docs/qh08-durable-host-handoff.md` for the host mapping table, sink-atomicity warning, crash matrix, and the upstream releases still blocking full QH-08.
- Host-owned pre-execution authority preflight ([ADR 0020](docs/decisions/0020-host-owned-authority-preflight.md), preview API change, baselines updated): new optional `IDelegationAuthorityPreflight` host seam runs after the delegation generation exists and before any durable startable record is committed. A `Permit` persists one opaque `DelegationExecutionAttachment` (`Kind` + `Value`, never interpreted) with the generation and surfaces it on `ExternalOperationStartRequest.ExecutionAttachment` (excluded from the semantic fingerprint). `Deny`/`Unavailable` fail closed without starting. Invoked only for delegations requesting derived authority (`ParentGrantId`/`RequestedAuthority`); no Hufu dependency in core.
- Upstream durability audit (QH-07, docs-only): capability/version/evidence matrix and QH-08 entry gate in `docs/decisions/0018-qh07-upstream-durability-audit.md`. Published and usable: Zhinu `0.1.0-preview.14` signal/restart/artifact/lease primitives; Fuwen `0.1.0-preview.11` admission/lineage/gates; Hongxian `0.1.0-preview.4` session/outbox primitives. Still blocking durable integration: a Zhinu release with external-operation handles, parked waits, and generations; a Hongxian release with the reconciled projection read; Fuwen supervisor-node and FI-04/Hufu work in their owning projects. Corrects the stale ADR 0016 Siming dependency claim (core vendors its own pinned fingerprint envelope).

- New `Penghou.Qingniao.Codex` package (first published in this preview): process-isolated
  `codex exec --json` execution adapter with approved workspaces, explicit
  sandbox, bounded output, early thread capture, and resume. Proven against
  recorded CLI transcripts and a 2026-09-26 live proof on codex-cli 0.157.0 (bounded execution plus interrupt/resume); repeatable protocol/recovery coverage remains open per QH-06.
- `Penghou.Qingniao.Codex` process I/O hardening (QH-01): stdout and stderr are drained concurrently with a 262144-byte per-line cap enforced before allocation; over-long lines are discarded while draining continues; the pump kills, settles both drains, completes and disposes as one lifecycle and fails closed as `codex.pump-failed`. Adds `ICodexProcess.ReadErrorLinesAsync` and an optional `ProcessCodexProcessFactory(maxLineBytes)` bound (preview API change, baselines updated).
- `Penghou.Qingniao.Codex` workspace containment (QH-02): options now store canonical absolute paths with symlinks/junctions/reparse points resolved (including dangling links); the workspace must sit strictly beneath the approved root and is never the root itself; case rules follow the filesystem (probed, platform default otherwise); new `ValidateWorkspaceBeforeLaunch` refuses closed with `UnauthorizedAccessException` when containment no longer holds. Hosts passing the root as its own workspace must use a dedicated candidate subdirectory.
- `Penghou.Qingniao.Codex` operation identity and atomic terminal state (QH-03): pre-cancelled starts spawn nothing; one gated terminal outcome per operation (intent wins over clean exit, confirmed failure wins over late cancellation, late cancel reports `AlreadyTerminal`); cancellation receipts replay by key with `codex.cancel-conflict` on divergence; lookups fence provider/protocol/delegation/generation/attempt correlation; concurrent same-key starts and resumes single-flight (duplicates attach, divergent semantics conflict); Transport-kind terminals project as `Unknown` per frozen contracts; `CancelAsync` now waits for the confirmed outcome. Wires the QH-02 launch-time workspace check (`codex.workspace-refused`). No public API change.
- Admitted execution inputs (QH-04): new `IDelegationInputMaterializer` host seam binds immutable input artifacts, budget/deadline hints, and the trusted agent/protocol record into the fingerprinted provider start identity (default path preserves the legacy shape byte-for-byte); replayed acceptance without live runtime state completes only virgin initialization, otherwise fails closed instead of silently relaunching; retention model documented on the runtime (process-local state, bounded 256-entry execution store, acceptance alone is never durable execution).
- Cancellable provider operations (QH-05): provider start/observe/result/cancel/resume calls run outside the per-runtime gate after reserving attempt and revision under it, then reconcile under the same fence (terminal store or revision movement discards late outcomes); cancellation intent and duration bounds stay observable through stalled calls; provider work ignores caller transport cancellation; an in-flight marker stops duplicate concurrent provider work while bounds checks still run first. No public API change.
- Frozen Codex receipts and verified completion (QH-06): `turn.completed` is modeled (with tolerant `input_tokens`/`cached_input_tokens`/`output_tokens` usage parsing, unknown stays unknown) and is the only verified-completion evidence — a clean exit without one fails closed as `codex.incomplete-evidence`; terminal time/state/output/exit/usage freeze once so repeated result reads are identical; result reads acknowledge receipts and eviction drops acknowledged receipts before unread ones and counts unread overflow; adds `CodexJsonlEvent.TurnCompleted`/`CodexUsage` (preview API, baselines updated) and `docs/codex-protocol-support.md`. No coordinator change.
- Fix (post-review, QH-02/QH-03): workspace containment now resolves symlinks/junctions/reparse points in **every** path component (a leaf-only resolver accepted a path under an intermediate junction that pointed outside the approved root; verified with a Windows directory junction). Also, `StartAsync` now projects the real terminal state from the frozen outcome (it previously reported `Running` for an already-succeeded run and `Failed` for a Transport-kind terminal that `ObserveAsync` reports as `Unknown`), and `CodexExecOptions` no longer throws when the (non-existent) workspace path is probed for a link target. Adds real junction containment regressions.
- Fix (second review, QH-05/QH-06): the coordinator now escalates a non-retryable `Unknown` observation to `NeedsSupervisor` instead of re-observing to worker-call exhaustion (a Codex Transport-kind terminal reported as `Unknown` previously surfaced as an unrelated `BudgetExceeded`); the Codex adapter now honors the request deadline during start, failing closed as `codex.start-timeout` instead of parking forever on a child that never reports a thread identity. Adds a coordinator provider-observation conformance test and adapter start-deadline regressions.

## 0.1.0-preview.2

- Add the public `DelegationRuntime` facade over the in-memory delegation
  coordinator: the host entry point for delegate, pump, resume, intervene,
  checkpoint context, and terminal result reads.
- Guard publishing against orphan symbol pushes.

## 0.1.0-preview.1

- Initial extraction of the provider-neutral delegated-execution runtime from
  Marang: stable identities (`DelegationId`, `RequestKey`, `NodeGeneration`),
  lifecycle, budgets, capabilities, supervision, artifacts, evidence, and
  provider contracts (`Penghou.Qingniao.Abstractions`).
- In-memory reference coordinator proving deterministic acceptance,
  execution, handle reconciliation, cancellation, waiting and resume, bounded
  supervisor context, revision-fenced interventions, candidate publication,
  concurrent evaluation, bounded semantic correction, budget enforcement, and
  the terminal outcome matrix (Milestones 2.1 through 2.8).
- Frozen contract surface with public API baselines; first NuGet publication.
