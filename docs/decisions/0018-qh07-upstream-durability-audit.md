# ADR 0018: QH-07 upstream durability audit — capability matrix and QH-08 entry gate

## Status

Accepted 2026-09-30 (QH-07 complete). Read-only audit; no Qingniao code changed.

## Context

QH-08 (durable host integration, M4) may only start after verified released
upstream capabilities plus QH-05/QH-06/QH-07. This record pins the exact
published versions, separates published contracts from local-source-only work,
and defines the QH-08 entry gate. Published availability was verified against
the NuGet index plus release commits/tags — not inferred from local HEAD or
prior checkboxes. Sibling paths below assume sibling checkouts.

## Verified published versions (2026-09-30)

| Primitive | Published | Local HEAD | Publish commit / tag |
| --- | --- | --- | --- |
| Zhinu | `0.1.0-preview.14` | same version, AHEAD in content | commit `98ad00d` (2026-09-18); repo has no tags and no CHANGELOG |
| Fuwen | `0.1.0-preview.11` | same version, AHEAD in content | tag `v0.1.0-preview.11`; post-tag corrective work is `Unreleased` |
| Hongxian | `0.1.0-preview.4` | same version, AHEAD by one fix | tag `v0.1.0-preview.4`; HEAD `e7d0feb` adds one unreleased Sqlite fix |
| Siming | `0.1.0-preview.7` | matches | — |
| Baize | `0.3.0-preview.6` | matches | — |
| Cangjie | `0.1.0-preview.3` | matches | — |
| Hetu | `0.2.0-preview.6` | matches | — |

Zhinu published content was established by symbol-presence checks of the key
contract files at commit `98ad00d`. Fuwen published content was established by
`git diff v0.1.0-preview.11..HEAD`. Hongxian published content was established
by `git diff v0.1.0-preview.4..HEAD`.

## Capability matrix

Verdict scale: SUPPORTED = in the published package named under Minimum
version. SOURCE-ONLY = in local sibling source, not in any published package.
MISSING = absent everywhere; owner named.

### Zhinu (durable execution)

| Capability | Verdict | Minimum version | Evidence |
| --- | --- | --- | --- |
| Idempotent signal send with receipts | SUPPORTED | Zhinu `0.1.0-preview.14` | `IIdempotentWorkflowSignalRepository.SendSignalIdempotentlyAsync`, `SignalSendReceipt`, `WorkflowEngine.SendSignalWithReceiptAsync` |
| Fenced signal delivery (revision/generation/lease) | SUPPORTED | Zhinu `0.1.0-preview.14` | `IWorkflowSignalRepository.TryDeliverSignalAsync`; exactly-once per `docs/semantics.md` |
| Signal waits (supervisor-wait mapping) | SUPPORTED | Zhinu `0.1.0-preview.14` | `WorkflowContext.WaitForSignalAsync` |
| Selective restart (dependents / step-only) | SUPPORTED | Zhinu `0.1.0-preview.14` | `IWorkflowStepRepository.PlanRestartAsync`, `StepRestartMode.StepOnly` |
| Idempotent restart receipts | SUPPORTED | Zhinu `0.1.0-preview.14` | `IIdempotentWorkflowRestartRepository`, `WorkflowEngine.RestartStepWithReceiptAsync` |
| Artifact publication fencing | SUPPORTED | Zhinu `0.1.0-preview.14` | `IWorkflowArtifactRepository.PublishArtifactAsync`; stale/late fails closed (no caller idempotency key — content-keyed) |
| Run/step lease and fenced claims | SUPPORTED | Zhinu `0.1.0-preview.14` | `IWorkflowLeaseRepository.TryClaimRunAsync`, fenced step claims |
| Cancellation (terminal, no rollback) | SUPPORTED | Zhinu `0.1.0-preview.14` | `WorkflowEngine.CancelAsync`; rollback is separate and resumable-not-idempotent |
| Durable external-operation handle persistence | SOURCE-ONLY | first Zhinu release after `preview.14` | `IWorkflowExternalOperationRepository` (register/acquire/complete/fail, idempotent on `IdempotencyKey`) |
| Parked durable waits (signal/retry/delay/child) | SOURCE-ONLY | first Zhinu release after `preview.14` | `IWorkflowWaitRepository` |
| Execution generations + atomic cutover | SOURCE-ONLY | first Zhinu release after `preview.14` | `IWorkflowInstanceRepository` (dense ordinals, quiesced-predecessor cutover, dispositions) |
| Fenced admin commands with expected-state checks | MISSING | — | owner: Zhinu (cancel/fork/rollback/retention have no receipts) |
| Typed authority waits | MISSING | — | owner: Zhinu/Hufu (`docs/hufu-integration.md`: "Add typed authority waits") |

### Fuwen (workflow-backed delegation only; direct delegation stays independent)

| Capability | Verdict | Minimum version | Evidence |
| --- | --- | --- | --- |
| Immutable admission with opaque receipt | SUPPORTED | Fuwen `0.1.0-preview.11` | `WorkflowAdmissionService.AdmitAsync`; receipt bound to definition + catalogue snapshot + policy + grants + budget |
| Plan revision lineage + deterministic comparison | SUPPORTED | Fuwen `0.1.0-preview.11` | `PlanRevisionDocument.LoadVerified`, `PlanRevisionComparer` (explanatory only — never authorizes reuse) |
| Checkpoint / external-wait interaction gates | SUPPORTED | Fuwen `0.1.0-preview.11` | `CheckpointNode` (no suspension), `WaitNode` (signal suspension, optional timeout); presentation host-owned |
| First-class supervisor-authorized external-input node | MISSING | — | owner: Fuwen (no `SupervisorNode`/`ExternalInputNode`; 0 hits in source) |
| Typed context requirements | SUPPORTED | Fuwen `0.1.0-preview.11` | IR v3+ `ContextRequirement` |
| Plan fingerprints + verified definition-store loading | SUPPORTED | Fuwen `0.1.0-preview.11` | `WorkflowDefinitionDocument.LoadVerified`, single `fuwen-ir/v1` |
| Fuwen-to-Zhinu execution port | SUPPORTED | `Penghou.Fuwen.Zhinu 0.1.0-preview.11` | `FuwenZhinuWorkflowFactory.CreateAsync`; coordinated fan-out rejected |
| FI-04 resource-grant representation | MISSING | — | owner: Hufu (+ Fuwen binding); shipped `ScopeFor` is an explicitly unsafe `name@version` pseudo-scope, not authorization |
| Coordinated inference (model/tool loop) | LIMITED / SOURCE-ONLY | — | post-preview.11 corrective work unreleased; publication on hold (Fuwen H-01) |

### Hongxian (session/correlation authority)

| Capability | Verdict | Minimum version | Evidence |
| --- | --- | --- | --- |
| Session append + `ExpectedHead` + idempotency | SUPPORTED | Hongxian `0.1.0-preview.4` | `ISessionEventStore.AppendAsync`, `SessionEventRequest.ExpectedHead`, enforced atomically by Siming |
| Rebuildable projections + verified history | SUPPORTED | Hongxian `0.1.0-preview.4` | `ISessionProjectionStore.RebuildAsync(VerifiedSessionHistory)` |
| Crash-healing reconciled projection read | SOURCE-ONLY | first Hongxian release after `preview.4` | `HongxianSqliteStoreSet.ReadReconciledProjectionAsync` (HEAD `e7d0feb`) |
| Decisions / incidents / recovery records | PARTIAL (published; generic vocabulary) | Hongxian `0.1.0-preview.4` | `SessionRecoveryCoordinator`; no supervised-work/checkpoint/intervention types — typed mapping stays in Qingniao |
| Session decision leases | SUPPORTED (session-scope only) | Hongxian `0.1.0-preview.4` | `ISessionDecisionLeaseProvider`; cannot override Zhinu fencing |
| Evidence outbox + forward reconciliation | SUPPORTED (store-local atomic; relay host-owned) | Hongxian `0.1.0-preview.4` | `ISessionEvidenceOutbox`, `SessionEvidenceOutboxDispatcher`; no background forwarder; no distributed transaction |
| Indexed / as-of / re-entry session queries | MISSING (later upstream) | — | owner: Hongxian; use bounded event pages + projections (experience recall exists separately and is not a session query) |
| Export / import, cross-ledger evidence discovery | MISSING | — | owner: Hongxian |
| Package independence (no code-analysis stack) | VERIFIED for core + Sqlite | Hongxian `0.1.0-preview.4` csproj refs | core: analyzer only; Sqlite: + `Microsoft.Data.Sqlite` + `Penghou.Siming.Sqlite`; LatticeDb provider is local-only and unpublished |

### Siming, Baize, Cangjie, Hetu (carried forward, re-audit at M5)

Gate 0.5 findings stand. Siming `0.1.0-preview.7` (canonical-json-v2, SHA-256)
and Baize `0.3.0-preview.6` remain the pins; Baize P0 tool-integrity gaps still
block authoritative complex-tool integration. Cangjie `0.1.0-preview.3` and
Hetu `0.2.0-preview.6` P1 gaps remain in the owning roadmaps. None of these
block QH-08: Qingniao core takes no Siming package dependency (see D2).

## Decisions

- **D1 — QH-08 entry versions.** QH-08 planning may use Fuwen `preview.11` and
  Hongxian `preview.4` content as audited above. QH-08 implementation must wait
  for: (a) a Zhinu release containing external-operation handle persistence,
  parked waits, and execution generations; (b) a Hongxian release containing
  the reconciled projection read. Until then, deliver tested fail-closed seams
  and a precise blocked handoff, not a "durable" in-memory substitute.
- **D2 — ADR 0016 dependency claim superseded.** ADR 0016 says Qingniao
  "depends on Penghou.Siming 0.1.0-preview.4". The dependency was removed:
  core vendors its own pinned `external-start-semantics-v1` envelope
  (`LocalSemanticFingerprintVerifier`: explicitly ordered writer, lowercase-D
  GUIDs, exact tick durations, UTC round-trip deadlines, explicit nulls), and
  `BoundaryArchitectureTests.Core_contains_no_workflow_plan_or_provider_specific_semantics`
  forbids Siming types in core. Rationale: keep the delegation core free of
  upstream package binds so direct delegation never inherits a store/ledger
  dependency. Consequence: Qingniao fingerprints are opaque strings outside
  Qingniao; hosts persist them opaquely and verify only through Qingniao's
  verifier (`Matches`). No cross-package recomputation is supported.
- **D3 — Supervisor mapping is signal waits.** Qingniao waiting maps to Zhinu
  signal waits resumed by host intervention; do not expect typed authority
  waits from Zhinu before Hufu. Fencing/duplicate-suppression/authorization
  must not be inferred from Fuwen `WaitNode.TimeoutSeconds`.
- **D4 — No Qingniao integration packages.** Reaffirm ADR 0017: the Zhinu
  activity-to-delegation mapping lives in the host; no `Penghou.Qingniao.Fuwen`
  or `Penghou.Qingniao.Zhinu` until Marang and Guyabano demonstrate substantial
  identical adapter code.
- **D5 — Cross-store rule.** Each outbox is atomic only with its owning store.
  Qingniao–Zhinu–Hongxian integration reconciles forward through idempotent
  store-local outboxes; never claim a distributed transaction.
- **D6 — Upstream hygiene notes (owning projects).** Zhinu has no git tags and
  no CHANGELOG, so published content had to be reconstructed from the
  version-bump commit; tag releases to make the next audit trivial. Fuwen
  publication remains on hold (Fuwen H-01); do not consume unreleased Fuwen
  source paths (budget ledger, protected payload store, turn executor) from
  Qingniao or its hosts.

## Non-code packed consumer (design; implementation in QH-10)

A `PackedConsumer` test project restores only packed NuGet packages — never
project references or sibling checkouts — and proves the Gate 0.5
package-independence acceptance:

- Packages under test: `Penghou.Qingniao`, `Penghou.Qingniao.Abstractions`,
  `Penghou.Hongxian`, `Penghou.Hongxian.Sqlite`, `Penghou.Siming`,
  `Penghou.Siming.Sqlite` (minimum versions per this ADR).
- Scenario: a non-code research delegation with a deterministic local provider —
  delegate, session history via Hongxian.Sqlite, fault injection plus retry,
  artifact/evidence publication, and recovery.
- Forbidden-graph assertion: the resolved runtime closure must not contain
  Hetu, Roslyn, ANTLR, LadybugDB, LatticeDbSharp, or any code-memory
  configuration contract. (Verified by inspection today for core/Sqlite
  packages; this test locks it in.)
- Session/recovery assertions run now; restart-durable assertions (waiting,
  intervention, terminal result across process restart) are added with QH-08.
- Removing a future optional code adapter must preserve ordinary
  execution/session behavior — recorded as invariant, tested when such an
  adapter exists.

## Consequences

- QH-07 is complete; QH-08 remains blocked on the two named upstream releases.
- `docs/dependency-release-plan.md` Gate 1 is updated to these pins; Gate 3
  records QH-04/QH-05 complete.
- Marang/Guyabano may rely on: published Zhinu signal/restart/artifact/lease
  primitives, published Fuwen admission/lineage/gates, published Hongxian
  session/outbox primitives — and on nothing marked SOURCE-ONLY or MISSING
  above.
