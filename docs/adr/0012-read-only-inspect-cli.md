# ADR 0012: Read-only inspect CLI with bounded previews

Status: Accepted (Day 12)

## Context

The Core inspection pipeline and build reporting exist, but CLI users can only
request help and version. Planning needs a practical way to inspect a template,
identify paragraph scopes and understand rejection diagnostics.

## Decision

Add `inspect <file.docx>` with text output and opt-in JSON v1.0. The command calls
`DocxIndexInspector` using the existing default limits. It validates arguments
before opening input, preserves layer diagnostics, and returns distinct usage,
I/O, document-rejection and cancellation exit codes. Ctrl+C uses cancellation
tokens. No new dependency or Core mutation API is added.

Expose total structure counts and a paragraph page with stable IDs, cell
ownership, Run counts and explicit text previews. Default page size is 20,
maximum 1000; each preview is at most 200 UTF-16 units without splitting a
surrogate pair. Pagination limits output, not the inspection itself.

## Consequences

- CLI users can inspect real templates without modifying or extracting files.
- Scripts receive one JSON object on stdout for handled outcomes and can rely
  on process exit codes. Human-readable failures use stderr.
- Rejected documents expose no partial semantic summaries. Unreached stages
  remain explicit, and the full Core diagnostics are preserved where available.
- Text previews aid target selection but are not a full text export, matcher,
  approval or editability guarantee. Source-derived output uses escaped strings.
- Serialized plans, dry-run, apply and atomic output publication remain the
  Day 13/14 milestones. Inspection and build report profiles stay distinct.

See [CLI contract](../cli-inspect.md) for syntax and wire-format details.
