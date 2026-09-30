# Codex CLI protocol support

Which `codex exec --json` behavior this adapter supports, how it is verified,
and what remains unverified. The adapter is tolerant by construction: unknown
events are counted, never interpreted, and completion is never inferred from
process exit alone.

## Supported versions

| CLI version | Evidence | Status |
| --- | --- | --- |
| `codex-cli 0.157.0` | Live proof 2026-09-26: one success turn and one interrupt/resume turn against a disposable workspace-write directory; first stdout line `thread.started` with the thread id; `exec resume <thread> --json` argv with no prompt text; `turn.completed` usage keys `input_tokens`, `cached_input_tokens`, `output_tokens`. | Supported; not exhaustively characterized |

No other version is claimed. "Supported" here means the mapped behaviors below
were observed once and are reproduced by fixtures; it is not a compatibility
guarantee for every flag combination or CLI schema change.

## Event mapping

Modeled stdout events:

| `type` | Modeled as | Adapter effect |
| --- | --- | --- |
| `thread.started` | `ThreadStarted` | Captures the durable thread id; a changed id mid-operation fails closed. |
| `turn.started` | `TurnStarted` | Progress only. |
| `turn.completed` | `TurnCompleted` | The only verified-completion evidence; last-wins usage is parsed. |
| `error` | `ErrorEvent` | Classified failure (usage-limit vs remote). |
| `turn.failed` | `TurnFailed` | Classified failure. |
| anything else | `Unknown` | Counted; never converted into success. |

Event shapes and versions are also pinned by `CodexJsonlEventTests` and by the
reconstructed `0.157.0` fixture in `CodexExecAdapterTests`
(`Cli0157SuccessWithUsage`).

## Verified completion

A clean process exit is **not** success. Success requires a `turn.completed`
event. A zero exit without one fails closed as `codex.incomplete-evidence`
(`Remote`), so chatter-only, truncated, or infra-level runs cannot masquerade
as completed work. An exit code alone never grants completion.

## Usage

`turn.completed` usage is parsed from a nested `usage` object (observed) or
flat event fields (accepted), keys `input_tokens`, `cached_input_tokens`,
`output_tokens`. Missing, non-numeric, or negative values stay `null`
(unknown) rather than being zero-filled. Usage is frozen into the terminal
receipt; the set of schema versions actually observed is limited to 0.157.0.

## Frozen receipts and retention

Terminal time, state, normalized output, exit code, usage, thread id and drain
counters are frozen once when the operation terminalizes, so repeated result
reads are byte-identical. The result read acknowledges the receipt; eviction
past the 128-operation tracking capacity removes acknowledged receipts before
unread ones and counts unread evictions (`EvictedUnreadReceipts`) instead of
silently discarding them.

## Bounded start

A start is bounded by the request deadline when the host supplies one: if the
child does not report a `thread.started` identity before the deadline, the
adapter kills it and fails closed as `codex.start-timeout` rather than parking
the start forever. Without a deadline the adapter waits indefinitely for a
thread identity or process exit, so hosts should supply a deadline (the input
materializer can do so).

## Recovery

- **Cold resume** (adapter never tracked the thread): a fresh observer process
  runs `codex exec resume <thread> --json` with no prompt, and a
  `thread.started` matching the same id is expected; no new semantic work is
  submitted.
- **Response loss**: a lost resume response reconnects through the same
  attempt; a captured rotated handle is durable acceptance evidence and replay
  observes it instead of resuming again.
- **Missing or ephemeral session**: no verified CLI missing-session shape
  exists yet, so this is not special-cased. If `resume` errors, the observer
  fails closed (classified remote error) rather than silently recovering or
  starting fresh work. `codex exec --ephemeral` sessions are not resumable by
  construction and must be treated as non-recoverable by the host.
- **Still-live original process**: a resume launched while the original
  process is alive relies on the CLI reconciling the same thread; duplicate
  semantic work is never emitted by the adapter.

## Unverified / open

- Ephemeral (`--ephemeral`) session identity and exact resume rejection shape.
- Exact turn/attempt semantics for resume against a still-live original
  process (only the interrupt/resume turn was recorded).
- Whether thread identity alone identifies the exact accepted turn across all
  supported flag combinations.
- SDK/app-server comparison: decided 2026-09-30 in
  [ADR 0019](decisions/0019-codex-appserver-comparison.md) — stay on
  `exec --json`; revisit only on a concrete gap plus a maintained .NET
  transport.

A paid/account live run to characterize these is an explicitly scoped task and
is not part of default unit/CI coverage.
