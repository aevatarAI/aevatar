# Dynamic Runtime 架构实施评分卡（2026-02-28，v9）

## 1. 审计范围与模式

1. 评分模式：**实施评分模式（Implementation Scoring）**。
2. 审计对象：
- `src/Aevatar.DynamicRuntime.*`
- `test/Aevatar.DynamicRuntime.Application.Tests/*`
- `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md`
- `docs/architecture/ai-script-runtime-implementation-change-plan.md`
- `tools/ci/*`（与 Dynamic Runtime 相关守卫）
3. 评分规范：`docs/audit-scorecard/README.md`。
4. 重构口径：**删除优先，不做兼容壳层**（旧命名/旧路径/旧矩阵应一次性清理）。

## 2. 客观验证结果（实施证据）

| 检查项 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build aevatar.slnx --nologo --tl:off` | 通过（0 error） |
| 全量测试 | `dotnet test aevatar.slnx --nologo --tl:off` | 通过（关键分片均通过） |
| 架构守卫 | `bash tools/ci/architecture_guards.sh` | 通过 |
| 投影路由守卫 | `bash tools/ci/projection_route_mapping_guard.sh` | 通过 |
| 分片构建守卫 | `bash tools/ci/solution_split_guards.sh` | 通过 |
| 分片测试守卫 | `bash tools/ci/solution_split_test_guards.sh` | 通过 |
| 稳定性守卫 | `bash tools/ci/test_stability_guards.sh` | 通过 |
| 文档一致性守卫 | `bash tools/ci/architecture_doc_consistency_guards.sh` | 通过 |
| DynamicRuntime 应用测试 | `dotnet test test/Aevatar.DynamicRuntime.Application.Tests/Aevatar.DynamicRuntime.Application.Tests.csproj --nologo --tl:off` | 通过（32/32） |
| 性能门禁 | `bash tools/ci/script_runtime_perf_guards.sh` | **失败**：缺少 `exec_start_latency_p95_ms`、`first_token_latency_p95_ms` |
| 可用性门禁 | `bash tools/ci/script_runtime_availability_guards.sh` | **失败**：缺少 `run_success_rate_30m` |
| 韧性门禁 | `bash tools/ci/script_runtime_resilience_guards.sh` | **失败**：缺少 cancel/timeout/restart 与回收卸载指标 |

## 3. 总分

**78 / 100（B）**

结论：实现主链路已具备可运行与可测试基础，但与架构蓝图的关键约束仍有实质差距，主要集中在 **reconcile 真收敛、Envelope 端到端闭环、timeout/retry 事件化推进、统一投影主链** 四个核心点。

## 4. 六维评分

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 16 | Host 基本只做承载；但 Application 直接内嵌投影执行，边界仍偏厚。 |
| CQRS 与统一投影链路 | 20 | 14 | 有 `Command -> Event -> ReadStore` 路径，但并非完全经统一 CQRS projection runtime。 |
| Projection 编排与状态约束 | 20 | 13 | 未出现中间层事实态字典违规；但 Actor 树链接与编排收敛未真正落地。 |
| 读写分离与会话语义 | 15 | 12 | 幂等/并发/冲突码较完整；timeout/retry 会话语义缺失。 |
| 命名语义与冗余清理 | 10 | 8 | 代码命名已迁移到 `Aevatar.DynamicRuntime.*`，但核心文档仍有旧命名与旧路径。 |
| 可验证性（门禁/构建/测试） | 15 | 15 | build/test/架构守卫均可复现通过；运行时 SLO 门禁因缺指标未闭环（在扣分项主维度处理）。 |

## 5. 分模块评分

| 模块 | 分数 | 结论 |
|---|---:|---|
| DynamicRuntime Core + Application | 80 | 主链路可运行，关键控制点（幂等/If-Match/事件发布）已成型。 |
| DynamicRuntime Infrastructure | 73 | 端口齐全但多为最小实现，生产语义闭环不足。 |
| DynamicRuntime Hosting + API | 84 | API 面完整，命令/查询边界清晰。 |
| Docs + Guards 对齐 | 70 | 守卫到位，但文档口径仍有旧命名与“状态冲突”。 |

## 6. 关键对齐项（加分证据）

1. Adapter-only 与 RoleGAgent 复用链路明确：
- `src/Aevatar.DynamicRuntime.Abstractions/Contracts/IDynamicRuntimeServices.cs:56`
- `src/Aevatar.DynamicRuntime.Core/Agents/ScriptRoleContainerAgent.cs:7`
- `src/Aevatar.DynamicRuntime.Core/Adapters/ScriptRoleCapabilityAdapter.cs:8`
2. 写接口一致性协议落地：
- 幂等获取与回放：`src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:1363`
- If-Match 并发推进：`src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:1274`
3. Compose/Build/Container/Run 主链路可跑通并有业务级测试覆盖：
- `test/Aevatar.DynamicRuntime.Application.Tests/DynamicRuntimeApplicationServiceTests.cs:646`
- `test/Aevatar.DynamicRuntime.Application.Tests/DynamicRuntimeScriptBusinessOrchestrationTests.cs:22`

## 7. 扣分项（按严重度）

| 级别 | 扣分 | 问题 | 证据 |
|---|---:|---|---|
| Blocking | -7 | Compose Reconcile 仍是占位实现，未体现依赖拓扑、滚动策略、失败回滚的“收敛过程”。 | `src/Aevatar.DynamicRuntime.Infrastructure/DefaultScriptComposeReconcilePort.cs:7`；`docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:227` |
| Blocking | -6 | Envelope 仅“订阅登记+发布入队”，缺少订阅消费、投递执行、ack/retry 对账闭环。 | `src/Aevatar.DynamicRuntime.Infrastructure/InMemoryEventEnvelopeSubscriberPort.cs:8`；`src/Aevatar.DynamicRuntime.Infrastructure/InMemoryEventEnvelopePublisherPort.cs:8`；`src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:1311` |
| Major | -4 | Exec 生命周期要求 `cancel/timeout/retry`，当前只具备 cancel，未形成 timeout/retry 事件化推进。 | `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:220`；`src/Aevatar.DynamicRuntime.Abstractions/Contracts/IDynamicRuntimeServices.cs:25` |
| Major | -3 | “统一投影链路”目标未完全落地：Application 直接 new projector 并内联投影执行。 | `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:35`；`src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:74`；`src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:1310` |
| Medium | -2 | 架构文档仍保留旧 `Aevatar.AI.Script.*` 路径与状态冲突描述，降低实施对齐可追踪性。 | `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:401`；`docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md:453`；`docs/architecture/ai-script-runtime-implementation-change-plan.md:576`；`docs/architecture/ai-script-runtime-implementation-change-plan.md:684` |

## 8. Blocking 修复准入标准（合并前）

1. Reconcile 必须从“返回成功”升级为真实收敛引擎：
- 基于 `desired_generation/observed_generation` 驱动
- 服务依赖排序 + 滚动更新 + 失败回退事件
- 收敛失败必须有稳定错误码与可审计事件链
2. Envelope 必须打通端到端闭环：
- `subscribe -> deliver -> run consume -> ack/retry`
- 明确 lease 失效与重复投递语义
- 至少补齐一组端到端合同测试
3. Exec 必须补齐 timeout/retry 事件化语义：
- 内部触发事件携带最小相关键（`run_id + step/version`）
- Actor 内进行活跃态校验，拒绝陈旧事件

## 9. 非扣分观察项（按规范豁免）

1. `InMemory` 存储/端口实现用于当前阶段开发与测试，不单独扣分（规范基线允许）。
2. Local Actor Runtime 形态不单独扣分（规范基线允许）。
3. `solution_split_guards.sh` 依赖 `--no-restore` 前置条件；在先完成 restore 后可稳定通过。

## 10. 优先级建议（删除优先，不做兼容）

1. P0：按单主链重构 Reconcile 与 Envelope，不保留“占位实现兼容壳”。
2. P0：补齐 timeout/retry 事件化推进，去除对“仅 cancel”语义的默认依赖。
3. P1：把 Application 内联投影推进迁移到统一 projection runtime 链路，删除双轨投影入口。
4. P1：一次性清理文档中的 `Aevatar.AI.Script.*` 旧路径与冲突状态描述，统一 `Aevatar.DynamicRuntime.*`。
5. P1：将 perf/availability/resilience 指标采集接入 CI 产物，解除三项门禁“缺指标失败”。

## 11. 结论

Dynamic Runtime 当前已经达到“可运行、可测试、可守卫”的工程基线，但尚未达到蓝图定义的“生产级编排收敛 + Envelope 闭环 + 完整会话语义”。在坚持“彻底无需兼容性重构”的前提下，建议按第 8 节 Blocking 项先行完成主链修复，再推进 P1 治理项。
