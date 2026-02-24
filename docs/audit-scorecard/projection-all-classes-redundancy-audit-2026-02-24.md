# Projection 全量类架构审计与打分（冗余专项，2026-02-24）

- 审计日期：2026-02-24
- 审计范围：`src/Aevatar.CQRS.Projection.*`（不含 `obj/bin` 自动生成文件）
- 审计对象：75 个公开类型（按“类型”去重，`partial` 多文件实现按单类型计）
- 审计重点：冗余（重复抽象、职责重叠、实现不并行、无效层）

---

## 1. 审计方法

### 1.1 打分模型（10 分制）

- `40%` 冗余风险（重复层、重复语义、无效中间层）
- `25%` 边界清晰度（职责是否单一、是否跨层）
- `20%` 并行一致性（Document/Graph、抽象/实现是否对称）
- `15%` 可维护性（复杂度、可读性、变更成本）

### 1.2 判定规则

1. 同职责同层重复实现且无差异语义，直接扣分。
2. 为补丁式场景引入额外抽象（例如“可配置态”标记）按“必要但增心智”计中等扣分。
3. Provider 大类高耦合（查询构造、序列化、存储协议混合）按维护冗余计分。
4. 仅命名重复但语义清晰（如 DI `ServiceCollectionExtensions`）记低风险，不作为结构冗余。

---

## 2. 总体结论

- **总体分数：9.14 / 10（四舍五入：9.1）**
- 主干架构已稳定：`Store Abstractions -> Runtime Abstractions -> Runtime -> Providers`。
- Document/Graph 已是平行关系，`1 ReadModel -> N Stores` 已实装。
- 主要扣分点集中在 Provider 主类复杂度和少量“为容错引入的新抽象”。

---

## 3. 目标架构图（当前实现）

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
    RM["ReadModel"] --> DSP["ProjectionStoreDispatcher<TReadModel,TKey>"]
    DSP --> DB["ProjectionDocumentStoreBinding<TReadModel,TKey>"]
    DSP --> GB["ProjectionGraphStoreBinding<TReadModel,TKey>"]

    DB --> DS["IProjectionDocumentStore<TReadModel,TKey>"]
    GB --> GS["IProjectionGraphStore"]

    DS --> EP["Elasticsearch/InMemory"]
    GS --> NP["Neo4j/InMemory"]

    DSP --> Q["Get/List/Mutate (queryable 0..1)"]
```

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart TD
    A["Stores.Abstractions"] --> B["Runtime.Abstractions"]
    B --> C["Runtime"]
    A --> D["Providers.InMemory"]
    A --> E["Providers.Elasticsearch"]
    A --> F["Providers.Neo4j"]
    A --> G["StateMirror"]
```

---

## 4. 冗余问题清单（按严重度）

### 4.1 Medium

1. Provider 主类复杂度仍高（维护冗余）。
- 证据：
  - `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionDocumentStore.cs`（400 行）
  - `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionDocumentStore.Helpers.cs`（320 行）
  - `src/Aevatar.CQRS.Projection.Providers.Neo4j/Stores/Neo4jProjectionGraphStore.cs`（382 行）
  - `src/Aevatar.CQRS.Projection.Providers.Neo4j/Stores/Neo4jProjectionGraphStore.Helpers.cs`（318 行）
- 影响：查询构造、序列化、存储协议聚合在同一类型内，演进成本偏高。

2. Runtime 为“无配置 binding 自动失活”引入额外能力抽象，降低了显式性但增加心智负担。
- 证据：
  - `src/Aevatar.CQRS.Projection.Runtime.Abstractions/Abstractions/Stores/IProjectionStoreBindingAvailability.cs:3`
  - `src/Aevatar.CQRS.Projection.Runtime/Runtime/ProjectionStoreDispatcher.cs:207`
- 影响：抽象数量变多，排障时需要同时理解 Binding + Availability 双层语义。

### 4.2 Low

1. `ServiceCollectionExtensions` 在多个投影项目重复命名。
- 证据：
  - `src/Aevatar.CQRS.Projection.Runtime/DependencyInjection/ServiceCollectionExtensions.cs:7`
  - `src/Aevatar.CQRS.Projection.Providers.*/*/ServiceCollectionExtensions.cs`
  - `src/Aevatar.CQRS.Projection.StateMirror/DependencyInjection/ServiceCollectionExtensions.cs:9`
- 影响：IDE 搜索时噪声增加，但不构成架构冗余。

2. `StateMirror` 与业务 mapper 并存，存在“映射策略双轨”潜在重叠。
- 证据：
  - `src/Aevatar.CQRS.Projection.StateMirror/Services/JsonStateMirrorProjection.cs:8`
  - `src/workflow/Aevatar.Workflow.Projection/ReadModels/WorkflowExecutionReadModelMapper.cs:5`（项目内另一路映射）
- 影响：不是错误，但应明确“自动镜像 vs 业务映射”适用边界。

### 4.3 High / Critical

- 未发现阻断级冗余（0 项）。

---

## 5. 分项目打分

| 项目 | 公开类型数 | 分数 | 结论 |
|---|---:|---:|---|
| `Aevatar.CQRS.Projection.Stores.Abstractions` | 11 | 9.23 | 抽象边界清晰，Document/Graph 平行关系明确。 |
| `Aevatar.CQRS.Projection.Runtime.Abstractions` | 9 | 9.18 | 契约稳定；`BindingAvailability` 引入轻微抽象成本。 |
| `Aevatar.CQRS.Projection.Runtime` | 6 | 8.93 | 主链路正确；Dispatcher/GraphBinding 复杂度中高。 |
| `Aevatar.CQRS.Projection.Providers.InMemory` | 3 | 9.03 | 对称且轻量，冗余低。 |
| `Aevatar.CQRS.Projection.Providers.Elasticsearch` | 4 | 8.95 | 语义完整，但主类偏重。 |
| `Aevatar.CQRS.Projection.Providers.Neo4j` | 3 | 8.77 | 语义完整，但主类偏重最明显。 |
| `Aevatar.CQRS.Projection.Core.Abstractions` | 21 | 9.30 | 契约细分充分，未见明显重复抽象。 |
| `Aevatar.CQRS.Projection.Core` | 14 | 9.04 | 编排链路完整，基类层次略深。 |
| `Aevatar.CQRS.Projection.StateMirror` | 4 | 9.10 | 轻量、可复用，需与业务 mapper 边界约束。 |

---

## 6. 全量逐类打分（75/75）

> 说明：以下为 `Aevatar.CQRS.Projection.*` 全部公开类型逐类评分（按类型去重）。

## src/Aevatar.CQRS.Projection.Core

| 类型 | 分数 | 冗余审计结论 | 证据 |
|---|---:|---|---|
| `class ActorProjectionOwnershipCoordinator : IProjectionOwnershipCoordinator` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Core/Orchestration/ActorProjectionOwnershipCoordinator.cs:11` |
| `class ActorStreamSubscriptionHub<TMessage> : IActorStreamSubscriptionHub<TMessage>, IAsyncDisposable` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Core/Streaming/ActorStreamSubscriptionHub.cs:10` |
| `class ProjectionAssemblyRegistration` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Core/DependencyInjection/ProjectionAssemblyRegistration.cs:10` |
| `class ProjectionCoordinator<TContext, TTopology> : IProjectionCoordinator<TContext, TTopology>` | 8.9 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionCoordinator.cs:6` |
| `class ProjectionDispatchAggregateException : Exception` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionDispatchAggregateException.cs:6` |
| `class ProjectionDispatcher<TContext, TTopology> : IProjectionDispatcher<TContext>` | 9.0 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionDispatcher.cs:6` |
| `class ProjectionLifecyclePortServiceBase<TLeaseContract, TRuntimeLease, TSink, TEvent>` | 8.8 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionLifecyclePortServiceBase.cs:6` |
| `class ProjectionLifecycleService<TContext, TCompletion>` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionLifecycleService.cs:6` |
| `class ProjectionOwnershipCoordinatorGAgent` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionOwnershipCoordinatorGAgent.cs:12` |
| `class ProjectionQueryPortServiceBase<TSnapshot, TTimelineItem, TGraphEdgeItem, TGraphSubgraph>` | 8.8 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionQueryPortServiceBase.cs:6` |
| `class ProjectionSessionEventHub<TEvent> : IProjectionSessionEventHub<TEvent>` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Core/Streaming/ProjectionSessionEventHub.cs:8` |
| `class ProjectionSubscriptionRegistry<TContext>` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionSubscriptionRegistry.cs:8` |
| `class SystemProjectionClock : IProjectionClock` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Core/Orchestration/SystemProjectionClock.cs:6` |
| `record ProjectionDispatchFailure` | 9.2 | 数据承载类型，结构简洁。 | `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionDispatchAggregateException.cs:30` |

## src/Aevatar.CQRS.Projection.Core.Abstractions

| 类型 | 分数 | 冗余审计结论 | 证据 |
|---|---:|---|---|
| `interface IActorStreamSubscriptionHub<TMessage>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Streaming/IActorStreamSubscriptionHub.cs:9` |
| `interface IActorStreamSubscriptionLease : IAsyncDisposable` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Streaming/IActorStreamSubscriptionLease.cs:6` |
| `interface IProjectionClock` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/IProjectionClock.cs:6` |
| `interface IProjectionContext` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/IProjectionContext.cs:6` |
| `interface IProjectionCoordinator<in TContext, in TTopology>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionCoordinator.cs:6` |
| `interface IProjectionDispatchFailureReporter<in TContext>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionDispatchFailureReporter.cs:6` |
| `interface IProjectionDispatcher<in TContext>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionDispatcher.cs:6` |
| `interface IProjectionEventApplier<in TReadModel, in TContext, in TEvent>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionEventApplier.cs:6` |
| `interface IProjectionEventReducer<in TReadModel, in TContext>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionEventReducer.cs:6` |
| `interface IProjectionLifecycleService<in TContext, in TCompletion>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionLifecycleService.cs:6` |
| `interface IProjectionOwnershipCoordinator` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionOwnershipCoordinator.cs:6` |
| `interface IProjectionPortActivationService<TLease>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionPortActivationService.cs:6` |
| `interface IProjectionPortLiveSinkForwarder<TLease, TSink, TEvent>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionPortLiveSinkForwarder.cs:6` |
| `interface IProjectionPortReleaseService<TLease>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionPortReleaseService.cs:6` |
| `interface IProjectionPortSinkSubscriptionManager<TLease, TSink, TEvent>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionPortSinkSubscriptionManager.cs:6` |
| `interface IProjectionProjector<in TContext, in TTopology>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionProjector.cs:6` |
| `interface IProjectionRuntimeOptions` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/IProjectionRuntimeOptions.cs:6` |
| `interface IProjectionSessionEventCodec<TEvent>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Streaming/IProjectionSessionEventCodec.cs:6` |
| `interface IProjectionSessionEventHub<TEvent>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Streaming/IProjectionSessionEventHub.cs:6` |
| `interface IProjectionStreamSubscriptionContext` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/IProjectionStreamSubscriptionContext.cs:6` |
| `interface IProjectionSubscriptionRegistry<TContext>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionSubscriptionRegistry.cs:6` |

## src/Aevatar.CQRS.Projection.Providers.Elasticsearch

| 类型 | 分数 | 冗余审计结论 | 证据 |
|---|---:|---|---|
| `class ElasticsearchProjectionDocumentStore<TReadModel, TKey>` | 8.4 | Provider 主类仍偏重，职责聚合较多。 | `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionDocumentStore.cs:11` |
| `class ElasticsearchProjectionDocumentStoreOptions` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Configuration/ElasticsearchProjectionDocumentStoreOptions.cs:3` |
| `class ServiceCollectionExtensions` | 8.9 | 命名重复但职责清晰，属常见 DI 约定。 | `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/DependencyInjection/ServiceCollectionExtensions.cs:8` |
| `enum ElasticsearchMissingIndexBehavior` | 9.4 | 枚举语义明确。 | `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Configuration/ElasticsearchMissingIndexBehavior.cs:3` |

## src/Aevatar.CQRS.Projection.Providers.InMemory

| 类型 | 分数 | 冗余审计结论 | 证据 |
|---|---:|---|---|
| `class InMemoryProjectionDocumentStore<TReadModel, TKey>` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Providers.InMemory/Stores/InMemoryProjectionDocumentStore.cs:7` |
| `class InMemoryProjectionGraphStore` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Providers.InMemory/Stores/InMemoryProjectionGraphStore.cs:5` |
| `class ServiceCollectionExtensions` | 8.9 | 命名重复但职责清晰，属常见 DI 约定。 | `src/Aevatar.CQRS.Projection.Providers.InMemory/DependencyInjection/ServiceCollectionExtensions.cs:7` |

## src/Aevatar.CQRS.Projection.Providers.Neo4j

| 类型 | 分数 | 冗余审计结论 | 证据 |
|---|---:|---|---|
| `class Neo4jProjectionGraphStore` | 8.3 | Provider 主类仍偏重，Cypher/序列化耦合较高。 | `src/Aevatar.CQRS.Projection.Providers.Neo4j/Stores/Neo4jProjectionGraphStore.cs:9` |
| `class Neo4jProjectionGraphStoreOptions` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Providers.Neo4j/Configuration/Neo4jProjectionGraphStoreOptions.cs:3` |
| `class ServiceCollectionExtensions` | 8.9 | 命名重复但职责清晰，属常见 DI 约定。 | `src/Aevatar.CQRS.Projection.Providers.Neo4j/DependencyInjection/ServiceCollectionExtensions.cs:8` |

## src/Aevatar.CQRS.Projection.Runtime

| 类型 | 分数 | 冗余审计结论 | 证据 |
|---|---:|---|---|
| `class LoggingProjectionStoreDispatchCompensator<TReadModel, TKey>` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Runtime/Runtime/LoggingProjectionStoreDispatchCompensator.cs:6` |
| `class ProjectionDocumentMetadataResolver : IProjectionDocumentMetadataResolver` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Runtime/Runtime/ProjectionDocumentMetadataResolver.cs:5` |
| `class ProjectionDocumentStoreBinding<TReadModel, TKey>` | 9.0 | 轻量桥接层，无显著冗余。 | `src/Aevatar.CQRS.Projection.Runtime/Runtime/ProjectionDocumentStoreBinding.cs:3` |
| `class ProjectionGraphStoreBinding<TReadModel, TKey>` | 8.7 | 包含 owner 差集清理与分页逻辑，复杂度中高。 | `src/Aevatar.CQRS.Projection.Runtime/Runtime/ProjectionGraphStoreBinding.cs:3` |
| `class ProjectionStoreDispatcher<TReadModel, TKey>` | 8.8 | 核心分发器，新增补偿/重试后复杂度上升。 | `src/Aevatar.CQRS.Projection.Runtime/Runtime/ProjectionStoreDispatcher.cs:6` |
| `class ServiceCollectionExtensions` | 8.9 | 命名重复但职责清晰，属常见 DI 约定。 | `src/Aevatar.CQRS.Projection.Runtime/DependencyInjection/ServiceCollectionExtensions.cs:7` |

## src/Aevatar.CQRS.Projection.Runtime.Abstractions

| 类型 | 分数 | 冗余审计结论 | 证据 |
|---|---:|---|---|
| `class ProjectionGraphManagedPropertyKeys` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Runtime.Abstractions/Abstractions/Graphs/ProjectionGraphManagedPropertyKeys.cs:3` |
| `class ProjectionStoreDispatchCompensationContext<TReadModel, TKey>` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Runtime.Abstractions/Abstractions/Stores/ProjectionStoreDispatchCompensationContext.cs:3` |
| `class ProjectionStoreDispatchOptions` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Runtime.Abstractions/Abstractions/Stores/ProjectionStoreDispatchOptions.cs:3` |
| `interface IProjectionDocumentMetadataResolver` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Runtime.Abstractions/Abstractions/ReadModels/IProjectionDocumentMetadataResolver.cs:3` |
| `interface IProjectionQueryableStoreBinding<TReadModel, in TKey>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Runtime.Abstractions/Abstractions/Stores/IProjectionQueryableStoreBinding.cs:3` |
| `interface IProjectionStoreBinding<in TReadModel, in TKey>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Runtime.Abstractions/Abstractions/Stores/IProjectionStoreBinding.cs:3` |
| `interface IProjectionStoreBindingAvailability` | 8.8 | 为无配置 binding 过滤提供能力，增加少量抽象心智。 | `src/Aevatar.CQRS.Projection.Runtime.Abstractions/Abstractions/Stores/IProjectionStoreBindingAvailability.cs:3` |
| `interface IProjectionStoreDispatchCompensator<TReadModel, TKey>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Runtime.Abstractions/Abstractions/Stores/IProjectionStoreDispatchCompensator.cs:3` |
| `interface IProjectionStoreDispatcher<TReadModel, in TKey>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Runtime.Abstractions/Abstractions/Stores/IProjectionStoreDispatcher.cs:3` |

## src/Aevatar.CQRS.Projection.StateMirror

| 类型 | 分数 | 冗余审计结论 | 证据 |
|---|---:|---|---|
| `class JsonStateMirrorProjection<TState, TReadModel>` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.StateMirror/Services/JsonStateMirrorProjection.cs:8` |
| `class ServiceCollectionExtensions` | 8.9 | 命名重复但职责清晰，属常见 DI 约定。 | `src/Aevatar.CQRS.Projection.StateMirror/DependencyInjection/ServiceCollectionExtensions.cs:9` |
| `class StateMirrorProjectionOptions` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.StateMirror/Configuration/StateMirrorProjectionOptions.cs:3` |
| `interface IStateMirrorProjection<TState, TReadModel>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.StateMirror/Abstractions/IStateMirrorProjection.cs:3` |

## src/Aevatar.CQRS.Projection.Stores.Abstractions

| 类型 | 分数 | 冗余审计结论 | 证据 |
|---|---:|---|---|
| `class ProjectionGraphEdge` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/Graphs/ProjectionGraphEdge.cs:3` |
| `class ProjectionGraphNode` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/Graphs/ProjectionGraphNode.cs:3` |
| `class ProjectionGraphQuery` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/Graphs/ProjectionGraphQuery.cs:3` |
| `class ProjectionGraphSubgraph` | 9.1 | 职责聚焦，冗余风险低。 | `src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/Graphs/ProjectionGraphSubgraph.cs:3` |
| `enum ProjectionGraphDirection` | 9.4 | 枚举语义明确。 | `src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/Graphs/ProjectionGraphDirection.cs:3` |
| `interface IGraphReadModel : IProjectionReadModel` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/ReadModels/IGraphReadModel.cs:3` |
| `interface IProjectionDocumentMetadataProvider<out TReadModel>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/ReadModels/IProjectionDocumentMetadataProvider.cs:3` |
| `interface IProjectionDocumentStore<TReadModel, in TKey>` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/ReadModels/IProjectionDocumentStore.cs:3` |
| `interface IProjectionGraphStore` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/Graphs/IProjectionGraphStore.cs:3` |
| `interface IProjectionReadModel` | 9.3 | 契约边界清晰，无直接实现冗余。 | `src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/ReadModels/IProjectionReadModel.cs:3` |
| `record DocumentIndexMetadata` | 9.2 | 数据承载类型，结构简洁。 | `src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/ReadModels/DocumentIndexMetadata.cs:3` |

---

## 7. 冗余治理建议（按优先级）

1. `P1`：继续拆分 Elasticsearch/Neo4j 主类（按 `QueryBuilder / Serializer / Transport` 维度下沉），把单类控制在 250~300 行内。
2. `P1`：给 `IProjectionStoreBindingAvailability` 增加统一观测日志字段（激活原因），降低运行时心智负担。
3. `P2`：在 `StateMirror` README 明确“自动镜像 vs 业务映射器”的边界，避免双轨混用。
4. `P2`：统一 DI 扩展命名前缀（可选），降低跨项目搜索噪声。

---

## 8. 审计结语

Projection 子系统当前没有阻断级冗余，主干已经稳定，问题主要转向“复杂度治理”而非“架构方向错误”。
现阶段应把重心放在 Provider 主类降复杂与运行时可观测性增强上。
