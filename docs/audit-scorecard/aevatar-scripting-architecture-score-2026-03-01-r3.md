# Aevatar.Scripting 架构复评分（彻底重构后，2026-03-01 R3）

## 1. 结论

- 本轮复评分总分：`99/100`
- 上轮分数：`97/100`
- 提升：`+2`

## 2. 分项评分

| 维度 | 上轮 | 本轮 | 说明 |
|---|---:|---:|---|
| 分层清晰度（Domain/Application/Infrastructure/Host） | 15 | 15 | Core 保留领域 Actor 与端口，Application 负责运行时编排与 AI 能力组合，Infrastructure 承载 Roslyn。 |
| 依赖反转与端口设计 | 15 | 15 | Actor 与运行时能力均改为显式抽象依赖，无 `Service Locator`。 |
| CQRS 与 Projection 单链路一致性 | 15 | 15 | 维持统一契约与单链路，无双轨。 |
| Actor 化执行与无锁一致性 | 14 | 15 | Actor 运行推进路径收敛到构造注入端口，消除运行时动态取服务分支。 |
| 读写分离与事件化推进 | 10 | 10 | `Command -> Event -> Projection` 链路完整。 |
| 中间层状态约束合规性 | 9 | 9 | 无中间层 `actor/session/run` 字典事实态字段。 |
| 测试与门禁可验证性 | 10 | 10 | build、单测、集成、架构守卫、分片守卫/测试守卫全部通过。 |
| 命名与语义一致性 | 5 | 5 | 命名空间与目录语义一致，缩写语义稳定。 |
| 精简性与无无效层 | 4 | 5 | 删除 `IScriptCapabilityFactory` 与 `DefaultScriptCapabilityFactory` 冗余中间层。 |

## 3. 关键证据

### 3.1 Actor 去 Service Locator（核心改进）

- `ScriptDefinitionGAgent` 改为构造注入：
  `src/Aevatar.Scripting.Core/ScriptDefinitionGAgent.cs`
- `ScriptRuntimeGAgent` 改为构造注入：
  `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs`
- 删除 `Services.GetService(...)` 与 fallback 策略分支。

### 3.2 运行时能力组合去冗余抽象

- 删除冗余抽象与实现：
  `src/Aevatar.Scripting.Core/Runtime/IScriptCapabilityFactory.cs`（已删除）
  `src/Aevatar.Scripting.Application/Runtime/DefaultScriptCapabilityFactory.cs`（已删除）
- `ScriptRuntimeExecutionOrchestrator` 直接依赖能力端口并构造 `ScriptRuntimeCapabilities`：
  `src/Aevatar.Scripting.Application/Runtime/ScriptRuntimeExecutionOrchestrator.cs`
- `ScriptRuntimeCapabilities` 改为显式依赖：
  `src/Aevatar.Scripting.Core/Runtime/ScriptRuntimeCapabilities.cs`
- `ScriptRuntimeExecutionRequest` 去除 `IServiceProvider` 透传：
  `src/Aevatar.Scripting.Core/Runtime/ScriptRuntimeExecutionRequest.cs`

### 3.3 Host 侧 AI 依赖解耦（避免 scripting 硬绑 role）

- `AddScriptCapability` 中 `IAICapability` 改为：有 `IRoleAgentPort` 则委托，无则 `NoopAICapability`：
  `src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`
  `src/Aevatar.Scripting.Application/AI/NoopAICapability.cs`

### 3.4 测试契约同步

- 重放契约测试改为构造注入：
  `test/Aevatar.Scripting.Core.Tests/Runtime/ScriptDefinitionGAgentReplayContractTests.cs`
  `test/Aevatar.Scripting.Core.Tests/Runtime/ScriptRuntimeGAgentReplayContractTests.cs`
- 文档驱动集成测试更新缺失快照端口断言路径：
  `test/Aevatar.Integration.Tests/ClaimScriptDocumentDrivenFlexibilityTests.cs`

## 4. 验证结果（本轮）

- `dotnet build aevatar.slnx --nologo` -> `passed`
- `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --no-build` -> `51 passed`
- `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --no-build` -> `180 passed`
- `bash tools/ci/architecture_guards.sh` -> `passed`
- `bash tools/ci/solution_split_guards.sh` -> `passed`
- `bash tools/ci/solution_split_test_guards.sh` -> `passed`

## 5. 剩余扣分点

1. 运行时目前仍采用“每次执行编译脚本源码”的方式，架构正确但在高吞吐场景可能引入性能波动；后续可在 Application 层引入可替换的编译结果缓存端口（不回退为中间层事实态）。
