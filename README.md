# DocxGuard

DocxGuard is a contract-driven, verifiable DOCX template compiler for AI agents.

The project builds editing capability through explicit trust, planning, execution, and verification boundaries rather than unrestricted document mutation.

## Current status

Day 11 adds `DocxBuildPipeline` and a versioned JSON `BuildReport`, pairing
verified in-memory DOCX bytes with source/output hashes, operation evidence,
check statuses, diagnostics and entry comparisons. It captures one bounded
source snapshot, validates the plan, executes and rebuilds through the existing
gates. Failed builds expose no candidate artifact. See the
[report contract](docs/build-report.md),
[ADR 0011](docs/adr/0011-versioned-build-report.md), and
[project progress](docs/project-progress.md).

Day 12 exposes read-only CLI `inspect` with text/JSON output, paged paragraph
previews and stable exit codes. See [CLI usage](docs/cli-inspect.md) and
[ADR 0012](docs/adr/0012-read-only-inspect-cli.md).

Day 13 adds source-bound JSON `plan` and full in-memory `dry-run`. Plans bind
schema/profile, the exact source ZIP hash, main Part and all typed preconditions.
Loading regenerates and compares the complete plan; `BuildBound` rechecks source
identity before execution. See [usage and protocol](docs/cli-plan-and-dry-run.md)
and [ADR 0013](docs/adr/0013-source-bound-plan-and-dry-run.md).

Day 14 completes the single-text CLI MVP with `apply --output-dir`. A passed
build is staged, flushed and hash-checked before its DOCX/report directory is
published. Existing destinations are never overwritten. Failures clean only
owned staging files and report residual paths. See [apply usage](docs/cli-apply.md)
and [ADR 0014](docs/adr/0014-directory-publication.md).

Day 15 adds `multi-plan`, which reads a JSON request array and emits a
source-bound multi-patch plan. Any unresolved request or overlapping target
rejects the whole batch. See [ADR 0015](docs/adr/0015-multi-patch-conflict-plan.md).

Day 16 lets `dry-run` and `apply` consume the batch plan. Every operation is
checked against the original document, and the rebuilt DOCX must pass a full
inspection and structural diff before publication. The batch report uses the
`docxguard.multi-text.v1` profile. See [multi-plan usage](docs/cli-multi-plan.md).

Day 17 exposes inspection, planning, and preview over local MCP stdio.
Publication is available when the server is started with `--allow-apply`.
See the [MCP server guide](docs/mcp-server.md).

Day 18 adds the repo-scoped [`docxguard-edit` Skill](.agents/skills/docxguard-edit/SKILL.md).
With DocxGuard MCP connected, invoke `$docxguard-edit` to inspect a template,
disambiguate exact text targets, preview the batch, and confirm the output
directory before publication.

Day 10 adds bounded DOCX rebuilding and independent structural-diff verification
to the preconditioned in-memory text executor. The full pipeline builds on
the Day 2 ZIP, Day 3 Content Types, Day 4 Relationships, Day 5
WordprocessingML, Day 6 document-index, and Day 7 PatchPlan gates:

- EOCD inspection that enforces entry count before .NET materializes central-directory objects;
- configurable archive, entry-size, total-size, name-length, and compression-ratio limits;
- strict raw UTF-8 decoding plus byte-for-byte local-header/central-directory consistency checks;
- platform-independent rejection of absolute, traversal, backslash, encoded-separator, and malformed entry names;
- case-insensitive and percent-decoded duplicate-name detection;
- explicit rejection of encrypted entries;
- exact checks for `[Content_Types].xml` and `_rels/.rels`;
- contiguous physical-record validation, including bounded data descriptors;
- independent bounded reads of raw Stored/Deflate spans to EOS, with actual-length, actual-ratio, and ZIP CRC-32 verification;
- stable structured findings without extracting or modifying input files.

After that preflight passes, `OpcPackageInspector` also:

- safely parses `[Content_Types].xml` with DTD/external resolution disabled and explicit resource limits;
- enforces the exact OPC namespace, element shape, attributes, extension grammar, logical part names, and media types;
- applies Override-before-Default resolution with ASCII-only case matching;
- maps every physical package Part to a typed logical `OpcPartName`;
- verifies complete content-type coverage for non-Relationships Parts and
  applies the fixed OPC content type to Relationships Parts;
- fails closed without publishing a partial manifest or inventory.

Day 4 then:

- identifies the package or Part that owns each Relationships part;
- parses every `.rels` stream with the same DTD-free, bounded XML policy;
- validates namespace, element shape, attributes, unique IDs, absolute
  relationship Types, and exact TargetMode values;
- resolves internal relative targets with package-root and source-Part rules;
- requires every internal target to exist and prohibits relationships from or
  to Relationships parts;
- rejects external relationships in the controlled V1 profile;
- publishes an immutable `RelationshipGraph` only when the whole layer passes.

After the OPC result passes, `DocxDocumentInspector`:

- finds exactly one package-level `officeDocument` relationship instead of
  assuming the main Part is always `/word/document.xml`;
- supports the Transitional and Strict relationship/namespace families and
  requires them to agree;
- requires the target to use the macro-free DOCX main-document content type;
- parses the verified main Part with DTD and external resolution disabled;
- enforces separate main-document byte, character, node, depth, body-child,
  attribute-count, and attribute-value limits;
- verifies one `document` root, an optional `background`, exactly one
  `body`, and final-position `sectPr`; and
- publishes an immutable `WordMainDocumentInfo` only when the whole Word layer
  passes.

After the Word result passes, `DocxIndexInspector`:

- reparses only the relationship-selected and already verified main Part with
  the same bounded, DTD-free XML policy;
- publishes stable paragraph, run, table, and cell IDs in document order;
- reconstructs text split across runs while retaining half-open UTF-16 offsets
  for mapping a match back to each source run;
- applies an explicit visible-text profile for tabs, breaks, hyphens, objects,
  insertions, deletions, and move revisions;
- records nested-table and parent-cell ownership in both directions;
- enforces independent paragraph, run, table, cell, nesting, and text limits;
  and
- fails closed without a partial index when structure or MCE branch ownership
  is ambiguous.

After a document index passes, `TextLocator` and `TextPatchPlanner`:

- perform exact, ordinal, case-sensitive searches inside individual
  paragraphs;
- retain all overlapping candidates and never fall back to the first match;
- allow explicit paragraph-ID scope to resolve repeated template text;
- map every candidate back to half-open slices in the contributing runs;
- publish explicit no-match, unique, ambiguous, and rejected states;
- compile only one unique supported target into a deterministic typed
  `PatchPlan`; and
- attach full paragraph text, SHA-256, target range, expected text, and run
  slices as mandatory execution preconditions.

`PatchPreconditionValidator` and `InMemoryTextPatchExecutor` then:

- recheck paragraph identity, complete text, SHA-256, exact target range, and
  run slices before mutation;
- apply disjoint planned replacements across plain, non-revised `w:t` runs;
- preserve each run and its `w:rPr` formatting subtree;
- map replacement tabs and LF characters to `w:tab` and `w:br`;
- enforce operation, affected-run, generated-node, and output-byte limits;
- re-run Word structure and document indexing against the output; and
- publish a private-copy main-document XML artifact only when every logical
  postcondition passes.

`DocxPackageRebuilder` and `DocxStructuralDiffVerifier` now:

- bind executed XML patches to the SHA-256 of the exact source package;
- re-inspect a private source snapshot and replace the physical entry selected
  by the main-document relationship, including encoded/nonstandard names;
- copy all other decompressed entry bytes in source order into a new bounded ZIP;
- enforce output archive, per-entry, and total uncompressed-size limits;
- re-run ZIP, OPC, Word, and index checks on the completed output;
- independently compare the entry inventory and SHA-256 of every entry;
- verify planned text/tab/break tokens and compare all remaining main XML,
  including run properties, section properties, namespaces, comments, and instructions;
- reject changes outside that allowlist and publish no partial artifact; and
- publish private-copy DOCX bytes and complete per-entry comparison evidence.

Day 9/10 produce a complete DOCX **in memory**; Day 11 adds its versioned report.
Day 14 adds directory-based disk publication. Rebuilt entries use no compression, so the
output can be larger than the input. Existing package/XML/index limits still
apply. An SDK-generated fixture is reopened and schema-validated in tests;
this is not a claim of Word/LibreOffice visual compatibility or unchanged pagination.

The CLI supports help, version, `inspect`, `plan`, `multi-plan`, `dry-run` and `apply`.
Dry-run performs real execution and verification in memory. Apply publishes
the verified DOCX and matching report into a new result directory.

## Quick start

Prerequisite: a .NET 10 SDK. `global.json` accepts the latest installed .NET 10 feature band. If the SDK is installed project-locally, replace `dotnet` below with `.\.dotnet\dotnet.exe`.

```powershell
dotnet restore --locked-mode --configfile NuGet.Config
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --no-restore
dotnet run --project src/DocxGuard.Cli --configuration Release --no-build --no-restore -- --help
```

The current CLI surface is deliberately small:

```text
docxguard --help
docxguard --version
docxguard inspect "template.docx"
docxguard inspect "template.docx" --json --offset 0 --limit 20
docxguard plan "template.docx" --find "old text" --replace "new text" --json
docxguard multi-plan "template.docx" --requests "requests.json" --json
docxguard dry-run "template.docx" --plan "plan.json" --json
docxguard apply "template.docx" --plan "plan.json" --output-dir "result-001" --json
```

Inspect returns `0` for passed inspection, `2` for usage/format errors, `3` for
input I/O errors, `4` for rejected documents and `130` for cancellation.
`--json` emits one JSON report to stdout for handled outcomes. The command
does not modify the source. Preview pagination does not skip safety checks.

```powershell
dotnet run --project src/DocxGuard.Cli --configuration Release --no-build --no-restore -- inspect "template.docx" --json
```

### Core build and report API

For a passed build, `new DocxBuildPublisher().Publish(result, "result-001")`
returns a separate publication receipt. The parent must exist and the result
directory must not exist. Publication failures have CLI exit code `6`; the
Build Report can still be passed when file delivery failed. See the apply
contract for local-filesystem assumptions and cleanup behavior.

For source-bound serialized intent, use `BoundPatchPlan.Create` / `Parse` and
`DocxBuildPipeline.BuildBound`. The lower-level `Build` API below retains its
original paragraph-precondition contract. Plans are not signed approvals.

Given a typed `PatchPlan` from `TextPatchPlanner`:

```csharp
using FileStream source = File.OpenRead("template.docx");
DocxBuildResult result = new DocxBuildPipeline().Build(source, plan);
Console.WriteLine(result.Report.ToJson());
if (result.Status == DocxBuildStatus.Passed)
{
    ReadOnlyMemory<byte> verifiedDocx = result.Artifact!.Bytes;
}
```

These types are in `DocxGuard.Core.Building`. The stream remains open with its
original position; the API performs no output file writes. Reports use schema
version `1.0`, string statuses and explicit null unknown/output values. Text
summaries use hashes and lengths; reports are not executable approval records.

### Core rebuild API

Given a passed index inspection and a successfully executed XML patch:

```csharp
DocxPackageRebuildResult rebuilt = new DocxPackageRebuilder().Rebuild(
    "template.docx", execution.Artifact!);

if (rebuilt.Status == DocxPackageRebuildStatus.Passed)
{
    ReadOnlyMemory<byte> docx = rebuilt.Artifact!.Bytes;
    Console.WriteLine(rebuilt.Artifact.OutputPackageSha256);
    Console.WriteLine(rebuilt.StructuralDiff!.Entries.Count);
}
```

The rebuild types are in `DocxGuard.Core.Building`. See the
[rebuilding contract](docs/safe-package-rebuilding.md) for the complete input
contract and the [structural diff contract](docs/structural-diff.md) for the
independent candidate-verification API.

## Repository layout

```text
src/
├── DocxGuard.Core/       Inspection, planning, execution, rebuilding, verification
└── DocxGuard.Cli/        Arguments, output, and exit codes
tests/
└── DocxGuard.Tests/      Core, CLI, and adversarial contract tests
docs/
└── adr/                  Architecture decisions
```

Dependency direction:

```text
DocxGuard.Cli   ──→ DocxGuard.Core ──→ DocumentFormat.OpenXml
                                     └→ SharpZipLib raw Inflater
DocxGuard.Tests ──→ DocxGuard.Core + DocxGuard.Cli
```

## V1 guardrails

- Input documents are untrusted and immutable.
- V1 supports only `.docx` gold templates.
- No in-place overwrite is permitted.
- Ambiguous or structurally invalid input must fail closed.
- A ZIP safety preflight must run before Open XML processing.
- External relationships, macros, encryption, and signed files are outside V1.

See the [package preflight contract](docs/package-preflight.md),
[OPC Content Types contract](docs/opc-content-types.md),
[OPC Relationships contract](docs/opc-relationships.md),
[Word main-document contract](docs/word-main-document.md),
[read-only document index contract](docs/document-index.md),
[text location and PatchPlan contract](docs/text-location-and-patch-planning.md),
[in-memory text patch execution contract](docs/in-memory-text-patch-execution.md),
[safe package rebuilding contract](docs/safe-package-rebuilding.md),
[structural diff contract](docs/structural-diff.md),
[ADR 0001](docs/adr/0001-v1-scope.md),
[ADR 0002](docs/adr/0002-safe-package-preflight.md),
[ADR 0003](docs/adr/0003-opc-content-types.md),
[ADR 0004](docs/adr/0004-opc-relationship-graph.md),
[ADR 0005](docs/adr/0005-word-main-document-boundary.md),
[ADR 0006](docs/adr/0006-read-only-document-index.md),
[ADR 0007](docs/adr/0007-exact-location-and-patch-plan.md),
[ADR 0008](docs/adr/0008-preconditioned-in-memory-text-execution.md),
[ADR 0009](docs/adr/0009-safe-package-rebuilding.md), and
[ADR 0010](docs/adr/0010-allowlisted-structural-diff.md) for the
full decisions and residual risks.
