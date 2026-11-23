# Safe in-memory DOCX rebuilding contract

`DocxPackageRebuilder` accepts a readable, seekable source stream (or a read-only
source path) and an `InMemoryMainDocumentPatch` produced by the executor. It
returns a complete new DOCX in memory only after inspection and structural diff
pass. There is no output path or in-place update API.

## Input identity

The source is captured once into a bounded private snapshot. Its stream position
is restored and it remains open. Source snapshot read failures are structured
findings; invalid API arguments and cancellation throw.

The entire snapshot is re-inspected with the configured ZIP, OPC, Word and index
limits. The executed patch must match its package SHA-256, main Part name,
conformance family, and original main XML SHA-256. A source with the same main
XML but changed media is rejected. The bound package hash refers to the snapshot
used by the XML executor; Day 7 plans alone do not yet carry a package binding.

## Rebuilding rules

1. Resolve the **physical** ZIP entry name from the inspected main Part. A
   logical OPC name can differ from its percent-encoded physical name.
2. Iterate the inspected source entries in source order. Replace only the main
   entry with the patch XML and copy each remaining decompressed payload exactly.
3. Preserve entry names, directory entries, timestamps and external attributes.
   Recreate compression records and ZIP metadata; do not promise preservation of
   extra fields, comments, compressed bytes or overall ZIP binary identity.
4. Use no compression. This avoids introducing output compression-ratio failures,
   at the cost of larger output archives and potentially rejecting an input that
   fitted the archive limit before decompression.
5. Enforce entry and total payload limits before copying each entry. A bounded
   output stream also checks all ZIP writes, including the central directory.
6. Re-inspect the completed package from ZIP preflight through the document index
   and require its main XML to match the execution artifact.
7. Run the independent [structural diff](structural-diff.md). Any failure prevents
   artifact publication. A passed inspection alone is insufficient.

## Resource limits

`DocxPackageRebuildLimits` defaults to 50 MiB each for source and output packages.
The source is also bounded by `PackageLimits.MaxArchiveBytes` and
`OpcInspectionLimits.MaxPackageSnapshotBytes`. Output writes also obey the
package archive limit; output inspection applies the OPC snapshot and all other
existing limits. ZIP entry count cannot increase because no entries are added.

These are byte/structure limits, not a claim that process memory equals archive
size. Snapshots, copied artifacts, parsed XML and indexes have additional bounded
allocations. Entry copies use an 80 KiB buffer; cancellation is checked during
snapshot capture, entry copying, inspection and comparison.

## Results

`DocxPackageRebuildResult` exposes `SourceInspection`, `OutputInspection`,
`StructuralDiff`, findings and an optional `Artifact`. Passed results require all
verification layers to pass. Failed results never contain an artifact; completed
inspection results may remain available for diagnostics.

`InMemoryDocxPackage` contains the plan ID, main Part, source/output package
hashes, original/patched XML hashes, byte length and private-copy `Bytes`.
Modifying a returned array cannot alter the stored artifact. These fields are
evidence consumed by Day 11's [versioned Build Report](build-report.md).
Use `DocxBuildPipeline` to obtain a report paired with the verified artifact.

```csharp
using DocxGuard.Core.Building;
using DocxGuard.Core.Execution;
using DocxGuard.Core.Indexing;
using DocxGuard.Core.Packaging;
using DocxGuard.Core.Planning;

using FileStream source = File.OpenRead("template.docx");
var inspection = new DocxIndexInspector().Inspect(source);
if (inspection.Status != PackageInspectionStatus.Passed)
    throw new InvalidDataException("Source inspection failed.");

var planning = new TextPatchPlanner().CreatePlan(inspection.Index!,
    new TextReplacementRequest("{{customer}}", "Example Ltd"));
if (planning.Plan is null)
    throw new InvalidDataException("No unique supported replacement target.");

var execution = new InMemoryTextPatchExecutor().Execute(inspection, planning.Plan);
if (execution.Artifact is null)
    throw new InvalidDataException("XML execution failed.");

var rebuild = new DocxPackageRebuilder().Rebuild(source, execution.Artifact);
if (rebuild.Status == DocxPackageRebuildStatus.Passed)
{
    ReadOnlyMemory<byte> docx = rebuild.Artifact!.Bytes;
    Console.WriteLine(rebuild.Artifact.OutputPackageSha256);
}
```

## Stable findings

| Code | Meaning |
|---|---|
| `REBUILD_001` | Source snapshot exceeds a configured limit |
| `REBUILD_002` | Source inspection failed |
| `REBUILD_003` | Executed patch does not match the source |
| `REBUILD_004` | Source snapshot or entry could not be read exactly |
| `REBUILD_005` | Output archive, entry or total payload limit exceeded |
| `REBUILD_006` | ZIP rebuilding failed |
| `REBUILD_007` | Output inspection failed |
| `REBUILD_008` | Output main identity or XML hash differs from the artifact |
| `REBUILD_009` | Independent structural diff failed; inspect `StructuralDiff` |

## Current boundary

This layer does not save files, implement output-path rollback, batch plans,
render Word pages, or establish a complete package-feature security policy.
Reopening and SDK-validating a generated fixture is automated in tests; actual
Word/LibreOffice layout compatibility requires a separate real-document corpus.
