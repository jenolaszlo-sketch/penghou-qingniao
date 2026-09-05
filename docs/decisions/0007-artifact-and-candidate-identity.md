# ADR 0007: Artifact and Candidate Identity

## Status

Accepted for Batch 5A. This decision defines references and validation only;
it does not add artifact storage or provider execution.

## Decision

Marang carries immutable references to artifacts published by Zhinu (or an
equivalent host-owned artifact provider). A reference is owned by one
delegation, structural node, and node generation, and includes the provider,
repository, artifact identifier, kind, schema version, opaque location, and a
required content identity. Provider, repository, and location are resolver
inputs, not authorization or filesystem/URL authority.

Content identity is explicitly versioned. `sha256-bytes-v1` means SHA-256 of
the exact immutable bytes and requires 64 lowercase hexadecimal characters.
Unknown non-SHA contracts are preserved without interpretation. Marang does
not claim a JSON canonicalization contract; logical JSON identity is deferred
to the owning canonicalizer and Batch 5B. Existing external hash contracts
must never be reinterpreted.

Candidate revisions are identified by a Marang-owned `CandidateId`, delegation,
structural node, node generation, and positive revision. A candidate must
contain at least one artifact with matching ownership, and its collections are
snapshotted at construction. Aggregate result references use a strong
`DelegationResultId`, reference one candidate, and may include any distinct
same-delegation evidence artifacts; aggregate evidence need not be a subset of
the candidate's artifacts. No payloads or raw content are embedded.

The small in-memory publication proof is asynchronous and storage-ready. Its
key is `(DelegationId, CandidateId, Revision)`. The first immutable reference
is authoritative; an exact retry returns an idempotent receipt, while a
different reference for that key is rejected. A durable implementation must
enforce this atomically in its owning store.

## Consequences

Marang can attribute and aggregate outputs without duplicating Zhinu's
publication/storage semantics. Provider adapters remain responsible for
resolving opaque locations and verifying any provider-specific contract.
Persistence, evidence normalization, result publication, and JSON logical
hashing remain later batches.
