# Qingniao agent execution as a durable capability (pre-extraction)

> This kernel design migrates from the original Qingniao namespace to
> `Penghou.Qingniao` under [ADR 0015](decisions/0015-qingniao-extraction-and-marang-service-boundary.md).
> References to Qingniao owning provider execution below are historical until the
> source rename; the Qingniao product owns only the remote service boundary.

## Decision

Qingniao treats coding agents as execution providers. An agent owns how it
performs one bounded activity; Qingniao owns how that activity participates in a
durable outcome.

> Agents decide how to perform bounded work. Qingniao decides how that work is
> constrained, verified, retried, escalated, and reported.

An internal Codex worker tree is one opaque provider operation by default. It
does not become a tree of Zhinu steps, and Qingniao does not reproduce native
subagent scheduling, repository exploration, or prompt iteration.

An accepted supervised work item may have multiple immutable `NodeGeneration`s
inside linked workflow runs. A retry reconnects to the same provider operation;
deliberate selective re-execution creates a new linked node generation. Qingniao
never reopens a terminal execution or rewrites its result.

## Execution categories

Qingniao recognizes three semantic categories:

- **Agent execution** pursues a bounded outcome using its own tools and internal
  delegation, such as Codex, Claude Code, or OpenCode.
- **Model execution** performs a bounded inference through Baize, such as
  planning, review, classification, or summarization.
- **Deterministic execution** runs host-controlled validation, such as tests,
  builds, formatting, static analysis, or diff inspection.

These categories describe trust and evidence semantics. They should not become
a closed routing enum that prevents a provider from offering multiple
capabilities. Workflow policy requests capabilities such as `implement-code`,
`review-code`, or `run-tests`; host configuration selects an eligible provider.

Deterministic evidence wins over conflicting model claims. A successful agent
message is never proof that tests passed.

## Durable provider protocol

A blocking `ExecuteAsync` is insufficient as the foundational adapter contract.
An external agent may accept work before Qingniao crashes or loses its response.
Blindly retrying would duplicate work and cost.

The provider seam must support the semantic equivalent of:

```text
Start(request, idempotency identity) -> external execution handle
Observe(handle)                      -> state/progress revision
GetResult(handle)                    -> normalized result/evidence
Cancel(handle)                       -> idempotent cancellation request
Resume(handle, optional correction)  -> continued execution
```

`Start`, `Observe`, `GetResult`, and `Cancel` are durable provider operations;
`Resume` is an explicit continuation capability, not permission to reopen a
terminal execution. A provider may expose richer protocol details behind this
seam.

The exact API remains a Milestone 1 design gate. Required invariants are:

1. Persist the external handle as soon as the provider reveals it.
2. Re-observe or resume a known handle after workflow replay.
3. Never start a second provider operation merely because an acknowledgement
   was lost.
4. Treat cancellation as a request and record its confirmed disposition.
5. Preserve provider event and result identities without treating raw
   transcripts as Qingniao's state.

Zhinu owns workflow replay. The provider handle lets a replay reconnect to the
external activity safely.

Notification and wake values are hints, not authorization. They may schedule
supervisor attention, but only a durable, revision-fenced Qingniao intervention
can change policy or resume a planned waiting checkpoint.

## Codex provider

The current official Codex surface makes an initial provider practical:

- The [Codex SDK](https://learn.chatgpt.com/docs/codex-sdk) can start, continue,
  and resume local Codex threads. Its documented SDKs are currently TypeScript
  and Python; the Python SDK controls the local app server over JSON-RPC.
- [Non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode)
  exposes `codex exec`, JSONL lifecycle events, JSON Schema output, explicit
  sandbox settings, usage, and session resume.

For the capability spike, a process adapter around `codex exec --json` is the
shortest proof. It must capture `thread.started` before relying on later output,
use an output schema for the final receipt, isolate stdout from diagnostics,
and use `workspace-write` only inside a disposable candidate workspace.

For a longer-lived provider, prefer a small bridge over the SDK/app server so
Qingniao can observe thread events, approvals, cancellation, and resume without
parsing a human CLI surface. Keep this adapter outside the core .NET package so
the transport can evolve independently.

Authentication is host policy. Do not assume that a subscription is free,
available, or appropriate for automation. Never expose Codex authentication to
repository-controlled processes, and do not pass credentials into test/build
environments.

## Capability routing

Routing uses semantic requirements plus policy:

```text
implement-code       -> economical Codex agent provider
review-code          -> independent Baize model or read-only agent provider
run-tests            -> deterministic host executor
architectural-choice -> supervisor
```

Provider selection records capability, provider, concrete profile/model,
policy version, and budget hints. A provider may internally choose subordinate
models; Qingniao only requires deeper visibility when needed for cancellation,
cost, policy, or audit.

## Two budget layers

Qingniao budgets control workflow attempts, correction cycles, duration,
parallelism, and escalation. Provider budgets may control reasoning effort,
subagents, tokens, provider cost, or subscription usage. Qingniao passes hints
where supported and records actual telemetry where available, but does not
invent precision a provider cannot supply.

Budget exhaustion is a result with evidence, not an infrastructure exception.

## Recursion boundary

Native delegation inside an execution provider is allowed by provider policy.
Qingniao recursion is denied in version 1: subordinate executions do not receive
the Marang MCP endpoint or credentials needed to start another Qingniao
delegation. Any future recursion requires an explicit depth, ancestry, and
budget contract.

## Simple Implement preset/provider flow

This is the bootstrap provider flow for `marang_delegate`, not the complete
supervisory vertical slice. The durable waiting and intervention sequence is
defined in [product direction](product-direction.md).

```text
Accept -> Execute agent -> seal candidate revision
                             |                 |
                             v                 v
                    deterministic tests   independent review
                             +--------v--------+
                                   Evaluate
                              pass | correct once
                                   v
                                  Result
```

Inspection and planning may occur inside the agent operation and may be
returned as optional typed artifacts. They become separate Qingniao workflow
steps only when independent approval, reuse, routing, or observability makes
that separation valuable.
