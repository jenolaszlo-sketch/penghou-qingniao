# ADR 0008: Normalized Evidence and Review Independence

## Status

Accepted for Batch 5D. These contracts describe immutable evidence; they do
not start workers, persist evidence, or decide whether a result is acceptable.

## Decision

Marang exposes `WorkerInvocationEvidence` as the normalized receipt for one
agent, model, or deterministic invocation. It is attributed to the exact
delegation, structural node, and node generation, and carries the provider
attempt/handle, disposition, time bounds, requested provider/model hints,
resolved model, profile/capability names, tool capabilities, input/output
artifact references, optional candidate revision, usage, and provider data.

The execution category, disposition, capability names, and extension keys are
versioned lowercase names rather than a closed provider enum. This keeps the
common evidence surface useful for Codex, Baize, deterministic hosts, and
future providers. Provider-specific details are bounded and copied into
extension maps; large payloads and transcripts must be published as immutable
artifacts and referenced instead. `ProviderData` and `Usage` must never carry
credentials, access tokens, secret material, or other authentication data.
They are not a covert transcript or payload channel; sensitive or large data
must follow the applicable retention/redaction policy as an artifact reference.

`ValidationEvidence` may only wrap a deterministic invocation. `ReviewEvidence`
may wrap an agent or model invocation and must identify its candidate subject
and reviewer identity. `ReviewIndependenceEvidence` records the observable
dimensions (invocation, context, profile, model, and provider) as
`Same`, `Different`, `Unknown`, or `NotApplicable`. It is evidence rather than
an authorization decision; host policy determines which dimensions are
required. The invocation identity is checked for internal consistency so a
review cannot claim a different invocation while naming the same one.

The existing coarse `DelegationEvidence` remains compatible for the current
vertical slice. `EvidenceBundle` is the bounded publication surface for the
richer records. Candidate references snapshot an optional bundle and validate
that every attached invocation belongs to the candidate's delegation,
structural node, and node generation; explicit candidate subjects must match
the published candidate identity. Aggregate result references snapshot a
bundle at delegation scope, allowing evidence from multiple node generations
while rejecting other delegations. The terminal `DelegationResult`
may carry the same bundle, and terminal replay equality compares it
semantically, so evidence cannot be added, removed, or rewritten after
publication. Bundle records remain references and bounded metadata: transcripts
and provider payloads are still separate retained artifacts.

## Security and integrity consequences

- Artifact references are ownership-checked and deduplicated; no payload is
  trusted merely because a worker claims it exists.
- Invocation input artifacts may reference any artifact in the same
  delegation, while invocation output artifacts must belong to the exact
  structural node and node generation that produced the invocation.
- When an invocation carries an external-operation correlation, Marang checks
  the exact delegation, node, generation, attempt id, and provider against the
  provider attempt reference; an arbitrary attempt cannot be combined with a
  different invocation owner.
- Candidate references must belong to the same delegation, node, and
  generation as the invocation.
- Collections and extension values are bounded and snapshotted at
  construction, preventing later caller mutation and accidental unbounded
  evidence growth.
- Provider data and usage are informational until a provider-specific
  validator establishes their meaning. They must not contain credentials,
  secrets, or large payloads. A model claim cannot replace a deterministic
  validation receipt.
- Review independence is auditable but not inferred from a numeric confidence
  value or a single boolean.

## Non-goals

This ADR does not define provider routing, budgets, schema migration,
evidence storage, transcript retention, cryptographic canonicalization, or the
policy that accepts/rejects a candidate. Those remain later batches or the
owning primitive's responsibility.
