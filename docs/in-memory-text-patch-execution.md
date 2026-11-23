# In-memory text patch execution contract

`PatchPreconditionValidator` and `InMemoryTextPatchExecutor` are the first
controlled mutation layer. They consume a passed `DocxIndexInspectionResult`
and a ready `PatchPlan`, verify that the plan still describes the current
index, and modify only a private in-memory copy of the verified main-document
XML.

The source stream and source DOCX bytes remain untouched. Day 8 does not
repack or save a DOCX.

Day 9/10 add a separate [package rebuilder](safe-package-rebuilding.md) and
[structural verifier](structural-diff.md). A passed index now exposes the
source package SHA-256, and the XML artifact carries that execution-source hash
plus the internal immutable plan for independent downstream verification.

## Preconditions

Before XML is loaded for mutation, every operation is checked against the
current immutable index:

1. the planned paragraph ID still exists;
2. its complete logical text still equals the planned snapshot;
3. its lowercase UTF-8 SHA-256 still matches;
4. the exact expected text still occupies the planned UTF-16 range; and
5. the current visible run slices still equal the planned slices; and
6. no two operations overlap in the same source paragraph.

A failed check publishes structured findings and no output artifact. The
executor never relocates stale text or searches for a replacement target.

## Supported execution profile

The executor accepts disjoint `ReplaceText` operations up to its configured
limit. Day 16's `BuildBoundMulti` uses a 64-operation default when no options
are supplied. Every affected run must be a plain text run:

- at most one direct `w:rPr`, in first element position;
- all other direct elements are `w:t`;
- `w:t` has no child elements and only optional `xml:space` attributes;
- non-element run content is whitespace only; and
- no `w:ins`, `w:del`, `w:moveFrom`, or `w:moveTo` ancestor exists.

Fields, tabs or breaks already embedded in the affected run, drawings,
objects, symbols, revision content, and extension elements fail closed as
unsupported run content. Unaffected complex runs remain untouched.

## Cross-run replacement

For a target spanning several runs:

1. the first run keeps text before its slice and receives the replacement;
2. intermediate target text is removed while those run elements remain;
3. the last run keeps text after its slice; and
4. every run keeps its existing `w:rPr` formatting subtree.

Replacement LF and tab characters become `w:br` and `w:tab`. Plain segments
become `w:t`, with `xml:space="preserve"` when a segment begins or ends with
whitespace. Empty replacement expresses deletion; empty affected runs are
retained rather than removed.

When several operations touch one run, the executor composes them from the
original run offsets. The affected-run and generated-node limits apply to
the whole batch.

## Atomic publication and postconditions

Mutation occurs on a newly parsed XML tree. The serialized output is published
only after it:

- remains within both execution and Word main-document byte limits;
- passes the Word main-document structural parser again;
- passes the bounded document indexer again;
- changes the target paragraph to the exact expected replacement result;
- produces the exact predicted run text distribution;
- leaves all other paragraph text, run identities, and visibility unchanged;
  and
- leaves all table and cell identities and ownership unchanged.

Any failure publishes no partial XML. Successful output is an
`InMemoryMainDocumentPatch` containing the plan ID, main Part name,
conformance family, original and patched XML SHA-256 values, and read-only
patched XML bytes.

The XML artifact is serialized as UTF-8. Semantic structure and indexed
postconditions are verified, but insignificant lexical details such as prefix
choice, indentation, attribute order, or XML declaration formatting are not a
byte-preservation guarantee.

## API behavior

```csharp
TextPatchExecutionResult execution =
    new InMemoryTextPatchExecutor().Execute(indexInspection, plan);

if (execution.Status == TextPatchExecutionStatus.Passed)
{
    ReadOnlyMemory<byte> mainXml = execution.Artifact!.Xml;
    Console.WriteLine(execution.Artifact.PatchedXmlSha256);
}
```

## Default execution limits

| Limit | Default |
|---|---:|
| Operations | 1 |
| Affected runs | 1,024 |
| Generated text, tab, and break nodes | 16,384 |
| Output main-document XML | 16 MiB |

All WordprocessingML node, character, depth, and document-index limits also
apply when output is verified.

## Stable precondition finding codes

| Code | Meaning |
|---|---|
| `PRE_001` | Planned paragraph is missing |
| `PRE_002` | Full paragraph text changed |
| `PRE_003` | Paragraph SHA-256 changed |
| `PRE_004` | Expected target range changed |
| `PRE_005` | Run slice identities or boundaries changed |
| `PRE_006` | Planned ranges overlap in a source paragraph |

## Stable execution finding codes

| Code | Meaning |
|---|---|
| `EXEC_001` | Source inspection did not pass |
| `EXEC_002` | PatchPlan preconditions failed |
| `EXEC_003` | Affected run content is outside the plain execution profile |
| `EXEC_004` | An execution resource limit was exceeded |
| `EXEC_005` | The verified XML could not be mutated safely |
| `EXEC_006` | Re-indexed output failed logical postconditions |
| `EXEC_007` | PatchPlan shape is unsupported by this executor |

## Deliberate boundary

Day 8 does not publish a DOCX package, replace the main Part in a ZIP, preserve
ZIP metadata, validate a rebuilt Relationship Graph, perform structural diff,
write to disk, overwrite the source, batch operations, or support non-text
Word constructs. The CLI remains help and version only.
