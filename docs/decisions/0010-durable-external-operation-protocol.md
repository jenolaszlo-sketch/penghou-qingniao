# ADR 0010: Durable External-Operation Protocol Contracts

## Status

Accepted for Batch 6B. These contracts define the provider-neutral seam; they
do not implement A2A, process, SDK, transport, or durable-store adapters.

## Decision

Marang represents one external execution with an immutable
`ExternalOperationCorrelation` containing the delegation, workflow run and
execution epoch, structural node, node generation, execution attempt, stable
external agent identity, and (once known) provider-issued task identity.
Semantic re-execution must create a new node generation and attempt. A retry
or reconnect keeps the same generation and attempt identity.

Starting work requires an `ExternalOperationStartIdentity` containing a
caller/provider idempotency key and semantic input fingerprint. Providers must
support the following durable operations:

```text
Start(request, handle capture sink) -> start receipt + handle
Observe(handle)                     -> monotonic state revision
GetResult(handle)                    -> normalized immutable result
Cancel(handle, cancellation key)    -> confirmed disposition
Resume(handle, resume key)           -> continued resume receipt
```

The handle capture sink is mandatory at the provider seam. The provider calls
it immediately after learning a reconnectable handle, before waiting for the
final start response or result. The capture is idempotent and must be persisted
by the host. A lost acknowledgement therefore causes observation or resumption
of the known handle, never an unkeyed second start. A handle is not considered
durably reconnectable until it carries an external task identity; transient
connection identifiers are not sufficient.

External failures retain a precise classification: transport, remote,
cancellation, timeout, rejection, or result validation. Transport loss is
represented by non-terminal `Unknown` state because the remote operation may
still be running; it must never be reported as terminal `Failed`. State-
specific validation prevents adapters from collapsing a remote rejection, a
deadline, or an invalid result into one generic error. Cancellation is a
request; its receipt records whether it was merely requested, confirmed,
already terminal, rejected, or unknown, with a rejected cancellation staying
non-terminal and carrying cancellation evidence.

Cancellation and resume receipts retain their caller idempotency keys. A
resume receipt also retains both the previous and resulting handles and must
preserve the exact operation correlation, so a repeated resume cannot be
mistaken for a new execution.

The semantic start fingerprint is computed outside Marang. Its exact input
envelope is the versioned `external-start-semantics-v1` shape containing the
delegation, selected external agent, capability, delegation-scoped input
artifact references, optional budget hint, and optional deadline.
`ExternalOperationSemanticInputEnvelope` exposes this shape and
`IExternalOperationSemanticFingerprintVerifier` is the verification seam;
Siming owns canonical JSON and SHA-256 serialization, so Marang does not
duplicate that authority. Hosts must verify the request through this seam
before calling `StartAsync`. Changed semantic inputs must therefore fail an
adapter's verifier even when the transport and execution correlation remain
unchanged.

External result artifacts are owned by the exact correlation node and
generation. Start/resume correction inputs remain delegation-scoped inputs.

Provider/model/tool/usage provenance is represented by versioned snapshots.
Snapshots contain bounded identities, capabilities, and usage measurements;
they do not contain credentials, authorization material, raw prompts,
transcripts, or large provider payloads. Known dangerous key names are rejected
as a guardrail, but values are not scanned for secrets: adapters and the
applicable redaction/retention policy remain authoritative. Such data follows
the artifact policy and is referenced rather than copied into operation state.

## Consequences

- Zhinu or another durable host can replay a workflow and re-observe a known
  external task without duplicating work or cost.
- Provider-specific wire models remain outside Marang's public contracts.
- Stable correlation supports audit and policy without requiring transcript
  retention.
- Providers must implement idempotency and early handle persistence; a
  blocking `ExecuteAsync` adapter is insufficient.
- Resume is explicit continuation of an accepted non-terminal operation; it is
  not permission to reopen a terminal result.

## Non-goals

This ADR does not define provider routing, authentication, credential storage,
callback endpoints, artifact materialization, transport retries, or workflow
replay. Those remain responsibilities of the host and concrete adapters.
