# Allowlisted structural diff contract

`DocxStructuralDiffVerifier` checks whether a candidate DOCX contains only the
changes authorized by an executed text patch plan. It is also a mandatory gate
inside `DocxPackageRebuilder` before the in-memory artifact is published.

## Trust and invocation

```csharp
using DocxGuard.Core.Verification;

StructuralDiffResult diff = new DocxStructuralDiffVerifier().Verify(
    sourceStream, candidateStream, execution.Artifact!);

if (diff.Status == StructuralDiffStatus.Passed)
    Console.WriteLine(diff.Entries.Count);
```

Public streams must be readable and seekable. Each is copied into a bounded
private snapshot, inspected from ZIP preflight through OPC, Word and indexing,
and left open with its original position. Both snapshots obey package and OPC
limits. Invalid arguments/limits and cancellation throw. Rejected documents
produce findings and no partial comparison evidence.

The rebuild integration reuses its already inspected private snapshots. The
internal entry point is not a public shortcut for unverified streams.

## Package allowlist

- The execution-source package SHA-256, main Part, conformance and original XML
  hash must match the patch. This is an execution-artifact binding, not yet a
  serialized plan/approval protocol.
- Entry names and kinds must match exactly, without added, removed or renamed
  entries. ZIP entry ordering and compression representation are not document
  content and need not match in an independently supplied candidate.
- Every entry is read to its inspected decompressed length and hashed with
  SHA-256. Every non-main entry must retain both length and hash.
- This protects `[Content_Types].xml`, all `.rels`, media, styles, headers,
  footers and unreferenced resources as bytes. Even semantically equivalent
  reserialization of those protected entries is rejected.

Both full inspections resolve Parts, content types and relationships. Exact
preservation of their defining entry bytes and the physical inventory proves
these package models did not change, without a separate relaxed graph comparison.

## Main XML allowlist

The comparer does not call the mutation function or accept a matching aggregate
plain-text index as sufficient proof. It independently:

1. Revalidates the retained plan against the source index and locates the exact
   paragraphs and affected runs, rejecting overlapping ranges.
2. Requires source runs to meet the plain, non-revised `w:t` profile.
3. Computes expected prefix, replacement and suffix content from the original
   run text and approved slices. Replacement Tab/LF must be the corresponding
   `w:tab`/`w:br` tokens, without added attributes or content.
4. Requires output `w:t` children to contain only text and the expected optional
   `xml:space="preserve"`; no extra attributes, comments or extension elements
   are allowed within these rewritten text children. Adjacent plain segments are
   compared by their combined value; Run boundaries are retained.
5. Excludes only these validated text/token children from comparison. Iteratively
   compares every remaining XML element, attribute, namespace declaration,
   text/whitespace, comment and processing instruction. `w:rPr`, run attributes,
   paragraph/section properties and all non-target content are protected.
6. Also requires the resulting main XML hash to equal the exact executor artifact.

Attribute ordering, CDATA versus text representation and output encoding are not
compared as lexical source details. Namespace bindings are compared explicitly
because attribute values can contain qualified names. Declaration presence,
version and standalone value are preserved. Adjacent text nodes are coalesced
after allowed children are excluded; arbitrary whitespace is not discarded.

This is a deliberately conservative profile, not general XML canonicalization.
A separately reserialized candidate main XML can still fail the final artifact
hash even when its structural comparison passed.

## Evidence and diagnostics

A passed result contains complete `VerifiedEntryChange` records: entry name,
before/after lengths, SHA-256 hashes, and whether the entry is the main document.
A failed result stops at the first rejected difference, exposes no partial entry
list, and provides a stable code plus an entry name and XML path where available.
An XML path uses expanded namespace names and same-name sibling ordinals; it is
a diagnostic location, not a permanently stable template identifier.

| Code | Meaning |
|---|---|
| `DIFF_001` | Package snapshot exceeds a limit |
| `DIFF_002` | Source or output failed the complete inspection pipeline |
| `DIFF_003` | Patch source identity or retained plan does not match |
| `DIFF_004` | Entry inventory, selected main Part or conformance changed |
| `DIFF_005` | Protected non-main entry bytes changed |
| `DIFF_006` | Unauthorized XML structure/attribute/content change |
| `DIFF_007` | Affected run text or token sequence differs from the plan |
| `DIFF_008` | Snapshot or verified entry read failed |
| `DIFF_009` | Candidate main XML differs from the executor artifact |

## Limits and residual scope

The existing package, OPC, Word XML and index budgets apply to both inputs.
Entry hashing is streamed; XML comparison uses bounded DOMs and an iterative
traversal. The verifier adds memory and CPU to full inspection. It does not
promise unchanged line wrapping/pagination, validate every Word schema feature,
sanitize dangerous features already accepted by the input policy, or establish
user approval. Source immutability permits retaining the original; disk rollback
remains a later milestone. Day 11 now provides [versioned Build Reports](build-report.md).
