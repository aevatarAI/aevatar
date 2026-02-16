# Aevatar.Context.Retrieval

语义检索模块，提供简单检索（`FindAsync`）和复杂检索（`SearchAsync`）两条路径。

## 职责

- `IContextVectorIndex`：向量索引抽象
- `LocalVectorIndex`：本地内存暴力检索实现
- `IntentAnalyzer`：LLM 意图分析，生成 `TypedQuery`
- `HierarchicalRetriever`：实现 `IContextRetriever`
- `ContextInjectionMiddleware`：LLM 调用前自动注入检索上下文（由 Bootstrap 注册）

## 检索路径

### `FindAsync`

```text
query -> embedding -> vectorIndex.SearchAsync(topK=10) -> 按 ContextType 分组
```

特点：

- 不走意图分析
- 可通过 `targetScope` 限定检索范围

### `SearchAsync`

```text
query + session
  -> IntentAnalyzer.AnalyzeAsync
  -> 0..5 条 TypedQuery
  -> 每条 TypedQuery:
       1) 全局检索 topK=3
       2) 优先队列下钻 SearchChildrenAsync(topK=5)
       3) 分数传播: 0.5 * child + 0.5 * parent
       4) 连续 3 轮 topScore 变化 < 0.001 视为收敛
  -> 合并去重并截取前 10
```

说明：`ContextType.Memory` 的根范围为 `null`，表示跨 user/agent 记忆一起检索。

## 默认参数

| 组件 | 参数 | 当前值 |
|---|---|---|
| `IntentAnalyzer` | `MaxTokens` | `500` |
| `IntentAnalyzer` | `Temperature` | `0.0` |
| `IntentAnalyzer` | 最近消息窗口 | `5` |
| `IntentAnalyzer` | 最大查询数 | `5` |
| `HierarchicalRetriever` | `GlobalSearchTopK` | `3` |
| `HierarchicalRetriever` | `SearchChildrenTopK` | `5` |
| `HierarchicalRetriever` | `FinalTake` | `10` |
| `HierarchicalRetriever` | `ScorePropagationAlpha` | `0.5` |
| `HierarchicalRetriever` | `MaxConvergenceRounds` | `3` |
| `HierarchicalRetriever` | `ConvergenceThreshold` | `0.001` |

## Context 注入中间件

`ContextInjectionMiddleware` 当前行为：

- 从消息中提取最后一条 `user` 输入
- 调用 `IContextRetriever.FindAsync`
- 将检索结果格式化为 `system` 消息注入
- 预算基于字符估算：`3000 * 4` 字符
- 失败时降级为“不注入但继续”

## 向量索引

`IContextVectorIndex` 条目字段：

| 字段 | 说明 |
|---|---|
| `Uri` | 条目 URI |
| `ParentUri` | 父目录 URI |
| `ContextType` | `Resource` / `Memory` / `Skill` |
| `IsLeaf` | 是否叶节点 |
| `Vector` | 向量数据 |
| `Abstract` | L0 摘要 |
| `Name` | 条目名称 |

`LocalVectorIndex` 使用余弦相似度（`TensorPrimitives.CosineSimilarity`）。

## 已知限制

- 当前仓库中暂无自动索引构建流水线，检索前需确保索引已写入。
- `LocalVectorIndex` 适合开发和小规模场景，不适合生产高并发检索。
- `DeleteByPrefixAsync` 基于键遍历删除，建议在高并发场景下替换为外部向量库。

## DI 注册

```csharp
services.AddContextRetrieval();
```

说明：`ContextInjectionMiddleware` 不在 `AddContextRetrieval()` 内注册，而是在 `Aevatar.Bootstrap` 中随 `EnableContextDatabase` 一并注册。

## 依赖

- `Aevatar.Context.Abstractions`
- `Aevatar.AI.Abstractions`
- `Microsoft.Extensions.AI`
