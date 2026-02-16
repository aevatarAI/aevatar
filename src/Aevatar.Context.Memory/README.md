# Aevatar.Context.Memory

记忆自演进模块。负责从对话中提取结构化记忆，去重后写入 Context Store，并可通过 Projection Pipeline 挂接到 Workflow。

## 职责

- `IMemoryExtractor` / `LLMMemoryExtractor`：从消息中提取候选记忆
- `MemoryDeduplicator`：基于向量相似度做 `Create` / `Update` / `Skip`
- `MemoryWriter`：将去重结果落库
- `MemoryExtractionProjector<TContext, TTopology>`：投影管线接入点（Order=200）

## 6 类记忆

| 分类 | 归属 | 说明 | 可合并 |
|---|---|---|---|
| `Profile` | user | 用户身份属性 | 是 |
| `Preferences` | user | 用户偏好 | 是 |
| `Entities` | user | 人物/项目/技术实体 | 是 |
| `Events` | user | 事件与决策 | 否 |
| `Cases` | agent | 问题与解决方案案例 | 否 |
| `Patterns` | agent | 可复用模式与经验 | 是 |

## 存储路径

```text
aevatar://user/{userId}/memories/
├── preferences/
├── entities/
└── events/

aevatar://agent/{agentId}/memories/
├── cases/
└── patterns/
```

## 提取与去重流程（当前实现）

```text
messages -> LLMMemoryExtractor -> CandidateMemory[]
         -> embedding -> vector search topK=3
         -> decision -> MemoryWriter
```

决策规则：

| 条件 | 决策 |
|---|---|
| 无匹配或最佳相似度 `< 0.85` | `Create` |
| 相似度 `>= 0.85` 且分类可合并 | `Update` |
| 相似度 `> 0.95` 且分类不可合并 | `Skip` |
| 相似度 `>= 0.85` 且分类不可合并但未达 skip | `Create` |

说明：`DeduplicationDecision.Merge` 枚举存在，但 `MemoryDeduplicator` 当前不会产出 `Merge` 决策。

## 默认参数

| 组件 | 参数 | 当前值 |
|---|---|---|
| `LLMMemoryExtractor` | `MaxTokens` | `2000` |
| `LLMMemoryExtractor` | `Temperature` | `0.0` |
| `LLMMemoryExtractor` | 对话截断长度 | `10000` 字符 |
| `MemoryDeduplicator` | `SimilarityThreshold` | `0.85` |
| `MemoryDeduplicator` | `SkipThreshold` | `0.95` |
| `MemoryDeduplicator` | `SearchTopK` | `3` |
| `MemoryWriter` | 文件名模式 | `yyyyMMdd-HHmmss-{slug}.md` |
| `MemoryExtractionProjector` | `Order` | `200` |

## Projection Pipeline 集成

`MemoryExtractionProjector<TContext, TTopology>` 实现 `IProjectionProjector<TContext, TTopology>`：

```text
InitializeAsync -> 清空累积消息
ProjectAsync    -> 累积可提取文本
CompleteAsync   -> Extract -> Deduplicate -> Write
```

当前实现约束：

- `CompleteAsync` 中 `userId` / `agentId` 仍使用硬编码 `"default"`。
- 事件文本提取采用 payload 字节的 UTF-8 解码兜底策略。

## DI 注册

```csharp
// 注册记忆服务
services.AddContextMemory();

// 按具体 Workflow 上下文注册投影器
services.AddWorkflowExecutionProjectionProjector<
    MemoryExtractionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
```

## 已知限制

- `MemoryWriter` 的 merge 路径是读后写，非原子更新。
- 生产环境建议增加存储层并发保护和版本控制。

## 依赖

- `Aevatar.Context.Abstractions`
- `Aevatar.Context.Retrieval`
- `Aevatar.AI.Abstractions`
- `Aevatar.CQRS.Projection.Abstractions`
