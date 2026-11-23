# CLI apply and verified file publication

Day 14 completes the single-text CLI MVP: inspect → plan → dry-run → apply.
Apply uses the same bound-plan loader and build pipeline as dry-run, then saves
the verified DOCX and Build Report together in a new directory.

## 使用方法

先按照 [plan / dry-run 使用说明](cli-plan-and-dry-run.md) 生成并审阅 `plan.json`。
在项目目录下运行：

```powershell
& .\.dotnet\dotnet.exe src/DocxGuard.Cli/bin/Release/net10.0/DocxGuard.Cli.dll apply "template.docx" --plan "plan.json" --output-dir "result-001" --json
```

`result-001` 必须不存在，它的父目录必须已存在。成功后得到：

```text
result-001/
  output.docx
  build-report.json
```

默认文本输出会显示保存路径；`--json` 返回机器可读结果。原始 DOCX 和计划文件不变。
再次使用相同目录会被拒绝；要生成另一份结果，请指定新的目录名。

```text
docxguard apply [--json] --plan <plan.json> --output-dir <new-directory> [--] <file.docx>
docxguard apply --help
```

Source format, plan size/version checks, option ordering, `--` and `--name=value`
syntax follow the existing commands. `--output-dir` is required; there is no
`--force` or overwrite option. Parent directories are never created implicitly.

## Publication contract

`DocxBuildPublisher.Publish(passedBuild, outputDirectory, token)` accepts only a
passed `DocxBuildResult`. It does not read or execute new plan intent itself.
CLI apply first re-inspects the source, validates the bound JSON plan, rebuilds
using `BuildBound`, and completes output inspection and structural diff.

The publisher then:

1. Resolves a full destination directory path, rejects any existing file or
   directory (including empty directories and detected symbolic links), and
   requires an existing parent. Ancestor reparse points/symlinks are rejected.
   Windows path components ending in dot/space or containing alternate-stream
   colons are rejected.
2. Creates a uniquely named sibling `.docxguard-<random>.tmp` staging directory.
3. Writes `output.docx` and UTF-8/no-BOM `build-report.json` using create-new
   streams. Writes observe cancellation and flush file data to disk.
4. Reopens both files and checks their SHA-256 against the in-memory DOCX and
   serialized Build Report. Checks that the staging directory has exactly the
   two expected entries.
5. Rechecks the destination and cancellation, then commits using one
   same-parent `Directory.Move` with no overwrite.

The two files are staged together because separate final file moves cannot
provide a single visibility boundary for the pair. On local filesystems that
support atomic same-volume directory rename, the final directory appears with
both completed files. This is not a power-loss-durable database transaction:
directory metadata is not fsynced, abrupt process termination can leave staging
files, and network/unusual filesystem behavior is not covered. Use a trusted
local output parent; the implementation does not defend against a hostile
process continuously replacing parent/staging paths between filesystem calls.

No final file is overwritten. In particular, pointing `--output-dir` at the
source file, plan file or any existing directory is rejected. The original
input is retained, so publication does not need to restore an overwritten
source version. Committed result directories are not automatically deleted.

## Failure, cancellation and cleanup

Before commit, ordinary failure or cancellation triggers best-effort cleanup
of only the files opened by this attempt. Directory deletion is non-recursive.
Unexpected files in the staging directory are preserved. If removal fails,
`PUBLISH_004` findings and `residualPaths` identify the retained paths; cleanup
failure is never silently reported as success. No cleanup touches a competing
destination created by another process.

Once the directory move succeeds, publication is complete. Later cancellation
does not roll back or mislabel that result. If console output fails after commit
(for example a broken pipe), the result directory can still exist; inspect it
before retrying. No retry overwrites it.

## Result and exit codes

The command uses the v1 planning-command envelope and adds `publication`.
`buildReport` describes verified content; `publication` describes file delivery.
They are separate: a build can pass while publication fails.

| Exit | Meaning |
|---|---|
| 0 | Both files published, or help shown |
| 2 | Usage or source extension error |
| 3 | Source/plan input I/O failure |
| 4 | Document, bound plan or build rejected; publication not attempted |
| 6 | Destination refused, disk write/commit/verification failed |
| 130 | Cancelled before publication commit |

In JSON mode, handled results produce one object on stdout and no stderr.
Default text mode uses stdout for success and stderr for failure. A successful
command has `status: published`; publication contains `status: published`,
absolute `directory`, `docxPath`, `reportPath`, `reportSha256`, empty findings
and empty residual paths. Failure/cancellation publishes no final paths in that
object; partial staging is identified only through diagnostics/residual paths.

The persisted Build Report is exactly `build.Report.ToJson()` encoded as UTF-8.
Its `outputPackageSha256` identifies `output.docx`; the command receipt's
`reportSha256` identifies the report file. Neither is a digital signature.

| Code | Meaning |
|---|---|
| `PUBLISH_001` | Existing/invalid output destination or parent policy rejection |
| `PUBLISH_002` | Filesystem write, flush, path handling or commit failure |
| `PUBLISH_003` | Staged file hash or inventory mismatch |
| `PUBLISH_004` | Temporary cleanup failed; see retained path |
| `PUBLISH_005` | Cancelled before commit |

## Validation and remaining scope

Tests cover actual CLI subprocess publication, Strict/Transitional fixtures,
Chinese text, saved-file hashes, source/plan immutability, repeat publication,
competing empty/nonempty destinations, partial writes, flush/move failures,
staged corruption, cancellation on both sides of commit, cleanup failures and
preservation of unowned staging content.

This completes the one-text-patch MVP, not unrestricted Word editing. Multi-patch
transactions, Agent integration, broad dangerous-feature policy, schema coverage,
and Word/LibreOffice visual/real-template evaluation remain later work.
