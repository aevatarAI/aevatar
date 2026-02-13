# Aevatar.AI.Skills

`Aevatar.AI.Skills` 提供本地技能（`SKILL.md`）发现与工具化能力，便于将“技能文档”直接注入 AI Agent 的工具系统。

## 职责

- 扫描目录发现 `SKILL.md`
- 解析技能定义（名称、描述、指令、路径）
- 将技能适配为 `IAgentTool`，供 LLM 在运行时调用
- 提供 DI 扩展 `AddSkills(...)`

## 核心类型

- `SkillDefinition`：技能模型
- `SkillDiscovery`：技能扫描与解析
- `SkillToolAdapter`：技能 -> `IAgentTool`
- `ServiceCollectionExtensions`：DI 注册入口

## 快速接入

```csharp
services.AddSkills(o => o
    .ScanDirectory("~/.aevatar/skills")
    .ScanDirectory("./skills"));
```

## 依赖

- `Aevatar.AI`
- `Microsoft.Extensions.*.Abstractions`
