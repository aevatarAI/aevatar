# Aevatar 架构评分卡（2026-02-20，复评）

## 1. 审计范围与方法

- 审计对象：`aevatar.slnx` 全量工程（`src` + `test` + `docs/guards`）。
- 评分规范：`docs/audit-scorecard/README.md`（标准化 100 分模型）。
- 基线口径：`InMemory` 与 `Actor Local` 不作为扣分项。
- 本轮重点：确认 Projection 协同逻辑已从 Workflow 下沉到通用 CQRS 层，并基于当前代码重新完整评分（整体 + 分模块）。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过（含 projection route-mapping guard） |
| 全量构建 | `dotnet build aevatar.slnx --nologo` | 通过（0 warning / 0 error） |
| 全量测试 | `dotnet test aevatar.slnx --nologo` | 通过（7 个测试程序集，239 通过，0 失败） |

## 3. 整体评分（Overall）

**96 / 100（A+）**

### 3.1 维度评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 18 | Workflow 已依赖通用抽象；Maker 仍对 Workflow 具体实现存在工程与类型耦合。 |
| CQRS 与统一投影链路 | 20 | 20 | ReadModel 与 AGUI 共用单一 Projection Pipeline，路由与分发统一。 |
| Projection 编排与状态约束 | 20 | 20 | 启动裁决与会话事件分发已通用 Actor/Stream 化，移除 Workflow 专有并发锁路径。 |
| 读写分离与会话语义 | 15 | 15 | `Command -> Event`、`Query -> ReadModel` 边界清晰，lease/session 语义明确。 |
| 命名语义与冗余清理 | 10 | 10 | 项目/命名空间/目录语义一致，无明显空转层。 |
| 可验证性（门禁/构建/测试） | 15 | 13 | 全量门禁、构建、测试均通过；Maker 仍缺独立测试项目。 |

## 4. 分模块评分（Subsystem）

| 模块 | 分数 | 结论 |
|---|---:|---|
| Foundation + Runtime | 95 | 基础能力稳定；当前 Local/InMemory 口径按规范不扣分。 |
| CQRS Core + Projection Core | 97 | 新增通用 Ownership Coordinator 与 Session Event Hub 后，职责边界更清晰。 |
| Workflow Capability（App/Infra/Projection/AGUI） | 96 | 已消费通用编排抽象，Workflow 层聚焦业务语义与事件编解码。 |
| Maker Capability | 84 | 功能可用，但对 Workflow 具体实现耦合仍重，且测试独立性不足。 |
| Host 装配层（Mainnet/Workflow/Maker） | 96 | 宿主职责统一且仅做组合，不承载核心业务编排。 |
| 文档与治理（Docs + Guards） | 94 | 评分规范与门禁口径一致，关键反模式有自动化守卫。 |

## 5. 关键证据（加分项）

1. Projection 编排抽象上提到通用层：`src/Aevatar.CQRS.Projection.Abstractions/Abstractions/IProjectionOwnershipCoordinator.cs:6`、`src/Aevatar.CQRS.Projection.Abstractions/Abstractions/IProjectionSessionEventHub.cs:6`。
2. Ownership 裁决由通用 Actor 承载：`src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionOwnershipCoordinatorGAgent.cs:10`、`src/Aevatar.CQRS.Projection.Core/Orchestration/ActorProjectionOwnershipCoordinator.cs:10`。
3. Session 事件通过通用 stream hub 发布/订阅：`src/Aevatar.CQRS.Projection.Core/Streaming/ProjectionSessionEventHub.cs:8`、`src/Aevatar.CQRS.Projection.Core/Streaming/ProjectionSessionEventHub.cs:46`。
4. Workflow 服务改为依赖通用协调端口：`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionService.cs:15`、`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionService.cs:21`、`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionService.cs:212`。
5. `Attach/Detach` 走 session stream 订阅/退订：`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionService.cs:112`、`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionService.cs:123`。
6. AGUI 分支仍走同一投影链路并写入 run-event stream：`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/WorkflowExecutionAGUIEventProjector.cs:14`、`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/WorkflowExecutionAGUIEventProjector.cs:48`。
7. Workflow 仅保留领域级 codec，不再承载通用编排实现：`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowRunEventSessionCodec.cs:10`。
8. DI 绑定明确通用实现与领域 codec：`src/workflow/Aevatar.Workflow.Projection/DependencyInjection/ServiceCollectionExtensions.cs:49`、`src/workflow/Aevatar.Workflow.Projection/DependencyInjection/ServiceCollectionExtensions.cs:50`、`src/workflow/Aevatar.Workflow.Projection/DependencyInjection/ServiceCollectionExtensions.cs:51`。
9. CQRS Projection Core 新增 proto 契约承载通用消息：`src/Aevatar.CQRS.Projection.Core/Aevatar.CQRS.Projection.Core.csproj:24`、`src/Aevatar.CQRS.Projection.Core/Aevatar.CQRS.Projection.Core.csproj:25`。
10. TypeUrl 精确路由保持稳定：`src/workflow/Aevatar.Workflow.Projection/Reducers/WorkflowExecutionEventReducerBase.cs:14`、`src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionReadModelProjector.cs:56`。
11. 并发启动单 lease 回归测试存在：`test/Aevatar.Workflow.Host.Api.Tests/WorkflowExecutionProjectionServiceTests.cs:113`。
12. 架构守卫覆盖关键反模式（`SemaphoreSlim`、反查上下文、字符串路由等）：`tools/ci/architecture_guards.sh:55`、`tools/ci/architecture_guards.sh:157`、`tools/ci/architecture_guards.sh:162`、`tools/ci/architecture_guards.sh:186`。

## 6. 主要扣分项（按影响度）

1. Maker 对 Workflow 具体实现耦合偏高（工程依赖 + 运行时类型绑定）。  
证据：`src/maker/Aevatar.Maker.Infrastructure/Aevatar.Maker.Infrastructure.csproj:14`、`src/maker/Aevatar.Maker.Infrastructure/Aevatar.Maker.Infrastructure.csproj:15`、`src/maker/Aevatar.Maker.Infrastructure/Runs/WorkflowMakerRunExecutionPort.cs:156`、`src/maker/Aevatar.Maker.Infrastructure/Runs/WorkflowMakerRunExecutionPort.cs:236`。
2. 测试层缺少 Maker 独立测试工程，当前主要依赖跨模块与集成测试覆盖。  
证据：`aevatar.slnx:60`、`aevatar.slnx:67`。

## 7. 非扣分观察项（按规范）

1. Runtime provider 当前主要为 `InMemory/Local`：不扣分。
2. Actor Runtime 当前为 Local 实现（未分布式）：不扣分。
3. Session stream 跨节点一致性由底层 `IStreamProvider` 分布式实现决定：当前记录为演进项，不扣分。

## 8. 改进优先级建议

1. P1：在 Maker 引入更稳定的“可运行工作流 Actor 抽象”，逐步消除 `WorkflowGAgent` 具体类型依赖。
2. P2：补齐 Maker 独立测试项目（`Application` + `Infrastructure`），将关键行为下沉到快速测试层。
3. P2：在分布式落地阶段补充 session 路由与 stream provider 的一致性回归测试矩阵。
