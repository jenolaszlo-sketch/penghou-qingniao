# Qingniao and Marang delivery plan

## Purpose

Record the dependency order after separating the reusable delegated-execution runtime
from the Marang service product.

## Delivery order

```text
Published Penghou primitives
  Zhinu preview.12
  Hongxian preview.2
          |
          v
Penghou.Qingniao.Abstractions
          |
          v
Penghou.Qingniao delegated-execution runtime
          |
      +---+------------------+
      |                      |
      v                      v
Marang.Server          Guyabano embedded dogfood
      |
      v
MCP + real provider + durable Zhinu/Hongxian integration
```

## Gate 1 — Available primitive releases

Status: **re-audited 2026-09-30 (QH-07); see ADR 0018 for the full
capability/version/evidence matrix**

- Penghou.Zhinu `0.1.0-preview.12` is published.
- Penghou.Hongxian `0.1.0-preview.2` is published.
- Penghou.Siming `0.1.0-preview.4` is published and provides
  `penghou-canonical-json-v2` plus SHA-256 computation/verification.

QH-07 pins (published, verified against the NuGet index plus release
commits/tags): Zhinu `0.1.0-preview.14` (release commit `98ad00d`;
signal receipts, fenced delivery, signal waits, selective restart with
receipts, artifact fencing, leases, and terminal cancel are published —
external-operation handle persistence, parked waits, and execution
generations are source-only and need the next Zhinu release); Fuwen
`0.1.0-preview.11` (admission, revision lineage, checkpoint/wait gates,
typed context requirements, fingerprints, and the Zhinu port are published —
no supervisor node, no FI-04 grants, coordinated inference unreleased);
Hongxian `0.1.0-preview.4` (append with `ExpectedHead`, projections, leases,
and store-local outbox reconciliation are published — the crash-healing
reconciled projection read is post-tag and needs the next release). Siming
`0.1.0-preview.7`, Baize `0.3.0-preview.6`, Cangjie `0.1.0-preview.3`, Hetu
`0.2.0-preview.6` are published; Qingniao core takes no Siming package
dependency (it vendors its own pinned fingerprint envelope; ADR 0016's
dependency claim is superseded by ADR 0018).

Fuwen's advanced-plan validation gate and Zhinu's remaining durable external-
operation fencing work block only their later integrations; they do not block
Qingniao extraction or its deterministic in-memory proof.

The optional composition direction is:

```text
Fuwen -> WorkflowPlan -> Zhinu activity -> Qingniao -> executor
```

Fuwen P0 admission, immutable revision lineage, and its Zhinu execution port,
together with Zhinu external-operation fencing, are upstream gates for
workflow-backed delegation. They are not gates for publishing or consuming
Qingniao's direct-delegation contracts and policy components.

## Gate 2 — Qingniao extraction

Status: **extraction complete; 0.1.0-preview.1 and 0.1.0-preview.2 published;
0.1.0-preview.3 cut (Codex first publication; publishes on tag push)**

- [x] Rename the reusable projects, namespaces, packages, tests, and API baselines.
- [x] Prove no dependency on Marang, ASP.NET Core, or MCP.
- Treat the existing Marang-named preview packages as superseded; do not create
  compatibility shims without a consumer.
- [x] Publish `Penghou.Qingniao.Abstractions` and `Penghou.Qingniao`
  `0.1.0-preview.1`/`0.1.0-preview.2` so Marang can remove the transitional duplicate source (Marang consumes `0.1.0-preview.2`).
- [x] Consume Siming preview.4 and close the external-start canonical
  fingerprint integration.

## Gate 3 — Qingniao coordinator

Status: **complete for the M2.8 in-memory coordinator proof; durable integration remains downstream**

- [x] Complete the internal explicit-pump in-memory coordinator proof.
- [x] Prove early handle capture and ambiguous-start reconnect.
- [x] Add typed provider-failure classification and enforce call, retry, and
  duration budgets without leaking raw exception messages.
- [x] Complete cancellation/resume and supervision behavior, including
  confirmed/requested/rejected/unknown reconciliation, bounded safety calls,
  honest `NeedsSupervisor` when no adapter or handle authority remains, and
  stable `WaitingForSupervisor` checkpoints.
- [x] Prove candidate sealing, deterministic Test, independent Review, bounded
  supervision, one repair generation, and terminal-result immutability.
- Resolve atomic acceptance/state initialization, bounded retained state, and
  immutable objective input as post-preview.2 hardening (QH-04/QH-05 —
  complete 2026-09-28); the DelegationRuntime facade is already public and in-memory-only.
- Run API, XML, multi-target, package, and isolated-consumer verification.

Marang and Guyabano exercise the package boundary before any stability graduation (first previews already published).

## Gate 4 — Marang service

Status: **queued**

- Scaffold the non-packable ASP.NET Core MVC server.
- Compose Qingniao under authenticated, authorized, bounded service policy.
- Expose the minimal MCP submission/status/result/cancel surface.
- Add supervision, artifact retrieval, and HTTP operations only after their
  authorization and response-bound tests pass.
- Add Fuwen workflow submission only after Fuwen P0 admission and the
  Fuwen-to-Zhinu port are available; Marang composes the peers rather than
  routing Fuwen or Zhinu through Qingniao.

## Gate 5 — Provider, durability, and dogfood

Status: **queued**

- Prove one isolated Codex provider and reconnect without duplicate work.
- Map Qingniao activities to Zhinu durability and Hongxian/Siming audit.
- Exercise Qingniao directly from Guyabano and remotely through Marang.
- Exercise workflow-backed delegation in both Marang and Guyabano through the
  same `Fuwen -> Zhinu -> Qingniao` boundary before considering an adapter
  package.
- Add provider/client packages only when demonstrated reuse justifies them.

## Release discipline

Every release gate requires updated ownership documentation, independent
design/security/API review, complete relevant tests, formatting, package and
isolated-consumer checks, a version bump, commit, push, and indexed dependency
availability before downstream consumption.
