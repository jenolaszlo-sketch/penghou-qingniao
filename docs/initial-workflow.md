# Qingniao simple Implement preset (pre-extraction)

> This delegation-kernel contract migrates to `Penghou.Qingniao` under
> [ADR 0015](decisions/0015-qingniao-extraction-and-marang-service-boundary.md).
> `marang_delegate` remains the MCP operation that maps to it; Qingniao does not
> own the reusable strategy or workflow semantics.

This document describes the current `marang_delegate` preset and remains a
small, opinionated bootstrap slice. It is not the complete product
direction: supervisors will later select or author compiled Fuwen workflows,
wait at durable checkpoints, intervene, and selectively re-execute linked
`NodeGeneration`s. See [product direction](product-direction.md) and the
[roadmap](roadmap.md).

This document preserves the behavioral detail of the bootstrap preset. It is a
predefined Qingniao strategy, not a caller-authored workflow or a new DSL, and it
is not the durable supervisory vertical slice described by the product
direction.

## Request

The supervisor supplies:

- a caller-scoped idempotency key;
- an objective;
- a host-resolved workspace reference;
- explicit acceptance criteria;
- constraints;
- worker-call, retry, duration, and parallelism budgets;
- the `Implement` strategy.

The host authenticates the caller and authorizes the workspace before durable
acceptance. The same request key and normalized request return the existing
delegation; conflicting reuse is rejected.

## Steps

### Execute

Submit the bounded outcome to an agent execution provider. The agent may inspect,
plan, edit, test, iterate, and use native subordinate workers according to its
provider policy. Qingniao does not represent that internal activity as workflow
steps.

The provider returns a normalized `AgentExecutionResult` and a sealed candidate
revision. Inspection and plan details are optional artifacts. Repository
content remains untrusted data, not policy.

Qingniao stores the external execution handle before awaiting completion. Replay
observes or resumes that handle rather than starting duplicate work.

### Test

Run deterministic host-approved commands against the sealed candidate. Publish
a `TestReport` with commands, exit outcomes, counts, bounded failure excerpts,
duration, and candidate identity. A model may summarize this report but cannot
change command outcomes.

### Review

Review the same candidate against the objective, criteria, constraints,
inspection, and plan. Publish structured findings with severity, location,
rationale, and criterion linkage. Record the dimensions by which the review was
independent from implementation.

### Evaluate

Policy combines deterministic tests, structured review, remaining budget, and
artifact integrity. It chooses exactly one of:

- complete with a validated candidate;
- create one bounded fix revision;
- fail due to a runtime or contract error;
- finish as budget exhausted;
- finish as supervisor judgment required.

### Correct

Address only the accumulated test failures and review findings. A correction creates a
new candidate revision and new Test and Review artifacts. It does not overwrite
the original candidate or evidence. Version 1 supports at most one fix cycle.

## Result

The result is concise and includes:

- terminal state and summary;
- candidate revision or patch reference;
- changed-file and command evidence;
- tests passed and failed;
- review disposition and resolved findings;
- typed artifact references;
- unresolved concerns;
- budget consumption and model/tool provenance.

Raw worker transcripts are excluded by default. Applying, committing, pushing,
or publishing the candidate is outside the initial workflow.

## MCP surface

- `marang_delegate` accepts and durably starts a delegation.
- `marang_status` returns compact progress and a monotonic revision.
- `marang_result` returns a terminal result and artifact references.
- `marang_cancel` requests idempotent cancellation.

The transport maps onto `IDelegationService`; it does not expose Zhinu workflow
objects or contain orchestration policy.
