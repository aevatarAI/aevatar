# Aevatar.Scripting 架构审计评分（2026-03-01）

## 1. 审计范围

- `src/Aevatar.Scripting.Abstractions` 全部源码文件
- `src/Aevatar.Scripting.Core` 全部源码文件（含 `script_host_messages.proto`）
- `src/Aevatar.Scripting.Projection` 全部源码文件
- `src/Aevatar.Scripting.Hosting` 全部源码文件
- `test/Aevatar.Scripting.Core.Tests` 全部测试文件

说明：本次共核查 76 个源码/测试文件（已排除 `bin/obj` 生成产物）。

## 2. 审计方法

- 静态结构审查：分层边界、依赖方向、运行态状态管理、CQRS/Projection 链路一致性。
- 约束模式扫描：`Task.Run/new Timer/new Thread/lock/ConcurrentDictionary` 等。
- 可验证性核查：单测与守卫脚本执行结果。

已执行验证命令：

- `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo`  
  结果：43/43 通过。
- `bash tools/ci/projection_route_mapping_guard.sh`  
  结果：passed。
- `bash tools/ci/test_stability_guards.sh`  
  结果：passed。
- `bash tools/ci/architecture_guards.sh`  
  结果：passed（worktree diff mode）。

## 3. 评分模型与总分

| 维度 | 满分 | 得分 | 结论 |
|---|---:|---:|---|
| 分层清晰度（Domain/Application/Infrastructure/Host） | 15 | 11 | 有明确 Host/Core/Projection 分区，但 Core 内混入 Roslyn 编译执行基础设施。 |
| 依赖反转与端口设计 | 15 | 13 | Core 以 Port 抽象对外，Hosting 实现适配，整体良好。 |
| CQRS 与 Projection 单链路一致性 | 15 | 14 | Command->Event->Projector->ReadModel 链路清晰，TypeUrl 精确路由。 |
| Actor 化执行与无锁一致性 | 15 | 14 | 未见并发补丁式锁状态；状态推进在事件处理/状态机内完成。 |
| 读写分离与事件化推进 | 10 | 8 | 主流程符合事件化；但 Runtime 直接读取 Definition Actor 具体类型状态。 |
| 中间层状态约束合规性 | 10 | 9 | 未发现以 actor/run/session 作为事实源的中间层全局映射。 |
| 测试与门禁可验证性 | 10 | 9 | 单测和守卫均通过；个别“架构测试”有效性偏弱。 |
| 命名与语义一致性 | 5 | 5 | 项目名、命名空间、目录语义一致。 |
| 精简性与无无效层 | 5 | 3 | 存在未使用字段和弱价值测试。 |
| **总分** | **100** | **86** | **良好（可上线，但建议进行一次架构净化迭代）** |

## 4. 关键证据（按维度）

### 4.1 分层与依赖方向

- `Core` 依赖 `Foundation` 与 `Scripting.Abstractions`，并直接引入 `Microsoft.CodeAnalysis.CSharp`（Roslyn）：
  - `src/Aevatar.Scripting.Core/Aevatar.Scripting.Core.csproj:10-12`
  - `src/Aevatar.Scripting.Core/Aevatar.Scripting.Core.csproj:20`
- `Projection` 直接引用 `Scripting.Core`（读侧对写侧实现程序集存在耦合）：
  - `src/Aevatar.Scripting.Projection/Aevatar.Scripting.Projection.csproj:12`
- `Hosting` 负责注入和适配：
  - `src/Aevatar.Scripting.Hosting/Aevatar.Scripting.Hosting.csproj:10-11`
  - `src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs:18-28`

### 4.2 依赖反转（Port/Adapter）

- Core 定义 Port：
  - `src/Aevatar.Scripting.Core/Ports/IGAgentEventRoutingPort.cs:6`
  - `src/Aevatar.Scripting.Core/Ports/IGAgentInvocationPort.cs:5`
  - `src/Aevatar.Scripting.Core/Ports/IGAgentFactoryPort.cs:3`
- Hosting 实现 Port：
  - `src/Aevatar.Scripting.Hosting/Ports/RuntimeGAgentEventRoutingPort.cs:9`
  - `src/Aevatar.Scripting.Hosting/Ports/RuntimeGAgentInvocationPort.cs:8`
  - `src/Aevatar.Scripting.Hosting/Ports/RuntimeGAgentFactoryPort.cs:6`
- Runtime 能力通过 Port 间接调用，不直接耦合 runtime 实现：
  - `src/Aevatar.Scripting.Core/Runtime/ScriptRuntimeCapabilities.cs:29-99`

### 4.3 CQRS 与 Projection 单链路

- 运行编排将决策统一转为 `ScriptRunDomainEventCommitted`：
  - `src/Aevatar.Scripting.Core/Runtime/ScriptRuntimeExecutionOrchestrator.cs:84-146`
- Projection 以 `TypeUrl` 精确路由 reducer：
  - `src/Aevatar.Scripting.Projection/Projectors/ScriptExecutionReadModelProjector.cs:53-68`
- reducer 将 committed event 映射到读模型：
  - `src/Aevatar.Scripting.Projection/Reducers/ScriptRunDomainEventCommittedReducer.cs:19-43`

### 4.4 Actor 化与状态推进

- Definition Actor 通过 `PersistDomainEventAsync` + `TransitionState` 演进：
  - `src/Aevatar.Scripting.Core/ScriptDefinitionGAgent.cs:57-111`
  - `src/Aevatar.Scripting.Core/ScriptDefinitionGAgent.cs:113-179`
- Runtime Actor 通过 `PersistDomainEventsAsync` + `TransitionState` 演进：
  - `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:42-57`
  - `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:66-87`
- 禁止并发补丁式 API 的脚本沙箱策略存在：
  - `src/Aevatar.Scripting.Core/Compilation/ScriptSandboxPolicy.cs:7-23`

## 5. 主要扣分点（按优先级）

### P1：Runtime 对 Definition 的“具体类型耦合 + 直接状态读取”

- 证据：
  - `src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:99-103`
- 问题：
  - `ScriptRuntimeGAgent` 强制要求目标 actor 是 `ScriptDefinitionGAgent`，并直接读取其 `State`。
  - 这会削弱读写边界与抽象边界，运行时无法通过更稳定的读侧契约解耦。
- 影响：
  - 替换/拆分 Definition 实现时，Runtime 需要同时改动；跨模块演化成本高。

### P1：Core 层承载 Roslyn 运行时基础设施，分层纯度不足

- 证据：
  - `src/Aevatar.Scripting.Core/Compilation/RoslynScriptExecutionEngine.cs:11`
  - `src/Aevatar.Scripting.Core/Compilation/RoslynScriptPackageCompiler.cs:12`
  - `src/Aevatar.Scripting.Core/Aevatar.Scripting.Core.csproj:20`
- 问题：
  - Core 同时承载业务编排与动态编译执行基础设施。
- 影响：
  - Core 波动面增大，不利于“内核最小化”和 infra 可替换。

### P2：存在无效字段（可删除）

- 证据：
  - `src/Aevatar.Scripting.Core/Runtime/ScriptRuntimeExecutionRequest.cs:18`
  - 使用检索仅命中构造处：`src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs:54`
- 问题：
  - `EventPublisher` 字段未被编排器使用，增加模型噪音。

### P3：架构测试有效性偏弱

- 证据：
  - `test/Aevatar.Scripting.Core.Tests/Architecture/ScriptInheritanceGuardTests.cs:10-18`
- 问题：
  - 当前测试只对硬编码字符串做 regex，不校验真实生产代码。

## 6. 改进建议（按落地顺序）

1. 抽离 Definition 快照读取端口（`IScriptDefinitionSnapshotPort`），Runtime 仅依赖抽象快照契约，不再强转 `ScriptDefinitionGAgent`。
2. 将 Roslyn 编译/执行实现迁移至 `Aevatar.Scripting.Infrastructure`（或等价 infra 项目），Core 保留接口与业务策略。
3. 删除 `ScriptRuntimeExecutionRequest.EventPublisher` 未使用字段，保持请求模型最小化。
4. 把 `ScriptInheritanceGuardTests` 改为真实源码扫描/反射校验，避免“假阳性通过”。
5. 若 Projection 仅消费事件契约，考虑将共享事件契约下沉到更稳定的 contracts/abstractions 层，降低 `Projection -> Core` 耦合。

## 7. 结论

`Aevatar.Scripting` 当前架构总体健康，核心流程事件化、Projection 单链路、Actor 状态推进和门禁可验证性表现较好。  
主要风险集中在“Core 层职责过重”和“Runtime 对 Definition 具体实现耦合”。若完成上述 P1/P2 改进，预计可提升至 **92-94/100**。
