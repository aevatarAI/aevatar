# Aevatar.Foundation.Core

`Aevatar.Foundation.Core` 提供 Agent 框架的核心实现，位于契约层与运行时之间。

## 职责

- 提供 `GAgentBase` 继承体系
- 统一静态处理器与动态模块的消息/事件 Pipeline
- 提供 State 写保护、运行上下文与作用域传播
- 作为 Runtime 组装 Agent 行为的实现基础

## 核心类型

- `GAgentBase`：无状态基类，统一事件分发、模块管理、Hook 生命周期
- `GAgentBase<TState>`：状态型基类，内建 EventSourcing 生命周期（Replay 恢复 + 事件提交）
- `GAgentBase<TState, TConfig>`：有效配置型基类（类默认值 + 事件/状态覆盖）
- `StateGuard`：限制状态写入时机
- `EventPipelineBuilder`：合并并排序静态/动态处理器
- `RunManager`：latest-wins 的运行上下文管理
- `AsyncLocalAgentContext`：上下文注入与提取
- `DefaultEnvelopePropagationPolicy` / `DefaultCorrelationLinkPolicy`：事件关联字段自动透传策略

## 典型流程

1. Runtime 创建 Agent 并注入依赖
2. Agent 激活时加载 Hook，并通过 EventStore Replay 恢复状态
3. 入站消息以 Raw `EventEnvelope` 进入统一 Pipeline
4. 按优先级依次执行处理器，并触发 Hook
5. 若业务需要形成事实，Actor 显式持久化领域事件到 EventStore
6. 出站 envelope 按传播策略自动继承 `EnvelopePropagation.CorrelationId` 并写入 `EnvelopePropagation.CausationEventId`
7. Agent 停用时 flush pending events，并按策略持久化快照（可选）

## 口径澄清

- `EventEnvelope` 在这里是 runtime message envelope，不要求 payload 一定是“已发生的领域事件”。
- `HandleEventAsync(EventEnvelope)` 本质上是 Actor mailbox dispatch 入口；名字沿用历史命名，但语义上更接近 `HandleMessageAsync(...)`。
- Event Sourcing 的事实层仍然是 `PersistDomainEventAsync(...)` 之后进入 EventStore 的领域事件。

## 依赖

- `Aevatar.Foundation.Abstractions`
- `Google.Protobuf`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Logging.Abstractions`
