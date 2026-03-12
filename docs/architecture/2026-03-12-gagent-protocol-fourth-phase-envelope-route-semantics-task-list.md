# GAgent 协议优先第四阶段任务清单：EnvelopeRoute 正交重构（2026-03-12）

## 1. 文档元信息

- 状态：Completed
- 版本：R5
- 日期：2026-03-12
- 关联文档：
  - `docs/architecture/2026-03-12-gagent-protocol-third-phase-scripting-query-observation-task-list.md`
  - `docs/FOUNDATION.md`
  - `docs/SCRIPTING_ARCHITECTURE.md`
  - `AGENTS.md`
- 文档定位：
  - 本文记录第四阶段 `EnvelopeRoute` 正交重构的最终落点与验收结果。
  - 本阶段不再处理 Scripting query / observation 双轨，也不回头重做 actor ownership。
  - 本阶段已经完成 `EventDirection -> BroadcastDirection`、`EnvelopeRoute.oneof { broadcast | direct | observe }`、`PublishCommittedAsync`、Local/Orleans route 语义对齐与上层迁移。

## 2. 问题定义

第三阶段为了修复“只有 committed domain event 才能被 projection / observer 看见”，在 Foundation 中新增了：

1. `EventDirection.Observe`
2. `PersistDomainEventsAsync(...) -> PublishAsync(..., EventDirection.Observe)`
3. Orleans / Local runtime 对 `Observe` 的 inbox 忽略处理

行为修复是对的，但当前模型把三类不同路由语义硬塞进了一个 `EventDirection` 字段：

1. `Broadcast`
   - 基于 actor topology 的传播
2. `Direct`
   - 发给某个明确 actor inbox
3. `Observe`
   - 发给 projection / observer / live sink 的 committed fact 观察流

这不是“容器统一”的问题，而是“单字段过载”的问题。

## 3. 结论

第四阶段的目标模型固定为：

1. `EventEnvelope` 继续作为统一消息包络
2. `EnvelopeRoute` 继续作为统一路由模型
3. `EnvelopeRoute` 改为 `oneof { broadcast | direct | observe }`

也就是：

1. 统一 envelope
2. 统一 route
3. 但不再用一个枚举字段勉强编码所有路由语义

## 4. 为什么这是最佳实践

### 4.1 单一语义字段

当前 `EventDirection` 同时承担：

1. broadcast direction
2. direct-send 编码辅助
3. observer-only 模式

这违反仓库已经明确写入 `AGENTS.md` 的约束：

1. 一个字段只能表达一个含义
2. 核心语义必须强类型

改成 `oneof route` 之后：

1. `BroadcastRoute` 只表达 broadcast
2. `DirectRoute` 只表达 direct
3. `ObserveRoute` 只表达 observe

### 4.2 非法状态不可表达

当前模型天然允许很多语义别扭的状态，例如：

1. `Direction=Observe + TargetActorId=actor-x`
2. `Direction=Up + TargetActorId=actor-x`
3. `Direction=Self + TargetActorId=other`

改成 `oneof` 后，这些组合在协议层直接消失。

### 4.3 比 “kind + 一堆 only 字段” 更干净

如果做成：

1. `kind`
2. `broadcast_direction // only for broadcast`
3. `target_actor_id // only for direct`

本质上还是把非法状态留给运行时校验。

一旦设计里开始出现一堆 “only for X” 的字段，通常就说明：

`oneof` 更适合。

### 4.4 Runtime 职责会更清晰

重构后：

1. `EventRouter` 只处理 `BroadcastRoute`
2. runtime direct delivery 只处理 `DirectRoute`
3. projection / observer / live sink 只处理 `ObserveRoute`

runtime 不再通过特判某个 `direction` 值来猜 envelope 到底属于哪一类消息。

## 5. 非目标

第四阶段不做以下内容：

1. 不重做 `publish/send` runtime-neutral 这条已经完成的语义
2. 不回头重做 Scripting 第三阶段的 query / observation 收敛
3. 不把 `IStream.ProduceAsync` 命名清理作为本阶段主线
4. 不重写 workflow / scripting / static actor 的业务协议
5. 不为了兼容旧 envelope 继续保留公共双轨契约

## 6. 目标架构

### 6.1 统一 Envelope，正交 Route

目标态下：

1. `EventEnvelope`
   - 继续作为统一消息包络
2. `EnvelopeRoute`
   - 继续作为统一路由模型
   - 但改成显式互斥的 route 变体

建议形态：

```proto
message EnvelopeRoute {
  string publisher_actor_id = 1;

  oneof route {
    BroadcastRoute broadcast = 2;
    DirectRoute direct = 3;
    ObserveRoute observe = 4;
  }
}

message BroadcastRoute {
  BroadcastDirection direction = 1;
}

message DirectRoute {
  string target_actor_id = 1;
}

message ObserveRoute {}

enum BroadcastDirection {
  BROADCAST_DIRECTION_UNSPECIFIED = 0;
  BROADCAST_DIRECTION_DOWN = 1;
  BROADCAST_DIRECTION_UP = 2;
  BROADCAST_DIRECTION_BOTH = 3;
  BROADCAST_DIRECTION_SELF = 4;
}
```

关键点：

1. `Direction` 不再承载 direct / observe
2. `TargetActorId` 只存在于 `DirectRoute`
3. `Observe` 不再伪装成某个特殊 direction 值

### 6.2 Broadcast

`PublishAsync(...)` 只构造 `BroadcastRoute`

规则：

1. broadcast 继续支持 `Down / Up / Both / Self`
2. `EventRouter` 只处理 `BroadcastRoute`
3. workflow / actor 自推进仍然可以继续用 `Self`

### 6.3 Direct

`SendToAsync(targetActorId, ...)` 只构造 `DirectRoute`

规则：

1. direct send 不再通过 `Self + targetActorId` 编码
2. `DirectRoute` 只表达目标 inbox
3. runtime 直接按 target actor 处理，不再经过 broadcast router 语义

### 6.4 Observe

committed domain event observation 只构造 `ObserveRoute`

规则：

1. `PersistDomainEventsAsync(...)` 改为发布 `ObserveRoute`
2. actor inbox 永远不处理 `ObserveRoute`
3. projection / observer / live sink 只消费 `ObserveRoute`

## 7. API 与职责建议

目标态下建议收敛成下面这组接口：

1. `IEventPublisher`
   - `PublishAsync<TEvent>(..., BroadcastDirection direction = Down, ...)`
   - `SendToAsync<TEvent>(string targetActorId, ...)`
2. 若后续需要进一步收窄，也可以再拆：
   - `IActorEventPublisher`
   - `IActorDirectSender`
   - `ICommittedEventObserverPublisher`

当前阶段最关键的不是先拆接口数量，而是先把公共协议层从 `EventDirection` 单字段过载里解开。

## 8. Runtime 分工

重构后的 runtime 分工固定为：

1. `EventRouter`
   - 只处理 `BroadcastRoute`
2. `RuntimeActorGrain`
   - 消费 `BroadcastRoute` 与 `DirectRoute`
   - 忽略 `ObserveRoute`
3. Local / Orleans publisher
   - broadcast：写 actor topology 链路
   - direct：写 target inbox
   - observe：写 observer/projection 可见流

一句话总结：

`BroadcastRoute -> router -> actor topology`
`DirectRoute -> target inbox`
`ObserveRoute -> projection/observer stream`

## 9. 执行顺序

严格按以下顺序推进：

1. 先把 `EventDirection` 拆成 `BroadcastDirection + oneof route`
2. 再把 `SendToAsync(targetActorId, ...)` 改为构造 `DirectRoute`
3. 再把 committed observation 改为构造 `ObserveRoute`
4. 再改 Local / Orleans runtime
5. 最后统一改 workflow / scripting / tests / docs

不要先改业务 actor，再回头补 Foundation 契约。

## 10. 任务清单

## T1. 重构 Foundation proto 契约

### 目标

让公共 envelope route 直接表达三类互斥路由语义。

### 任务

1. 删除 `EventDirection.Observe`
2. 将 `EventDirection` 重构为 `BroadcastDirection`
3. 将 `EnvelopeRoute` 改为：
   - `publisher_actor_id`
   - `oneof route { broadcast | direct | observe }`
4. 将 `target_actor_id` 移入 `DirectRoute`

### 涉及位置

1. `src/Aevatar.Foundation.Abstractions/agent_messages.proto`
2. `test/Aevatar.Foundation.Abstractions.Tests/*`

### 验收

1. route 语义强类型化
2. 非法组合在 proto 层不可表达

## T2. 收窄 Publish / SendTo 语义

### 目标

让 `PublishAsync` 和 `SendToAsync` 分别构造正确 route 变体。

### 任务

1. `PublishAsync(...)` 只接受 `BroadcastDirection`
2. `SendToAsync(...)` 只构造 `DirectRoute`
3. committed fact publish 只构造 `ObserveRoute`

### 涉及位置

1. `src/Aevatar.Foundation.Abstractions/IEventPublisher.cs`
2. `src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorPublisher.cs`
3. `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansGrainEventPublisher.cs`
4. `src/Aevatar.Foundation.Core/GAgentBase.TState.cs`

### 验收

1. direct send 不再编码成 `Self + target`
2. committed observation 不再编码成特殊 direction 值

## T3. 收敛 Runtime 路由处理

### 目标

让 runtime 按 route 变体直接分派，而不是按 direction 特判。

### 任务

1. `EventRouter` 只处理 `BroadcastRoute`
2. `RuntimeActorGrain` 对 `DirectRoute` 直接投 inbox
3. `RuntimeActorGrain` 对 `ObserveRoute` 明确忽略
4. 清理 Local / Orleans runtime 中依赖 `EventDirection.Observe` 的分支

### 涉及位置

1. `src/Aevatar.Foundation.Runtime/Routing/EventRouter.cs`
2. `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`
3. `src/Aevatar.Foundation.Runtime.Implementations.Local/*`
4. `src/Aevatar.Foundation.Runtime.Implementations.Orleans/*`

### 验收

1. runtime 不再依赖特殊 direction 值判断消息种类
2. 分支按 route 变体直接表达

## T4. 迁移 workflow / scripting / query-reply 使用点

### 目标

让上层能力全部落到新 route 语义上。

### 任务

1. workflow 模块继续用 `PublishAsync(..., BroadcastDirection.Self)` 与 `SendToAsync(...)`
2. scripting runtime / evolution / definition query 改到新 route 模型
3. query-reply 场景继续用 `SendToAsync(replyStreamId, ...)`，但底层 route 变为 `DirectRoute`

### 涉及位置

1. `src/workflow/*`
2. `src/Aevatar.Scripting.*`

### 验收

1. workflow 自驱动与跨 actor 发消息语义保持不变
2. scripting query / reply / manager mirror 语义保持不变

## T5. 测试、回归与文档收口

### 目标

确保这次不是“名字换了，编码方式还混着”。

### 任务

1. 更新 proto coverage tests
2. 更新 Local / Orleans publisher tests
3. 更新 `RuntimeActorGrain` inbox / observe 行为测试
4. 更新 workflow / scripting 使用点测试
5. 更新 `docs/FOUNDATION.md`、`docs/SCRIPTING_ARCHITECTURE.md`、必要 guard

### 验收

1. Local / Orleans parity 继续通过
2. 3-node 与 mixed-version smoke 继续通过
3. committed event observation 主链不回退

## 11. 实施结果（2026-03-12）

### 已完成落点

1. `agent_messages.proto` 已删除 `EventDirection`，改为 `BroadcastDirection + EnvelopeRoute.oneof { broadcast | direct | observe }`。
2. `IEventPublisher` / `IEventContext` 已切到 `PublishAsync(..., BroadcastDirection)`，并补齐 `PublishCommittedAsync(...)`。
3. `EnvelopeRouteSemantics` 已作为统一工厂与判定入口，集中构造 `BroadcastRoute / DirectRoute / ObserveRoute`。
4. `GAgentBase<TState>.PersistDomainEventsAsync(...)` 已在 commit 后统一调用 `PublishCommittedAsync(...)`，committed domain event 不再伪装成 broadcast/self message。
5. `LocalActorPublisher`、`OrleansGrainEventPublisher`、`LocalActor`、`RuntimeActorGrain` 已全部切到 route 变体分支：
   - `BroadcastRoute` 走 topology / inbox
   - `DirectRoute` 走 target inbox
   - `ObserveRoute` 对 actor inbox 忽略，但对 stream observer 可见
6. workflow / scripting / query-reply 工厂与样例已经迁移到新语义：
   - `PublishAsync(..., BroadcastDirection.Self)` 表达 actor 内 continuation
   - `SendToAsync(targetActorId, ...)` 构造 `DirectRoute`
   - 协议样例与集成测试只从 `ObserveRoute` 读取 committed completion
7. 第四阶段相关文档已同步：
   - `docs/FOUNDATION.md`
   - `docs/SCRIPTING_ARCHITECTURE.md`
   - `docs/WORKFLOW.md`
   - `src/Aevatar.Foundation.Abstractions/README.md`
   - `src/Aevatar.Foundation.Runtime.Implementations.Local/README.md`

### 验证结果

1. `dotnet build aevatar.slnx --nologo`：通过。
2. `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo`：`97/97` 通过。
3. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo`：`229/229` 通过。
4. `dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj --nologo --collect:"XPlat Code Coverage"`：`145/145` 通过。
5. `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --collect:"XPlat Code Coverage"`：`154/154` 通过，另有 `16` 个环境相关 skip。
6. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --collect:"XPlat Code Coverage"`：`164/164` 通过。
7. `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --collect:"XPlat Code Coverage"`：`141/141` 通过。
8. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --collect:"XPlat Code Coverage"`：`229/229` 通过。
9. `bash tools/ci/architecture_guards.sh`：通过。
10. `bash tools/ci/test_stability_guards.sh`：通过。
11. `bash tools/ci/distributed_mixed_version_smoke.sh`：通过。
12. `AEVATAR_TEST_ORLEANS_3NODE=1 dotnet test test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj --nologo --filter "FullyQualifiedName=Aevatar.Integration.Tests.ScriptAutonomousEvolutionOrleans3ClusterConsistencyTests.ComplexScriptFlow_ShouldRemainConsistentAcrossThreeOrleansSilos"`：`1/1` 通过。

## 12. 验收命令建议

至少执行：

1. `dotnet build aevatar.slnx --nologo`
2. `dotnet test test/Aevatar.Foundation.Abstractions.Tests/Aevatar.Foundation.Abstractions.Tests.csproj --nologo`
3. `dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj --nologo`
4. `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --filter "FullyQualifiedName~OrleansGrainEventPublisherTests|FullyQualifiedName~OrleansRuntimeActorStateStoreIntegrationTests|FullyQualifiedName~RuntimePersistenceAndRoutingCoverageTests"`
5. `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo`
6. `dotnet test test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj --nologo --filter "FullyQualifiedName=Aevatar.Integration.Tests.ScriptExternalEvolutionE2ETests.ExternalEvolutionFlow_ShouldPromoteRevisionThroughUnifiedManagerChain"`
7. `AEVATAR_TEST_ORLEANS_3NODE=1 dotnet test test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj --nologo --filter "FullyQualifiedName=Aevatar.Integration.Tests.ScriptAutonomousEvolutionOrleans3ClusterConsistencyTests.ComplexScriptFlow_ShouldRemainConsistentAcrossThreeOrleansSilos"`
8. `bash tools/ci/distributed_mixed_version_smoke.sh`
9. `bash tools/ci/architecture_guards.sh`
10. `bash tools/ci/test_stability_guards.sh`

## 13. 收束性结论

第四阶段的重点不是继续给 `EventDirection` 打补丁，而是让 `EnvelopeRoute` 成为真正强类型的统一路由模型。

最终主线应当是：

1. `BroadcastRoute`
2. `DirectRoute`
3. `ObserveRoute`

并且它们通过 `oneof route` 互斥表达。

这比：

1. `EventDirection` 单字段混轴
2. `kind + 一堆 only 字段`

都更符合最佳实践、设计模式与面向对象原则。
