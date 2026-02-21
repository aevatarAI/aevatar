# Aevatar AI 子解决方案评分卡（2026-02-21，重算）

## 1. 审计范围与方法

1. 审计对象：`aevatar.ai.slnf`（单一子解决方案）。
2. 评分规范：`docs/audit-scorecard/README.md`（100 分模型，6 维度）。
3. 证据类型：`.slnf`、`csproj`、核心源码、测试源码、CI guard 脚本、本地命令结果。

## 2. 子解决方案组成

`aevatar.ai.slnf` 当前包含 7 个项目（6 个生产项目 + 1 个测试项目）：

1. `src/Aevatar.AI.Abstractions/Aevatar.AI.Abstractions.csproj`
2. `src/Aevatar.AI.Core/Aevatar.AI.Core.csproj`
3. `src/Aevatar.AI.LLMProviders.MEAI/Aevatar.AI.LLMProviders.MEAI.csproj`
4. `src/Aevatar.AI.LLMProviders.Tornado/Aevatar.AI.LLMProviders.Tornado.csproj`
5. `src/Aevatar.AI.ToolProviders.MCP/Aevatar.AI.ToolProviders.MCP.csproj`
6. `src/Aevatar.AI.ToolProviders.Skills/Aevatar.AI.ToolProviders.Skills.csproj`
7. `test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj`

证据：`aevatar.ai.slnf:5`、`aevatar.ai.slnf:11`。

## 3. 相关源码架构分析

### 3.1 分层与依赖方向

1. 依赖主干保持 `AI.Abstractions -> AI.Core -> Providers/ToolProviders`，Provider/ToolProvider 均依赖抽象层。  
证据：`src/Aevatar.AI.Core/Aevatar.AI.Core.csproj:10`、`src/Aevatar.AI.LLMProviders.MEAI/Aevatar.AI.LLMProviders.MEAI.csproj:10`、`src/Aevatar.AI.ToolProviders.MCP/Aevatar.AI.ToolProviders.MCP.csproj:10`。
2. `AI.Core` 运行逻辑拆分为 `ChatRuntime/ToolCallLoop/ToolManager/HookPipeline`，基类负责组合。  
证据：`src/Aevatar.AI.Core/AIGAgentBase.cs:46`、`src/Aevatar.AI.Core/Chat/ChatRuntime.cs:13`、`src/Aevatar.AI.Core/Tools/ToolCallLoop.cs:14`。

### 3.2 运行主链路（Event -> LLM -> Tool -> Event）

1. `RoleGAgent` 通过事件处理接收请求并流式发布文本事件。  
证据：`src/Aevatar.AI.Core/RoleGAgent.cs:51`、`src/Aevatar.AI.Core/RoleGAgent.cs:68`、`src/Aevatar.AI.Core/RoleGAgent.cs:86`。
2. `AIGAgentBase` 在激活/配置变更时重建运行时，使用 `ILLMProviderFactory + IAgentToolSource` 注入扩展能力。  
证据：`src/Aevatar.AI.Core/AIGAgentBase.cs:64`、`src/Aevatar.AI.Core/AIGAgentBase.cs:72`、`src/Aevatar.AI.Core/AIGAgentBase.cs:147`、`src/Aevatar.AI.Core/AIGAgentBase.cs:167`。
3. `ToolCallLoop` 统一执行 tool-calling 循环并串接 hook。  
证据：`src/Aevatar.AI.Core/Tools/ToolCallLoop.cs:30`、`src/Aevatar.AI.Core/Tools/ToolCallLoop.cs:53`、`src/Aevatar.AI.Core/Tools/ToolCallLoop.cs:80`。

### 3.3 本轮修复后的关键架构点

1. 工具来源重载语义改为“替换 source 工具集合”：旧 source 工具先移除，再注册新发现工具，避免陈旧工具残留。  
证据：`src/Aevatar.AI.Core/AIGAgentBase.cs:56`、`src/Aevatar.AI.Core/AIGAgentBase.cs:190`、`src/Aevatar.AI.Core/AIGAgentBase.cs:193`、`src/Aevatar.AI.Core/Tools/ToolManager.cs:27`。
2. LLM provider 冲突注册由“静默忽略”改为“显式失败”：若 `ILLMProviderFactory` 已注册，直接抛异常。  
证据：`src/Aevatar.AI.LLMProviders.MEAI/ServiceCollectionExtensions.cs:28`、`src/Aevatar.AI.LLMProviders.Tornado/ServiceCollectionExtensions.cs:27`。
3. AI 分片测试已落地并进入分片测试守卫。  
证据：`test/Aevatar.AI.Tests/AIGAgentBaseToolRefreshTests.cs:11`、`test/Aevatar.AI.Tests/LLMProviderServiceCollectionExtensionsTests.cs:11`、`test/Aevatar.AI.Tests/ToolProviderServiceCollectionExtensionsTests.cs:11`、`tools/ci/solution_split_test_guards.sh:10`。
4. Provider/ToolProvider 文档依赖说明已与 `csproj` 对齐。  
证据：`src/Aevatar.AI.LLMProviders.MEAI/README.md:28`、`src/Aevatar.AI.LLMProviders.Tornado/README.md:28`、`src/Aevatar.AI.ToolProviders.MCP/README.md:28`、`src/Aevatar.AI.ToolProviders.Skills/README.md:29`。

## 4. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 子解构建 | `dotnet build aevatar.ai.slnf --nologo --no-restore --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false` | 通过（0 warning / 0 error） |
| 子解测试 | `dotnet test aevatar.ai.slnf --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false --no-restore` | 通过（`9 passed / 0 failed`） |
| 覆盖率采集 | `dotnet test aevatar.ai.slnf ... --collect:"XPlat Code Coverage"` | 行覆盖率 `6.44%`，分支覆盖率 `2.43%` |
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过 |
| 分片测试门禁 | `bash tools/ci/solution_split_test_guards.sh` | 通过（含 AI 分片） |

覆盖率证据：`test/Aevatar.AI.Tests/TestResults/84622032-b4ac-4d4d-ba97-a32dd4a0e576/coverage.cobertura.xml:2`。

## 5. 评分结果（100 分制）

**总分：96 / 100（A+）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | `Abstractions -> Core -> Providers/Tools` 依赖方向稳定。 |
| CQRS 与统一投影链路 | 20 | 19 | 事件驱动主链路清晰；该子解本身不承担读侧投影编排。 |
| Projection 编排与状态约束 | 20 | 20 | 无中间层事实态映射反模式；source 工具重载残留问题已修复。 |
| 读写分离与会话语义 | 15 | 15 | 输入事件与输出事件语义清晰，工具重载一致性已补齐。 |
| 命名语义与冗余清理 | 10 | 10 | 命名一致；README 依赖描述与工程引用已对齐。 |
| 可验证性（门禁/构建/测试） | 15 | 12 | build/test/guard 全绿，AI 分片测试已接入；但覆盖率仍偏低。 |

## 6. 主要扣分项（按影响度）

### P1

1. 暂无 P1 阻断项。

### P2

1. 覆盖率偏低（行 6.44%，分支 2.43%），当前测试以回归保障为主，关键执行分支仍需补齐。  
证据：`test/Aevatar.AI.Tests/TestResults/84622032-b4ac-4d4d-ba97-a32dd4a0e576/coverage.cobertura.xml:2`。

## 7. 改进建议（优先级）

1. P1：围绕 `ChatRuntime/ToolCallLoop` 的异常路径、空响应路径、流式中断路径补齐行为测试，提升分支覆盖。
2. P2：为 AI 分片补充覆盖率阈值门禁（line/branch 双阈值），防止覆盖率回落。
