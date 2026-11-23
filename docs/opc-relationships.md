# OPC Relationships inspection contract

`OpcPackageInspector` treats Relationships as the third read-only trust
boundary. It runs package preflight and Content Types validation first, then
opens every verified Relationships part from the same immutable package
snapshot.

## Guarantee

A `Passed` result with a non-null `Relationships` graph means:

1. every Relationships part stayed within the per-part and aggregate byte
   limits;
2. every stream was parsed forward-only with DTD processing prohibited, no XML
   resolver, character checking enabled, and explicit character, node, depth,
   relationship-count, and attribute limits;
3. only UTF-8 and UTF-16 encodings were accepted;
4. the root and child expanded names matched the OPC Relationships namespace;
5. only direct `Relationship` children and the unqualified `Id`, `Type`,
   `Target`, and optional `TargetMode` attributes were accepted;
6. each ID matched the controlled ASCII xsd:ID profile and was unique within
   its Relationships part;
7. each Type was a well-formed absolute URI and was preserved without
   case-folding or escape normalization;
8. TargetMode was exactly `Internal` or `External`, with omitted values
   interpreted as `Internal`;
9. each Relationships part mapped to the package or an existing source Part;
10. every internal target resolved against the correct source directory,
    normalized dot segments, produced a valid `OpcPartName`, and identified an
    existing Part; and
11. no relationship originated from or targeted a Relationships part.

External relationships are structurally recognized but rejected by the V1
controlled-template policy. Consequently, every relationship in a published
graph is internal and has a non-null resolved `TargetPartName`.

## Atomic publication

The layers have separate publication rules:

- package or Content Types failure publishes neither the manifest, Part
  inventory, nor relationship graph;
- Relationships failure preserves the already verified manifest and Part
  inventory, returns structured `RelationshipFindings`, and publishes no
  graph; and
- complete success publishes one immutable `RelationshipGraph`, including an
  empty relationship set when a valid `.rels` stream contains no children.

This prevents callers from accidentally traversing a partial graph while still
allowing them to report exactly which verified source Part or raw target failed.

## Target resolution

The package Relationships part is `/_rels/.rels`. A Part such as
`/word/document.xml` owns `/word/_rels/document.xml.rels`.

Internal targets are resolved as follows:

- a leading `/` starts at the package root;
- otherwise the target starts in the source Part's directory, or at the package
  root for package relationships;
- `.` segments are removed;
- `..` removes one preceding segment and clamps at the package root; and
- the result must be a non-directory logical Part name.

The controlled profile rejects empty targets, network-path references, absolute
URI schemes, backslashes, whitespace or controls, queries, fragments, trailing
slashes, encoded separators, and percent-encoded unreserved dot segments.

## API behavior

```csharp
OpcInspectionResult result = new OpcPackageInspector().Inspect(stream);

if (result.Status == PackageInspectionStatus.Passed)
{
    foreach (OpcRelationshipSet set in result.Relationships!.RelationshipSets)
    {
        string source = set.SourcePartName?.Value ?? "(package)";
        foreach (OpcRelationship relationship in set.Relationships)
        {
            Console.WriteLine(
                $"{source} --{relationship.Id}--> " +
                relationship.TargetPartName.Value);
        }
    }
}
```

Caller-owned streams remain open and return to their original position.
Cancellation and invalid caller-supplied limits throw. Untrusted relationship
failures return findings rather than exceptions.

## Default relationship limits

| Limit | Default |
|---|---:|
| One Relationships part, uncompressed | 4 MiB |
| All Relationships XML, uncompressed | 16 MiB |
| Relationships in one part | 4,096 |
| Relationships in one package | 16,384 |
| XML characters per stream | 4 Mi characters |
| XML depth per stream | 16 |
| XML nodes per stream | 16,384 |
| One attribute value | 2,048 characters |

Package and Content Types limits continue to apply before these limits.

## Stable Relationships finding codes

| Code | Meaning |
|---|---|
| `REL_001` | Bounded XML reader or verified entry read rejected the stream |
| `REL_002` | XML encoding is outside UTF-8/UTF-16 |
| `REL_003` | Relationships schema shape, namespace, or attribute is invalid |
| `REL_004` | Byte, XML, or relationship-count resource limit exceeded |
| `REL_005` | Relationship ID is invalid |
| `REL_006` | Relationship ID is duplicated within its stream |
| `REL_007` | Relationship Type is not an absolute URI |
| `REL_008` | TargetMode is not exactly Internal or External |
| `REL_009` | External relationship is outside V1 |
| `REL_010` | Internal target cannot form a valid Part reference |
| `REL_011` | Resolved internal target Part does not exist |
| `REL_012` | Source Part for a Relationships part does not exist |
| `REL_013` | A relationship originates from or targets a Relationships part |

## Compatibility boundary

The controlled ASCII ID profile intentionally accepts the IDs emitted by
supported gold templates while excluding the wider Unicode xsd:ID space.
Relationship Types must be absolute URIs even though target resolution also
supports Unicode Part names. External links, alternate compatibility markup,
and unusual but legal IRI forms require fixtures plus an explicit policy
revision; they are not enabled by relaxing the parser ad hoc.

This layer proves package relationship integrity only. It does not prove that a
target has the semantic relationship type expected by WordprocessingML, that
the main document relationship exists, that Word XML is schema-valid, or that
the package is safe to modify.
