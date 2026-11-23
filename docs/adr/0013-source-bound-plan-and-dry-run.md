# ADR 0013: Source-bound serialized plans and real dry-run

Status: Accepted (Day 13)

## Context

CLI inspect exposes paragraph identities, and the Core can build verified DOCX
artifacts. The existing typed plan binds paragraph text and Run slices but does
not bind the complete source package. Serializing it directly would allow an
unchanged paragraph in a different package to satisfy the same preconditions.

## Decision

Introduce an immutable `BoundPatchPlan` wrapper with schema/profile, source ZIP
SHA-256, main Part, conformance, request and full typed plan. Parsing is bounded
and duplicate-aware. Regenerate from the inspected source and compare all fields
instead of trusting serialized operation objects. Keep the existing typed API
for compatibility; use a separate `BuildBound` entry point that checks the hash
again against the private build snapshot before execution.

CLI `plan` emits the wrapper as JSON for review and explicit shell persistence.
CLI `dry-run` loads that format and performs the complete in-memory build. It
returns a command result containing the Day 11 Build Report, never DOCX bytes
or a saved file. No-match and ambiguity have exit code 5 and bounded candidate
summaries. Document/protocol/build rejection has exit code 4.

## Consequences

- Media-only source changes and source changes after plan loading fail closed.
- Whitespace/property-order changes in JSON are harmless; duplicate, unknown,
  inconsistent, malformed or unsupported fields are rejected.
- Source binding, schema compatibility and regenerated intent are verified;
  this is not a cryptographic user-approval protocol.
- Plans include full target paragraph text, so they require document-data care.
- Unique location does not imply editable Run content; full dry-run is the
  execution check. Existing limits and dangerous-feature/visual boundaries remain.
- Day 14 remains responsible for apply, no-overwrite output publication and
  cleanup of incomplete disk artifacts.

See [plan and dry-run contract](../cli-plan-and-dry-run.md).
