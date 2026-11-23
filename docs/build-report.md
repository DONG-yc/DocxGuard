# Build Report and immutable artifact contract

Day 11 introduces `DocxBuildPipeline` in `DocxGuard.Core.Building`.
It executes an existing typed plan against a bounded private source snapshot,
then returns a versioned report together with the verified in-memory DOCX.

## Invocation

```csharp
using DocxGuard.Core.Building;

// plan is a PatchPlan returned by TextPatchPlanner.
using FileStream input = File.OpenRead("template.docx");
DocxBuildResult result = new DocxBuildPipeline().Build(input, plan);
string json = result.Report.ToJson();
if (result.Status == DocxBuildStatus.Passed)
{
    ReadOnlyMemory<byte> docx = result.Artifact!.Bytes;
    Console.WriteLine(result.Report.OutputPackageSha256);
}
```

The input stream must be readable and seekable. The complete stream, from byte
zero, is copied with the minimum of Package, OPC and rebuild source budgets.
The stream remains open and its original position is restored on success,
document rejection, read failure and cancellation, provided the caller's stream
still supports position restoration. Snapshot length changes are rejected.
Callers must not concurrently modify the input: copying is not a filesystem
transaction and does not detect every same-length concurrent write.

`DocxBuildOptions` carries all six existing limit groups, validates them before
work, and records their values in the report. The pipeline performs source
inspection, execution and rebuilding using the same captured bytes. It never
opens an output path, overwrites an input, or publishes a partial candidate.

## JSON v1.0

Use `BuildReport.ToJson()` for the supported wire format: camelCase property
names, lowercase camelCase enum strings, explicit nulls, ordered arrays and
indented JSON. Report generation adds no timestamp, random ID or source path.
Repeated builds with the same source bytes, plan, limits and engine produce the
same report. Serialization preserves the report, not an executable approval.

| Field | Contract |
|---|---|
| `schemaVersion` | `1.0`; consumers must check the supported major version |
| `profile` | `docxguard.single-text.v1` 或 `docxguard.multi-text.v1` |
| `engineVersion` | Core assembly version |
| `planId` | Supplied typed plan's ID; not a user-approval token |
| `status` | `passed` only when an artifact exists and all gates pass; otherwise `failed` |
| `sourcePackageSha256` | SHA-256 of captured ZIP bytes; null if capture failed |
| `mainDocumentPartName` | Relationship-selected logical Part name when known |
| `outputPackageSha256`, `outputPackageLength` | Published DOCX identity; null on failure |
| `originalMainDocumentXmlSha256`, `patchedMainDocumentXmlSha256` | Published artifact XML identities; null on failure |
| `limits` | Configured limits for every independent gate |
| `checks` | Ordered stage/status pairs described below |
| `findings` | Stable codes, severity, stage, message and available source locations |
| `operations` | Planned target, ranges, run slices and text/hash summaries |
| `entries` | Complete verified per-entry lengths/hashes; empty on failure |
| `changedEntryCount` | Number of verified entries whose content hashes changed |

Checks have `passed`, `failed` or `notRun` status. Their fixed order is
`snapshot`, `sourceInspection`, `preconditions`, `execution`, `rebuild`,
`outputInspection`, `structuralDiff`. Execution is an aggregate gate including
preconditions and logical postconditions. Rebuild is an aggregate gate including
output inspection and structural diff; it can fail while output inspection
passes. Unreached gates are never represented as successful checks.

Operations contain match ID, paragraph ID, UTF-16 start offset, removed and
inserted lengths, and original run slices. Expected and replacement text are
represented by SHA-256 of UTF-8 bytes. Source paragraph hash is a required plan
precondition. Expected output paragraph hash reflects all planned edits in
that paragraph. An inconsistent typed plan can leave this hash unavailable.
Actual output paragraph hash is taken only from a successfully verified output.
On failure the latter is null. Planning resolves a unique target before Build
is called; no-match and ambiguous results remain the planner's contract.

Raw document text, DOCX/XML bytes and complete indexes are not serialized.
Diagnostic messages and package names may still contain source-derived data;
the report is not a privacy sanitizer. SHA-256 alone does not hide predictable
text or authenticate a report. Consumers should use codes and location fields,
not parse prose messages. Unknown fields may be ignored within major version 1;
breaking changes to meanings/types require a new major version.

## Failure and artifact invariants

- Invalid arguments/limits and cancellation throw; they do not manufacture a
  document failure report. `BUILD_001` means snapshot budget rejection and
  `BUILD_002` means snapshot read/length failure.
- Document errors retain existing ZIP, OPC, Word, index, execution, precondition,
  rebuild and diff codes, with stage and available location information.
- A failed build returns diagnostics, no artifact, null output hashes and no
  partial entry evidence. A passed build binds source hash, output hash, plan ID,
  main Part and verified actual paragraph hashes in one internally created pair.
- Report collections are defensive read-only copies. Artifact bytes are copied
  for each public access. Mutating a returned buffer cannot mutate the retained
  artifact or change the report. SHA-256 covers actual ZIP bytes, not filenames.
- Rollback at this stage means retaining the original bytes: no source write is
  attempted. Day 14 adds [new-directory publication and temporary cleanup](cli-apply.md).
  Existing files are never overwritten; restoring overwritten disk versions
  is not part of the API. Publication status is separate from Build Report.

The facade does not add whole-source binding to the Day 7 plan format. It
revalidates that plan against the snapshot and binds the execution artifact to
the snapshot. Day 13 adds [BoundPatchPlan and BuildBound](cli-plan-and-dry-run.md)
for serialized source/version binding and a second source hash gate before
execution. A mismatch is reported as `BUILD_003`, with no candidate artifact.
Day 16 adds `BuildBoundMulti` for plans returned by
`BoundMultiPatchPlan.Create` or `Parse`. With no options supplied this entry
point allows up to 64 operations per batch.
Structural checks do not establish Word/LibreOffice visual fidelity or replace
the remaining macro/ActiveX/OLE/signature feature policy work.
