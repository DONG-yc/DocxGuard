# ADR 0011: Versioned build evidence paired with immutable DOCX

Status: Accepted (Day 11)

## Context

Execution and rebuilding expose individual results, but a caller would otherwise
need to join unrelated objects to produce an audit report. Joining by plan ID
alone can accidentally mix evidence from different packages. CLI consumers also
need a stable representation of failures and stages that were never reached.

## Decision

Add a Core `DocxBuildPipeline` taking a stream and typed plan, capturing one
bounded private snapshot, and running the existing inspection, executor and
rebuilder. Publish a `DocxBuildResult` containing a versioned `BuildReport` and
verified immutable DOCX, or a failure report without a candidate artifact.
Construction is internal; no public report factory accepts arbitrary joined
evidence. Existing low-level APIs remain available and unchanged.

The report records source/output hashes, selected Part, operation/range evidence,
precondition and observed output paragraph hashes, check statuses, diagnostics,
complete entry comparisons, engine version and limits. JSON v1.0 uses string
enums and null unknown values. Text is summarized by hashes and lengths.
No new dependency is required.

## Consequences

- Additional snapshots and inspections cost memory and CPU but preserve existing
  trust boundaries. All are bounded by existing limits.
- A single returned pair avoids independently joining artifacts and reports.
- Failed and unexecuted gates are distinguishable, including failures before
  inspection. Source streams remain open and retain position.
- No filesystem writes occur; retaining the source is the rollback strategy
  until the CLI publication milestone.
- Reports are evidence, not signatures, approval records or deserializable plans.
  Planning no-match/ambiguity remains outside this build entry point.
- Full source/version binding of serialized plans, dangerous-feature policy and
  visual compatibility remain separate milestones.

See [the report contract](../build-report.md) for JSON fields and failure rules.
