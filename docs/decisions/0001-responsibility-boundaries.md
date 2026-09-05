# ADR 0001: Keep Marang a delegation composition layer

> **Superseded by ADR 0015:** the delegation composition semantics migrate to
> Penghou.Qingniao; Marang owns the MVC/MCP service boundary.

Status: accepted for the initial vertical slice; expanded by the planned
supervisory lifecycle in [product direction](../product-direction.md).

## Decision

Marang owns delegation lifecycle, predefined strategy selection, budgets,
semantic worker profiles, evaluation policy, escalation, and result
aggregation.

Zhinu remains authoritative for durable workflow execution. Baize remains
authoritative for bounded model invocation. Hongxian may record session
continuity and evidence but is not a worker environment. Agentic coding runtimes
are execution providers: their internal tool and subagent behavior stays opaque,
while Marang controls capability selection, budgets, required evidence,
verification, retry, escalation, and durable external-operation correlation.

The public request uses an opaque `WorkspaceReference`. The first successful
output is an isolated candidate revision or patch, not an automatically applied,
committed, or published change.

## Consequences

- Marang can be tested with fake execution providers and an in-memory workflow
  policy.
- Core contracts do not force Zhinu, Baize, Hongxian, MCP, or filesystem types
  on consumers.
- Host policy remains the authority for capabilities and workspace resolution.
- Integration packages may evolve independently.
- Codex, Baize, deterministic, and future agent adapters can evolve without
  changing delegation semantics.
- Every non-atomic provider call needs an external handle so workflow replay can
  reconnect instead of duplicating work.

## Protocol boundary

MCP is Marang's primary northbound interface for supervising agents. A2A is the
preferred southbound interface for compatible external agents. Process, SDK,
Baize, and deterministic adapters remain valid alternatives. Protocol wire
types and provider-specific behavior stay outside core orchestration.

Protocols are adapters. The durable outcome model is Marang.

The initial predefined strategy remains the simple entry point. Future
supervisor-selected or supervisor-authored workflows use Fuwen for semantics
and compilation; they do not turn Marang into a workflow language. Planned
waiting, wake, and intervention behavior extends supervision while preserving
Zhinu's authority over durable execution.
