# Aevatar.AI.ToolProviders.Skills

`Aevatar.AI.ToolProviders.Skills` 提供本地技能（`SKILL.md`）发现与工具化能力，便于将“技能文档”直接注入 AI Agent 的工具系统。

## 职责

- 扫描目录发现 `SKILL.md`（支持 YAML frontmatter）
- 解析技能定义（名称、描述、参数、指令、元数据）
- 通过统一 `UseSkillTool` 提供单一 `use_skill` 工具入口
- `SkillRegistry` 汇聚本地 + 远程技能，支持系统 prompt 集成
- 提供 DI 扩展 `AddSkills(...)`

## 核心类型

- `SkillDefinition`：技能模型（含 frontmatter 元数据）
- `SkillDiscovery`：技能扫描与解析
- `SkillFrontmatterParser`：SKILL.md frontmatter 解析
- `SkillRegistry`：统一技能注册表
- `UseSkillTool`：统一 use_skill 工具（替代散装 skill_xxx 工具）
- `IRemoteSkillFetcher`：远程技能拉取抽象
- `ServiceCollectionExtensions`：DI 注册入口

## 快速接入

```csharp
services.AddSkills(o => o
    .ScanDirectory("~/.aevatar/skills")
    .ScanDirectory("./skills"));
```

## 依赖

- `Aevatar.AI.Abstractions`
- `Microsoft.Extensions.*.Abstractions`
