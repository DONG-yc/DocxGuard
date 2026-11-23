# CLI plan / dry-run and source-bound plans

Day 13 adds reviewable JSON plans and full in-memory build previews. These
commands read files and write console output; neither command saves a DOCX.

## Generate, save and preview a plan

From the repository root after a Release build, using the local SDK:

```powershell
$planText = & .\.dotnet\dotnet.exe src/DocxGuard.Cli/bin/Release/net10.0/DocxGuard.Cli.dll plan "template.docx" --find "旧项目名称" --replace "新项目名称" --json
if ($LASTEXITCODE -ne 0) { throw "Plan generation failed; inspect the diagnostic output." }
$planText | Set-Content -LiteralPath "plan.json" -Encoding utf8

& .\.dotnet\dotnet.exe src/DocxGuard.Cli/bin/Release/net10.0/DocxGuard.Cli.dll dry-run "template.docx" --plan "plan.json" --json
```

The shell explicitly saves the plan in this example. The command itself writes
no files. Check the process exit code before saving its output as a plan.
Use a distinct `.json` destination. Windows PowerShell's plain `>` can emit
UTF-16; `Set-Content -Encoding utf8` produces supported input, with or without
a UTF-8 BOM. To use a system SDK, replace `.\.dotnet\dotnet.exe` with `dotnet`.

```text
docxguard plan <file.docx> --find <text> --replace <text> [--paragraph <id>] [--json]
docxguard dry-run <file.docx> --plan <plan.json> [--json]
```

Both commands support standalone `--help` / `-h`, options before/after the source
path, and `--` to end option parsing. Value options also support `--name=value`.
Use `--replace=` to delete the matched text, including in shells that discard
empty arguments. Actual Tab/LF characters are accepted; literal `\t` / `\n`
are not interpreted as escapes. CR replacement is rejected by the existing
planner. Empty or XML-unsafe searches/replacements follow Core diagnostics.

Matching is exact, case-sensitive and confined to a paragraph. Repeated matches
are never silently resolved. Use `inspect` paragraph IDs with `--paragraph` to
scope a request. Multiple occurrences within the same paragraph still require
a more specific search. Failure output includes total candidate count and up to
20 candidate IDs/ranges; it does not produce a partial plan.

## Output and exit codes

| Exit | Meaning |
|---|---|
| 0 | Plan published, dry-run passed, or help shown |
| 2 | Command syntax / unsupported source extension |
| 3 | Source or plan file I/O failure |
| 4 | Source, serialized plan, planning request, or build rejected |
| 5 | Plan generation found no match or ambiguous matches |
| 130 | Cancelled |

Successful `plan` always emits one source-bound plan JSON object on stdout,
even without `--json`. Failures are human-readable on stderr by default;
`--json` puts one structured command result on stdout with stderr empty.
Standalone help remains text. Duplicate/unknown options are usage errors.

`dry-run --json` emits a result with `schemaVersion: 1.0`,
`profile: docxguard.planning-command.v1`, `command`, `status`, `exitCode`,
`sourcePackageSha256`, `planId`, `findings`, candidate summary, and optional
`inspection` / `buildReport`. `buildReport` is the complete Day 11 report when
building was attempted; it is null if an earlier gate rejected the request.
Raw XML/DOCX buffers are never emitted. Successful output hashes identify the
in-memory candidate only, not a saved file. Default text mode prints check
statuses and an explicit no-file-saved notice. Untrusted strings are escaped.

All expected errors keep their Core diagnostic codes. CLI errors retain
`DOCXGUARD_CLI_001` through `004`. Consumers should check profile, status and
exit code rather than assume every JSON object is a reusable plan.

## Bound plan JSON v1.0

The Core `BoundPatchPlan` wraps the existing `PatchPlan` with:

- `schemaVersion: 1.0`, `profile: docxguard.bound-plan.v1`;
- SHA-256 of the complete source ZIP bytes;
- relationship-selected logical main Part and conformance family;
- original request (expected text, replacement text, optional paragraph scope);
- the full deterministic plan, operation IDs, target range, expected paragraph
  text/hash and every contributing Run slice.

The full plan includes document text. Review and store it as document data.
The source hash identifies exact bytes; even media-only changes or equivalent
recompression require a new plan. Plan IDs retain Day 7 semantics and do not
alone identify the source package: always keep the wrapper.

Loading does not instantiate trusted operations from arbitrary JSON. It:

1. Bounds input to 4 MiB and JSON depth to 32; accepts UTF-8 and optional BOM.
2. Rejects duplicate properties at every depth, malformed types, comments and
   trailing commas. Property order and insignificant whitespace may differ.
3. Requires the exact schema/profile and matching source package hash.
4. Regenerates a unique plan from the verified source index and recorded request.
5. Compares all wrapper and nested plan fields to the regenerated representation;
   missing, extra or inconsistent fields cannot enter execution.
6. `BuildBound` captures the source again and checks its hash before mutation,
   covering changes between plan loading and the build snapshot.

Unknown fields are rejected for this executable-intent format. This differs
from additive report fields, which consumers may ignore. A new incompatible
schema/profile requires an explicit supported implementation. Plans at this
stage use the existing default planning, package, XML, index and execution limits.

| Code | Meaning |
|---|---|
| `BPLAN_001` | Source inspection did not pass |
| `BPLAN_002` | Serialized plan exceeds 4 MiB |
| `BPLAN_003` | Unsupported schema or profile |
| `BPLAN_004` | Source ZIP hash mismatch |
| `BPLAN_005` | Plan/request inconsistent with source-derived plan |
| `BPLAN_006` | Malformed JSON, types, missing fields, encoding or duplicates |
| `BUILD_003` | Source changed before the actual build snapshot |

Bound plans are not signed approvals. A party can generate another internally
consistent plan; source binding and regeneration do not authenticate who
approved it. When changing intent, generate and review a new plan. The low-level
Day 7 `PatchPlan` and `Build` remain available with their original paragraph-only
preconditions; CLI uses `BoundPatchPlan` and `BuildBound`.

## Preflight is not full editing support

A unique plan can still target a Run that execution rejects (fields, revisions
or complex content). Dry-run executes the real in-memory executor, package
rebuilder, full output inspection and independent structural diff, so it exposes
those failures. No original or output file is changed. Word/LibreOffice visual
fidelity and the remaining macro/ActiveX/OLE/signature policy are unchanged.
Day 14 now provides [apply and directory publication](cli-apply.md). The same
command envelope gains an optional `publication` receipt; plan/dry-run keep
their existing behavior and never publish DOCX files.
