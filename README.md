# Penghou.Qingniao

Penghou.Qingniao is a provider-neutral delegated-execution runtime for .NET.
It gives an application a stable way to hand bounded work to an execution
actor, observe it, reconnect after ambiguity, supervise it, and publish an
immutable result with evidence.

Qingniao is deliberately not a general workflow engine. It owns one delegated
activity and the lifecycle around that activity. [Penghou.Zhinu](https://github.com/jenolaszlo-sketch/penghou-zhinu)
owns durable workflow scheduling, retries, waits, and recovery; Qingniao can be
called from a Zhinu activity or embedded directly by an application.

Applications may optionally compose the wider stack as:

```text
Fuwen -> WorkflowPlan -> Zhinu -> Qingniao -> execution actor
```

[Penghou.Fuwen](https://github.com/jenolaszlo-sketch/penghou-fuwen)
describes and validates workflow semantics, Zhinu executes them durably, and
Qingniao performs each bounded delegated activity. Qingniao itself has no
compile-time dependency on either Fuwen or Zhinu, so direct delegation remains
a small first-class use case.

## Why it exists

Delegating work to an AI agent or another long-running executor is more than a
method call. Acceptance can be ambiguous, the caller can restart, the provider
may expose its durable handle late, budgets can be exhausted, and a person may
need to inspect or redirect work without corrupting its history. Qingniao makes
those concerns explicit and testable without coupling application code to a
particular model SDK, agent protocol, web framework, or persistence provider.

Its contracts distinguish:

- caller idempotency from provider-side execution identity;
- reconnecting to the same attempt from semantic re-execution;
- transient progress from immutable terminal results;
- model review from deterministic evidence;
- wake-up hints from authority to resume or extend work;
- delegated execution from the workflow that invoked it.

## Packages

| Package | Responsibility |
| --- | --- |
| `Penghou.Qingniao.Abstractions` | Stable identities, requests, lifecycle, budgets, capabilities, supervision, artifacts, evidence, and provider contracts. |
| `Penghou.Qingniao` | Validation, Siming-backed canonical semantic identity, provider resolution, in-memory execution proof, lifecycle policy, and supervision behavior. |

Neither package exposes MVC, HTTP, MCP, authentication, tenant, filesystem, or
Marang product configuration types. Neither package parses Fuwen source or owns
workflow graphs, loops, fan-out, compensation, or workflow persistence.

The first preview keeps its coordinator internal while the remaining
supervision, atomic acceptance, and durable-adapter semantics are proven. The
public packages currently provide the reviewed contracts and reusable policy,
identity, registry, and fingerprint components; they should not yet be treated
as a production scheduler.

## Ecosystem boundary

```text
Marang service --------> Penghou.Qingniao <-------- Guyabano
                              |
                              v
                    delegated execution actor

Fuwen   = typed workflow semantics and compilation
Zhinu   = durable workflow execution
Hongxian/Siming = session narrative and verifiable audit evidence
Baize   = bounded model execution
```

See the [architecture](docs/architecture.md), [roadmap](docs/roadmap.md), and
[extraction decision](docs/decisions/0015-qingniao-extraction-and-marang-service-boundary.md)
for the current design and implementation state.

## Development

The solution targets .NET 8 and .NET 10.

```powershell
dotnet build Penghou.Qingniao.slnx --configuration Release
dotnet test Penghou.Qingniao.slnx --configuration Release --no-build
dotnet pack Penghou.Qingniao.slnx --configuration Release --no-build --output artifacts
```

## License

MIT
