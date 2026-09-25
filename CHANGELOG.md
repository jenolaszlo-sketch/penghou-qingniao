# Changelog

Notable changes to Penghou.Qingniao are recorded here. The project follows
[Semantic Versioning](https://semver.org/) for package versions. Preview
releases may still revise public contracts; every public API is tracked in
`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` per package.

## Unreleased

- New `Penghou.Qingniao.Codex` package (not yet published): process-isolated
  `codex exec --json` execution adapter with approved workspaces, explicit
  sandbox, bounded output, early thread capture, and resume. Proven against
  recorded CLI transcripts; the live bounded-execution and resume proofs wait
  on model quota.

## 0.1.0-preview.2

- Add the public `DelegationRuntime` facade over the in-memory delegation
  coordinator: the host entry point for delegate, pump, resume, intervene,
  checkpoint context, and terminal result reads.
- Guard publishing against orphan symbol pushes.

## 0.1.0-preview.1

- Initial extraction of the provider-neutral delegated-execution runtime from
  Marang: stable identities (`DelegationId`, `RequestKey`, `NodeGeneration`),
  lifecycle, budgets, capabilities, supervision, artifacts, evidence, and
  provider contracts (`Penghou.Qingniao.Abstractions`).
- In-memory reference coordinator proving deterministic acceptance,
  execution, handle reconciliation, cancellation, waiting and resume, bounded
  supervisor context, revision-fenced interventions, candidate publication,
  concurrent evaluation, bounded semantic correction, budget enforcement, and
  the terminal outcome matrix (Milestones 2.1 through 2.8).
- Frozen contract surface with public API baselines; first NuGet publication.
