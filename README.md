# DocxGuard

DocxGuard 帮开发者和 AI Agent 更新已有的 Word 报告和模板。比如一份站点统计报告已经排好版，现在要补上“南站，42 个样本”，并换掉对应的统计图。DocxGuard 可以定位表格和图片，先预演这两项修改，确认后再生成新的 Word 文件和修改报告，保留原稿。

## 选择适合的用法

| 你想做什么 | 怎么用 |
|---|---|
| 批量修改项目名、日期等文字 | 在主线版本中用 CLI 检查文档、定位原文、生成替换计划，再预演或发布。支持文字被 Word 拆成多个格式片段的情况。 |
| 给统计表追加数据并同步换图 | 使用下方的表格与图片版本。按表头和图题找到目标，提交新增行和同尺寸 PNG，核对预演结果再发布。 |
| 让 AI Agent 帮忙编辑 | 通过 MCP 提供本地文档工具。比如有春季和秋季两张采样图时，Agent 可以先询问要换哪张，再带着原来的表格要求继续预演。 |

## 跑一次完整演示

[打开已验证的表格与图片版本及安装步骤](https://github.com/DONG-yc/DocxGuard/tree/5ec7f2a8553998036e84b56dc1cbb15152239692#五分钟演示)。在 Windows 上准备 .NET 10 SDK 和 Python 3.12，按该版本 README 运行演示脚本。样例会自行生成，无需准备私人文档。

演示结束后，可以对照三份文件检查结果：

- `template.docx` 是原始站点报告，表格里有 North 和 West 两行数据。
- `result/output.docx` 增加了 South 的 42 个样本，柱状图也同步更新。
- `result/build-report.json` 记录变动的文档部件和输出文件哈希，可用于核对发布内容与预演是否一致。

## 适用范围

适合对已知模板做可复核的定点修改。表格与图片版本目前支持正文中的普通顶层表格，复制末行格式追加数据，并替换独占引用的同尺寸 PNG。合并单元格、共享图片等不支持的目标会被拒绝。涉及换行或跨页时，应打开输出文件核对实际排版。

[文字编辑命令](docs/cli-plan-and-dry-run.md) · [MCP 接入](docs/mcp-server.md) · [新版架构与演示讲解](https://github.com/DONG-yc/DocxGuard/blob/5ec7f2a8553998036e84b56dc1cbb15152239692/docs/project-walkthrough.md) · [新版评测记录](https://github.com/DONG-yc/DocxGuard/blob/5ec7f2a8553998036e84b56dc1cbb15152239692/docs/evaluation.md)

## 许可证

本项目目前尚未指定开源许可证。
