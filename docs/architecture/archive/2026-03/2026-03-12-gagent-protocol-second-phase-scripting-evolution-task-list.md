# GAgent 协议优先第二阶段任务清单：Scripting Evolution Actor 化收敛（2026-03-12）

## 1. 文档元信息

- 状态：Completed
- 版本：R2
- 日期：2026-03-12
- 关联文档：
  - `docs/architecture/2026-03-12-gagent-protocol-first-phase-1-task-list.md`
  - `docs/architecture/2026-03-12-gagent-protocol-first-implementation-plan.md`
  - `docs/architecture/2026-03-12-gagent-implementation-source-unification-blueprint.md`
  - `docs/FOUNDATION.md`
  - `docs/SCRIPTING_ARCHITECTURE.md`
  - `src/workflow/README.md`
  - `AGENTS.md`
- 文档定位：
  - 本文定义第一阶段完成后的真实第二阶段，而不是沿用旧实施文档中已经被后续收边吃掉的阶段编号。
  - 本文只覆盖 `Scripting Evolution` 主链的 actor 化收敛，不回头重做已经完成的 Foundation / workflow 通信边界。
  - 本文以当前仓库代码为准，目标是把剩余的核心架构债务收敛到单一主线。
  - 当前文档已转为完成归档，记录第二阶段最终落点与验收结果。

## 2. 为什么第二阶段需要重定义

旧实施文档中的“阶段 2：公共 actor 通信能力上移”和“阶段 3：workflow 通用通信面补齐”已经不再是当前仓库的真实剩余问题。

当前代码已经具备以下事实：

1. Foundation 已回归最小原语边界：`IActorRuntime`、`IActorDispatchPort`、`IEventPublisher`、`IEventContext`。
2. workflow 已落地 `actor_send`，不再依赖公共 messaging/session 过渡层。
3. cross-source protocol contract tests 已建立，协议优先主线已经成立。

当前真正剩余的结构问题集中在 `Scripting Evolution`：

1. `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptEvolutionFlowPort.cs` 同时承担 policy、compile、catalog baseline query、definition upsert、promotion、compensation，职责过重。
2. `src/Aevatar.Scripting.Core/ScriptEvolutionSessionGAgent.cs` 明明是 proposal-scoped actor，却只做转发和完成态收口，没有真正拥有 proposal 执行过程。
3. `src/Aevatar.Scripting.Core/ScriptEvolutionManagerGAgent.cs` 作为长期 actor，却承担按 proposal 变化的长耗时编排，并把大量 proposal facts 堆在 manager state 上，违反仓库“长期 actor 只保留给事实拥有者”和“run/session 优先短生命周期 actor”的规则。
4. `src/Aevatar.Scripting.Infrastructure/Ports/ScriptEvolutionDurableCompletionResolver.cs` 仍通过 manager fallback 查询完成态，导致 `ScriptEvolutionAcceptedReceipt` 泄露 `ManagerActorId`，也让 durable completion 依赖错误的事实拥有者。

结论：

第二阶段的正确目标不是继续抽象“通用通信”，而是让 `Script Evolution` 真正回到 actor-owned facts + session-scoped execution 的主线。

## 3. 第二阶段目标

第二阶段只达成五个结果：

1. `ScriptEvolutionSessionGAgent` 成为 proposal 执行的事实拥有者和主编排者。
2. `ScriptEvolutionManagerGAgent` 收窄为长期索引/治理 actor，而不是 proposal 执行器。
3. `RuntimeScriptEvolutionFlowPort` 被拆解为职责单一的组合式服务，不再充当全能 façade。
4. script evolution 的 durable completion / fallback 改为围绕 session actor，而不是 manager actor。
5. 不破坏现有 CQRS dispatch + projection observation 主链，继续走 `accepted -> session event stream -> completion`。

## 4. 完成定义

第二阶段完成时，必须满足：

1. `StartScriptEvolutionSessionRequestedEvent` 进入 session actor 后，proposal 的 policy / validation / promotion / compensation / terminal completion 由 session actor 主导。
2. manager actor 不再直接调用 compile / upsert / promote / rollback 等外部 side effect 端口。
3. `IScriptEvolutionFlowPort` 不再作为对外核心主口存在；若保留薄 orchestrator，也只能是 session actor 的内部协作者，不能再承载多职责总入口。
4. `ScriptEvolutionAcceptedReceipt` 不再包含 `ManagerActorId`。
5. 超时兜底查询直接面向 session actor 的 typed contract，不再依赖 manager proposal map。
6. Orleans 集群和本地 runtime 下的 script evolution 行为保持一致，现有 external evolution / autonomous evolution / interaction completion 语义不退化。

当前仓库已满足以上条件。

## 5. 非目标

第二阶段不做以下内容：

1. 不引入统一 `implementation_kind` 或 `source_binding` 总模型。
2. 不重写 Foundation runtime/dispatch/context 主链。
3. 不回退 workflow 的 `actor_send` 设计，也不为 workflow 重造通用 query 模块。
4. 不要求 scripting、workflow、static 的内部状态结构完全同构。
5. 不做存量 run 热替换；升级仍遵守“旧 run 留旧实现，新 run 走新实现”。

## 6. 当前结构诊断

### 6.1 当前主链是对的，但 actor 归属错位

当前 CQRS 入口实际上已经命中 session actor：

1. `src/Aevatar.Scripting.Infrastructure/Ports/ScriptEvolutionCommandTargetResolver.cs`
2. `src/Aevatar.Scripting.Infrastructure/Ports/ScriptEvolutionEnvelopeFactory.cs`
3. `src/Aevatar.Scripting.Infrastructure/Ports/ScriptEvolutionCommandTargetBinder.cs`

也就是说：

1. external request 已经先创建/解析 `ScriptEvolutionSessionGAgent`
2. accepted receipt 已经围绕 session actor 建立 projection lease 与 live sink
3. interaction completion 主链已经是 `session actor -> projection -> completion`

当前错位在于：

1. session actor 只转发到 manager actor
2. manager actor 反而执行 proposal 主流程

这让真正的 proposal-scoped facts 没有被 proposal-scoped actor 持有。

### 6.2 `RuntimeScriptEvolutionFlowPort` 是典型 god service

`src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptEvolutionFlowPort.cs` 当前同时包含：

1. 输入 policy 判断
2. 编译与 validation
3. catalog baseline query 与 fallback
4. definition upsert
5. catalog promotion
6. promotion failure compensation
7. rollback

这违反：

1. 单一职责原则
2. actor 边界内聚原则
3. 组合优于 façade 聚合的设计原则

### 6.3 manager actor 当前承载了错误类型的事实

`src/Aevatar.Scripting.Abstractions/script_host_messages.proto` 中的 `ScriptEvolutionManagerState` 当前保存：

1. `proposals`
2. `latest_proposal_by_script`

其中真正长期稳定、值得长期 actor 持有的只有索引类事实，例如：

1. `latest_proposal_by_script`
2. 可能的去重/限流/治理事实

而 proposal 的逐步执行状态：

1. validation diagnostics
2. promoted definition actor id
3. candidate revision terminal status
4. failure reason

本质都属于 session/proposal facts，更适合落在 `ScriptEvolutionSessionState`。

### 6.4 fallback 事实源当前选错了 actor

当前 durable completion 链路：

1. receipt 保存 `ManagerActorId`
2. durable fallback 通过 manager query `QueryScriptEvolutionDecisionRequestedEvent`
3. manager 依赖自己的 proposal map 返回 `ScriptEvolutionDecisionRespondedEvent`

这条链路的问题是：

1. query 面向的不是执行 owner
2. durable completion 依赖长期 actor 的缓存式聚合状态
3. session actor 已经有 terminal completion event，却没有成为 fallback 的唯一权威来源

## 7. 目标架构

## 7.1 Session Actor 成为 proposal owner

目标态下：

1. `ScriptEvolutionSessionGAgent` 负责 proposal 生命周期推进。
2. session actor 自己持久化 proposal 事实、validation 事实、promotion 结果与 terminal completion。
3. session actor 直接发布 `ScriptEvolutionSessionCompletedEvent` 作为 interaction 和 projection 的统一终态。

建议 `ScriptEvolutionSessionState` 扩展为明确 typed facts，至少包含：

1. `candidate_source_hash`
2. `policy_allowed`
3. `validation_succeeded`
4. `validation_diagnostics`
5. `definition_actor_id`
6. `catalog_actor_id`
7. `promotion_compensation_status`
8. `completed / accepted / status / failure_reason`

## 7.2 Manager Actor 收窄为长期索引/治理边界

第二阶段不要求立即删除 manager actor，但要求将其收窄到长期事实边界。

manager actor 允许保留的职责：

1. `latest_proposal_by_script`
2. 显式建模的去重/限流/治理规则
3. 后续若需要的 catalog 级索引事实

manager actor 禁止继续承担：

1. compile
2. validation
3. definition upsert
4. promotion
5. compensation
6. proposal terminal callback 组装

manager 若仍需更新索引，必须通过 session actor 结束时显式发送 typed index-update message，而不是通过外部直接读取 session 内部状态。

## 7.3 Flow Port 拆为组合式协作者

建议将当前 `RuntimeScriptEvolutionFlowPort` 拆为以下协作者：

1. `IScriptEvolutionPolicyEvaluator`
2. `IScriptPackageValidationService`
3. `IScriptCatalogBaselineReader`
4. `IScriptDefinitionWriter`
5. `IScriptCatalogPromotionService`
6. `IScriptPromotionCompensationService`
7. `IScriptEvolutionRollbackService`

actor 侧使用方式：

1. session actor 按固定顺序调用这些窄接口
2. side effect service 只负责与外部 actor / compiler / query client 交互
3. proposal 生命周期决策、领域事件持久化和错误语义仍由 actor 自己做

## 7.4 Durable Completion 回到 session actor

目标态下：

1. `ScriptEvolutionAcceptedReceipt` 只保留 `SessionActorId`、`ProposalId`、`CommandId`、`CorrelationId`
2. `IScriptEvolutionDecisionFallbackPort` 改为面向 session actor 查询
3. `QueryScriptEvolutionDecisionRequestedEvent` / `ScriptEvolutionDecisionRespondedEvent` 迁到 session actor，或等价替换为新的 session-scoped typed query contract
4. `ScriptEvolutionDurableCompletionResolver` 只依赖 session actor 的权威终态

## 8. 执行顺序

严格按以下顺序推进：

1. 先收敛事实归属与 state/proto
2. 再把 proposal 执行主链移到 session actor
3. 再拆解 flow port
4. 再收窄 manager 与 fallback
5. 最后补守卫、测试和文档

禁止一开始同时重写 session、manager、projection、Host 多条链路。

## 9. 任务清单

## T1. 重新定义 Script Evolution 的事实归属

### 目标

把 proposal 过程事实从 manager state 迁回 session state。

### 任务

1. 调整 `script_host_messages.proto` 中 `ScriptEvolutionSessionState` 的 typed 字段。
2. 缩减 `ScriptEvolutionManagerState`，保留长期索引/治理最小集合。
3. 明确哪些事实只允许由 session actor 持有，哪些事实允许由 manager actor 持有。

### 涉及位置

1. `src/Aevatar.Scripting.Abstractions/script_host_messages.proto`
2. `src/Aevatar.Scripting.Core/ScriptEvolutionSessionGAgent.cs`
3. `src/Aevatar.Scripting.Core/ScriptEvolutionManagerGAgent.cs`

### 验收

1. proposal 生命周期关键事实不再以 manager 的 `proposals` map 为唯一事实源。

## T2. 让 Session Actor 直接拥有 proposal 执行主链

### 目标

消除 `session -> manager -> flow port -> callback -> session` 这条绕路链路。

### 任务

1. `HandleStartScriptEvolutionSessionRequested` 直接在 session actor 内推进 proposal 执行。
2. session actor 自己持久化 `proposed / validated / promoted / rejected / rolled_back / completed` 等领域事件。
3. session actor 直接产出终态 `ScriptEvolutionSessionCompletedEvent`。

### 涉及位置

1. `src/Aevatar.Scripting.Core/ScriptEvolutionSessionGAgent.cs`
2. `src/Aevatar.Scripting.Core/ScriptEvolutionManagerGAgent.cs`

### 验收

1. session actor 不再只是请求转发器。
2. manager actor 不再是 proposal 执行主编排者。

## T3. 拆解 `RuntimeScriptEvolutionFlowPort`

### 目标

把 god service 拆成组合式协作者。

### 任务

1. 按职责拆出 policy、validation、baseline、definition writer、promotion、compensation、rollback 服务。
2. 删除 `IScriptEvolutionFlowPort`，或将其降为内部薄 orchestrator，不再暴露为核心对外主口。
3. 调整 DI 注册，使协作者按职责注入。

### 涉及位置

1. `src/Aevatar.Scripting.Core/Ports/IScriptEvolutionFlowPort.cs`
2. `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptEvolutionFlowPort.cs`
3. `src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`

### 验收

1. 不再存在同时承担 compile + query + promote + compensate 的全能 flow port。

## T4. 收窄 Manager Actor

### 目标

把 manager actor 重新定义为长期索引/治理边界。

### 任务

1. 去掉 manager actor 对 proposal 主流程的直接执行。
2. 若保留 manager query，只允许查询索引级事实，不再查询 proposal 执行细节。
3. 若 session 完成后需要更新 manager，使用显式 typed index update message。

### 涉及位置

1. `src/Aevatar.Scripting.Core/ScriptEvolutionManagerGAgent.cs`
2. `src/Aevatar.Scripting.Core/Ports/IScriptingActorAddressResolver.cs`
3. `src/Aevatar.Scripting.Infrastructure/Ports/DefaultScriptingActorAddressResolver.cs`

### 验收

1. manager actor 的职责描述可以用一句话说清：索引/治理，而不是 proposal 执行。

## T5. 重写 Durable Completion 与 Fallback

### 目标

让 completion fallback 面向 session actor 的权威终态。

### 任务

1. `ScriptEvolutionAcceptedReceipt` 删除 `ManagerActorId`。
2. `IScriptEvolutionDecisionFallbackPort` 改为按 `SessionActorId + ProposalId` 查询。
3. `ScriptEvolutionDurableCompletionResolver` 直接依赖 session actor fallback。
4. 把 query handler 移到 session actor，或用新的 session-scoped typed query 替换旧 query。

### 涉及位置

1. `src/Aevatar.Scripting.Application/Application/ScriptEvolutionInteractionModels.cs`
2. `src/Aevatar.Scripting.Core/Ports/IScriptEvolutionDecisionFallbackPort.cs`
3. `src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptEvolutionDecisionFallbackPort.cs`
4. `src/Aevatar.Scripting.Infrastructure/Ports/ScriptEvolutionDurableCompletionResolver.cs`
5. `src/Aevatar.Scripting.Core/ScriptEvolutionSessionGAgent.cs`
6. `src/Aevatar.Scripting.Core/ScriptEvolutionManagerGAgent.cs`

### 验收

1. durable completion 不再依赖 manager proposal map。

## T6. 保持 CQRS / Projection 主链不变

### 目标

在重构 actor 边界时，不破坏现有 accepted / live sink / durable completion 主链。

### 任务

1. 保留 `ScriptEvolutionCommandTargetResolver` 的 session actor 解析思路。
2. 保留 `ScriptEvolutionCommandTargetBinder` 的 projection lease + live sink 绑定路径。
3. 保留 `ScriptEvolutionSessionCompletedEventProjector` 作为对外完成态主链。

### 涉及位置

1. `src/Aevatar.Scripting.Infrastructure/Ports/ScriptEvolutionCommandTargetResolver.cs`
2. `src/Aevatar.Scripting.Infrastructure/Ports/ScriptEvolutionCommandTargetBinder.cs`
3. `src/Aevatar.Scripting.Projection/Projectors/ScriptEvolutionSessionCompletedEventProjector.cs`

### 验收

1. interaction service、projection、API 不需要重新发明第二套完成态通路。

## T7. 补测试与门禁

### 目标

让第二阶段重构有明确回归网。

### 必测范围

1. session actor 成为 proposal owner 的单元测试
2. manager 收窄后的单元测试
3. durable completion fallback 改查 session 的基础设施测试
4. external evolution / autonomous evolution / Orleans 3-node 集成测试

### 涉及位置

1. `test/Aevatar.Scripting.Core.Tests/Runtime/`
2. `test/Aevatar.Hosting.Tests/`
3. `test/Aevatar.Integration.Tests/`
4. `test/Aevatar.Integration.Slow.Tests/`
5. `tools/ci/architecture_guards.sh`
6. `tools/ci/test_stability_guards.sh`

### 验收

1. 第二阶段的事实归属、fallback 归属和 actor 生命周期退化会被测试或 guard 拦截。

## T8. 更新文档

### 目标

让文档、代码与阶段认知一致。

### 必须更新

1. `docs/SCRIPTING_ARCHITECTURE.md`
2. `docs/architecture/2026-03-12-gagent-protocol-first-implementation-plan.md`
3. 必要时补充新的第二阶段完成记录文档

### 验收

1. 文档不再把 manager actor 描述成 proposal 执行 owner。
2. 文档不再把 `RuntimeScriptEvolutionFlowPort` 描述成推荐终态。

## 10. 建议任务分组

## Group A：事实归属与 actor 边界

1. `T1`
2. `T2`
3. `T4`

## Group B：服务拆分与 completion 收敛

1. `T3`
2. `T5`
3. `T6`

## Group C：验证与文档

1. `T7`
2. `T8`

## 11. 验收命令建议

实际完成验证：

1. `dotnet build aevatar.slnx --nologo`
2. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptEvolutionSessionGAgentTests|FullyQualifiedName~ScriptEvolutionManagerGAgentTests|FullyQualifiedName~ScriptEvolutionExecutionServicesTests|FullyQualifiedName~RuntimeScriptInfrastructurePortsTests"`
3. `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptCapabilityHostExtensionsTests"`
4. `dotnet test test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj --nologo --filter "FullyQualifiedName=Aevatar.Integration.Tests.ScriptExternalEvolutionE2ETests.ExternalEvolutionFlow_ShouldPromoteRevisionThroughUnifiedManagerChain"`
5. `AEVATAR_TEST_ORLEANS_3NODE=1 dotnet test test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj --nologo --filter "FullyQualifiedName=Aevatar.Integration.Tests.ScriptAutonomousEvolutionOrleans3ClusterConsistencyTests.ComplexScriptFlow_ShouldRemainConsistentAcrossThreeOrleansSilos"`
6. `bash tools/ci/architecture_guards.sh`
7. `bash tools/ci/test_stability_guards.sh`

## 13. 完成结果

第二阶段最终落点如下：

1. `ScriptEvolutionSessionGAgent` 已成为 proposal 执行 owner，直接持久化 proposal 生命周期事实、发出 terminal completion，并以标准 self-message inbox 下一拍事件启动执行。
2. `ScriptEvolutionManagerGAgent` 已收窄为长期索引 actor，只镜像 session 事实，不再执行 compile / promote / rollback 主流程。
3. `IScriptEvolutionFlowPort` / `RuntimeScriptEvolutionFlowPort` 已删除，替换为 policy / validation / baseline / compensation / rollback 等窄协作者。
4. `ScriptEvolutionAcceptedReceipt` 已移除 `ManagerActorId`，durable completion fallback 已改查 session actor。
5. 本地 runtime 与 Orleans 3-node 的 external / autonomous evolution 集群语义已重新对齐。

## 12. 收束性结论

第二阶段不该再围绕“统一来源通信抽象”打转。  
当前真正需要解决的是：让 `Script Evolution` 的 proposal facts、执行编排、completion fallback 和 actor 生命周期重新对齐。

最简洁的正确方向是：

1. session actor 拥有 proposal 执行
2. manager actor 退回长期索引/治理
3. god flow port 拆成组合服务
4. fallback 回到 session actor 的权威终态

这就是第二阶段的完整任务清单。
