# Word main-document inspection contract

`DocxDocumentInspector` is the first DOCX-specific trust boundary. It captures
one bounded immutable package snapshot, requires the complete OPC inspection to
pass, and only then identifies and parses the WordprocessingML main Part.

It is intentionally separate from `OpcPackageInspector`: a valid OPC package
does not have to be a Word document.

## Guarantee

A `Passed` `DocxInspectionResult` means:

1. package preflight, Content Types inspection, and Relationship Graph
   construction all passed for the same immutable package bytes;
2. the package has exactly one supported package-level `officeDocument`
   relationship;
3. the relationship is internal and resolves to an existing Part;
4. the target has the exact macro-free DOCX main-document content type;
5. the Transitional or Strict relationship family agrees with the
   WordprocessingML root namespace;
6. the main Part stayed within its dedicated byte, XML-character, node, depth,
   body-child, attribute-count, and attribute-value limits;
7. XML was parsed forward-only with DTD processing prohibited, no resolver,
   character checking enabled, and UTF-8/UTF-16 as the only encodings;
8. the root is `document` in the expected WordprocessingML namespace;
9. the root contains at most one optional `background`, followed by exactly
   one `body`; and
10. a direct `sectPr`, when present, is the final direct body child.

The main Part path is discovered from the relationship. It is not assumed to be
`/word/document.xml`.

## Supported conformance families

| Family | officeDocument relationship prefix | Main namespace |
|---|---|---|
| Transitional | `http://schemas.openxmlformats.org/officeDocument/2006/relationships/` | `http://schemas.openxmlformats.org/wordprocessingml/2006/main` |
| Strict | `http://purl.oclc.org/ooxml/officeDocument/relationships/` | `http://purl.oclc.org/ooxml/wordprocessingml/main` |

The exact relationship name is `officeDocument` in either family. Both use:

`application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml`

Macro-enabled documents, Word templates, and other main-Part content types fail
the controlled V1 policy.

## Layered publication

- OPC failure publishes only the `OpcInspectionResult`; Word inspection does
  not run and `WordprocessingFindings` remains empty.
- Word failure preserves the fully verified OPC manifest, Part inventory, and
  Relationship Graph, but publishes no `WordMainDocumentInfo`.
- Complete success publishes an immutable `WordMainDocumentInfo` containing
  relationship identity, Part identity, conformance family, direct body-child
  count, and section-properties presence.

## API behavior

```csharp
DocxInspectionResult result = new DocxDocumentInspector().Inspect(stream);

if (result.Status == PackageInspectionStatus.Passed)
{
    WordMainDocumentInfo main = result.MainDocument!;
    Console.WriteLine(
        $"{main.Part.PartName.Value}: " +
        $"{main.BodyChildElementCount} direct body children");
}
```

Path inputs use one read-only handle. Stream inputs must be readable and
seekable, remain open, and return to their original position. Cancellation and
invalid limit contracts throw; untrusted document failures return structured
findings.

## Default WordprocessingML limits

| Limit | Default |
|---|---:|
| Main document, uncompressed | 16 MiB |
| XML characters | 16 Mi characters |
| XML depth | 256 |
| XML nodes | 1,000,000 |
| Direct body children | 100,000 |
| Attributes on one element | 256 |
| One attribute value | 65,536 characters |

All package and OPC limits apply first.

## Stable WordprocessingML finding codes

| Code | Meaning |
|---|---|
| `WML_001` | Supported package-level officeDocument relationship is missing |
| `WML_002` | More than one supported officeDocument relationship exists |
| `WML_003` | Main target uses an unsupported content type |
| `WML_004` | Main-document byte, node, depth, body, or attribute limit exceeded |
| `WML_005` | XML encoding is outside UTF-8/UTF-16 |
| `WML_006` | Bounded XML reader or verified main-entry read rejected the stream |
| `WML_007` | Minimum document/body structure is invalid |
| `WML_008` | Relationship and WordprocessingML namespace families disagree |

## Deliberate boundary

This gate is not a full WordprocessingML schema validator. It does not yet:

- validate every paragraph, run, table, drawing, field, revision, or MCE branch;
- prove that all relationship types are semantically correct for their source;
- build text/run/table indexes;
- load the package into the Open XML SDK object model;
- modify, diff, roll back, or repack a document.

Those capabilities can rely on the main Part identity and bounded XML structure
established here. A document with any external relationship is still rejected
by the earlier V1 Relationships policy before this Word layer runs.
