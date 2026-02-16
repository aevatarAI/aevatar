# Aevatar.Demos.ContextDatabase

Context Database 示例程序，演示 `aevatar://` 虚拟文件系统与分层摘要/记忆提取的基础流程。

## 运行

```bash
# 1) Context Store CRUD
dotnet run --project demos/Aevatar.Demos.ContextDatabase store

# 2) 浏览 aevatar:// 目录树
dotnet run --project demos/Aevatar.Demos.ContextDatabase browse

# 3) 端到端流程（需要可用 LLM API Key）
dotnet run --project demos/Aevatar.Demos.ContextDatabase pipeline
```

## 命令说明

### `store`

演示 `IContextStore` 的基础能力：

- 写入资源文件到 `aevatar://resources/...`
- 写入用户记忆到 `aevatar://user/...`
- 读取、列举、存在性检查、glob 搜索

### `browse`

遍历并展示 5 个 scope：

- `skills`
- `resources`
- `user`
- `agent`
- `session`

### `pipeline`

演示最小可运行的上下文处理流程：

1. 写入示例资源文档
2. 调用 `SemanticProcessor.ProcessTreeAsync` 生成目录级 L0/L1
3. 调用 `LLMMemoryExtractor.ExtractAsync` 从对话提取记忆
4. 将提取出的记忆按分类写入 `aevatar://user/...` 与 `aevatar://agent/...`

说明：该 demo 当前不演示 `ContextInjectionMiddleware`、`MemoryDeduplicator`、`MemoryExtractionProjector` 的自动管线接入。

## LLM 配置

`pipeline` 命令会按以下顺序读取 API Key：

1. 环境变量：`DEEPSEEK_API_KEY` / `OPENAI_API_KEY` / `AEVATAR_LLM_API_KEY`
2. `~/.aevatar/secrets.json`（可通过 `aevatar-config` 配置）

未配置时 `pipeline` 会直接退出并提示配置。

## 物理目录

默认写入 `~/.aevatar/`：

```text
~/.aevatar/
├── resources/   <- aevatar://resources/
├── skills/      <- aevatar://skills/
├── users/       <- aevatar://user/{userId}/
├── agents/      <- aevatar://agent/{agentId}/
└── sessions/    <- aevatar://session/{runId}/
```
