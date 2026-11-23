# ADR 0014: Publish DOCX and report through one new directory

Status: Accepted (Day 14)

## Context

Dry-run produces verified bytes and a report in memory. A product-facing apply
command must write both while keeping the original input and existing outputs
intact. Publishing two independently named final files creates an intermediate
state where one file is visible without its matching report.

## Decision

Add `DocxBuildPublisher` and CLI `apply --output-dir`. Only passed build results
can be published. Require a nonexistent destination with an existing trusted
parent. Create a unique sibling staging directory, write two fixed filenames
with create-new streams, flush, and verify staged hashes and inventory. Publish
using a single directory move. Reject existing destinations; offer no overwrite.

Return a publication receipt separate from Build Report. On pre-commit failure
or cancellation, delete only owned files and the empty staging directory; report
cleanup failures and retained paths. After commit, return success even if the
cancellation token changes. Do not delete an existing/competing final directory.

Use a narrow internal filesystem interface for deterministic write, flush,
commit and cleanup failure tests. Core test access is enabled through
InternalsVisibleTo; the injection constructor is not a public product API.

## Consequences

- Apply completes the single-text CLI MVP and produces a matching DOCX/report
  pair with explicit hashes and filesystem outcome.
- Output is a directory bundle, not arbitrary independent file destinations.
- Same-parent directory rename provides the commit boundary on supported local
  filesystems. Power-loss durability and hostile concurrent filesystem mutation
  are not promised. Parent reparse points are rejected as a conservative policy.
- Input is never overwritten; rollback does not require restoring its bytes.
  Unexpected process death may leave staging directories, and cleanup is not
  recursive. Residual paths are explicit on handled cleanup failure.
- A successful in-memory build can have failed delivery; clients must inspect
  publication status and process exit code, not Build Report alone.

See [apply contract](../cli-apply.md) for output policy, diagnostics and examples.
