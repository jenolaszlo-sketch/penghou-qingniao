# Qingniao ideas to revisit

These suggestions from the 2026-09-29 architecture review need a concrete
consumer or measured gap before implementation.

- **Generic CLI subprocess base class.** The Codex adapter has provider-specific
  event and handle semantics. Extract shared framing or lifecycle helpers only
  after a second provider demonstrates the same behavior; an inheritance base
  would prematurely fix the abstraction.
- **Standalone Qingniao SQLite delegation store.** Zhinu already owns durable
  workflow state in the planned integration. Revisit a separate store only if a
  direct-delegation host needs restart recovery without Zhinu and can define
  one clear authority for handles, interventions, and terminal results.
- **Fluent runtime builder.** A second construction API would duplicate the
  current constructor. Reconsider if DI registration and real application use
  still leave repetitive or error-prone setup.
- **Qingniao operator CLI.** A command-line inspection tool needs durable
  status queries, authorization, and a real operator workflow first. Use the
  planned host/MCP query surface before adding another interface.
- **Sub-delegation trees.** Unbounded recursive delegation conflicts with the
  current bounded, single-activity model. Revisit only if measured cancellation,
  cost, and traceability gaps cannot be handled by Zhinu workflow structure or
  a provider's own opaque subagents.
- **OS-specific process containers.** AppContainers and namespaces could add
  defense in depth, but require platform-specific lifecycle and capability
  tests. Keep dedicated candidate workspaces and the configured sandbox as the
  current contract; revisit when a deployment has a concrete isolation gap.
