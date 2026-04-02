# Claude Code 架构分析：aevatar 可借鉴的关键机制

> 基于 Claude Code 源码研究，提炼出除 Skills 机制和 Agent Loop 之外，对 aevatar 有实际价值的架构模式与设计决策。

---

## 1. 多层级 Context 压缩系统

### Claude Code 的做法

Claude Code 实现了分级上下文压缩策略，而非简单截断：

| 层级 | 机制 | 触发条件 |
|------|------|----------|
| Auto-Compact | 整段对话摘要压缩（走 LLM） | token 使用接近上限阈值 |
| Micro-Compact | 就地消息压缩（不走 LLM） | 轻量级快速降噪 |
| Session Memory Compact | 旧对话抽取归档为持久记忆 | 会话过长时保留关键事实 |
| Post-Compact Cleanup | 合并冗余 tool results、去重 | 压缩完成后的清理阶段 |

关键设计点：

- **Token Budget 实时追踪**：`cost-tracker` 持续监控 token 消耗，在接近阈值时主动触发压缩而非等到溢出。
- **Tombstone Message**：压缩后在历史中插入占位标记，保留上下文连续性语义。
- **压缩前后 Hook**：`pre_compact` / `post_compact` 允许外部逻辑在压缩过程中介入（如保护关键消息不被压缩）。

### aevatar 现状与差距

当前 `ChatHistory` 仅实现 FIFO 截断（`MaxMessages=100` 时 `RemoveRange(0, toRemove)`），存在以下问题：

- 长对话中早期关键上下文（如用户身份、任务目标）被无差别丢弃。
- 没有摘要机制，截断后模型丧失对话全貌。
- 没有 tool result 去重，重复的文件读取结果占用大量 token。

### 建议方向

1. 引入**分级压缩**：简单截断 → 摘要压缩 → 记忆抽取，按 token 压力递进触发。
2. 对 tool results 做**去重与引用化**：相同文件内容只保留一份，后续引用指向首次结果。
3. 在 `IAIGAgentExecutionHook` 中增加 `OnCompactStart/End` 钩子点。

---

## 2. 权限系统的中间件化与分层规则

### Claude Code 的做法

权限检查是完整 pipeline，而非单点判定：

```
validateInput → checkPermissions → preToolHooks → canUseTool(classifier+UI) → execute → postToolHooks
```

核心设计：

- **规则来自 5+ 来源，优先级明确**：
  ```
  Remote managed policies > MDM policies > CLI flags > User settings > Project settings > Defaults
  ```
- **Denial Tracking**：累积拒绝次数，超过阈值后触发 fallback 策略（如切换到更保守的模式）。
- **Classifier 自动审批**：对已知无害模式（如只读文件操作）自动放行，减少审批疲劳。
- **Tool 元数据驱动调度**：每个 tool 声明 `isReadOnly`、`isDestructive`、`isConcurrencySafe`，这些元数据直接影响并发策略和权限策略。

### aevatar 现状与差距

`ToolApprovalMode` 三级枚举（`NeverRequire / AlwaysRequire / Auto`）+ `IToolCallMiddleware` 方向正确，但缺少：

- 规则来源分层（当前只有 tool 自身声明和 connector allowlist）。
- Denial tracking 和 escalation 机制。
- 基于 tool 特征的自动分类审批。

### 建议方向

1. 为 `IAgentTool` 增加 `IsReadOnly`、`IsDestructive` 等元数据声明。
2. 引入分层权限规则合并逻辑，支持 agent 级、租户级、平台级规则覆盖。
3. 在 middleware 中增加 denial counter，连续拒绝时自动降级。

---

## 3. Streaming Tool Executor（流式并发工具执行器）

### Claude Code 的做法

不等所有 `tool_use` blocks 返回完毕才执行：

- **边解析边执行**：LLM 流式返回的 `tool_use` block 一完整就立即调度。
- **并发分区**：`isReadOnly` 工具并行执行，写操作串行排队。
- **Progress 实时反馈**：通过 `onProgress` callback 把执行进度推给 UI/调用方。
- **Fallback Recovery**：流式过程中失败时丢弃 pending tools 并优雅降级，不导致整轮崩溃。

执行模型示意：

```
LLM streaming output:
  ├─ tool_use[0] (ReadFile, readOnly=true)  ──→ 立即执行 ─┐
  ├─ tool_use[1] (ReadFile, readOnly=true)  ──→ 立即执行 ─┤ 并行
  ├─ tool_use[2] (Grep, readOnly=true)      ──→ 立即执行 ─┘
  └─ tool_use[3] (Edit, readOnly=false)     ──→ 等前面完成 → 串行执行
```

### aevatar 现状与差距

`ToolCallLoop` 是顺序逐个执行 tool calls，没有利用 tool 的并发安全性。当 LLM 一次返回多个 tool call 时，性能损失明显（尤其是多个独立查询类 tool）。

### 建议方向

1. 为 `IAgentTool` 增加 `IsConcurrencySafe` 声明。
2. `ToolCallLoop` 改为分区执行：concurrent-safe 的 tool 一批并行，其余串行。
3. 配合流式 LLM 响应，实现边解析边调度。

---

## 4. Tool Result 预算与磁盘 Offload

### Claude Code 的做法

对 tool 输出实施精细管控：

- **Per-tool 截断 + Offload**：单个结果超过 `maxResultSizeChars` 时持久化到磁盘（`/tmp/cc-*`），只在上下文中保留文件引用路径。
- **Aggregate Budget**：`applyToolResultBudget()` 控制整轮所有 tool results 的总 token 开销，超出时按优先级裁剪。
- **LRU File Cache**：`FileStateCache` 防止重复读取同一文件内容，命中缓存时直接返回已有结果。

### aevatar 现状与差距

`ToolTruncationHook` 硬截断 8000 字符，缺少：

- 磁盘 offload 机制（截断丢失的信息无法恢复）。
- 整轮 tool results 的总量预算（多个 tool 各返回 8000 字符可能已经超出 context 承受能力）。
- 文件读取去重缓存。

### 建议方向

1. 超长 tool result 落盘 + 上下文中保留摘要/引用，而非硬截断丢弃。
2. 引入轮次级 tool result token 预算，按 tool 优先级或时序裁剪。
3. 在 `ToolManager` 层增加 result 缓存，相同参数的 tool call 去重。

---

## 5. 完整的 Hook 覆盖点

### Claude Code vs aevatar Hook 对比

| Hook 点 | Claude Code | aevatar | 价值说明 |
|---------|-------------|---------|----------|
| pre/post_tool_use | 有 | 有（OnToolExecuteStart/End） | 基础能力，已对齐 |
| pre/post_llm_request | 有 | 有（OnLLMRequestStart/End） | 基础能力，已对齐 |
| **post_sampling** | 有 | **无** | LLM 输出后、tool 执行前的拦截点。可用于输出过滤、安全检查、结构化输出校验 |
| **pre/post_compact** | 有 | **无** | 上下文压缩前后介入。可保护关键消息、记录压缩日志 |
| **file_changed** | 有 | **无** | 文件系统变更触发。可用于自动刷新 tool source、热重载配置 |
| session_start/stop | 有 | 有（OnSessionStart/End） | 基础能力，已对齐 |
| **notification** | 有 | **无** | 通知类事件分发（如 task 完成、agent 空闲） |

### 建议方向

优先增加 `post_sampling` hook——这是 LLM 响应和 tool 执行之间的关键拦截点，可以实现：

- 输出安全过滤（敏感信息检测）。
- 结构化输出校验（JSON schema 合规检查）。
- Tool call 预审批（在执行前检查 tool call 参数合理性）。

---

## 6. 多来源配置合并体系

### Claude Code 的做法

设置从 5 个来源按优先级合并：

```
Remote managed settings (组织策略，最高优先级)
  ↓ override
MDM policies (macOS/Windows 设备管理)
  ↓ override
CLI flags (命令行参数)
  ↓ override
User settings (~/.claude/settings.json)
  ↓ override
Project settings (.claude/settings.json)
  ↓ override
Defaults (内置默认值)
```

配套机制：

- **Drop-in 目录**：`managed-settings.d/*.json` 按字母序合并，支持分片管理。
- **Zod Schema 校验**：每层配置加载时都做 schema 验证，无效配置报错而非静默忽略。
- **文件变更检测**：`settingsChangeDetector` 监听配置文件变化，自动 reload。
- **Feature Flags**：通过 GrowthBook 控制功能开关，支持 A/B 测试和灰度发布。

### aevatar 的场景

aevatar 作为多租户 AI Agent 平台，配置分层需求天然存在：

```
平台全局策略 > 租户策略 > Agent 定义 > 运行时参数 > 默认值
```

### 建议方向

1. 定义统一的 Agent 配置 schema（对应 `AIAgentConfig` 的扩展）。
2. 实现分层合并逻辑，明确各层覆盖规则。
3. 配置变更后通过 Actor 消息通知相关 Agent 热重载。

---

## 7. 启动性能优化模式

### Claude Code 的做法

启动序列高度优化：

- **并行 Prefetch**：MDM 读取 + Keychain 获取同时进行（节省 65ms+），不阻塞后续初始化。
- **Lazy Import**：feature-gated 的模块用 `require()` 延迟加载，未启用的功能不占启动时间。
- **Dead Code Elimination**：构建时通过 feature flags 移除不需要的代码路径，减小运行时体积。
- **Tool 条件注册**：按 feature flag / 用户类型 / 运行环境过滤 tool 列表，不加载不需要的 tool。

### aevatar 的适用场景

Agent 激活时（`OnActivateAsync`）需要：发现 tool sources、注册 tools、加载配置、建立 MCP 连接。这些步骤中有大量可并行化的 I/O 操作。

### 建议方向

1. `RegisterToolsFromSourcesAsync` 中多个 `IAgentToolSource` 的发现并行化（当前是 `foreach` 顺序执行）。
2. Tool source 发现结果引入 TTL 缓存，避免每次 Agent 激活都重新发现。
3. MCP server 连接池化复用，而非每个 Agent 独立建连。

---

## 8. 团队记忆与多 Agent 协调

### Claude Code 的做法

多 Agent 协作的关键模式：

- **Team Memory**：`~/.claude/teams/{name}/memory/` 共享记忆文件，团队成员可读写同一份上下文。
- **Mailbox System**：Agent 间消息队列，idle 时自动投递排队消息。
- **Permission Sync**：子 Agent 的权限请求上抛给 leader 审批，避免子 Agent 越权操作。
- **Isolated Context**：每个子 Agent 有独立的 `ToolUseContext`，防止状态互相污染。
- **Idle/Busy 状态追踪**：自动检测 Agent 空闲状态，支持 leader 动态分配任务。

### aevatar 的天然优势

aevatar 的 Actor 模型天然适配这些模式：

| Claude Code 概念 | aevatar Actor 映射 |
|---|---|
| Team Memory | Actor 持久态中的共享 state section |
| Mailbox | Actor inbox（已有） |
| Permission Sync | 父 Actor → 子 Actor 的 command/event 协议 |
| Isolated Context | Actor 单线程隔离（已有） |
| Idle tracking | Actor activation/deactivation lifecycle |

### 建议方向

1. 定义 Team Actor 协议：`TeamCreated` / `TaskAssigned` / `TaskCompleted` / `TeamDisbanded` 等事件。
2. Team Memory 建模为 Team Actor 的 readmodel，子 Agent 通过 projection 读取。
3. Permission escalation 建模为 command + reply/timeout continuation。

---

## 9. Speculative Execution（投机执行）

### Claude Code 的做法

在用户还没批准 tool 执行时，**提前运行权限 classifier 和安全检查**，结果缓存起来。用户同意后直接使用缓存结果，跳过检查延迟。

这种"乐观执行 + 缓存"模式在交互式场景下可以显著降低感知延迟。

### aevatar 的适用场景

- Tool 参数校验可以在等待审批时预执行。
- MCP tool 的 schema 校验可以提前完成。
- 对于需要外部 API 调用的 tool，可以预热连接。

---

## 优先级建议

按对 aevatar 的实际价值排序：

| 优先级 | 机制 | 理由 |
|--------|------|------|
| P0 | Context 压缩系统 | 当前 FIFO 截断是长对话场景最大短板 |
| P0 | Streaming Tool Executor + 并发策略 | 直接提升多 tool call 场景吞吐 |
| P1 | Tool Result 预算 + 磁盘 offload | 防止大输出撑爆上下文窗口 |
| P1 | post_sampling hook 点 | LLM 输出和 tool 执行间的关键拦截能力 |
| P2 | 多来源配置合并 | 为多租户/企业部署做准备 |
| P2 | 团队协调协议 | 利用 Actor 模型优势实现多 Agent 协作 |
| P3 | 启动性能优化 | Agent 激活路径的并行化和缓存 |
| P3 | Speculative Execution | 交互式场景的延迟优化 |
