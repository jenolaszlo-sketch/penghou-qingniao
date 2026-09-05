# ADR 0015: Extract Qingniao and make Marang the service boundary

## Status

Accepted.

## Context

The first Marang implementation correctly discovered reusable delegation
semantics—identity, lifecycle, budgets, provider selection, external-operation
reconnect, supervision, evidence, and result aggregation—but placed them under
product-owned `Marang` package names. Guyabano and future applications need the
same capability without depending on another product's internal core.

## Decision

The reusable capability moves to:

```text
Penghou.Qingniao.Abstractions
Penghou.Qingniao
```

Qingniao is a delegated-execution runtime, not a general workflow runtime. It
owns delegation to an execution actor and the lifecycle and evidence
surrounding that delegation. It remains provider-neutral and contains
no MVC, HTTP, MCP, authentication, tenant, or Marang configuration types.

Marang becomes a non-packable ASP.NET Core MVC/MCP service. It owns remote
transport, caller authentication and authorization, request limits,
configuration, composition, operations, diagnostics, and service lifecycle.

The dependency direction is:

```text
Marang.Server ------> Penghou.Qingniao <------ Guyabano
                           |
                           v
                  external execution actor
```

Zhinu remains the workflow executor. Qingniao may be invoked by a Zhinu
activity, but it does not compile or durably schedule workflows. Fuwen owns
typed workflow semantics. Hongxian and Siming own session/audit evidence, not
execution truth.

## Migration policy

The published `Marang` and `Marang.Abstractions` artifacts are pre-release and
have no external consumers. They are superseded rather than maintained as
compatibility packages. The current source and tests will be renamed and moved
without changing their reviewed semantics, after which new work continues in
Qingniao.

ADRs 0001–0014 remain the design record for that delegated-execution runtime.
Their semantic decisions migrate to Qingniao; statements assigning runtime ownership or package
names to Marang are superseded by this ADR. They are not rewritten as though
the original decision had used the new names.

## Consequences

- Marang can evolve as an operable remote product without turning service
  concerns into reusable contracts.
- Guyabano can embed Qingniao directly.
- A future `Marang.Client` can support remote non-MCP consumers when proven.
- The source refactor is intentionally breaking; package and namespace changes
  require new Qingniao preview packages rather than a Marang version bump.
- Siming canonical fingerprint integration and the initial coordinator belong
  in Qingniao, then Marang hosts that coordinator.
