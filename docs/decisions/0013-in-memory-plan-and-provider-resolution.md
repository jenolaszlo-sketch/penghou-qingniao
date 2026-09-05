# ADR 0013: In-memory plan and provider resolution

## Status

Accepted for the Milestone 2 proof.

## Decision

The simple northbound `marang_delegate` operation resolves to Marang's stable
built-in `Implement/1` plan revision. A planless request is copied into an
immutable plan-bound request before acceptance or identity calculation, so it
uses the same v2 request fingerprint as an explicit `Implement/1` request.

The built-in definition is a sealed internal shape rather than a generic graph:
execute an agent, seal its candidate, run deterministic Test and independent
Review in parallel, evaluate them, optionally perform one semantic correction
with a new generation and revision, validate again, and publish the result.
Supervisor waits are coordinator policy and are not an unconditional preset
stage.

Fuwen references remain opaque. Marang routes every such reference through a
host verifier with the caller, workspace, identifier, revision, and canonical
fingerprint. The built-in catalog cannot authorize or interpret Fuwen plans.

Provider metadata is registered immutably under host authority. A revisioned
snapshot makes matching deterministic and returns an explicit no-match result.
Registration has provider-count, capability, attribute, and aggregate UTF-8
bounds. Equivalent replay returns the original registration revision; a changed
descriptor under the same identity is rejected.

External provider handles are captured immediately in a bounded in-memory
run/session-scoped registry. The stable pre-acceptance correlation can recover
a captured handle even when the provider task identifier was not returned to
the caller. Handle capture is idempotent, conflicting ownership is rejected,
and opaque handle values are not included in conflict diagnostics. Hosts must
treat snapshots as sensitive reconnect metadata and must not put credentials
inside provider handle values.

## Consequences

The next sub-batch can add an authorized provider-adapter catalog and a
deterministic coordinator without changing request identity or inventing a
workflow language. These registries are an in-memory proof, not durable stores;
Zhinu and Hongxian integrations replace their lifetime assumptions later.
