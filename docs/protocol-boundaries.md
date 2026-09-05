# Marang protocol boundaries

## Decision

Marang is the northbound service boundary. Qingniao is the provider-neutral
delegated-execution runtime behind it, not a general workflow runtime.

```text
supervising agent
      |
     MCP
      v
Marang ASP.NET Core service
  auth / authorization / DTO mapping / limits
      |
      v
Penghou.Qingniao
      |
      +--> A2A provider
      +--> process or SDK provider
      +--> Baize model provider
      +--> deterministic executor
```

- MCP is Marang's primary agent-facing protocol.
- HTTP is used where operations, health, or a real non-MCP client benefits.
- A2A, process, SDK, and Baize integrations are Qingniao provider adapters.
- Zhinu owns durable workflow execution around Qingniao activities.
- Protocol types never become Qingniao's lifecycle or outcome model.

## Northbound MCP

The intended bounded surface is:

- `marang_delegate`
- `marang_status`
- `marang_result`
- `marang_cancel`
- later, after authorization/fencing proof: `marang_wait`,
  `marang_intervene`, `marang_inspect`, and `marang_get_artifact`.

Marang authenticates the caller, derives a caller/tenant scope, authorizes the
workspace and requested disclosure, applies request/response/budget limits, and
maps transport DTOs to Qingniao contracts. An MCP request cannot introduce an
arbitrary provider endpoint, workspace path, credential, tool grant, or
publishing authority.

## Southbound providers

Qingniao selects only host-registered providers. Advertised capabilities are
discovery input, never authority. External work uses an idempotent start and a
durable handle so a lost acknowledgement can reconnect instead of launching a
duplicate task.

A2A is preferred where it provides real interoperability, but it is not
required. Process, SDK, HTTP, Baize, and deterministic adapters remain valid.
Provider-specific wire data stays outside Qingniao contracts and Marang MCP
DTOs.

## Artifact and workspace safety

Remote artifact references are untrusted. The service/provider boundary
enforces authorized schemes and hosts, bounded length, media type, hashes where
available, redirect/time limits, and safe materialization. Marang resolves
opaque workspace references under service policy; it never forwards an ambient
local path as though it were portable authority.

Large or sensitive artifacts return bounded metadata and an authorized
reference rather than being copied into model context. Credentials never enter
workflow artifacts, provider handle values, or diagnostic logs.

## Versioning

Marang versions its MCP/HTTP contract as a service. Qingniao versions its NuGet
API and semantic contracts independently. Provider protocol versions are pinned
and recorded per execution. `Marang.Client` and provider-specific packages are
created only after real consumers prove their boundaries.
