# Text location and PatchPlan contract

`TextLocator` and `TextPatchPlanner` form the first intent-compilation layer
above a passed `WordDocumentIndex`. They convert exact logical text into
candidate locations and, only for one unambiguous supported target, an
immutable `PatchPlan`.

This layer is read-only. A ready plan describes what a later executor may do;
it does not authorize or perform XML or package mutation.

## Exact location guarantee

`TextLocator` uses `StringComparison.Ordinal`. V1 search is therefore exact and
case-sensitive, with no Unicode normalization, culture rules, regex, fuzzy
matching, stemming, or LLM interpretation.

Search has these rules:

1. a query must be non-empty and remain within its character limit;
2. matching occurs independently inside each indexed paragraph;
3. a match never crosses the LF separator in `WordDocumentIndex.PlainText`;
4. every overlapping occurrence is retained, so `aa` has two candidates in
   `aaa`;
5. candidates are ordered by paragraph and then UTF-16 start offset;
6. an optional paragraph ID restricts search to one known paragraph; and
7. exceeding the match limit rejects the result without partial candidates.

The result status is `NoMatch`, `Unique`, `Ambiguous`, or `Rejected`.
`m000001` candidate IDs are deterministic within one location result, but are
not permanent identities across different queries or document versions.

## Mapping a candidate back to runs

Each `TextMatch` records paragraph and table-cell identity, a half-open
paragraph range, exact matched text, and one or more `RunTextSlice` values.
A slice records:

- the stable run ID and ordinal;
- its slice start in paragraph UTF-16 code units;
- its start inside the run; and
- its slice length.

Slice lengths must cover the complete match. This makes a phrase split across
several Word runs locatable without pretending it came from one XML text node.

## Planning guarantee

`TextPatchPlanner.CreatePlan` accepts one `TextReplacementRequest` containing
expected text, replacement text, and an optional paragraph scope.

- no match returns `NoMatch` and no plan;
- multiple matches return `Ambiguous`, preserve all candidates, and publish no
  plan;
- rejected location, resource, target, or replacement policy returns
  `Rejected` and no plan; and
- one supported match returns `Ready` with exactly one `ReplaceText`
  operation.

There is no first-match fallback. The caller must provide a paragraph scope or
otherwise refine ambiguous expected text.

## Strong preconditions

The ready operation stores:

| Field | Purpose |
|---|---|
| Paragraph ID | Stable target within the source index |
| Expected paragraph text | Detect any logical paragraph change |
| Paragraph SHA-256 | Compact deterministic snapshot check |
| Start, end, and expected text | Recheck the exact target range |
| Expected run slices | Recheck run ownership and boundaries |
| Replacement text | Plain-text intent for the later executor |

Hashes are lowercase SHA-256 hex over UTF-8 paragraph text. Plan IDs are
deterministic hashes of a canonical operation representation; the same index
and request produce the same ID, while replacement changes produce a different
ID. IDs support audit correlation and are not digital signatures.

A future executor must re-index the bytes it will modify and verify every
precondition immediately before mutation. A mismatch must fail instead of
relocating the target silently.

## Replacement policy

Replacement text may be empty to express deletion. It may contain XML-safe
plain text, LF, and tab characters. V1 rejects:

- CR line endings; callers must use LF;
- XML-invalid characters and unpaired UTF-16 surrogates;
- U+FFFC object placeholders, because planning cannot synthesize drawings,
  legacy objects, or symbol objects; and
- no-op requests where expected and replacement text are identical.

A unique target containing U+FFFC is also rejected so a text operation cannot
cross an indexed object boundary.

## API behavior

```csharp
TextPatchPlanningResult result = new TextPatchPlanner().CreatePlan(
    index,
    new TextReplacementRequest(
        ExpectedText: "Old project name",
        ReplacementText: "New project name",
        ParagraphScopeId: "p000014"));

if (result.Status == TextPatchPlanningStatus.Ready)
{
    PatchPlan plan = result.Plan!;
    Console.WriteLine(plan.Id);
}
```

## Default limits

| Limit | Default |
|---|---:|
| Location query | 65,536 UTF-16 code units |
| Candidate matches | 10,000 |
| Planning expected text | 65,536 UTF-16 code units |
| Planning replacement text | 65,536 UTF-16 code units |

The document index limits already bound the searched source text.

## Stable location finding codes

| Code | Meaning |
|---|---|
| `LOC_001` | No exact match |
| `LOC_002` | More than one exact candidate |
| `LOC_003` | Query or candidate resource limit exceeded |
| `LOC_004` | Requested paragraph scope does not exist |

## Stable planning finding codes

| Code | Meaning |
|---|---|
| `PLAN_001` | No exact target |
| `PLAN_002` | Target is ambiguous |
| `PLAN_003` | Planning resource limit exceeded |
| `PLAN_004` | Target crosses an unsupported object placeholder |
| `PLAN_005` | Request would make no change |
| `PLAN_006` | Replacement text is outside the plain-text policy |
| `PLAN_007` | Location was rejected for a non-resource reason |

## Deliberate boundary

Day 7 emits one-operation text plans only. It does not batch operations,
resolve overlaps between operations, search across paragraphs or Parts,
interpret natural language, select an ambiguous candidate, mutate XML, save a
DOCX, verify an output package, or roll back changes.
