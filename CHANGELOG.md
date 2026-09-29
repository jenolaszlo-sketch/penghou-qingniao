# Changelog

Notable changes to Penghou.Qingniao are recorded here. The project follows
[Semantic Versioning](https://semver.org/) for package versions. Preview
releases may still revise public contracts; every public API is tracked in
`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` per package.

## Unreleased

- New `Penghou.Qingniao.Codex` package (not yet published): process-isolated
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
