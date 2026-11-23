# DocxGuard MCP Server

Day 17 adds a local MCP server over stdio. The server shares the CLI's plan loading, build checks, and publication logic. The official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk/tree/v2.3.1) handles the protocol and transport.

## 启动

在项目目录完成 Release 构建，再把命令和参数交给支持 stdio 的 MCP 客户端。下面是 Windows 环境的示例，`--workspace` 路径必须事先存在。

```powershell
.\.dotnet\dotnet.exe restore --locked-mode --configfile NuGet.Config
.\.dotnet\dotnet.exe build --configuration Release --no-restore
.\.dotnet\dotnet.exe .\src\DocxGuard.Cli\bin\Release\net10.0\DocxGuard.Cli.dll mcp --workspace "D:\DocxWorkspace"
```

为客户端设置程序和参数时使用绝对路径。命令只有加上 `--allow-apply` 才会提供写入工具。工具运行在 MCP 客户端启动的本地进程中。

## 工具

| 工具 | 参数 | 结果 |
| --- | --- | --- |
| `docxguard_inspect` | `sourcePath`，可选 `offset` 和 `limit` | DOCX 结构、分页段落预览、诊断 |
| `docxguard_plan` | `sourcePath`、`requests` | 与 CLI `multi-plan` 兼容的来源绑定计划 |
| `docxguard_dry_run` | `sourcePath`、`planJson` | 内存构建报告 |
| `docxguard_apply` | `sourcePath`、`planJson`、`outputDirectory` | 构建报告及新目录发布凭据 |

`requests` 接受 1–64 项，每项包含 `expectedText`、`replacementText` 和可选的 `paragraphScopeId`。段落 ID 可从检查结果取得。`planJson` 是 `docxguard_plan` 返回的 JSON 文本。CLI 生成的单补丁和多补丁 JSON 也可作为该参数。预演与发布都会重新核对同一源文件的哈希、计划内容和前置条件。

工具同时返回 `structuredContent` 和可直接读取的 JSON 文本。请求被拒绝时 `isError` 为真，诊断保留 Core 或 CLI 已有错误码。MCP 入口另返回 `MCP_001`（工具参数无效）、`MCP_002`（路径访问失败）或 `MCP_003`（已有操作在运行）。

## 文件边界

工具的输入和输出路径都相对 `--workspace`。绝对路径、上级目录、设备名、符号链接和 junction 路径会被拒绝。使用受信任的文件夹，并通过操作系统权限控制其他进程的访问。路径校验无法防止同机进程在检查后并发替换目录。

发布前应由 MCP 客户端向用户确认。本地启动参数只授权提供发布工具。发布目标必须是尚不存在的目录，且其父目录已经存在。批次在全部通过复检后一起发布 DOCX 和报告。DOCX 段落内容和计划中的原文会作为数据返回给调用方。

## 使用 Agent Skill

在已接入 DocxGuard MCP 的项目会话中，可以显式调用 [`$docxguard-edit`](../.agents/skills/docxguard-edit/SKILL.md)。例如：

> $docxguard-edit 把 template.docx 正文中的「旧项目名」替换成「新项目名」，先给我预演结果。

同一个词命中多个位置时，可以先选择段落，再生成范围明确的计划。预演通过后，可确认这份计划和新的输出目录，再调用发布工具。
# 表格与 PNG 组合编辑

启动 CLI 或 MCP 前，设置 `DOCXGUARD_PYTHON` 为已安装 Pillow、lxml 的 Python 解释器绝对路径。文本工具可独立使用。构建和发布 CLI 时会一同复制图片处理脚本。

运行 `docxguard table-figure list input.docx`，或调用 `docxguard_table_figure_list`。按表头和相邻正文识别目标，从同一次发现结果取 `sourceSha256`、从 1 开始的 `table` 和 `imageRelationship`。CLI 的可选参数为 `offset limit`，MCP 使用同名字段。每页默认 50 项，最多 100 项；有后续结果时，按本页返回数量推进 offset。相邻文本最多展示 160 个字符，表头最多展示 32 格。重复标题需要结合表头或其他上下文确认目标。

MCP 支持直接提交 `requestJson` 字符串，也支持已有文件的 `requestPath`。预演和发布每次选择其中一种。以下为请求内容示例。

```json
{
  "sourceSha256": "<sourcePackageSha256>",
  "table": 1,
  "rows": [["新条目", "公开数据", "152 条", "工程测试"]],
  "imageRelationship": "rId7"
}
```

```text
docxguard table-figure preview input.docx request.json replacement.png
docxguard table-figure apply input.docx request.json replacement.png <outputSha256> new-result
```

MCP 的 `docxguard_table_figure_preview` 接收工作区相对路径 `sourcePath`、`requestPath`、`figurePath`。核对报告中的目标表格、行内容、图片关系和部件列表。确认发布后，向 `docxguard_table_figure_apply` 提交同样的三个路径、`previewSha256`（预演报告中的 `buildReport.outputSha256`）和新目录 `outputDirectory`。发布工具只在启动时启用 `--allow-apply` 后注册。

当前支持 Transitional DOCX 正文顶层表格，复制末行样式，追加 1 至 64 行，每格一个普通文本节点。替换图须为相同像素尺寸的 PNG，并由主文档独占引用。预演与发布都会重建、复检和比对包条目。发布时再次匹配预演哈希，将 `output.docx` 和 `build-report.json` 一并放入新目录。分页复核另需排版工具。
