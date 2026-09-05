# ADR 0002: Request identity and idempotent acceptance

## Decision

Marang fingerprints normalized request *content* with a versioned contract:

```text
v1:<lowercase SHA-256 of compact UTF-8 canonical JSON>
```

The canonical JSON contains the objective, workspace reference, acceptance
criteria, constraints, budget, and strategy. It does not contain the
caller-scoped `RequestKey` or host-authenticated caller scope. Those values
form the separate identity key `(caller scope, request key)`.

The caller scope and request key are not silently normalized: they must already
be canonical and are compared ordinally. A host must resolve the caller scope
from authentication; a request must never be allowed to select its own scope.

Request-content text values are Unicode NFC normalized, CRLF/CR is converted to
LF, and only leading/trailing whitespace is trimmed. Internal whitespace,
duplicate values, and list order remain meaningful. Opaque request identity and
workspace authority values must already be NFC-normalized, have no surrounding
whitespace or line breaks, and are compared byte-for-byte/ordinal. Marang
never silently normalizes these values because that could merge distinct
provider references or principals.

Budget duration is serialized as invariant integer ticks and enum values as
invariant integer values. `Utf8JsonWriter` provides compact deterministic
escaping; no culture-sensitive serializer or CLR type metadata participates in
the hash.

The version is part of the fingerprint so a future canonicalization change can
introduce `v2` without reinterpreting existing identities.

Normalized request content defines semantic identity. Once a caller-scoped key
has been accepted, the first accepted content and its generated delegation
identity are authoritative for execution. A duplicate submission does not
replace, merge, or update that content; it only recovers the existing identity.

## Acceptance boundary

`InMemoryDelegationAcceptanceRegistry.AcceptAsync` atomically binds the caller-scoped key
to the first normalized content fingerprint and generated delegation identity.
An identical retry returns the same identity and creates no second acceptance.
Reuse of a key for different content raises
`DelegationRequestKeyConflictException` before any provider or cost-bearing
operation can start. Caller scope is supplied by the host authentication
boundary and is never inferred from request content.

This registry is deliberately not a workflow engine or durable store. It proves
the contract needed by later durable implementations; its compare-and-bind
operation can be replaced by an atomic database conditional insert.

Requests are bounded to 256 list values, 4,096 characters per value, and
1,048,576 characters of total request text. These limits apply before canonical
hashing to prevent an identity endpoint from becoming an unbounded memory or
CPU sink. Public progress, evidence, and result collections are also copied at
construction so callers cannot mutate published state through an input list.

## Consequences

- Callers can safely retry after a lost response.
- Two callers may use the same request key without colliding.
- Request content can be audited and compared independently of transport
  identity.
- A future API must preserve the split between authenticated caller scope and
  caller-controlled request fields.
