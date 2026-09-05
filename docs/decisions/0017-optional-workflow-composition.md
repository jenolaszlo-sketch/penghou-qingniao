# ADR 0017: Keep workflow composition optional and outside Qingniao

## Status

Accepted.

## Context

Marang and Guyabano need both one-shot delegated execution and, eventually,
programmable durable multi-step workflows. Penghou already assigns workflow
authoring and execution to Fuwen and Zhinu. Pulling either responsibility into
Qingniao would enlarge a bounded delegated-execution runtime into a competing
workflow engine and would make direct use unnecessarily heavy.

## Decision

Qingniao remains independently usable for one bounded delegation. Applications
may optionally compose the wider stack in this direction:

```text
Fuwen source
    -> validated immutable WorkflowPlan
    -> Zhinu durable workflow/activity execution
    -> Qingniao bounded delegation
    -> execution actor
```

Qingniao has no compile-time dependency on Fuwen or Zhinu. A Zhinu activity or
other application adapter constructs a normal Qingniao request and maps its
structured result and evidence back to the workflow. Qingniao never parses
Fuwen syntax and never interprets workflow topology.

Qingniao owns provider-level retry only when the failure is classified as safe
and retryable within the same semantic delegation. Zhinu owns activity and
workflow retry, restart, dependency invalidation, loops, fan-out, signals,
compensation, persistence, and recovery.

Cross-system correlation uses neutral opaque identifiers and immutable
references for workflow run, structural node, session, application request,
and parent delegation. Core contracts do not expose product-specific identity
types.

Marang owns MCP/HTTP hosting and composes Fuwen, Zhinu, and Qingniao. Guyabano
may use Qingniao directly or compose the same three capabilities without
depending on Marang.

## Package policy

Do not create `Penghou.Qingniao.Fuwen`: the normal executable seam is between
Fuwen and Zhinu. Do not create `Penghou.Qingniao.Zhinu` until Marang and
Guyabano demonstrate substantial identical glue. If justified later, that
optional adapter may contain reusable Zhinu activities, correlation mapping,
cancellation propagation, and result/evidence mapping, but not workflow
semantics or Qingniao core implementation.

## Consequences

- Direct Qingniao use remains lightweight.
- Fuwen, Zhinu, and Qingniao evolve as peer reusable capabilities with clear
  authority boundaries.
- Advanced plan execution waits for Fuwen's authoritative validation and
  immutable revision work plus Zhinu's durable external-operation fencing.
- The current internal coordinator is not renamed or exposed merely to match
  illustrative APIs in a composition proposal.
- Marang and Guyabano provide evidence before optional integration packages
  are extracted.

## Non-goals

Qingniao does not provide a workflow DSL, compiler, arbitrary DAG executor,
loops, workflow fan-out, compensation graphs, durable workflow timers,
workflow persistence, MCP/ASP.NET hosting, or a Codex-specific protocol.
