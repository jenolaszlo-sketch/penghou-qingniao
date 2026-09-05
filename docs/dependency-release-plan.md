# Qingniao and Marang delivery plan

## Purpose

Record the dependency order after separating the reusable delegated-execution runtime
from the Marang service product.

## Delivery order

```text
Published Penghou primitives
  Zhinu preview.12
  Hongxian preview.2
  Siming preview.4
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

Status: **complete for the extraction and in-memory slice**

- Penghou.Zhinu `0.1.0-preview.12` is published.
- Penghou.Hongxian `0.1.0-preview.2` is published.
- Penghou.Siming `0.1.0-preview.4` is published and provides
  `penghou-canonical-json-v2` plus SHA-256 computation/verification.

Fuwen's advanced-plan validation gate and Zhinu's remaining durable external-
operation fencing work block only their later integrations; they do not block
Qingniao extraction or its deterministic in-memory proof.

## Gate 2 — Qingniao extraction

Status: **implementation complete; awaiting commit and first preview publication**

- [x] Rename the reusable projects, namespaces, packages, tests, and API baselines.
- [x] Prove no dependency on Marang, ASP.NET Core, or MCP.
- Treat the existing Marang-named preview packages as superseded; do not create
  compatibility shims without a consumer.
- [ ] Publish `Penghou.Qingniao.Abstractions` and `Penghou.Qingniao`
  `0.1.0-preview.1` so Marang can remove the transitional duplicate source.
- [ ] Consume Siming preview.4 and close canonical-fingerprint integration in
  the next implementation batch.

## Gate 3 — Qingniao coordinator

Status: **queued**

- Complete the explicit-pump in-memory coordinator.
- Prove early handle capture and ambiguous-start reconnect.
- Prove candidate sealing, deterministic Test, independent Review, bounded
  supervision, one repair generation, and terminal-result immutability.
- Run API, XML, multi-target, package, and isolated-consumer verification.

Publish the first preview after the extraction verification. Marang and
Guyabano then exercise the package boundary before any stability graduation.

## Gate 4 — Marang service

Status: **queued**

- Scaffold the non-packable ASP.NET Core MVC server.
- Compose Qingniao under authenticated, authorized, bounded service policy.
- Expose the minimal MCP submission/status/result/cancel surface.
- Add supervision, artifact retrieval, and HTTP operations only after their
  authorization and response-bound tests pass.

## Gate 5 — Provider, durability, and dogfood

Status: **queued**

- Prove one isolated Codex provider and reconnect without duplicate work.
- Map Qingniao activities to Zhinu durability and Hongxian/Siming audit.
- Exercise Qingniao directly from Guyabano and remotely through Marang.
- Add provider/client packages only when demonstrated reuse justifies them.

## Release discipline

Every release gate requires updated ownership documentation, independent
design/security/API review, complete relevant tests, formatting, package and
isolated-consumer checks, a version bump, commit, push, and indexed dependency
availability before downstream consumption.
