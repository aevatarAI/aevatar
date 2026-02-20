# Aevatar 架构评分卡（2026-02-20）

## 1. 审计范围与方法

- 审计对象：`aevatar.slnx`（`src` 33 项目，`workflow` 7 项目，`maker` 5 项目，`test` 7 项目）。
- 评分原则：以仓库当前实现为准，不按规划预支加分。
- 评分规范：遵循 `docs/audit-scorecard/README.md`。
- 基线豁免：仅 InMemory 实现、Actor 仅 Local（未分布式）不作为扣分项。
- 评分结构：整体分（100） + 分模块分。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过（含 route-mapping guard） |
| 全量构建 | `dotnet build aevatar.slnx --nologo` | 通过（0 warning / 0 error） |
| 全量测试 | `dotnet test aevatar.slnx --nologo` | 通过（7 个测试程序集，合计 239 通过，0 失败） |

## 3. 整体评分（Overall）

**94 / 100（A）**

### 3.1 评分维度

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 18 | Host 仅装配，Workflow 应用层走抽象端口；Maker 对 Workflow 具体实现依赖偏重。 |
| CQRS 与统一投影链路 | 20 | 19 | ReadModel 与 AGUI 共享同一 Projection 管线，路由契约严格。 |
| Projection 编排与状态约束 | 20 | 19 | `Ensure/Release` 并发裁决已 Actor 化；剩余风险集中在多节点 live sink 会话一致性。 |
| 读写分离与会话语义 | 15 | 14 | `Command -> Event`、`Query -> ReadModel` 边界清晰。 |
| 命名语义与遗留清理 | 10 | 10 | 旧 `Aevatar.Host.*` 已被守卫禁止，命名一致性较好。 |
| 可验证性（门禁/构建/测试） | 15 | 14 | CI 门禁覆盖深；Maker 缺独立测试项目。 |

## 4. 分模块评分（Subsystem）

| 模块 | 分数 | 结论 |
|---|---:|---|
| Foundation + Runtime | 94 | 基础抽象稳定；本期按规范不因 InMemory/Local 口径扣分。 |
| CQRS Core + Projection Core | 95 | 生命周期/分发/订阅职责清晰，门禁覆盖充分。 |
| Workflow Capability（App/Infra/Projection/AGUI） | 92 | 统一管线完整；`Ensure/Release` 并发已 Actor 化，待补齐多节点 live sink 一致性。 |
| Maker Capability | 84 | 功能可用并通过回归；对 Workflow 具体实现耦合偏高。 |
| Host 装配层（Mainnet/Workflow/Maker） | 95 | 三个 Host 装配模式一致、边界清晰。 |
| 文档与治理（Docs + Guards） | 92 | 架构文档、守卫与评分规范已统一。 |

## 5. 关键证据（加分项）

1. 统一宿主装配：`src/Aevatar.Mainnet.Host.Api/Program.cs:7`、`src/workflow/Aevatar.Workflow.Host.Api/Program.cs:18`、`src/maker/Aevatar.Maker.Host.Api/Program.cs:7`。
2. 投影接口采用 lease/session 句柄：`src/workflow/Aevatar.Workflow.Application.Abstractions/Projections/IWorkflowExecutionProjectionPort.cs:12`。
3. Projection 核心无中间层 in-memory registry 事实态：`src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionSubscriptionRegistry.cs:27`。
4. TypeUrl 精确路由：`src/workflow/Aevatar.Workflow.Projection/Reducers/WorkflowExecutionEventReducerBase.cs:14`、`src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionReadModelProjector.cs:25`、`src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionReadModelProjector.cs:56`。
5. AGUI 走同一投影分支：`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/WorkflowExecutionAGUIEventProjector.cs:15`。
6. Projection 启动并发裁决已由协调 Actor 承载：`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionService.cs:59`、`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionCoordinatorGAgent.cs:21`。
7. 并发回归测试覆盖“并发启动仅允许单 lease”：`test/Aevatar.Workflow.Host.Api.Tests/WorkflowExecutionProjectionServiceTests.cs:113`。
8. 架构门禁覆盖关键反模式：`tools/ci/architecture_guards.sh:50`、`tools/ci/architecture_guards.sh:55`、`tools/ci/architecture_guards.sh:110`、`tools/ci/architecture_guards.sh:181`、`tools/ci/architecture_guards.sh:162`。

## 6. 主要扣分项（按影响度）

1. live sink `Attach/Detach` 仍是进程内会话通道绑定，跨节点一致性需配套会话路由策略。  
证据：`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionService.cs:113`、`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionContext.cs:41`。

2. Maker 基础设施对 Workflow 具体实现耦合较高（工程依赖 + 运行时类型绑定）。  
证据：`src/maker/Aevatar.Maker.Infrastructure/Aevatar.Maker.Infrastructure.csproj:14`、`src/maker/Aevatar.Maker.Infrastructure/Aevatar.Maker.Infrastructure.csproj:15`、`src/maker/Aevatar.Maker.Infrastructure/Runs/WorkflowMakerRunExecutionPort.cs:156`、`src/maker/Aevatar.Maker.Infrastructure/Runs/WorkflowMakerRunExecutionPort.cs:236`。

3. 测试项目未单列 Maker 专项测试工程。  
证据：`aevatar.slnx:60`、`aevatar.slnx:67`。

## 7. 非扣分观察项（按规范）

1. Runtime provider 当前为 `InMemory/Local`：按 `docs/audit-scorecard/README.md` 不扣分。
2. Workflow 默认读模型存储为 InMemory：按 `docs/audit-scorecard/README.md` 不扣分。

## 8. 改进优先级建议

1. P1：为多节点场景补齐 live sink 会话一致性策略（会话通道或粘性路由）并增加回归测试。
2. P2：增加 Maker 独立测试项目（Application + Infrastructure），把关键路径从集成回归下沉到快速测试层。
3. P2：进一步收敛 Maker 对 Workflow 具体类型依赖，提升能力边界可替换性。
