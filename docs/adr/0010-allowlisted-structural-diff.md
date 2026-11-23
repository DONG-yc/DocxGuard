# ADR 0010: Independently verify the package and main XML change allowlist

- Status: Accepted
- Date: 2026-09-06

## Context

A rebuilt document passing the reader and matching an executor-produced XML hash
does not independently prove that formatting or unindexed XML remained unchanged.
The mutation implementation could have changed its own artifact incorrectly.

## Decision

1. Add a public candidate verifier that snapshots and inspects both streams.
2. Require the executed patch to match the exact source package and main document.
3. Compare the complete physical inventory and every decompressed entry hash.
   Only the main document may differ; relationships, types, media and other Parts
   remain byte-identical.
4. Independently derive the expected affected-run text/token sequence from the
   plan. Compare the entire remaining XML, including properties, attributes,
   namespaces, whitespace, comments and instructions, with an iterative walk.
5. Require the final main XML hash to match the execution artifact as well.
6. Return complete per-entry evidence on success or the first located finding
   without partial evidence on failure.
7. Make a passed diff mandatory before the rebuilder publishes its artifact.

## Consequences

Legally parseable but unauthorized output is rejected. Compression representation
and entry order are not treated as document changes. The main-XML policy is
conservative about namespace bindings and whitespace, while tolerating source
encoding and attribute-order changes introduced by serialization.

DOMs, hashing and repeated inspections add bounded costs. This gate does not
replace a full input feature policy, Word visual validation or an approval
protocol. Build Reports consume the evidence in the next milestone.

## Alternatives rejected

- Compare only plain text/indexes: misses formatting, section and extension changes.
- Compare only the patch hash: trusts the mutation implementation's own output.
- Reapply the mutator and compare: repeats the same possible implementation bug.
- Ignore all namespaces/whitespace: can conceal meaningful XML differences.
- Compare complete ZIP bytes: rejects valid repackaging even when Parts are intact.
