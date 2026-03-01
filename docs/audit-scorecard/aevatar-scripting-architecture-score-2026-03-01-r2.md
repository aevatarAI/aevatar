# Aevatar.Scripting 架构复评分（重构后，2026-03-01）

## 1. 结论

- 本轮复评分总分：`97/100`
- 上次分数：`86/100`
- 提升：`+11`

## 2. 分项评分

| 维度 | 上次 | 本次 | 说明 |
|---|---:|---:|---|
| 分层清晰度（Domain/Application/Infrastructure/Host） | 11 | 15 | 新增 `Aevatar.Scripting.Application`，命令适配/运行编排/AI 组合实现从 Core 迁出。 |
| 依赖反转与端口设计 | 13 | 15 | Runtime 通过快照端口读取定义事实；Host 仅注入端口与实现。 |
| CQRS 与 Projection 单链路一致性 | 14 | 15 | 事件契约统一下沉到 Abstractions，Projection 依赖稳定契约。 |
| Actor 化执行与无锁一致性 | 14 | 14 | 保持稳定，无并发补丁式事实态。 |
| 读写分离与事件化推进 | 8 | 10 | Command Adapter -> Requested Event -> Domain Event -> Projection 链路完整。 |
| 中间层状态约束合规性 | 9 | 9 | 未引入 actor/session/run 的中间层字典事实态。 |
| 测试与门禁可验证性 | 9 | 10 | 构建、核心测试、分片构建/测试守卫、架构守卫全部通过。 |
| 命名与语义一致性 | 5 | 5 | 项目名/命名空间/目录语义一致。 |
| 精简性与无无效层 | 3 | 4 | 删除 `Scripting.Contracts` 冗余层，CQRS 分片移除 scripting 依赖。 |

## 3. 关键证据

### 3.1 Application/Core/Infrastructure 分层清晰

- 新增应用层项目：  
  `src/Aevatar.Scripting.Application/Aevatar.Scripting.Application.csproj`
- 应用编排实现迁出 Core：  
  `src/Aevatar.Scripting.Application/Application/RunScriptCommandAdapter.cs`  
  `src/Aevatar.Scripting.Application/Application/UpsertScriptDefinitionCommandAdapter.cs`  
  `src/Aevatar.Scripting.Application/Runtime/ScriptRuntimeExecutionOrchestrator.cs`  
  `src/Aevatar.Scripting.Application/AI/RoleAgentDelegateAICapability.cs`
- Infrastructure 承载 Roslyn：  
  `src/Aevatar.Scripting.Infrastructure/Compilation/RoslynScriptPackageCompiler.cs`  
  `src/Aevatar.Scripting.Infrastructure/Compilation/RoslynScriptExecutionEngine.cs`

### 3.2 Contracts 回归 Abstractions（去冗余层）

- 事件契约 proto 位于 Abstractions：  
  `src/Aevatar.Scripting.Abstractions/script_host_messages.proto`
- Abstractions 直接负责 proto 生成：  
  `src/Aevatar.Scripting.Abstractions/Aevatar.Scripting.Abstractions.csproj`
- `Scripting.Contracts` 项目已移除（目录不存在）。

### 3.3 CQRS 分片去业务化（不承载 scripting）

- CQRS 分片不再包含 scripting 项目：  
  `aevatar.cqrs.slnf`
- CQRS Projection Core 测试工程不再引用 scripting 项目：  
  `test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj`
- 脚本投影相关测试回归 scripting 测试域：  
  `test/Aevatar.Scripting.Core.Tests/Projection/ClaimReadModelProjectorTests.cs`  
  `test/Aevatar.Scripting.Core.Tests/Projection/ScriptExecutionReadModelProjectorNeutralityTests.cs`

## 4. 验证结果（复评分复核）

- `dotnet build aevatar.slnx --nologo` -> `passed`
- `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --nologo --no-build` -> `124 passed, 1 skipped`
- `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --no-build` -> `51 passed`
- `bash tools/ci/architecture_guards.sh` -> `passed`
- `bash tools/ci/solution_split_guards.sh` -> `passed`
- `bash tools/ci/solution_split_test_guards.sh` -> `passed`

## 5. 剩余扣分点

1. `ScriptDefinitionGAgent`、`ScriptRuntimeGAgent` 仍有 `Services.GetService(...)` service-locator 风格依赖解析，后续可继续收敛。  
   证据：`src/Aevatar.Scripting.Core/ScriptDefinitionGAgent.cs`、`src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs`
2. Core 中 `DefaultScriptReadModelSchemaActivationPolicy` fallback 仍有轻微应用策略混入。  
   证据：`src/Aevatar.Scripting.Core/Schema/DefaultScriptReadModelSchemaActivationPolicy.cs`
