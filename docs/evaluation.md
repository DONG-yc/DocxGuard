# Day 19 真实 DOCX 评测基线

本轮使用用户提供的一份中文实验报告，在隔离目录里运行 DocxGuard MCP。样本及含原文的用例配置留在本机，项目只记录结构统计和评测结果。

## 样本与结果

样本约 4.82 MiB，采用 Transitional OOXML。DocxGuard 检查得到 136 段、139 个 Run、3 张表、67 个表格单元格和 29 个包条目。

| 用例 | 预期 | 结果 |
| --- | --- | --- |
| 标题的一项替换 | 发布新 DOCX 和报告 | 通过 |
| 正文与表格单元格的两项替换 | 整批发布 | 通过 |
| 正文目标有效，表格段落内同词重复 | `PLAN_002`，拒绝整批 | 通过 |
| 未指定段落的重复短语 | `PLAN_002` | 通过 |
| 不存在的目标 | `PLAN_001` | 通过 |
| 两个修改范围相交 | `MPLAN_004` | 通过 |
| 预演后篡改计划 | `MPLAN_007`，拒绝发布 | 通过 |

预期成功用例 2/2，预期拒绝用例 5/5。两个成功输出都只改变主文档 XML，其余 28 个包条目逐项保留原始内容哈希。预演、发布后的 DOCX 和保存的构建报告给出相同的输出哈希。预期拒绝用例没有发布产物，原附件和测试输入副本的 SHA-256 在运行前后相同。

首次将「同一单元格里重复的目标」当作成功用例时，计划阶段发现同段内有两个匹配，按预期拒绝规则返回 `PLAN_002`。最终保留这个拒绝用例，并选取一个唯一匹配的表格目标验证成功批处理。

本轮评测的是真实 MCP 进程与 DOCX 结构保留。尚未独立评测模型如何选择工具。文档技能的渲染器在本机找不到 `soffice.exe`，未生成页面图片，因此分页、字体和视觉布局仍待验证。用例比例来自一份样本，适合作为回归基线。

## 复跑方法

用 .NET 10 构建 Release 版本后，在项目目录运行：

```text
node scripts/eval-docxguard.mjs path/to/input.docx path/to/cases.json
```

`cases.json` 是一个 JSON 数组。以下是使用示例，文字需要与本次输入匹配。

```json
[
  {
    "name": "single-replacement",
    "mode": "success",
    "requests": [
      { "expectedText": "原标题", "replacementText": "新标题" }
    ]
  },
  {
    "name": "missing-target",
    "mode": "reject-plan",
    "expectedCode": "PLAN_001",
    "requests": [
      { "expectedText": "不存在的目标", "replacementText": "新标题" }
    ]
  }
]
```

`success` 会预演和发布临时测试产物。`reject-plan` 检查计划阶段诊断。`reject-tampered` 在预演通过后改动计划，再检查发布是否拒绝。`paragraphScopeId` 可使用检查结果中的段落 ID。脚本将状态、诊断码、计时和结构统计输出为 JSON，正常退出表示所有预期结果一致。

## Day 23 自然语言定位盲测

先构建 Release CLI，再用带 python-docx、lxml 和 Pillow 的 Python 执行 `python scripts/eval-agent-targets.py prepare <新目录>`。分别将生成的 atlas、delta 工作区交给两个独立 Agent。两者只接收各自的工作区、项目编辑说明、MCP 调用方式和同一条请求。

> 帮我在站点采样统计的表里追加南站 42 个样本，再用 new.png 换掉比较各站采样量的图。只预览。

通过标准输入向 `python scripts/eval-mcp-call.py <分配的工作区>` 提交 JSON，method 可为 tools/list 或 tools/call，params 使用 MCP 对应参数。传输脚本只负责启动本地服务、转发调用并在工作区旁记录交互。发布权限关闭。

完成后运行 `python scripts/eval-agent-targets.py check <目录>`。唯一匹配用例核对预演的目标表格、单元格内容、图片部件及原件哈希。歧义用例检查工具调用在预演前停止，并人工核对最终答复是否请用户在春季图与秋季图之间选择。两个工作区均应只保留原始输入文件。

本轮首次运行中断后，两名独立 Agent 在原工作区重新执行。atlas 正确定位第 2 张表，预演追加“南站｜42”并替换春季图；delta 找到同一张表，在预演前明确询问春季还是秋季。两个源文件的哈希均与发现时一致，未发布文件。日志保留了中断前的调用，因此 atlas 有两次发现记录。本轮覆盖两个合成样本，未测试用户答复后的多轮续接。
## Day 24 澄清后续接

在 Day 23 的 delta 会话中，仅向提出澄清的原 Agent 回复“秋季图。”。验收器将澄清前的日志快照与追加的 MCP 调用分开检查。预演报告包含第 2 张表的新增行“南站｜42”，差异仅涉及主文档和秋季图片部件 `word/media/image3.png`。春季图片未列入差异。两个输入文件的 SHA-256 与回复前一致，工作区未发布新文件。本次记录是一例两轮请求。

复核本机记录可运行 `python scripts/eval-agent-targets.py check artifacts/day23 --before artifacts/day24/before.jsonl`。复跑新样本时，在第一轮澄清结束后复制 delta.jsonl，向同一 Agent 提交澄清答复，再将快照路径传给 `--before`。
