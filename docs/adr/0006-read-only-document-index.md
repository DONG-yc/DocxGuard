# ADR 0006: Build a bounded read-only document index before patching

- Status: Accepted
- Date: 2026-08-29

## Context

The Word main-document boundary identifies a trusted Part and validates its
outer XML envelope, but later patch planning still needs deterministic content
locations. Searching only concatenated XML text loses run boundaries. Loading
the whole document into a high-level object model would also move resource and
compatibility decisions behind abstractions that do not express this product's
fail-closed contract.

Word text is commonly split across runs, revisions alter which text is current,
and tables create nested ownership. A usable locator must reconstruct logical
text while preserving enough provenance to map matches back to source runs.

## Decision

1. Add `DocxIndexInspector` above `DocxDocumentInspector`; do not change the
   meaning of any earlier passed result.
2. Reuse the immutable package snapshot and reopen only the already verified
   relationship-selected main entry.
3. Parse forward-only with the established secure XML settings and Word node
   and depth limits.
4. Add independent limits for paragraphs, runs, tables, cells, table depth,
   and indexed text.
5. Publish one immutable `WordDocumentIndex` only after the complete traversal
   succeeds. Preserve the passed document result on index failure.
6. Assign traversal-stable paragraph, run, table, and cell IDs. Use zero-based
   public ordinals and half-open UTF-16 run offsets.
7. Reconstruct paragraph text from `w:t` plus a small documented set of tabs,
   breaks, hyphens, drawings, objects, and symbols.
8. Exclude `w:del` and `w:moveFrom` text while retaining their run records;
   include `w:ins` and `w:moveTo` text.
9. Join global plain text with literal LF separators for platform-independent
   output.
10. Record bidirectional nested-table ownership through table parent-cell and
    cell nested-table IDs.
11. Fail closed on nested paragraph/table ownership that the V1 state machine
    cannot represent unambiguously.
12. Reject Markup Compatibility `AlternateContent` until explicit feature
    selection exists; never concatenate Choice and Fallback branches.
13. Index only the main body and keep the CLI surface unchanged.

## Consequences

Later locators can match text across runs and translate a paragraph span back
to the exact run ranges without guessing XML string boundaries. Tables and
cells have deterministic ownership, and every resource dimension is bounded.

The logical-text profile is intentionally smaller than Word rendering. It does
not evaluate styles, fields, font glyphs, layout, headers, notes, or other
Parts. Unsupported MCE and complex paragraph nesting reject the index rather
than return misleading text.

The current public layering makes an additional bounded in-memory package copy
inside document inspection. This preserves the already tested APIs and can be
optimized later with an internal shared-snapshot pipeline without changing the
result contract.

## Alternatives rejected

- Search raw `document.xml`: cannot reliably cross run boundaries or preserve
  table ownership and is vulnerable to matching markup instead of content.
- Flatten all text immediately: loses the provenance required for controlled
  patches.
- Load `WordprocessingDocument` and walk its DOM first: hides allocation and
  compatibility choices behind a broad object model before this layer's limits
  apply.
- Index both MCE Choice and Fallback: duplicates mutually exclusive content.
- Implement editing in the same milestone: mixes read-only discovery with
  mutation before stable location and failure contracts exist.
