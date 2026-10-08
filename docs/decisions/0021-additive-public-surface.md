# ADR 0021: Additive public surface — prefer body properties over optional positional record parameters

## Status

Accepted. Release guidance; refines [ADR 0011](0011-public-api-and-contract-freeze.md)
without changing any frozen contract.

## Context

Optional positional record parameters are binary-breaking additions. Adding an
optional parameter to a public record constructor changes the runtime method
signature, so callers compiled against the shorter constructor fail at load
with `MissingMethodException` even though the source change looks compatible.
The Roslyn public-API baselines track the source surface and do not catch this.

## Evidence (independent, two repos)

- Hufu `0.1.0-preview.4` was a partial release caused by a positional,
  breaking surface; the preview.5 refactor made the additions additive.
- Qingniao `DelegationRequest` gained optional positional `parentGrantId` /
  `requestedAuthority` parameters. Marang service code compiled against
  `Penghou.Qingniao 0.1.0-preview.2` threw `MissingMethodException` at runtime
  when run against `0.1.0-preview.4`, fixed only by recompiling all consumers
  against the new package.

The lesson is therefore not an API-style preference. It has now been paid for
twice, independently.

## Decision

Public extensible request and contract records must prefer additive body
properties (`{ get; init; }`) over optional positional constructor parameters.

- A new optional datum on a public record is a new settable body property,
  omitted (null/default) when absent. Existing constructors keep their exact
  runtime signatures.
- Optional positional parameters on public record constructors are forbidden
  for extensible contracts. (Non-public/internal construction is unaffected.)
- The authority preflight seam ([ADR 0020](0020-host-owned-authority-preflight.md))
  already follows this rule: `ExternalOperationStartRequest.ExecutionAttachment`
  and `DelegationExecutionSnapshot.ExecutionAttachment` are additive body
  properties, and the semantic fingerprint is unchanged by their presence.

## Consequences

- Future previews stay source- *and* binary-compatible for additive changes;
  consumers upgrade by version bump, not by recompilation lockstep.
- Any proposal to extend a public record constructor must instead propose the
  body property and update the corresponding `PublicAPI.Unshipped.txt` entry.
