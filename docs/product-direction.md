# Qingniao product direction

## Product boundary

Qingniao is a reusable delegated-execution runtime, not the remotely accessible
product. Marang is the ASP.NET Core MVC service with the MCP interface for Codex
and other supervising agents. Marang owns authentication, authorization,
transport, configuration, operations, diagnostics, and service lifecycle.

Qingniao succeeds when one bounded delegation is identifiable, supervised,
recoverable, provider-neutral, and evidenced independently of whether it was
initiated directly by an application, through Marang, or by a Zhinu activity.

## Reusable capability

Penghou.Qingniao is the reusable delegated-execution runtime used by Marang and
other applications. It owns provider-neutral request/result contracts, identity, lifecycle,
routing, budgets, external-operation reconnect, evidence, supervision, bounded
repair, and aggregation. Guyabano can use Qingniao directly without running
Marang.

Qingniao is not a general workflow runtime. Fuwen owns compiled workflow
semantics and Zhinu owns durable execution. Hongxian and Siming retain temporal
session/audit evidence. Baize, Cangjie, and Hetu retain their existing model,
memory, and code-graph responsibilities.

## Initial experience

The first Marang experience remains deliberately small:

```text
MCP submit
  -> authenticated and authorized service request
  -> Qingniao delegation runtime
  -> bounded provider execution and evidence
  -> status / optional supervisor interaction
  -> immutable result
```

The simple `marang_delegate` MCP operation maps to Qingniao's built-in
`Implement/1` preset. Advanced Fuwen workflows remain host-validated and are
not interpreted by Qingniao.

## Direct and remote consumers

- Guyabano first exercises Qingniao as an embedded library.
- Codex exercises the same behavior remotely through Marang's MCP surface.
- A typed `Marang.Client` is deferred until a real remote non-MCP consumer
  demonstrates the need.

## Non-goals

Qingniao is not a workflow engine, provider SDK, model, session ledger, memory,
code graph, MVC/MCP service, or replacement for Zhinu. Marang is not the owner
of Qingniao's reusable execution semantics. Neither component grants tools,
workspaces, credentials, budgets, or publishing authority based on remote or
model-generated claims.
