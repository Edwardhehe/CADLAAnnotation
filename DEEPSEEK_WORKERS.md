# DeepSeek 外部工作员

该目录提供三个只读辅助角色，由 Codex 主 Agent 分派任务并复核结果：

- `requirements`：需求分析和验收标准；
- `zwcad-review`：ZWCAD 技术方案评审；
- `code-review`：实现后的缺陷审查。

模型分工：

- `deepseek-v4-flash`：需求分析、任务拆分和快速复核；
- `deepseek-v4-pro`：ZWCAD 技术评审和代码审查。

调用器只允许以上两个模型，不使用旧的兼容模型别名。

API Key 只能通过用户级环境变量 `DEEPSEEK_API_KEY` 提供，不得写入工程文件、命令参数或日志。

测试：

```powershell
.\tools\test-deepseek-worker.ps1
```

调用示例：

```powershell
.\tools\invoke-deepseek-worker.ps1 `
  -Role requirements `
  -Task '评审 LA批注 第一版规划，指出遗漏场景'
```

工作员输出只是候选意见，所有改动仍由 Codex 主 Agent检查、实现和验证。
