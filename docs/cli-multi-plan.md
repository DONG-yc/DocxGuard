# `multi-plan` 命令

`multi-plan` 从一个 DOCX 和一个 JSON 请求数组生成来源绑定的多补丁计划。它只生成计划，不修改输入文件，也不执行补丁。

```text
docxguard multi-plan template.docx --requests requests.json
```

请求文件必须是 JSON 数组，每项包含 `expectedText`、`replacementText`，还可以包含 `paragraphScopeId`：

```json
[
  { "expectedText": "项目名称", "replacementText": "新项目名称" },
  { "expectedText": "负责人", "replacementText": "张三", "paragraphScopeId": "p-0002" }
]
```

成功时标准输出是 `docxguard.multi-bound-plan.v1` JSON，计划包含每个操作的来源匹配、段落前置条件和输入 DOCX 的 SHA-256。后续执行器必须重新核对这个哈希。

计划层采用全成全败语义：任何请求没有唯一匹配、请求超过 64 项、目标范围在同一段落中重叠，或序列化结果超过 4 MiB，都会拒绝整个计划。操作 ID 按请求顺序分配；为了让从后往前的执行安全，计划中的操作按段落和偏移倒序排列。

保存标准输出中的计划后，用同一源文件运行预演和发布。

```text
docxguard dry-run template.docx --plan batch.json --json
docxguard apply template.docx --plan batch.json --output-dir result-001 --json
```

`dry-run` 在内存中执行整个批次并返回构建报告。`apply` 通过相同的校验后，将 `output.docx` 和 `build-report.json` 一起发布到新的结果目录。任一操作失败时，整个批次没有输出。现有结果目录受禁止覆盖策略保护。

失败时使用已有规划命令 JSON 外壳和稳定错误码：`MPLAN_001`–`MPLAN_007` 表示批量计划边界、来源绑定或 JSON 协议错误；`PLAN_*` 表示具体请求定位失败。
