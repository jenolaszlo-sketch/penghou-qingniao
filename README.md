# Penghou.Qingniao

[![CI](https://github.com/jenolaszlo-sketch/penghou-qingniao/actions/workflows/ci.yml/badge.svg)](https://github.com/jenolaszlo-sketch/penghou-qingniao/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/jenolaszlo-sketch/penghou-qingniao)](LICENSE)

Penghou.Qingniao is a provider-neutral delegated-execution runtime for .NET. It
gives a host a stable way to accept one bounded unit of work, select an
authorized execution actor, reconnect after ambiguous failures, supervise the
work, and publish an immutable result with evidence.

Qingniao owns the lifecycle around one delegated activity. It is not a general
workflow engine, model SDK, task queue, session ledger, or application service.

```text
application or MCP host
    -> Qingniao delegation, budget, supervision, and evidence
        -> process, agent, A2A, model, or deterministic execution provider
```

## Why it exists

Delegated work is more than a method call. Acceptance can be ambiguous, a
provider may expose its durable handle late, the caller can restart, budgets can
expire, and a supervisor may need to inspect or redirect work without rewriting
its history.

Qingniao makes those concerns explicit:

- caller idempotency is separate from provider execution identity;
- reconnecting to an accepted attempt is separate from semantic re-execution;
- transient progress is separate from an immutable terminal result;
- deterministic evidence remains authoritative when a model explains it;
- wake-up hints cannot authorize work or extend budgets;
- cancellation stops future work but does not pretend to roll back artifacts;
- candidate revisions are sealed before concurrent test and review;
- every intervention is checkpoint-, revision-, identity-, and policy-fenced.

## Packages

Both packages target .NET 8 and .NET 10.

| Package | Responsibility |
| --- | --- |
| `Penghou.Qingniao.Abstractions` | Stable identities, requests, lifecycle, budgets, capabilities, supervision, artifacts, evidence, and provider contracts |
| `Penghou.Qingniao` | Validation, canonical semantic identity, provider resolution, in-memory execution proof, lifecycle policy, and supervision behavior |
| `Penghou.Qingniao.Codex` | Process-isolated Codex CLI execution adapter (not yet published; live execution proof pending) |

The abstractions intentionally contain no MVC, HTTP, MCP, authentication,
tenant, filesystem, Fuwen, Zhinu, Baize, or Marang types. Hosts adapt those
systems at the boundary.

## Lifecycle and identity

The public lifecycle is deliberately small:

```text
Queued -> Running -> Completed
                  -> Failed
                  -> Cancelled
                  -> BudgetExceeded
                  -> NeedsSupervisor
                  -> WaitingForSupervisor -> Running
Queued -------------------------------> WaitingForSupervisor
```

`DelegationId` is the stable caller-facing identity. `WorkflowReference` is an
opaque provider-qualified reference and may change with the implementation.
Every submission also carries a caller-scoped `RequestKey`:

1. first use creates one delegation;
2. the same key plus the same normalized request returns the accepted handle;
3. the same key with different semantics is a conflict;
4. a lost acceptance response is safe to retry.

Retries reconnect to the same semantic node generation. Deliberate re-execution
creates a new `NodeGeneration`. Terminal results are immutable; reopening
completed work creates linked execution lineage instead of mutating history.

## Supervision

A planned pause exposes a stable `SupervisorCheckpointId` and a monotonic
progress revision. Interventions are typed (`Approve`, `Reject`, `Retry`,
`ReexecuteNode`, `ReexecuteSubgraph`, `AddConstraint`, `ChangeExecutor`,
`SelectAlternative`, `Escalate`, `Cancel`, and bounded `Respond`).

Acceptance requires the expected checkpoint, revision, authorized supervisor,
and caller idempotency key. Competing or stale actions are rejected. Re-entry
context is demand-driven and bounded by item and UTF-8 byte limits; omitted or
truncated facets are reported explicitly.

An intentional pause is nonterminal `WaitingForSupervisor`. An unrecoverable
or policy-required escalation is terminal `NeedsSupervisor`. Qingniao never
silently resumes terminal work.

## Provider and evidence boundary

Providers are selected by semantic capability rather than vendor or model
name. A provider may run an external agent, local process, Baize route, test
runner, build, diff, or static analysis. It returns normalized handles,
observations, receipts, artifacts, and terminal evidence.

The coordinator distinguishes:

- starting, observing, resuming, and cancelling an external operation;
- handle capture before or after provider acceptance;
- safely classified transport retry from semantic re-execution;
- deterministic test outcomes from model-authored review;
- candidate publication from evaluation and correction.

The built-in Implement proof follows this bounded shape:

```text
execute -> sealed candidate N
               |          |
              test      review
               +---- evaluate
                       |
             accept, supervise, or correct -> candidate N+1
```

Test and review may run concurrently only against the same sealed candidate.
Correction creates a new candidate revision and preserves the evidence for
earlier revisions.

## Composition with Fuwen and Zhinu

Direct delegation is a first-class use case and requires neither Fuwen nor
Zhinu. Applications that need programmable, durable, multi-step work compose
the capabilities in this direction:

```text
Fuwen -> immutable WorkflowPlan -> Zhinu -> Qingniao -> execution actor
```

[Fuwen](https://github.com/jenolaszlo-sketch/penghou-fuwen) owns workflow
semantics and compilation. [Zhinu](https://github.com/jenolaszlo-sketch/penghou-zhinu)
owns durable scheduling, retries, waits, restart, cancellation, and recovery.
Qingniao performs one bounded delegated activity when invoked by the host or by
a normal Zhinu activity.

Hongxian owns session continuity and audit narrative. Hetu and Cangjie may
supply opaque code-graph and context-snapshot references. Baize may execute
bounded model calls. None of those responsibilities move into Qingniao core.

## Current status

Milestone 2.1 through 2.8 is complete. The internal in-memory reference
coordinator proves deterministic acceptance, execution, handle reconciliation,
cancellation, waiting and resume, bounded supervisor context, revision-fenced
interventions, candidate publication, concurrent evaluation, bounded semantic
correction, budget enforcement, and the terminal outcome matrix.

The coordinator remains internal because it is a semantic proof, not durable
production infrastructure. The public preview currently exposes the reviewed
contracts plus reusable validation, identity, registry, fingerprint, provider,
and policy components.

Next milestones add a bounded Codex execution adapter and map the proven
semantics to durable Zhinu execution. Packages are published as previews
(`0.1.0-preview.2`); consumers should still treat the repository as
pre-release source.

See:

- [Architecture](docs/architecture.md)
- [Roadmap and milestone status](docs/roadmap.md)
- [Changelog](CHANGELOG.md)
- [Runnable sample](samples/Penghou.Qingniao.Sample/Program.cs)
- [Agent execution boundary](docs/agent-execution.md)
- [Protocol boundaries](docs/protocol-boundaries.md)
- [Security](SECURITY.md)
- [Extraction decision](docs/decisions/0015-qingniao-extraction-and-marang-service-boundary.md)
- [Optional workflow composition](docs/decisions/0017-optional-workflow-composition.md)

## Development

```powershell
dotnet build Penghou.Qingniao.slnx --configuration Release
dotnet test Penghou.Qingniao.slnx --configuration Release --no-build
dotnet pack Penghou.Qingniao.slnx --configuration Release --no-build --output artifacts
```

## License

[AGPL-3.0](LICENSE)

Copyright (c) 2026 Jenő Konrád László
