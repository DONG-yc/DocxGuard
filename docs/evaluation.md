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
