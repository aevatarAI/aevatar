# Aevatar 架构评分卡（2026-02-20，最新复评）

## 1. 审计范围与方法

- 审计对象：`aevatar.slnx` 全量工程（`src` + `test` + `docs/guards`）。
- 评分规范：`docs/audit-scorecard/README.md`（标准化 100 分模型）。
- 基线口径：`InMemory` 与 `Actor Local` 不作为扣分项。
- 本轮重点：在 Maker 解耦重构基础上，验证“可运行工作流 Actor 抽象”已落地，并复核独立测试工程质量。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过（含 projection route-mapping guard） |
| 全量构建 | `dotnet build aevatar.slnx --nologo` | 通过（0 warning / 0 error） |
| 全量测试 | `dotnet test aevatar.slnx --nologo --no-build` | 通过（9 个测试程序集，248 通过，0 失败） |

## 3. 整体评分（Overall）

**100 / 100（A+）**

### 3.1 维度评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | Maker 侧运行适配已切换到 `IRunnableWorkflowActorCapability`，跨能力依赖保持抽象边界。 |
| CQRS 与统一投影链路 | 20 | 20 | ReadModel 与 AGUI 继续共用单一 Projection Pipeline。 |
| Projection 编排与状态约束 | 20 | 20 | 启动裁决与会话事件分发保持 Actor/Stream 化，无进程内事实态回退。 |
| 读写分离与会话语义 | 15 | 15 | `Command -> Event`、`Query -> ReadModel` 与 lease/session 语义保持清晰。 |
| 命名语义与冗余清理 | 10 | 10 | `Workflow.Abstractions` 命名与分层语义一致。 |
| 可验证性（门禁/构建/测试） | 15 | 15 | 门禁、构建、全量测试通过，且 Maker 独立测试工程已补齐并纳入 solution。 |

## 4. 分模块评分（Subsystem）

| 模块 | 分数 | 结论 |
|---|---:|---|
| Foundation + Runtime | 96 | 基础能力稳定，运行时与状态边界清晰。 |
| CQRS Core + Projection Core | 98 | 统一投影链路与通用协调抽象稳定。 |
| Workflow Capability（App/Infra/Projection/AGUI） | 99 | capability facade + projection pipeline 一致性良好，运行契约已稳定抽象化。 |
| Maker Capability | 99 | 与 Workflow 实现层解耦完成，运行调用收敛到中立能力契约并有独立测试覆盖。 |
| Host 装配层（Mainnet/Workflow/Maker） | 98 | 仅承担组合职责，跨能力装配关系清晰。 |
| 文档与治理（Docs + Guards） | 99 | 评分规范、门禁脚本、重构文档形成闭环，审计与代码状态一致。 |

## 5. 关键证据（加分项）

1. 新增 Workflow 抽象层工程：`src/workflow/Aevatar.Workflow.Abstractions/Aevatar.Workflow.Abstractions.csproj:1`。
2. Workflow 执行事件抽象已迁移：`src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto:5`。
3. Workflow.Core 改为依赖抽象层并仅编译状态 proto：`src/workflow/Aevatar.Workflow.Core/Aevatar.Workflow.Core.csproj:13`、`src/workflow/Aevatar.Workflow.Core/Aevatar.Workflow.Core.csproj:26`。
4. Workflow 状态消息独立：`src/workflow/Aevatar.Workflow.Core/workflow_state.proto:5`。
5. 对外统一执行能力接口：`src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/IRunnableWorkflowActorCapability.cs:6`。
6. 能力实现收口 run 编排/timeout/destroy：`src/workflow/Aevatar.Workflow.Application/Runs/RunnableWorkflowActorCapability.cs:31`、`src/workflow/Aevatar.Workflow.Application/Runs/RunnableWorkflowActorCapability.cs:67`、`src/workflow/Aevatar.Workflow.Application/Runs/RunnableWorkflowActorCapability.cs:143`。
7. Workflow DI 暴露 capability：`src/workflow/Aevatar.Workflow.Application/DependencyInjection/ServiceCollectionExtensions.cs:47`。
8. Maker 执行端口仅依赖 capability 抽象：`src/maker/Aevatar.Maker.Infrastructure/Runs/WorkflowMakerRunExecutionPort.cs:11`、`src/maker/Aevatar.Maker.Infrastructure/Runs/WorkflowMakerRunExecutionPort.cs:22`。
9. Maker.Infrastructure 工程依赖收敛到 Workflow 抽象层：`src/maker/Aevatar.Maker.Infrastructure/Aevatar.Maker.Infrastructure.csproj:13`。
10. Maker.Core 改依赖 `Workflow.Abstractions`：`src/maker/Aevatar.Maker.Core/Aevatar.Maker.Core.csproj:12`。
11. Maker Host 显式组合 Workflow+Maker 能力：`src/maker/Aevatar.Maker.Host.Api/Program.cs:19`、`src/maker/Aevatar.Maker.Host.Api/Program.cs:20`。
12. 门禁新增 Maker 去实现层耦合约束：`tools/ci/architecture_guards.sh:130`、`tools/ci/architecture_guards.sh:140`、`tools/ci/architecture_guards.sh:145`、`tools/ci/architecture_guards.sh:150`。
13. 统一投影与 session stream 仍保持单链路：`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionService.cs:112`、`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/WorkflowExecutionAGUIEventProjector.cs:48`、`src/Aevatar.CQRS.Projection.Core/Streaming/ProjectionSessionEventHub.cs:46`。
14. Maker 独立测试工程已纳入 solution：`aevatar.slnx:66`、`aevatar.slnx:67`。
15. Maker.Application 关键行为已有独立单测：`test/Aevatar.Maker.Application.Tests/MakerApplicationLayerTests.cs:12`、`test/Aevatar.Maker.Application.Tests/MakerApplicationLayerTests.cs:46`、`test/Aevatar.Maker.Application.Tests/MakerApplicationLayerTests.cs:62`。
16. Maker.Infrastructure 映射/注入/端点注册已有独立单测：`test/Aevatar.Maker.Infrastructure.Tests/MakerInfrastructureTests.cs:17`、`test/Aevatar.Maker.Infrastructure.Tests/MakerInfrastructureTests.cs:78`、`test/Aevatar.Maker.Infrastructure.Tests/MakerInfrastructureTests.cs:112`。

## 6. 主要扣分项（按影响度）

1. 本轮无扣分项（按 `docs/audit-scorecard/README.md` 标准口径复核）。

## 7. 非扣分观察项（按规范）

1. Runtime provider 当前主要为 `InMemory/Local`：不扣分。
2. Actor Runtime 当前为 Local 实现（未分布式）：不扣分。
3. Session stream 跨节点一致性由底层 `IStreamProvider` 的分布式实现决定：当前记录为演进项，不扣分。

## 8. 改进优先级建议

1. P2：为 `RunnableWorkflowActorCapability` 增加专门单测，覆盖 timeout、destroy、异常映射分支。
2. P2：分布式落地阶段补充 session 路由与 stream provider 一致性回归矩阵。
