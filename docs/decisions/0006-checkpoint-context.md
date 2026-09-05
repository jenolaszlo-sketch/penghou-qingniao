# ADR 0006: Bounded checkpoint re-entry context

Status: accepted for the provider-neutral Batch 4 core; Cangjie and Hetu
adapters remain future integration work.

## Decision

Re-entry is demand-driven. A host-authenticated supervisor requests an
explicit set of facets for one `DelegationId`, `SupervisorCheckpointId`, and
exact observable revision. The provider receives the host-supplied
`SupervisorIdentity` separately from the request and must remain cancellation
aware.

The provider resolves the authoritative waiting progress internally; callers
provide only the request and host-authenticated identity. The returned
`SupervisorContextPackage` is an immutable, checkpoint-bound envelope
containing bounded status/summary items, existing artifact references,
correlation identities, and optional provenance references. Every requested
facet has an explicit included, truncated, or omitted outcome and reason;
providers must never silently truncate data. Hard limits cover total item
count and total UTF-8 bytes of inline summaries, rather than CLR character
count.

`ContextProvenanceReference` is deliberately provider-neutral. Its factories
identify Cangjie context snapshots and associated Hetu repository/index-run
publications using provider, kind, identifier, optional revision, and optional
SHA-256 content identity (only where the primitive supplies one). These
references establish reproducibility and audit correlation, not authorization
or proof that a snapshot is valid. They do not embed stores, raw prompts,
full conversations, secrets, or repository contents.

## Consequences

- Marang has a stable context seam without taking direct Cangjie or Hetu
  dependencies or duplicating their storage semantics.
- A durable adapter must resolve and authorize the request, bind the response
  to the current waiting checkpoint atomically with its revision fence, and
  preserve omission/truncation metadata.
- Future adapters may map primitive-specific snapshots and graph revisions to
  these references; the provider-neutral contract does not prescribe ranking,
  retrieval, or storage.
