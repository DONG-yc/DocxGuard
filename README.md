# DocxGuard

DocxGuard 是给开发者和 AI Agent 使用的 Word 模板编辑工具。已有一份排好版的实验报告、项目周报或统计表时，可以用它精确替换文字，或在同一次操作中追加表格行并替换配图。每次修改都先生成可核对的预演结果，确认后将新 DOCX 和修改报告发布到新目录，原模板保留。

## 可以怎样使用

| 场景 | 操作 | 可核对的结果 |
|---|---|---|
| 批量更新模板文字 | 指定原文、替换文字，必要时限定段落 | 跨格式片段定位，遇到歧义或重叠时报告原因 |
| 更新统计报告 | 根据表头和图题选择目标，追加数据行并替换同尺寸 PNG | 查看新增行、图片引用、发生变化的文件条目和输出哈希 |
| 通过 Agent 操作 | 使用 MCP 检查文档、澄清目标、预演修改 | 对比图有春秋两张时可以先询问；发布需启用发布工具 |

## 五分钟演示

以下命令适用于安装了 .NET 10 SDK 和 Python 3.12 的 Windows。在仓库根目录运行，脚本会生成一份站点报告，再给表格追加 South 的 42 个样本，并同步更新柱状图。

```powershell
dotnet restore DocxGuard.sln --locked-mode
dotnet publish src/DocxGuard.Cli -c Release --no-restore -p:UseAppHost=false -o artifacts/release
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r scripts/requirements-demo.txt
.\.venv\Scripts\python.exe scripts/demo-release.py artifacts/release/DocxGuard.Cli.dll artifacts/demo
```

打开 `artifacts/demo/template.docx` 和 `artifacts/demo/result/output.docx` 比较表格和图表；同目录的 `build-report.json` 记录改动条目和哈希。再次演示时使用新的输出目录。

## 接入自己的文档

- **文字更新**：从 `inspect` 检查段落开始，用 `plan` 或 `multi-plan` 生成计划，再运行 `dry-run` 和 `apply`。[单项计划](docs/cli-plan-and-dry-run.md) · [批量计划](docs/cli-multi-plan.md)
- **表格与图片**：运行 `table-figure list` 获取表格序号与图片引用，准备数据和 PNG 后运行 `preview`。发布时需要提供预演的输出哈希。[组合编辑与 MCP 使用说明](docs/mcp-server.md)
- **支持范围**：组合编辑支持 Transitional DOCX 正文中的顶层普通表格、可复制的简单末行，以及独占引用的同尺寸 PNG。合并单元格、共享图片等情况会被拒绝。分页效果需通过 Word 等排版工具另行核对。
- **实现与验证**：[架构和演示讲解](docs/project-walkthrough.md) · [评测报告](docs/evaluation.md) · [项目进度](docs/project-progress.md)

## 许可证

DocxGuard 的自有代码采用 [MIT 许可证](LICENSE)。第三方依赖和外部素材遵循各自的许可证。

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
