# Aevatar.Foundation.Core

`Aevatar.Foundation.Core` 提供 Agent 框架的核心实现，位于契约层与运行时之间。

## 职责

- 提供 `GAgentBase` 继承体系
- 统一静态处理器与动态模块的事件 Pipeline
- 提供 State 写保护、运行上下文与作用域传播
- 作为 Runtime 组装 Agent 行为的实现基础

## 核心类型

- `GAgentBase`：无状态基类，统一事件分发、模块管理、Hook 生命周期
- `GAgentBase<TState>`：状态型基类，内建 EventSourcing 生命周期（Replay 恢复 + 事件提交）
- `GAgentBase<TState, TConfig>`：配置型基类，配置持久化到 manifest
- `StateGuard`：限制状态写入时机
- `EventPipelineBuilder`：合并并排序静态/动态处理器
- `RunManager`：latest-wins 的运行上下文管理
- `AsyncLocalAgentContext`：上下文注入与提取
- `DefaultEnvelopePropagationPolicy` / `DefaultCorrelationLinkPolicy`：事件关联字段自动透传策略

## 典型流程

1. Runtime 创建 Agent 并注入依赖
2. Agent 激活时恢复模块、配置，并通过 EventStore Replay 恢复状态
3. 事件到达后以 Raw `EventEnvelope` 构建统一 Pipeline
4. 按优先级依次执行处理器，并触发 Hook
5. 出站事件按传播策略自动继承 `correlation_id` 并写入 `metadata["trace.causation_id"]`
6. Agent 停用时 flush pending events，并按策略持久化快照（可选）

## 依赖

- `Aevatar.Foundation.Abstractions`
- `Google.Protobuf`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Logging.Abstractions`
