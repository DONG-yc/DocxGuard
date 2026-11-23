# ADR 0008: Verify preconditions before bounded in-memory text execution

- Status: Accepted
- Date: 2026-08-29

## Context

A ready PatchPlan identifies one exact logical range, but planning and
execution can be separated in time. Applying offsets to changed content would
turn a safe plan into an uncontrolled edit. Even with a fresh index, Word runs
can contain fields, objects, revisions, tabs, or other elements whose rewrite
semantics are not equivalent to plain text.

The first mutation milestone must demonstrate a real XML change without also
taking on package rebuilding, output-path policy, rollback, and full Open XML
validation.

## Decision

1. Retain the verified main-document XML as an internal capability only when
   `DocxIndexInspectionResult` passes and publishes its index.
2. Add a public precondition validator for paragraph identity, complete text,
   SHA-256, exact target range, and run slices.
3. Refuse execution on any failed precondition. Do not search or relocate.
4. Support exactly one `ReplaceText` operation in Day 8.
5. Load only the bounded verified XML snapshot, with DTD and external
   resolution still disabled.
6. Limit execution to runs containing optional first-position `w:rPr` and
   plain direct `w:t` elements.
7. Reject revision ancestors and every affected field, object, token, or
   extension run outside that profile.
8. Preserve each affected run and its `w:rPr`. Place replacement content in
   the first run, remove covered text from later runs, and retain final suffix.
9. Convert replacement tab and LF to `w:tab` and `w:br`, and apply
   `xml:space="preserve"` where required.
10. Bound operations, affected runs, generated nodes, and output XML bytes.
11. Serialize to a new UTF-8 byte array; never mutate the retained source
    snapshot.
12. Re-run the Word structural parser and document indexer on output.
13. Verify exact target paragraph and run-text postconditions plus unchanged
    non-target paragraph, run, table, and cell structure.
14. Publish the in-memory XML and before/after hashes atomically only on full
    success.
15. Do not rebuild or save a DOCX and do not change the CLI.

## Consequences

Stale plans and complex affected runs fail before output publication. A
successful artifact proves one narrow logical text change while preserving run
formatting properties and table ownership.

Retaining main XML increases the memory lifetime of a passed index result by a
bounded amount. The executor also builds an XML tree and a second byte array;
execution limits make those costs explicit.

XML reserialization is semantically verified but not lexically byte-stable.
Prefix spelling, indentation, declaration formatting, or attribute order can
change even when indexed content does not. Package-level structural diff and
repack verification are deferred.

## Alternatives rejected

- Apply a plan without revalidation: permits stale offsets and wrong-target
  edits.
- Relocate expected text when a precondition fails: silently changes the
  approved operation.
- Rewrite every run child as text: destroys fields, objects, and revision
  semantics.
- Collapse all affected runs into one: loses formatting boundaries and stable
  run ownership.
- Repack the DOCX in the same step: combines XML mutation with a separate set
  of ZIP, OPC, and output atomicity risks.
- Modify the source stream or file: violates the immutable-input contract.
