# OPC Content Types inspection contract

`OpcPackageInspector` is the second read-only trust boundary. It runs the Day 2
ZIP/OPC preflight and, only after that passes, validates `[Content_Types].xml`
against every physical package part.

## Guarantee

A `Passed` result means that, under the configured package and OPC policies:

1. the exact bytes passed the complete `SafePackageReader` preflight;
2. the Content Types entry stayed within its byte, XML-character, node, depth,
   mapping-count, and attribute-value limits;
3. XML was parsed with DTD processing prohibited, no resolver, character
   checking enabled, and UTF-8/UTF-16 as the only supported encodings;
4. the root and child expanded names matched the OPC Content Types namespace,
   at least one Default or Override was present, and only the schema-defined,
   unqualified attributes were accepted;
5. every Default extension, Override part name, and media type passed its
   dedicated grammar;
6. Default and Override keys were unique under ASCII-only case folding;
7. each non-directory ZIP item except `[Content_Types].xml` used an ASCII
   physical name and mapped to one valid logical `OpcPartName`, with no part
   also acting as another part's directory;
8. Override mappings took precedence over Default mappings;
9. every non-Relationships part had a Default or Override declaration; and
10. Relationships parts resolved to the fixed OPC Relationships content type
    without requiring a mapping; conflicting declarations were rejected by the
    controlled profile.

The manifest and resolved part inventory are returned only if the entire OPC
Content Types layer passes. An Override must target an existing Part; an unused
Override fails closed. An unused Default is explicitly valid and produces no
finding.

Day 3 only identifies Relationships parts, applies their fixed OPC content type,
and rejects an applicable conflicting manifest declaration. It does not open or
parse `.rels` XML, traverse or resolve relationship targets, or modify the
package. It also does not prove that the package is a supported Word document,
WordprocessingML is schema-valid, or macros and digital signatures are absent.
Those are later gates.

## API behavior

```csharp
OpcPackageInspector inspector = new();
OpcInspectionResult result = inspector.Inspect(stream);

if (result.Status == PackageInspectionStatus.Passed)
{
    foreach (OpcPartInfo part in result.Parts)
    {
        Console.WriteLine(
            $"{part.PartName.Value} -> {part.ContentType.Value} " +
            $"({part.ContentType.Source})");
    }
}
```

- The path overload opens one read-only handle and performs preflight plus XML
  inspection before closing it. It never checks one path and reopens another.
- Stream inputs must be readable and seekable. Inputs within the effective
  archive/snapshot limit are captured once, so preflight and XML parsing always
  use the same immutable bytes even if the caller owns a mutable stream.
- An input above that limit never reaches the XML layer or publishes trusted
  metadata. Day 2 may inspect the original stream only to return an earlier,
  more specific package-size failure; the overall result remains failed.
- Caller-owned streams remain open and return to their original position.
- Package failures remain in `PackageInspection.Findings`; Content Types
  failures are in `ContentTypeFindings`.
- Cancellation and caller/limit contract errors throw. Untrusted package and
  Content Types failures return structured findings.
- `OpcPartName` equality folds only ASCII `A-Z`; it does not apply Unicode or
  culture-sensitive case conversion.

## Default metadata limits

| Limit | Default |
|---|---:|
| Immutable package snapshot | 50 MiB |
| Content Types uncompressed bytes | 4 MiB |
| XML characters | 4 Mi characters |
| XML depth | 16 |
| XML nodes | 16,384 |
| Default + Override mappings | 4,096 |
| One attribute value | 2,048 characters |
| Part-name segments | 128 |

These limits apply in addition to all Day 2 package limits.

## Stable Content Types finding codes

| Code | Meaning |
|---|---|
| `DOCXGUARD_CT_001` | Bounded XML reader or verified Content Types entry read rejected the stream |
| `DOCXGUARD_CT_002` | XML encoding is outside UTF-8/UTF-16 |
| `DOCXGUARD_CT_003` | Content Types schema shape or namespace is invalid |
| `DOCXGUARD_CT_004` | Default extension is invalid |
| `DOCXGUARD_CT_005` | Override OPC part name is invalid |
| `DOCXGUARD_CT_006` | Media type is invalid |
| `DOCXGUARD_CT_007` | Duplicate ASCII-case-insensitive Default |
| `DOCXGUARD_CT_008` | Duplicate ASCII-case-insensitive Override |
| `DOCXGUARD_CT_009` | Non-Relationships part has no Default or Override mapping |
| `DOCXGUARD_CT_010` | OPC snapshot, XML, mapping, or Part-name resource limit exceeded |
| `DOCXGUARD_CT_011` | Relationships part conflicts with its fixed type |
| `DOCXGUARD_CT_012` | Parameters used on an OPC type that forbids them |
| `DOCXGUARD_CT_013` | Override does not target an existing part |
| `DOCXGUARD_CT_014` | ZIP item cannot form an unambiguous OPC part |

## Controlled profile and residual risk

The logical part-name implementation follows the OPC constraints needed by
controlled DOCX templates: absolute package paths, non-empty segments, no dot
segments or trailing dots, no query/fragment, no encoded separators, and no
percent-encoded `iunreserved` characters. Physical ZIP item names must be
ASCII. Their percent escapes that encode non-ASCII characters are decoded
before the leading `/` is added, so `word/%C3%A9.xml` maps to logical
`/word/é.xml`. Percent hex digits are normalized for matching.

This layer deliberately fails closed on raw URI whitespace, Unicode formatting
characters, some otherwise legal Latin-1 or HTAB media-type parameter content,
and package paths already excluded by the Day 2 profile. Day 2 also conservatively
rejects some distinct non-ASCII names that Unicode case folding treats as equal.
Broader OPC/IRI compatibility needs real fixtures and an explicit policy
revision; it must not be obtained by relaxing parsing ad hoc.

The inspector does not expose an API that combines an arbitrary preflight result
with another stream. It captures one bounded immutable snapshot before either
gate runs, preventing trusted metadata from being bound to different bytes.
