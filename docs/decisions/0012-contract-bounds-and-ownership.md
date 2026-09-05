# ADR 0012: Bounded legacy results and checkpoint authorization

## Status

Accepted for Batch 7 contract hardening.

## Decision

Marang validates ownership at every boundary where a delegation-scoped
artifact enters a result or supervisor context package. A terminal result may
contain only non-null, delegation-owned artifact references, with distinct
provider/repository/artifact identities and bounded collection size. A context
package applies the same ownership fence at construction and when it is
validated against a request, so a package cannot be relabeled to another
delegation by its enclosing checkpoint request.

The compatibility `DelegationEvidence` surface remains available, but its
changed-file and command lists are snapshotted and bounded, counters are
non-negative and bounded, and result concerns are normalized bounded prose.
Terminal result concern entries are distinct within one result.
`WorkspaceReference`, `WorkflowReference`, and `DelegationBudget` preserve the
same public constructor/property shapes while rejecting malformed values at
construction and applying finite upper bounds.

Checkpoint activation accepts at most 32 distinct host-authorized supervisors.
Reactivating an already-authorized supervisor remains idempotent; attempting
to add a 33rd principal is rejected with an explicit limit reason. The limit
is per active checkpoint and does not alter the global first-decision claim.

## Consequences

- Result and context stores can rely on immutable, delegation-scoped artifact
  references without reimplementing list, null, or duplicate checks.
- Legacy evidence remains usable by existing callers while resisting
  unbounded metadata and prose growth.
- Existing request validation remains compatible for valid identity and budget
  values, while malformed legacy records now fail at their own construction
  boundary instead of surviving as invalid immutable values.
- Authorization state has a finite memory and abuse bound, while the host can
  still authorize multiple competing supervisors within the cap.

## Non-goals

This ADR does not authorize artifact locations, resolve workspace paths,
change provider policy, or add durable persistence. Durable registries must
enforce the same ownership and authorization limits atomically in their own
stores.
