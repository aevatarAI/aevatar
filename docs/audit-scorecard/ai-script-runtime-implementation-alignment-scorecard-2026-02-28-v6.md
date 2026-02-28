# AI Script Runtime 实施对齐评分卡（2026-02-28，v6）

## 1. 审计范围与口径

1. 评分模式：**实现对齐评分（Implementation Alignment）**，按“文档承诺 vs 当前代码”逐项核对。
2. 对照文档：
- `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md`（v3.3）
- `docs/architecture/ai-script-runtime-implementation-change-plan.md`（v1.4）
3. 代码范围：
- `src/Aevatar.DynamicRuntime.*`
- `test/Aevatar.DynamicRuntime.Application.Tests`
- `tools/ci/*`（与 script runtime 相关守卫）
4. 本次验证命令：
- `dotnet test aevatar.dynamic.slnf --nologo`（通过：22/22）
- `bash tools/ci/architecture_doc_consistency_guards.sh`（通过）
- `rg -n "LinkAsync\(|UnlinkAsync\(" src/Aevatar.DynamicRuntime.*`（无结果）
- `rg -n "Reducer|Projector|Route|TypeUrl" src/Aevatar.DynamicRuntime.Projection`（无结果）

## 2. 总分

**68 / 100（B）**

结论：主链路已能跑通（Image/Compose/Container/Run/Build/Script 执行），但离文档定义的 Docker 对齐目标仍有结构性缺口，主要集中在 **Projection 单管道、Envelope 端到端投递闭环、Actor 树收敛与动态 IoC 子容器**。

## 3. 分维度评分

| 维度 | 权重 | 得分 | 结论 | 关键证据 |
|---|---:|---:|---|---|
| 架构边界与命名隔离 | 10 | 10 | 已对齐 | `aevatar.dynamic.slnf` 独立；`src/Aevatar.DynamicRuntime.*` 命名统一；动态运行时代码中未检出 `Aevatar.Workflow` 依赖。 |
| Adapter-only + RoleGAgent 复用 | 15 | 14 | 基本对齐 | `IScriptRoleEntrypoint.HandleEventAsync(EventEnvelope)`：`src/Aevatar.DynamicRuntime.Abstractions/Contracts/IDynamicRuntimeServices.cs:50`；`ScriptRoleContainerAgent : RoleGAgent`：`src/Aevatar.DynamicRuntime.Core/Agents/ScriptRoleContainerAgent.cs:7`；`ScriptRoleAgentHost : RoleGAgent`：`src/Aevatar.DynamicRuntime.Infrastructure/ScriptRoleAgentChatClient.cs:71`。 |
| Docker 核心对象与生命周期 | 20 | 15 | 部分对齐 | Image/Compose/Service/Container/Run/Build Actor 已落地；`exec` 接口走 `service_id + envelope`：`src/Aevatar.DynamicRuntime.Hosting/CapabilityApi/DynamicRuntimeEndpoints.cs:147`。但 run 的 timeout/retry 生命周期未闭环。 |
| Compose 收敛与 Actor 树 | 15 | 7 | 未充分对齐 | `Reconcile` 仍是恒成功占位：`src/Aevatar.DynamicRuntime.Infrastructure/DefaultScriptComposeReconcilePort.cs:7`；动态运行时代码未使用 `LinkAsync/UnlinkAsync` 建树。 |
| Envelope 路由与执行闭环 | 15 | 6 | 未对齐 | 订阅端仅记录 lease：`src/Aevatar.DynamicRuntime.Infrastructure/InMemoryEventEnvelopeSubscriberPort.cs:18`；发布端仅入队：`src/Aevatar.DynamicRuntime.Infrastructure/InMemoryEventEnvelopePublisherPort.cs:13`；未形成 `subscribe -> dispatch -> run -> ack/retry` 闭环。 |
| CQRS 与统一 Projection Pipeline | 15 | 5 | 未对齐 | Command 侧直接 `Upsert`/`Get` ReadStore：`src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:151`、`:184`、`:211`；Projection 项目未见 reducer/projector 路由实现（检索无结果）。 |
| 动态 IoC 与隔离策略 | 10 | 4 | 未对齐 | 文档要求实例级子容器；当前实现未见 `IServiceScopeFactory/CreateScope` 或子容器生命周期绑定。 |
| 治理门禁与 SLO | 10 | 7 | 部分对齐 | `script_runtime_*_guards.sh` 已有；但 `solution_split_guards.sh`/`solution_split_test_guards.sh` 未纳入 `aevatar.dynamic.slnf`：`tools/ci/solution_split_guards.sh:8`、`tools/ci/solution_split_test_guards.sh:8`。 |

## 4. 未对齐清单（按优先级）

| 优先级 | 对齐项 | 文档要求 | 当前实现状态 | 影响 | 证据 |
|---|---|---|---|---|---|
| P0 | 统一 Projection Pipeline | 写侧事件统一进投影管道（需求文档 `:35`、硬约束 `:75`） | Command 服务直接写读模型存储，未通过 reducer/projector 统一投影路由 | 读写耦合，无法保证“事件即事实”的统一回放口径 | `src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:151`、`:184`、`:211`；`src/Aevatar.DynamicRuntime.Projection` 无 reducer 检索结果 |
| P0 | Envelope 端到端投递 | Stack 内跨 service 必须走 Envelope 路由并完成去重对账（需求文档 `:78`、`:344`、`:346`） | 仅有订阅登记 + 发布入队；未实现投递、ack、retry、死信/补偿 | event/hybrid 语义不完整，无法对齐 Docker Compose 事件驱动服务行为 | `src/Aevatar.DynamicRuntime.Infrastructure/InMemoryEventEnvelopeSubscriberPort.cs:18`；`src/Aevatar.DynamicRuntime.Infrastructure/InMemoryEventEnvelopePublisherPort.cs:13` |
| P0 | Compose Reconcile 引擎 | generation 收敛需按依赖拓扑推进（需求文档 `:225`） | Reconcile 恒返回 converged | 无法验证真实收敛与失败回滚策略 | `src/Aevatar.DynamicRuntime.Infrastructure/DefaultScriptComposeReconcilePort.cs:11` |
| P0 | Actor Tree First | 编排拓扑应显式映射 Actor 树（需求文档 `:38`） | DynamicRuntime 源码中未调用 `LinkAsync/UnlinkAsync` | 父子生命周期与回放关系缺失 | `rg -n "LinkAsync\\(|UnlinkAsync\\(" src/Aevatar.DynamicRuntime.*` 无结果 |
| P1 | 动态 IoC 子容器 | 运行时需实例级子容器隔离（需求文档 `:53`、`:73`、`:275`） | 未见实例级 DI 子容器创建与回收实现 | 容器隔离边界不足，不满足文档的 Runtime 语义 | 代码检索未见 `CreateScope/IServiceScopeFactory` |
| P1 | Run timeout/retry 生命周期 | Exec 生命周期应支持 cancel/timeout/retry（需求文档 `:49`、`:218`、矩阵 `R-RUN-01`） | 仅 cancel 闭环；timeout/retry 未形成应用服务流程 | 运行控制能力未达成文档验收标准 | `ScriptRunTimedOutEvent` 存在但无对应应用层触发路径：`src/Aevatar.DynamicRuntime.Core/Agents/ScriptRunGAgent.cs:24` |
| P1 | CI 分片门禁纳入 Dynamic 子解 | 实施文档要求分片门禁覆盖 script 子解（实施文档 `:594`、`:595`） | split guard 列表无 `aevatar.dynamic.slnf` | 动态运行时回归风险未被分片门禁覆盖 | `tools/ci/solution_split_guards.sh:8`；`tools/ci/solution_split_test_guards.sh:8` |
| P2 | 文档命名与实现命名一致性 | 项目/命名空间需语义一致 | 实施文档仍出现 `Aevatar.AI.Script.*` 目录规划 | 增加后续实施与审计歧义 | `docs/architecture/ai-script-runtime-implementation-change-plan.md:552`、`:564` |

## 5. 已对齐的关键项

1. `exec` 请求体已采用 `service_id + EventEnvelope`，且会补齐 envelope 元数据，不再引入 `ScriptRoleRequest` 中间语义模型。  
证据：`src/Aevatar.DynamicRuntime.Hosting/CapabilityApi/DynamicRuntimeEndpoints.cs:147`、`:228`
2. Adapter-only 主路径成立，脚本入口与平台 `IRoleAgent` 能力桥接已落地。  
证据：`src/Aevatar.DynamicRuntime.Abstractions/Contracts/IDynamicRuntimeServices.cs:50`、`src/Aevatar.DynamicRuntime.Core/Adapters/ScriptRoleCapabilityAdapter.cs:8`
3. 执行面已复用 `RoleGAgent` 的 LLM 能力通道。  
证据：`src/Aevatar.DynamicRuntime.Infrastructure/ScriptRoleAgentChatClient.cs:71`
4. 镜像 digest 绑定与 build/publish/rollout 主链路可运行。  
证据：`src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:1163`、`:1261`
5. `daemon/event/hybrid` 三态有显式校验与订阅策略分支。  
证据：`src/Aevatar.DynamicRuntime.Infrastructure/DefaultServiceModePolicyPort.cs:14`、`src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:1175`

## 6. 最终结论

当前实现已完成“可运行的 P0/P1 骨架”，但距离文档定义的“Docker + Compose 完整语义对齐”还差关键的控制面与运行面闭环能力。下一轮应优先补齐 **P0 四项**（Projection 单管道、Envelope 投递闭环、Reconcile 引擎、Actor 树链接），否则总分难以进入 85+ 区间。
