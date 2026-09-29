# Penghou.Qingniao: pending Penghou.Hufu integration

Status: **Pending integration; not implemented.** Recorded 2026-09-28.

Penghou.Hufu is the new reusable authority library and authority-store boundary.
It currently contains a buildable scaffold and design documents, with no public
authority API, enforcement implementation, or persistent authority store.
This note records future consumer work; it does not announce a package dependency,
a shipped security guarantee, or an additional current-release acceptance gate.

Hufu will own reusable grants, envelopes, authority requests and decisions,
attenuation, revocation, and durable authority records. Hosts retain identity,
policy, credentials, resource resolution, and approval surfaces. Zhinu retains
execution state and recovery; existing budget services retain accounting.

## Qingniao's planned integration

- Bind each delegation to its authenticated parent subject, node/item,
  admission/envelope version, execution fence, selected provider, and restricted grants.
- Validate both use and delegation permission; children receive only authority
  covered by their parent and permitted by host/provider policy.
- Carry typed missing-authority evidence to the parent/host. Supervisor messages
  and wake-up hints cannot create grants or increase budget.
- Invalidate new descendant operations on parent revocation, expiry, or
  supersession, and require explicit revalidation before resuming stale work.
- Admit only provider profiles that can actually enforce the required isolation,
  resource access, data release, and credential boundaries.

Qingniao retains delegated-execution lifecycle and supervision. Hufu owns shared
authority mechanics and records; the host selects policy and provider. Existing
bounded workspace/provider behavior is not a claim of Hufu enforcement. Shared
budget allocation remains with the existing ledger rather than copied counters.

## Completion evidence

A child cannot exceed parent rights or borrow sibling authority. Rejection happens
before provider submission; parent revocation blocks new child I/O. Real adapter
tests prove claimed confinement, and unsupported providers reject admission.

## Dependency and design home

Implementation depends on Hufu's reviewed contracts, durable-store semantics,
and a proven host/resource-broker enforcement path. Continue current correctness
work independently; do not add placeholder dependencies or infer security from
the existence of Hufu's scaffold.

Canonical design (links assume sibling checkouts):

- [Hufu architecture](../../Penghou.Hufu/docs/architecture.md)
- [Authority specification](../../Penghou.Hufu/docs/workflow-authority-spec.md)
- [Hufu implementation roadmap](../../Penghou.Hufu/docs/roadmap.md)
