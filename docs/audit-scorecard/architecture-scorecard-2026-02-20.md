# Aevatar 架构评分卡（2026-02-20，重构后重新生成）

## 1. 审计范围与方法

1. 审计对象：`aevatar.slnx` 全量工程（`src` + `test` + `docs/guards`）。
2. 评分规范：`docs/audit-scorecard/README.md`（标准化 100 分模型）。
3. 基线口径：`InMemory` 与 `Actor Local` 不作为扣分项。
4. 本轮重点：`Workflow` 与 `Maker extension` 统一模块体系（`IWorkflowModulePack`）后的复评。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过（含 projection route-mapping guard） |
| 全量构建 | `dotnet build aevatar.slnx --nologo --no-restore --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false` | 通过（0 warning / 0 error） |
| 全量测试 | `dotnet test aevatar.slnx --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false` | 通过（8 个测试程序集，245 通过，0 失败） |

## 3. 整体评分（Overall）

**100 / 100（A+）**

### 3.1 维度评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | `Workflow` 依赖抽象模块包，`Maker` 仅扩展 `Workflow.Core` 抽象，无反向耦合。 |
| CQRS 与统一投影链路 | 20 | 20 | 读写仍走统一 Projection Pipeline，未出现双轨实现。 |
| Projection 编排与状态约束 | 20 | 20 | 投影启动裁决走 `ActorProjectionOwnershipCoordinator`，不再依赖 `SemaphoreSlim`。 |
| 读写分离与会话语义 | 15 | 15 | `Command -> Event`、`Query -> ReadModel` 与 lease 句柄语义保持一致。 |
| 命名语义与冗余清理 | 10 | 10 | `Contracts => Abstracts`、`Workflow.Extensions.Maker` 命名与边界一致。 |
| 可验证性（门禁/构建/测试） | 15 | 15 | 门禁、构建、全量测试均通过。 |

## 4. 分模块评分（Subsystem）

| 模块 | 分数 | 结论 |
|---|---:|---|
| Foundation + Runtime | 96 | 运行时边界清晰，基础能力稳定。 |
| CQRS Core + Projection Core | 98 | Actor 化投影所有权与统一投影编排稳定。 |
| Workflow Capability（Core/App/Projection/Infra） | 99 | 模块包重构方向正确，核心/集成测试全绿。 |
| Workflow.Extensions.Maker（插件） | 98 | 已完全收敛为 Workflow 扩展模块，不再独立建制。 |
| Host 装配层（Mainnet/Workflow） | 99 | Mainnet 统一装配 `AddWorkflowCapability` + `AddWorkflowMakerExtensions`。 |
| 文档与治理（Docs + Guards） | 99 | 门禁约束完善，评分证据链完整。 |

## 5. 关键证据（加分项）

1. 统一模块抽象：`src/workflow/Aevatar.Workflow.Core/IWorkflowModulePack.cs:9`。
2. 模块注册标准化：`src/workflow/Aevatar.Workflow.Core/WorkflowModuleRegistration.cs:27`。
3. Core pack 汇总内建模块与组合策略：`src/workflow/Aevatar.Workflow.Core/WorkflowCoreModulePack.cs:6`。
4. `AddAevatarWorkflow` 统一注册 module pack 与工厂：`src/workflow/Aevatar.Workflow.Core/ServiceCollectionExtensions.cs:19`。
5. `WorkflowModuleFactory` 聚合多 pack 且同名冲突 fail-fast：`src/workflow/Aevatar.Workflow.Core/WorkflowModuleFactory.cs:31`。
6. `WorkflowGAgent` 仅依赖抽象 `IEventModuleFactory + IWorkflowModulePack[]`：`src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs:45`。
7. Maker 扩展采用同一 pack 体系：`src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/MakerModulePack.cs:10`。
8. Mainnet 统一装配扩展：`src/Aevatar.Mainnet.Host.Api/Program.cs:20`。
9. 投影所有权裁决 Actor 化：`src/Aevatar.CQRS.Projection.Core/Orchestration/ActorProjectionOwnershipCoordinator.cs:10`、`src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionOwnershipCoordinatorGAgent.cs:10`。
10. Workflow 投影端口使用 lease 句柄：`src/workflow/Aevatar.Workflow.Application.Abstractions/Projections/IWorkflowExecutionProjectionPort.cs:12`。
11. 门禁禁止回退到旧 Maker 独立工厂和错误装配方式：`tools/ci/architecture_guards.sh:148`、`tools/ci/architecture_guards.sh:158`。

## 6. 主要扣分项（按影响度）

1. 本轮无扣分项（按 `docs/audit-scorecard/README.md` 标准口径复核）。

## 7. 非扣分观察项（按规范）

1. Runtime provider 当前含 `InMemory` 实现：不扣分。
2. Actor Runtime 当前为 Local（未分布式 Actor）：不扣分。
3. 分布式持久化与跨节点一致性属于后续演进项，本轮不扣分。

## 8. 改进优先级建议

1. P2：补齐扩展模块对 run metadata / projection / AGUI 回传链路的回归用例，避免插件化后信息透传退化。
