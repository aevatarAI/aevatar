# Aevatar.Context.Extraction

Context 分层摘要生成模块。负责调用 LLM 生成 L0/L1，并由 `SemanticProcessor` 以目录树方式执行处理。

## 职责

- `IContextLayerGenerator`：L0/L1 生成抽象
- `LLMContextLayerGenerator`：默认 LLM 实现
- `SemanticProcessor`：自底向上遍历目录并写回摘要文件

## 分层语义

| 层级 | 文件 | 典型用途 |
|---|---|---|
| L0 | `.abstract.md` | 快速过滤、检索摘要 |
| L1 | `.overview.md` | 结构化导航、Rerank 参考 |
| L2 | 原始文件 | 深度阅读 |

## 生成流程（当前实现）

```text
ProcessTreeAsync(root)
  -> 递归处理子目录
  -> 对叶文件调用 GenerateAbstractAsync
  -> 聚合子摘要后调用 GenerateDirectoryLayersAsync
  -> 写入目录级 .abstract.md / .overview.md
```

默认参数（`LLMContextLayerGenerator`）：

| 接口 | 参数 | 当前值 |
|---|---|---|
| `GenerateAbstractAsync` | `MaxTokens` | `150` |
| `GenerateAbstractAsync` | 内容截断 | `8000` 字符 |
| `GenerateOverviewAsync` | `MaxTokens` | `2500` |
| `GenerateOverviewAsync` | 内容截断 | `12000` 字符 |
| `GenerateDirectoryLayersAsync` | L0 `MaxTokens` | `150` |
| `GenerateDirectoryLayersAsync` | L1 `MaxTokens` | `2500` |

## `SemanticProcessor` 使用方式

1. 同步模式：`ProcessTreeAsync(root)`（立即处理）
2. 异步模式：`Start()` + `EnqueueAsync(uri)`（后台队列）

```csharp
// 同步
await processor.ProcessTreeAsync(AevatarUri.Parse("aevatar://resources/project/"));

// 异步
processor.Start();
await processor.EnqueueAsync(AevatarUri.Parse("aevatar://skills/"));
```

## 已知限制

- 文件级处理当前只显式调用 `GenerateAbstractAsync`，不直接生成文件级 `.overview.md`。
- 文件处理阶段写入的是父目录 `.abstract.md`，最终目录摘要由聚合流程覆盖为目录级内容。
- 队列模式使用单消费者，适合串行背景处理，不是高吞吐并行管线。

## DI 注册

```csharp
services.AddContextExtraction();
```

依赖：`ILLMProviderFactory` 与 `IContextStore` 已注册。

## 依赖

- `Aevatar.Context.Abstractions`
- `Aevatar.AI.Abstractions`
