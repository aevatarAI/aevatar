# Aevatar.Foundation.Abstractions

`Aevatar.Foundation.Abstractions` 是 Aevatar 的契约层，定义 Agent 框架的公共语言。

## 职责

- 定义 Agent/Actor/Runtime 的核心接口
- 定义事件发布、流、模块与持久化接口
- 定义框架级 connector 契约（`IConnector` / `IConnectorRegistry`）
- 定义框架级凭证解析契约（`AuthContext` / `ICredentialProvider`）
- 提供跨项目共享的 Proto 消息
- 提供少量基础工具类型（如 `AgentId`、时间工具、属性标记）

本项目不包含运行时实现，不依赖 `Aevatar.Foundation.Core` 或 `Aevatar.Foundation.Runtime`。

## 主要内容

```
Aevatar.Foundation.Abstractions/
├── IAgent.cs
├── IActor.cs
├── IActorDispatchPort.cs
├── IActorRuntime.cs
├── IEventPublisher.cs
├── IStream.cs
├── IStreamProvider.cs
├── Attributes/
├── EventModules/
├── Connectors/
├── Credentials/
├── Context/
├── Propagation/
├── Persistence/
├── Hooks/
├── Helpers/
└── agent_messages.proto
```

## 核心接口

- `IAgent` / `IAgent<TState>`：生命周期、事件处理、订阅类型、强类型状态
- `IActor`：Agent 包装容器，父子关系与消息分发入口
- `IActorRuntime`：Actor 创建、销毁、查询、链接
- `IActorDispatchPort`：向目标 Actor 定向投递 `EventEnvelope`
- `IEventPublisher`：按 `PublicationRoute.topology / DirectRoute` 语义发布或点对点发送业务消息
- `IEnvelopePropagationPolicy` / `ICorrelationLinkPolicy`：基于 Raw `EventEnvelope` 的关联字段传播策略
- `IEventContext`：模块上下文的共性根接口
- `IEventModule<TContext>`：可插拔事件处理模块（含优先级）
- `IConnector` / `IConnectorRegistry`：命名 connector 调用契约与注册表
- `AuthContext` / `ICredentialProvider`：principal-aware 凭证引用与延迟解析契约
- `IStateStore<TState>` / `IEventStore`：状态与事件持久化契约

## Proto 说明

`agent_messages.proto` 定义 Foundation 公共消息，包括：

- `TopologyAudience`
- `EnvelopeRoute`（`oneof { direct | publication }`）
- `EventEnvelope`
- `StateEvent`
- `CommittedStateEventPublished`
- 层级变更事件（`ParentChangedEvent`、`ChildAddedEvent`、`ChildRemovedEvent`）

语义边界：

- `EventEnvelope` 是 runtime message envelope，是 Actor 之间通过 stream 传递的统一包络。
- `StateEvent` 是 Event Sourcing 的持久化事实记录。
- `CommittedStateEventPublished` 是 commit 成功后由 framework 内部发出的 observer publication payload。
- 两者都叫 “event”，但不在同一层：前者服务 transport/runtime，后者服务事实持久化与 replay。

## 依赖

- `Google.Protobuf`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Logging.Abstractions`

版本统一由仓库根目录 `Directory.Packages.props` 管理。

## 设计原则

1. 纯契约，避免实现耦合
2. 最小依赖，保持可复用
3. 接口稳定优先，变更需配套测试与文档
