# CLI inspect contract

Day 12 exposes the existing read-only ZIP → OPC → Word → document-index
inspection pipeline through the CLI. No file is extracted or modified.

## Usage

```powershell
dotnet run --project src/DocxGuard.Cli --configuration Release --no-build --no-restore -- inspect "template.docx"
dotnet run --project src/DocxGuard.Cli --configuration Release --no-build --no-restore -- inspect "template.docx" --json
dotnet run --project src/DocxGuard.Cli --configuration Release --no-build --no-restore -- inspect "template.docx" --json --offset 20 --limit 20
```

For the project-local SDK, replace `dotnet` with `.\.dotnet\dotnet.exe`.
Run from the repository directory after a Release build. Relative input paths
resolve against the current working directory. Quote paths containing spaces.

```text
docxguard inspect [--json] [--offset N] [--limit N] [--] <file.docx>
docxguard inspect --help
```

Exactly one `.docx` path is required (extension matching is case-insensitive).
Options may appear before or after the path. `--` ends option parsing and
permits paths starting with `-`. Unknown and repeated options are usage errors.
Offset is a nonnegative decimal integer (maximum 2147483647); limit is 1–1000.
Defaults are offset 0 and limit 20. Signs, fractions and numeric overflow are
rejected. Help is a standalone command option, not combined with input/options.

## Inspection and preview scope

The complete document is inspected using existing default resource limits,
regardless of the requested page. Successful output contains source SHA-256,
relationship-selected main Part, Strict/Transitional conformance, and entry,
Part, relationship, paragraph, Run, table and cell counts.

Each preview contains paragraph ID, ordinal, table-cell ID, Run count, original
UTF-16 text length and at most 200 UTF-16 units of visible text. Truncation never
splits a surrogate pair. `truncated` is explicit. Paragraph IDs and ordinals are
those from the Core index; page `offset` is a separate zero-based list offset.
An offset beyond the paragraph count succeeds with an empty page and
`hasMore: false`. This is a preview, not full document export or exact search.
IDs belong to this inspected document version, not a permanent cross-edit ID.

Console previews and diagnostic strings use JSON string escaping so embedded
tabs/newlines/control characters cannot masquerade as extra output records.
Chinese text stays readable. The process emits UTF-8 without a BOM.

## Output and exit codes

| Exit | Meaning | CLI diagnostic |
|---|---|---|
| 0 | Full inspection passed, or help/version shown | None |
| 2 | Invalid command arguments or unsupported extension | `DOCXGUARD_CLI_001` / `DOCXGUARD_CLI_002` |
| 3 | Input open/read failure, invalid path, access denied or locked file | `DOCXGUARD_CLI_003` |
| 4 | The document was rejected by an inspection layer | Original Core finding codes |
| 130 | Cancellation, including Ctrl+C during inspection | `DOCXGUARD_CLI_004` |

Text mode writes successful summaries to stdout and failed reports to stderr.
JSON mode writes exactly one report to stdout and leaves stderr empty for
handled results, including document rejection, usage and I/O errors. Exit codes
still indicate failure. `--json` is recognized only before `--`. Standalone
help/version and global usage errors retain their text behavior. Unexpected
programming faults and output-writer failures are not disguised as document
errors; a broken output pipe cannot promise a complete JSON report.

## JSON v1.0

`schemaVersion` is `1.0`, `command` is `inspect`, and `profile` is
`docxguard.inspect.v1`. Property names are camelCase. Core conformance names
remain `Strict` / `Transitional`. Reports also contain:

- `engineVersion`, `status` (`passed`/`failed`), `exitCode`;
- `sourcePackageSha256`, `summary`, `paragraphs`;
- `checks`: ordered `package`, `contentTypes`, `relationships`, `word`, `index`
  stages, each with `passed`, `failed` or `notRun` status;
- `findings`: code, severity, stage, message, entry/Part names, line/column and
  relationship ID where available.

`summary` and `paragraphs` are null on any failure: partial models are never
published. The source hash is available only after complete index inspection
passes. `paragraphs` includes `offset`, `limit`, `total`, `hasMore`, and `items`.
For command/I/O errors all document checks are `notRun`. A failed layer leaves
downstream layers `notRun`. Consumers should use codes and fields rather than
parse diagnostic prose; unknown fields within v1 may be ignored.

JSON previews intentionally contain document text. Treat reports as document
data when storing or sharing them. This inspection result is distinct from a
Day 11 Build Report: it is not evidence of a patch or a produced DOCX. Passing
the current profile does not guarantee editability of every Run, complete OOXML
schema validity, unchanged pagination, or Word/LibreOffice visual compatibility.
The current external-relationship restriction and remaining dangerous-feature
policy gaps continue to apply.
