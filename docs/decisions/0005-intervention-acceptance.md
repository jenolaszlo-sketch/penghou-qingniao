# ADR 0005: Revision-fenced intervention acceptance

Status: accepted for the provider-neutral Batch 4 core; workflow mutation,
persistence, and transport remain future work.

## Decision

The host supplies a validated `SupervisorIdentity` outside model-controlled
payloads. A `SupervisorIntervention` targets one delegation, active
`SupervisorCheckpointId`, caller-scoped intervention key, exact expected
observable revision, and one value from the closed typed `SupervisorAction`
hierarchy. The hierarchy has separate contracts for response, approval,
rejection, retry, node/subgraph re-execution, constraint, executor profile,
   alternative selection, escalation, and cancellation. Arbitrary state mutation
   payloads and unknown action combinations are not representable. `Respond`
   is currently bounded normalized prose; artifact-bound responses and richer
   typed evidence belong to Batch 5.

Checkpoint activation is asynchronous and accepts a validated
`WaitingForSupervisor` `DelegationProgress` snapshot. It is a trusted-host
registration boundary: callers must not derive `SupervisorIdentity` from
untrusted request or model payloads. The in-memory registry
is the reference atomic decision:

1. A waiting checkpoint is registered with one or more host-authorized
   supervisors; later activation may refresh only the same checkpoint identity
   at the same or greater progress revision.
2. The first authorized intervention claims the checkpoint globally. After
   that claim, activation cannot refresh the checkpoint or add authorization.
   A later
   request from any supervisor, key, or content is rejected as
   `CheckpointAlreadyDecided`.
3. An exact replay by the original supervisor, intervention key, and canonical
   fingerprint returns the original receipt with `IsNew=false`.
4. A new intervention is rejected unless the supervisor is authorized, the
   checkpoint is active and still waiting, the delegation matches, and the
   expected revision exactly equals the current fence.
5. Acceptance records a correlation-rich immutable receipt only. It does not
   execute an action or mutate workflow state. Durable stores must persist the
   accepted decision before downstream application; recovery reapplies the
   decision when application fails or is ambiguous.

Intervention identity is compact deterministic UTF-8 JSON hashed with SHA-256
under explicit `v1`. Action-specific properties are emitted in a fixed shape;
human prose is NFC-normalized, line-ending-normalized, bounded, and trimmed.
Identity values remain exact canonical host/provider values. `WakeHint` uses
`ExpiresAt` and bounded prose; it is non-authorizing information and cannot
change state, budget, authority, or results.

## Consequences and non-goals

- Durable stores must couple revision observation, checkpoint claim, and
  idempotent receipt creation atomically in their own store.
- The global claim prevents two authorized supervisors from independently
  deciding the same checkpoint; a later recovery operation reuses the durable
  accepted decision rather than creating a second one.
- Intervention execution, Cangjie/Hetu context payloads, persistence, MCP,
  provider integration, and policy selection beyond typed action validation are
  deliberately outside this batch.
