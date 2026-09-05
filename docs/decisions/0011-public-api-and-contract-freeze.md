# ADR 0011: Public API Baselines and Contract Freeze

> **Superseded package ownership:** the frozen pre-release surface is the
> extraction baseline for Penghou.Qingniao, not the long-term Marang service
> API. See ADR 0015.

Status: Accepted for Batch 7.

## Decision

The shipped Marang packages are `Marang.Abstractions` and `Marang`. Their
public API is tracked with the Roslyn Public API Analyzers using one
`PublicAPI.Shipped.txt` baseline and one `PublicAPI.Unshipped.txt` staging file
per package project. The baselines apply to every supported target framework;
the public API must remain compatible across `net8.0` and `net10.0`.

New public types and members are added to `PublicAPI.Unshipped.txt` during
development and moved to `PublicAPI.Shipped.txt` only when the package release
is intentionally frozen. A removed shipped API is represented by the analyzer's
`*REMOVED*` entry and requires an explicit compatibility decision. Analyzer
diagnostics remain warnings-as-errors; no API diagnostic is suppressed to hide
an incompatible change.

Both package projects generate XML documentation and enable SDK package
validation. XML documentation is part of the package review surface, while
package validation checks the produced multi-target package and does not replace
the source-level API baseline.

The baselines describe only the public package boundary. Non-packable hosting,
MCP, and test projects do not establish independently versioned public API
contracts.

## Review checklist

- Review additions in `PublicAPI.Unshipped.txt` with the corresponding contract
  change.
- Before a release, move reviewed entries into `PublicAPI.Shipped.txt` and
  leave `PublicAPI.Unshipped.txt` containing only its nullability header.
- Run Release build, tests, package validation, and package inspection for both
  target frameworks.
- Treat any removed or changed public signature as a contract decision, not as a
  baseline regeneration exercise.
