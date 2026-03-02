# Orleans AgentContext 适配实现评分卡（2026-03-02）

## 1. 审计范围与方法

1. 审计对象：`codex/orleans-agent-context-adapter` 分支变更——Orleans AgentContext 入站恢复、出站透传适配。
2. 评分规范：`docs/audit-scorecard/README.md`（100 分模型，6 维度）。
3. 设计文档：`docs/architecture/orleans-agent-context-adapter-design.md`。
4. 证据来源：分支 diff（7 个修改文件 + 3 个新增文件）、测试结果、CI guard 结果。

## 2. 变更组成

### 2.1 新增文件

| 路径 | 职责 |
|---|---|
| `src/.../Context/OrleansAgentContextAccessor.cs` | `RequestContextAgentContext`（代理模式）+ `OrleansAgentContextAccessor`（桥接 `RequestContext`）+ `OrleansAgentContextRequestContext`（工具类） |
| `src/.../Filters/OrleansAgentContextCallFilters.cs` | `OrleansAgentContextIncomingFilter`（入站作用域隔离）+ `OrleansAgentContextOutgoingFilter`（出站规范化） |
| `test/.../OrleansAgentContextAccessorTests.cs` | Accessor 基础功能 + string 语义边界 + null 清除测试 |

### 2.2 修改文件

| 路径 | 变更要点 |
|---|---|
| `src/.../Context/AsyncLocalAgentContext.cs` | `Prefix` → 公开 `MetadataPrefix` 常量 |
| `src/.../Actors/OrleansGrainEventPublisher.cs` | 注入 `IAgentContextAccessor`，`Inject` 上下文到 envelope |
| `src/.../Grains/RuntimeActorGrain.cs` | 入站事件恢复上下文（`Extract` + `try/finally` 恢复旧 context） |
| `src/.../DependencyInjection/ServiceCollectionExtensions.cs` | `Replace` → `OrleansAgentContextAccessor`；注册 filter |
| `test/.../RuntimeAndContextTests.cs` | 使用 `MetadataPrefix` 常量替换硬编码 |
| `test/.../OrleansGrainEventPublisherTests.cs` | 新增 `PublishAsync_ShouldInjectAgentContextMetadata` 测试 |
| `test/.../OrleansRuntimeServiceCollectionExtensionsTests.cs` | 新增 DI 注册验证测试 |

## 3. 架构分析

### 3.1 分层与依赖反转

1. 所有 Orleans 适配代码位于 Infrastructure 层（`Aevatar.Foundation.Runtime.Implementations.Orleans`），不侵入 `Abstractions` / `Core`。
证据：`src/.../Context/OrleansAgentContextAccessor.cs:3`（namespace `...Orleans.Context`），`src/.../Filters/OrleansAgentContextCallFilters.cs:6`（namespace `...Orleans.Filters`）。
2. Core 层仅暴露 `MetadataPrefix` 常量作为传播契约，不反向依赖 Orleans 实现。
证据：`src/Aevatar.Foundation.Core/Context/AsyncLocalAgentContext.cs:33`。
3. DI 注册完全在 Orleans 扩展方法内完成，Abstractions/Core 无感知。
证据：`src/.../DependencyInjection/ServiceCollectionExtensions.cs:35`（`Replace`）、`:72-73`（filter 注册）。

### 3.2 CQRS 与统一投影链路

1. 事件链路（`EventEnvelope.Metadata`）通过 `AgentContextPropagator.Inject` / `Extract` 承载上下文传播。
证据：`src/.../Actors/OrleansGrainEventPublisher.cs:50`、`src/.../Grains/RuntimeActorGrain.cs:132`。
2. RPC 链路（`RequestContext`）通过 `OrleansAgentContextAccessor` 代理桥接 + Filter 作用域隔离。
证据：`src/.../Context/OrleansAgentContextAccessor.cs:62-93`（代理读写直接桥接 `RequestContext`）。
3. 两条链路通过统一的 `IAgentContextAccessor` 在处理点合流，未引入平行通道。
证据：设计文档 Section 6.4 + `RuntimeActorGrain` 的 `_agentContextAccessor` 统一接入。

### 3.3 Projection 编排与状态约束

1. 无中间层字典事实态。`RequestContext` 是 Orleans AsyncLocal 原生机制，不是进程内长期映射。
证据：`src/.../Context/OrleansAgentContextAccessor.cs:95-113`（`OrleansAgentContextAccessor` 无字典字段）。
2. `RequestContextAgentContext` 采用代理模式，读写直接桥接 `RequestContext`，不持有快照副本。
证据：`src/.../Context/OrleansAgentContextAccessor.cs:62-93`。
3. Filter 不持有服务级状态映射，仅在方法内使用局部临时快照（`SnapshotContextValues` 返回值在 `finally` 中用完即弃）。
证据：`src/.../Filters/OrleansAgentContextCallFilters.cs:19`（局部变量 `snapshot`）。

### 3.4 读写分离与会话语义

1. RPC 链路由 `IncomingFilter` try/finally 保证作用域隔离，防止 handler 对上下文的修改泄漏到调用方。
证据：`src/.../Filters/OrleansAgentContextCallFilters.cs:29-47`。
2. 事件链路由 `RuntimeActorGrain.HandleEnvelopeAsync` 的 try/finally 保证恢复旧上下文。
证据：`src/.../Grains/RuntimeActorGrain.cs:127-141`。
3. Filter 具备降级容错：快照失败不阻断业务调用，仅 log warning。
证据：`src/.../Filters/OrleansAgentContextCallFilters.cs:24-27`、`:37-44`、`:74-76`。

### 3.5 命名语义与冗余清理

1. 统一使用 `AgentContextPropagator.MetadataPrefix`（`"__ctx_"`），无硬编码。
证据：`src/.../Context/OrleansAgentContextAccessor.cs:7`（引用 `AgentContextPropagator.MetadataPrefix`）、`src/.../Context/OrleansAgentContextAccessor.cs:64`。
2. 命名空间与目录语义一致：`Context/`（Accessor + 代理）、`Filters/`（Call Filter）。
3. 无冗余抽象层：`OrleansAgentContextRequestContext` 为工具类集中管理 `RequestContext` 键枚举操作，避免多处重复逻辑。

## 4. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build aevatar.slnx --nologo --tl:off` | 通过（0 error / 116 NU1507 warnings，非本次引入） |
| Core 测试 | `dotnet test test/Aevatar.Foundation.Core.Tests/...csproj --nologo` | 通过（113 passed / 0 failed） |
| Runtime Hosting 测试 | `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/...csproj --nologo` | 通过（120 passed / 0 failed / 5 skipped） |
| 测试稳定性门禁 | `bash tools/ci/test_stability_guards.sh` | 通过 |

## 5. 评分结果（100 分制）

**总分：99 / 100（A+）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | Orleans 适配完全位于 Infrastructure 层，Core 仅暴露 `MetadataPrefix` 常量，无反向耦合。 |
| CQRS 与统一投影链路 | 20 | 20 | 事件链路（Metadata）与 RPC 链路（RequestContext）通过统一 `IAgentContextAccessor` 合流，无双轨通道。 |
| Projection 编排与状态约束 | 20 | 20 | 代理模式直接桥接 `RequestContext`，无中间层字典事实态；Filter 仅用局部临时快照。 |
| 读写分离与会话语义 | 15 | 15 | 入站 try/finally 隔离 + 事件链路 try/finally 恢复，语义清晰；Filter 降级不阻断业务。 |
| 命名语义与冗余清理 | 10 | 10 | `MetadataPrefix` 统一引用，命名空间/目录一致，无冗余抽象。 |
| 可验证性（门禁/构建/测试） | 15 | 14 | build/test 全绿；新增 4 项单元测试覆盖核心路径。但设计文档 Section 9 中 9 项测试场景仅部分覆盖（缺集成级多层调用链/并发隔离/reentrant/filter 降级测试）。 |

## 6. 关键证据（加分项）

1. **代理模式（活引用）**：`RequestContextAgentContext` 读写直接桥接 `RequestContext`，与 `AsyncLocalAgentContextAccessor` 行为语义一致，避免快照副本的一致性问题。
2. **降级容错**：`IncomingFilter` 和 `OutgoingFilter` 均 catch 异常并 log warning，不因 context propagation 失败阻断业务调用链。
3. **作用域隔离**：`IncomingFilter` try/finally 恢复快照 + `RuntimeActorGrain` try/finally 恢复旧 context，双重保护防止上下文泄漏。
4. **设计文档先行**：`docs/architecture/orleans-agent-context-adapter-design.md` 完整覆盖设计边界、时序图、验收标准、测试方案、风险缓解，实现与设计高度一致。

## 7. 主要扣分项（按影响度）

### P1

暂无 P1 阻断项。

### P2

1. 设计文档 Section 9 列出 9 项测试场景，当前仅覆盖基础功能子集（accessor 读写/string 语义/null 清除/DI 注册/事件注入）。缺失以下集成级测试：
   - 多层 Grain 调用链端到端传播
   - 并发隔离（10+ 并发不同 context 互不污染）
   - Reentrant Grain 场景隔离
   - Filter 降级验证（模拟 context 恢复异常）
   - 键空间隔离（非 prefix 键不受 filter 影响）

   证据：`test/.../OrleansAgentContextAccessorTests.cs`（2 tests）、`test/.../OrleansGrainEventPublisherTests.cs` 新增（1 test）、DI 注册（1 test）。

## 8. 改进建议（优先级）

1. **P2**：按设计文档 Section 9 补全集成测试（特别是多层调用链 + 并发隔离 + reentrant grain），验证端到端上下文传播的正确性与隔离性。
2. **P3**：为 `OrleansAgentContextOutgoingFilter` 补充"非代理路径写入差异规范化"的专项测试，确认 accessor.Context = extracted 后发起 grain 调用的场景。
3. **P3**：考虑为 `RequestContextAgentContext.GetAll()` 增加空键防御（`RequestContext.Keys` 返回 null 场景的兼容性处理），当前代码已通过 `?? []` 保护但工具类 `EnumerateContextKeys` 直接使用 `RequestContext.Keys` 未加 null guard。
