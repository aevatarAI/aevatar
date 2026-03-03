# Aevatar.Scripting 架构实施文档（2026-03-02 R2）

## 1. 实施结论

当前实现已经满足以下目标：

1. 脚本内可发起自我演化，并完成提案、验证、发布与回滚。
2. 外部入口可发起同构提案，进入同一演化治理链路。
3. 运行态与演化态全部通过事件通信协作，事实状态由 Actor 持久态承载。
4. Orleans 3 集群场景可稳定运行复杂多脚本、多轮演化测试。

## 2. 分层实施清单

### 2.1 Abstractions

文件：`src/Aevatar.Scripting.Abstractions/script_host_messages.proto`

已落地内容：

1. 增加演化状态与事件链。
2. 增加查询契约：Definition/Catalog/Evolution 的 request/response 事件。
3. 保持运行事件与演化事件统一在 protobuf 契约层表达。

### 2.2 Core

关键 Actor：

1. `ScriptDefinitionGAgent`
2. `ScriptRuntimeGAgent`
3. `ScriptEvolutionManagerGAgent`
4. `ScriptCatalogGAgent`

关键实现点：

1. Definition/Catalog/EvolutionManager 均实现 Query Handler，直接基于自身状态响应。
2. `ScriptRuntimeGAgent` 在 Orleans 路径启用事件化 definition 查询，收到响应后恢复执行。
3. `ScriptRuntimeGAgent` 对 Query Response 做 revision 与 source 对账，拒绝陈旧/不一致响应。
4. 运行模式判断采用“显式配置优先 + Runtime 类型回退”策略：`Scripting:Runtime:UseEventDrivenDefinitionQuery` 未配置时，Orleans 自动启用事件化 definition 查询，Local 保持关闭。

### 2.3 Application

已落地 Query Adapter：

1. `QueryScriptDefinitionSnapshotRequestAdapter`
2. `QueryScriptCatalogEntryRequestAdapter`

职责：

1. 统一构造 `EventEnvelope`。
2. 统一 `request_id` 作为 `correlation_id`。
3. 保持 query 事件发布者语义可区分（definition/catalog/evolution）。

### 2.4 Hosting

关键端口：

1. `RuntimeScriptDefinitionSnapshotPort`
2. `RuntimeScriptLifecyclePort`

实施要点：

1. Query 端口都采用“订阅 reply stream -> 发送 query -> 等待响应/超时”的一致模式。
2. `RuntimeScriptLifecyclePort.SpawnRuntimeAsync` 已去除 definition snapshot 依赖，只做 runtime 实例生命周期管理。
3. Orleans 集成测试引入 `Microsoft.Orleans.Serialization.Protobuf` 支撑跨 silo 的 protobuf 事件序列化。
4. `RuntimeScriptLifecyclePort` 已改为“Session Actor 会话化收敛”模型：先启动会话 actor，再等待 `scripting.evolution.session.reply:{proposalId}` 终态事件。
5. 端口超时由 `IScriptingPortTimeouts` 统一提供，默认实现为 `DefaultScriptingPortTimeouts`。

## 3. 关键实现细节

### 3.1 响应链路防丢

在以下三个 Actor 中，Query Response 均使用 `sourceEnvelope: null`：

1. `ScriptDefinitionGAgent`
2. `ScriptCatalogGAgent`
3. `ScriptEvolutionManagerGAgent`

目的：

1. 避免沿用原入站 envelope 的 publisher chain 导致响应被回路保护误丢。

### 3.2 Runtime 事件化恢复执行

`ScriptRuntimeGAgent` 采用 `_pendingRuns` 暂存待执行请求（Actor 内运行态），在收到 `ScriptDefinitionSnapshotRespondedEvent` 后恢复执行。

该设计满足：

1. 无跨线程共享写入（Actor 单线程事件处理）。
2. 无中间层全局事实缓存。
3. 可在 Orleans 激活队列模型下避免同步阻塞。

### 3.3 Evolution 决策会话化回传

`ProposeScriptEvolutionRequestedEvent` 增加决策回传字段：

1. `callback_actor_id`
2. `callback_request_id`

执行路径：

1. `RuntimeScriptLifecyclePort` 创建/获取 `ScriptEvolutionSessionGAgent`，发送 `StartScriptEvolutionSessionRequestedEvent`。
2. Session actor 转发 `ProposeScriptEvolutionRequestedEvent` 到 `ScriptEvolutionManagerGAgent`，并把自身 `actor_id` 作为 `callback_actor_id`。
3. manager 在终态（promoted/rejected）回调 `ScriptEvolutionDecisionRespondedEvent` 到 session actor。
4. session actor 持久化 `ScriptEvolutionSessionCompletedEvent`，并推送到 `scripting.evolution.session.reply:{proposalId}`。
5. Port 单次等待会话终态并返回 `ScriptPromotionDecision`；若会话流等待超时，执行一次 manager query fallback 作为可靠性补偿。

### 3.4 Spawn 解耦

`RuntimeScriptLifecyclePort.SpawnRuntimeAsync` 当前不做 Definition 快照读取：

1. 输入参数校验后直接生成 runtime actor id。
2. 若 actor 已存在返回复用；否则创建。
3. Definition 版本正确性由后续 `RunScriptRequestedEvent` 路径校验。

## 4. 测试覆盖落地

### 4.1 功能完备性

`test/Aevatar.Integration.Tests/ScriptAutonomousEvolutionComprehensiveE2ETests.cs` 覆盖：

1. 多自定义脚本协作。
2. 运行中创建临时脚本 runtime。
3. 运行中创建新的脚本定义与新的脚本 runtime。
4. 多轮自我演化与目录发布结果对账。

### 4.2 Orleans 3 集群一致性

`test/Aevatar.Integration.Tests/ScriptAutonomousEvolutionOrleans3ClusterConsistencyTests.cs` 覆盖：

1. 三节点 Silo 启动与跨节点可见性。
2. 脚本编排创建新 definition/runtime 并跨节点可读。
3. 外部提案在不同节点发起并达成一致 catalog 结果。

### 4.3 基础回放契约

`test/Aevatar.Scripting.Core.Tests/Runtime/ScriptRuntimeGAgentReplayContractTests.cs` 与
`test/Aevatar.Scripting.Core.Tests/Runtime/ScriptEvolutionManagerGAgentTests.cs` 覆盖运行/演化核心状态机与契约行为。

## 5. 本次验证记录（2026-03-02）

1. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo`：`58/58` 通过。
2. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptAutonomousEvolutionE2ETests|FullyQualifiedName~ScriptAutonomousEvolutionComprehensiveE2ETests|FullyQualifiedName~ScriptAutonomousEvolutionOrleans3ClusterConsistencyTests"`：`6/6` 通过。
3. `bash tools/ci/test_stability_guards.sh`：通过。
4. `bash tools/ci/architecture_guards.sh`：通过。

## 6. 与“全脚本迭代”目标的对应关系

目标：在框架边界稳定后，后续能力演进主要通过脚本完成。

当前支撑能力：

1. 脚本可上载新定义并实例化新 runtime。
2. 脚本可触发自我演化并推进发布。
3. 外部入口与自演化入口语义一致，可并行治理。

当前未完成项（仍需框架侧增量）：

1. 当前运行模式采用显式配置 `Scripting:Runtime:UseEventDrivenDefinitionQuery`，后续可升级为分层策略提供器（dev/staging/prod + runtime capability）。
2. `IScriptingPortTimeouts` 当前为固定默认实现，建议提供环境分层实现（dev/staging/prod）。
3. 可进一步补充高并发提案下的背压与限流策略验证。

## 7. 风险与治理建议

1. 若提案量激增，需对 `RuntimeScriptLifecyclePort` 的决策回传 stream 增加更严格背压与限流策略。
2. 若未来扩展更多运行时实现，建议在显式配置基础上增加可插拔运行模式策略提供器。
3. 保持 `test_polling_allowlist` 仅覆盖跨节点最终一致性探测测试，防止轮询扩散到一般测试。
