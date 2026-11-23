# ADR 0007: Require exact unique location before publishing a PatchPlan

- Status: Accepted
- Date: 2026-08-29

## Context

The read-only document index reconstructs logical paragraph text and preserves
run boundaries, but an editing system still needs to turn a requested change
into one explicit target. Selecting the first string occurrence would make
agents dangerous: repeated labels, table values, and overlapping occurrences
could cause a valid-looking change at the wrong location.

Mutation also needs optimistic concurrency. A location that was valid during
planning must not be applied after the paragraph or run layout changes.

## Decision

1. Add exact `TextLocator` and `TextPatchPlanner` services above
   `WordDocumentIndex`; keep both independent from package mutation.
2. Use ordinal, case-sensitive matching with no normalization or fuzzy logic.
3. Search within individual paragraphs only. Do not create cross-paragraph
   text operations from the global LF-joined view.
4. Retain overlapping matches and order every candidate deterministically.
5. Allow an optional stable paragraph ID to narrow search, but reject an
   unknown scope.
6. Publish explicit NoMatch, Unique, Ambiguous, and Rejected location states.
7. Map every candidate range back to all intersecting visible run slices and
   require complete range coverage.
8. Never choose the first ambiguous candidate. Publish candidates but no plan.
9. In this milestone, compile exactly one unique text replacement into one
   immutable `ReplaceText` operation.
10. Store expected full paragraph text, its SHA-256, target range and text, and
    run slices as execution preconditions.
11. Make plan IDs deterministic from a canonical operation representation.
12. Permit empty replacement for deletion, but reject no-ops, CR line endings,
    XML-invalid text, invalid UTF-16, and U+FFFC synthesis.
13. Reject targets crossing U+FFFC rather than treating objects as ordinary
    text.
14. Apply separate expected-text, replacement-text, and candidate limits.
15. Keep the CLI unchanged and perform no DOCX mutation.

## Consequences

An agent can inspect all candidates and refine its request without gaining
write authority. A later executor receives one explicit operation with enough
source evidence to detect stale or structurally changed content before it
touches XML.

Exact matching is predictable but intentionally less convenient than fuzzy or
natural-language targeting. Callers must use known template text and paragraph
scope. Candidate IDs are query-local; paragraph and run IDs remain tied to one
document version.

Full paragraph text in the precondition increases plan size. For the bounded
V1 index this is acceptable and provides a directly auditable source snapshot
in addition to the compact hash.

## Alternatives rejected

- Replace the first match: silently chooses among ambiguous targets.
- Search `PlainText` across paragraph separators: creates operations whose XML
  ownership spans multiple paragraphs.
- Case-insensitive or normalized matching by default: changes user data and
  match counts without an explicit policy.
- Store only offsets: stale offsets can point to different text after changes.
- Store only a hash: less auditable and cannot explain the exact failed
  precondition.
- Mutate while locating: removes the reviewable plan boundary and makes
  failure atomicity harder.
