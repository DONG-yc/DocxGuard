# Read-only document index contract

`DocxIndexInspector` builds the first semantic view of a verified DOCX main
document. It runs package preflight, Content Types, Relationships, and the Word
main-document boundary first. Only a complete success at every earlier layer
allows indexing to begin.

The index is read-only. It is designed to support later text location and
patch planning without loading untrusted XML into a general-purpose object
model or changing the package.

## Guarantee

A `Passed` `DocxIndexInspectionResult` means:

1. all earlier trust boundaries passed against an immutable package snapshot;
2. the relationship-selected main Part was reopened by its verified physical
   entry name;
3. the main XML was parsed forward-only with the same DTD-free, resolver-free,
   character, node, and depth limits as Word inspection;
4. paragraphs, runs, tables, and cells stayed within dedicated index limits;
5. supported visible-text tokens stayed within one global text budget;
6. indexed structures opened and closed in an unambiguous order; and
7. one immutable `WordDocumentIndex` was published atomically.

An index-layer failure preserves the passed `DocxInspectionResult` but never
publishes a partial `WordDocumentIndex`.

## Stable IDs and offsets

IDs are deterministic for the same accepted main-document XML traversal:

| Item | Example | Ordering |
|---|---|---|
| Paragraph | `p000001` | All body paragraphs in document order |
| Run | `p000001/r0001` | Runs within one paragraph |
| Table | `t000001` | All tables in document order |
| Cell | `t000001/r0001/c0001` | One-based row and cell position in the ID |

Public `Ordinal`, `RowIndex`, and `ColumnIndex` values are zero-based. IDs are
location handles for one accepted document version; they are not permanent
identities across edits that insert earlier content.

Each `IndexedRun` records a half-open `[StartOffset, EndOffset)` range in its
paragraph text. Offsets and lengths use .NET UTF-16 code units. Adjacent runs
can therefore reconstruct a split phrase while still mapping a match back to
the source run boundaries.

`WordDocumentIndex.PlainText` joins paragraph text with one literal LF
character (`\n`) regardless of the host operating system.

## Visible-text profile

The V1 index is a controlled logical-text profile, not a Word layout engine.

| WordprocessingML token | Indexed text |
|---|---|
| `w:t` | Element text |
| `w:tab`, `w:ptab` | Tab (`\t`) |
| `w:br`, `w:cr` | LF (`\n`) |
| `w:noBreakHyphen` | U+2011 |
| `w:softHyphen` | U+00AD |
| `w:drawing`, `w:pict`, `w:object`, `w:sym` | U+FFFC object replacement character |

Runs inside `w:del` or `w:moveFrom` remain addressable but have
`IsVisible == false` and empty indexed text. Runs inside `w:ins` and
`w:moveTo` are included. Field instruction text such as `w:instrText` and
deleted text tokens are not included; ordinary `w:t` field results are.

This profile does not calculate style-driven visibility such as `w:vanish`,
font glyph substitution, page layout, tab positions, field evaluation, or
rendered line wrapping.

## Tables

All tables are indexed in encounter order. A nested table records the ID of
its parent cell, and that cell records the nested table ID. Each cell records
the paragraph IDs directly encountered while that cell is active. Paragraphs
inside cells also remain in the global paragraph sequence.

The V1 state machine fails closed when a table or paragraph appears inside an
already open paragraph. This excludes ambiguous text-box and similar complex
content from the current profile instead of silently producing incorrect
ownership. Markup Compatibility `AlternateContent` also fails until a later
milestone implements explicit branch selection; indexing both Choice and
Fallback would duplicate content.

## API behavior

```csharp
DocxIndexInspectionResult result = new DocxIndexInspector().Inspect(stream);

if (result.Status == PackageInspectionStatus.Passed)
{
    foreach (IndexedParagraph paragraph in result.Index!.Paragraphs)
    {
        Console.WriteLine($"{paragraph.Id}: {paragraph.Text}");
    }
}
```

Path inputs use one read-only handle. Stream inputs must be readable and
seekable, remain open, and return to their original position. Cancellation and
invalid limit contracts throw. Rejected untrusted content returns structured
findings.

## Default index limits

| Limit | Default |
|---|---:|
| Paragraphs | 100,000 |
| Runs | 1,000,000 |
| Tables | 10,000 |
| Table cells | 100,000 |
| Table nesting depth | 32 |
| Visible text plus paragraph separators | 10,000,000 UTF-16 code units |

All package, OPC, and WordprocessingML limits apply first.

## Stable index finding codes

| Code | Meaning |
|---|---|
| `IDX_001` | An index resource limit was exceeded |
| `IDX_002` | The bounded XML reader or verified main-entry read failed |
| `IDX_003` | Indexed WordprocessingML structures are ambiguous or out of sequence |

## Deliberate boundary

Day 6 indexes only the relationship-selected main body. It does not index
headers, footers, footnotes, endnotes, comments, or text in other Parts. It
does not perform complete ISO schema validation, select MCE branches, render
Word layout, edit XML, create patches, validate an output package, or repack a
document.

The next patch-planning layer can use stable paragraph/run ranges without
weakening this read-only boundary.
