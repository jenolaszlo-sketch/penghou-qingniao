# ADR 0019: Codex CLI vs SDK/app-server bridge comparison (M3 follow-up)

## Status

Accepted 2026-09-30. Decision: **stay on `codex exec --json` subprocess**;
no app-server adapter. Revisit only on a concrete capability gap plus a
maintained .NET transport. This closes the M3 "SDK/app-server comparison"
follow-up; it is not an M4 gate.

## Context

The `Penghou.Qingniao.Codex` adapter spawns `codex exec --json` per operation
(proven 2026-09-26 on codex-cli 0.157.0; hardened QH-01–QH-06). The roadmap
asked for a comparison against an SDK/app-server bridge across cancellation,
progress, approvals, and long-lived support. Research date 2026-09-30;
upstream moves fast, so versions below are evidence, not a stable survey.

## Options surveyed

- **A. Current: `codex exec --json` child process.** Stable CLI surface;
  `thread.started` durable handle; `exec resume <thread>` reconnect;
  `turn.completed` verified completion; explicit sandbox/approval CLI flags.
- **B. Official SDKs: TypeScript `@openai/codex-sdk` (Node 18+) and Python
  `openai-codex` (stable).** Both drive the local app-server over JSON-RPC.
  **No official .NET SDK exists.** Adopting an SDK would mean shelling to
  Node/Python (a second runtime plus IPC beside the CLI process) or
  reimplementing the client — neither is a bridge, both are new stacks.
- **C. Direct app-server client in C#.** `codex app-server` speaks JSON-RPC
  2.0 over stdio (default), Unix socket, or WebSocket (experimental); it
  powers the VS Code extension. Primitives: threads, turns, sandbox presets,
  server-initiated approval requests, streamed agent events, thread resume by
  ID. Schema can be generated from the CLI itself
  (`app-server generate-ts` / `generate-json-schema`). Note `codex mcp-server`
  was removed: app-server is the sanctioned deep-integration path, exec the
  sanctioned automation path.
- **D. Community bridges (Go, Elixir).** Third-party, non-.NET; useful as
  protocol references, not as dependencies.

## Comparison

| Dimension | A. `exec --json` (current) | C. App-server in C# |
| --- | --- | --- |
| Cancellation | Kill child process: coarse but proven, no protocol needed. | Protocol turn-cancel: finer, no kill. Improvement, not a gap. |
| Progress | JSONL event counting + non-authorizing wake hints. Bounded and sufficient today. | Streamed turn events: richer. Improvement, not a gap. |
| Approvals | Fixed at spawn via sandbox/approval flags; approval interactivity is out of scope by construction. | **Server-initiated approval requests the client answers** — the one structural advantage. Maps directly onto `WaitingForSupervisor` + fenced `Approve`/`Reject`. |
| Long-lived support | One process per operation: spawn cost per delegation, but failures are isolated per operation. | One server, many threads: amortized spawn, but the server is a stateful pet — its death kills all in-flight turns at once. |
| Crash recovery fit | Matches Qingniao's model: disposable processes, reconnect by durable thread handle (`exec resume`). | Reconnect exists (resume by thread ID) but now depends on server liveness plus CLI state. |
| Transport maturity | Stable `exec` surface, fixture-pinned. | WebSocket experimental; stdio/Unix-socket viable but schema drifts with CLI releases (pin + regenerate per CLI version). |
| .NET cost | Zero new dependencies; ~ executor + tests already proven. | New JSON-RPC client, generated schema bindings, version-tracking harness, auth handling, server-lifetime management — plus re-proof of every QH-01–QH-06 fail-closed guarantee on the new transport. |
| Authorization surface | Small: argv allow-list, no bypass flags, workspace containment. | Larger: answering an approval request "yes" is a new privileged act that must itself be a fenced supervisor decision. Implementable in Qingniao's model, but new code with real blast radius. |

## Decision

Stay on option A. The app-server's only structural win (interactive
approvals) has no demanding consumer today: Qingniao delegations run
non-interactive bounded work with approvals fixed at spawn, and Marang's
supervision happens at delegation checkpoints, not inside a model turn. The
long-lived argument cuts against Qingniao's failure model, and the .NET cost
is a new transport stack with no official SDK to build on.

Revisit when **both** hold: (1) a concrete gap exec cannot fill —
in-turn approvals, progress wake hints cannot express, or measured
spawn-cost pressure; (2) a maintained .NET transport or two consumers
demonstrating identical adapter code (ADR 0017 rule). A future package
would be `Penghou.Qingniao.Codex.AppServer`, reusing the coordinator,
receipt, and supervision contracts unchanged.

## Consequences

- No app-server code, dependencies, or schema tracking are added.
- The designed future seam is recorded here, not lost: app-server approval
  request ↔ `WaitingForSupervisor` + fenced intervention; thread ID ↔
  durable handle; turn cancel ↔ intent-first cancellation.
- M3 comparison item is complete; M4 and QH-08 are unaffected.
