# 工作流引擎设计与实践

> 状态更新（2026-03-08）
>
> 当前权威实现已经从“`WorkflowGAgent + WorkflowLoopModule`”演进为“`WorkflowGAgent + WorkflowRunGAgent + WorkflowExecutionKernel`”。
>
> - `WorkflowGAgent` 只承载 definition facts，不再直接推进 run。
> - `WorkflowRunGAgent` 是单次 run 的唯一写侧事实源。
> - `Foundation` 统一只保留 `IEventModule<TContext>`；workflow step 模块实现 `IEventModule<IWorkflowExecutionContext>`，并通过 `WorkflowExecutionBridgeModule` 接入 Foundation pipeline。
> - `IEventContext` 是共性根接口；`IEventHandlerContext` 与 `IWorkflowExecutionContext` 只在能力上分化。
> - `WorkflowExecutionKernel` 已替代 `WorkflowLoopModule` 负责主循环推进。
>
> 下文保留了大量 DSL 与原语说明；凡提到旧的 `WorkflowLoopModule`/`WorkflowGAgent` 执行职责，均以上述现状为准。

这份文档回答三个问题：

1. 为什么需要工作流引擎（`Aevatar.Workflow.Core` + `IEventModule<TContext>`）？
2. 代码里怎么实现的？
3. 实际开发时怎么用？

---

## 一、它解决了什么问题？

普通 Agent 模式下，收到事件 -> 写固定代码处理 -> 发布下一个事件。直接但有两个限制：

- **流程变更成本高**：改一次步骤顺序就要改代码
- **复用性差**：`if/while/并行/投票` 这些控制逻辑会重复出现在很多 Agent 里

工作流引擎的思路：

- 把流程控制能力做成通用模块（Event Modules）
- 把业务流程写成 YAML（可配置）
- 让 `WorkflowGAgent` 负责 definition 绑定，让 `WorkflowRunGAgent` 在运行时装配模块并驱动流程

一句话：**硬编码 Agent 适合固定逻辑，工作流适合可编排、可调整、可复用的流程逻辑。**

口径先说清楚：

- workflow 运行主链路建立在 `EventEnvelope` 消息流之上。
- `EventEnvelope` 在这里是 runtime message envelope，不等于 Event Sourcing 的领域事件记录。
- `WorkflowRunGAgent` / `WorkflowGAgent` 只有在显式 `PersistDomainEventAsync(...)` 时，才把领域事实写入 EventStore。

---

## 二、核心概念

### WorkflowGAgent / WorkflowRunGAgent

当前实现下，workflow 职责拆成两个 Actor：

1. `WorkflowGAgent`
   - 持有 workflow YAML（definition facts）
   - 解析 YAML、校验结构、维护版本与编译结果
   - 作为 definition/source actor 被解析与绑定
2. `WorkflowRunGAgent`
   - 一次 run 一个 actor
   - 按 `roles` 创建 run-scoped `RoleGAgent` 树
   - 通过依赖推导（`IWorkflowModuleDependencyExpander`）确定所需模块，经 `WorkflowModuleFactory` 创建并安装
   - 收到 `ChatRequestEvent` envelope 后发布 `StartWorkflowEvent`
   - 由 `WorkflowExecutionKernel` 推进 `StepRequestEvent -> StepCompletedEvent -> WorkflowCompletedEvent`

```
BindWorkflowDefinition(yaml)
  -> WorkflowParser.Parse (YAML -> WorkflowDefinition)
  -> WorkflowValidator.Validate (结构校验)
  -> BindWorkflowRunDefinition(yaml/run binding)
  -> InstallCognitiveModules on WorkflowRunGAgent:
       IWorkflowModuleDependencyExpander[]: 推导模块名集合
       WorkflowModuleFactory: 按名称创建实例
       IWorkflowModuleConfigurator[]: 配置实例
       WorkflowExecutionBridgeModule: 接入 Foundation 事件管线
```

### Event Module

可插拔的事件处理器（实现 `IEventModule<TContext>`），四个要素：

- `Name`：模块名（如 `"llm_call"`）
- `Priority`：数值越小优先级越高
- `CanHandle(envelope)`：判断是否处理该事件
- `HandleAsync(envelope, ctx, ct)`：处理逻辑

当前分层里：

- `Foundation` 管线使用 `IEventModule<IEventHandlerContext>`
- workflow step 模块使用 `IEventModule<IWorkflowExecutionContext>`
- 两者共享 `EventEnvelope` 与 `IEventContext` 根抽象

模块和静态 `[EventHandler]` 方法一起进入统一事件管线。可以在不改业务代码的情况下替换流程行为。

### WorkflowModuleFactory

按名称创建模块实例。DI 注册时每个模块有一个或多个名称：

```csharp
services.AddWorkflowModule<LLMCallModule>("llm_call");
services.AddWorkflowModule<ParallelFanOutModule>("parallel_fanout", "parallel", "fan_out");
```

YAML 里 `type: parallel` 会经工厂解析到 `ParallelFanOutModule`。

---

### Workflow Roles（正式 schema）

`workflow yaml` 里的 `roles` 现在是 `RoleGAgent` 的正式初始化入口，运行时会完整透传到 `InitializeRoleAgentEvent`：

```yaml
roles:
  - id: planner
    name: Planner
    system_prompt: "You are a planning assistant."
    provider: openai
    model: gpt-4o-mini
    temperature: 0.2
    max_tokens: 512
    max_tool_rounds: 4
    max_history_messages: 50
    stream_buffer_capacity: 128
    event_modules: "llm_handler,tool_handler"
    event_routes: |
      event.type == ChatRequestEvent -> llm_handler
    connectors: [incident_api, search_mcp]
    extensions:
      event_modules: "fallback_module"
      event_routes: "event.type == X -> fallback_module"
```

语义规则：

- `workflow roles` 与 `role yaml` 共用同一份解析归一化逻辑（`RoleConfigurationNormalizer`）。
- `event_modules` / `event_routes` 支持平铺写法和 `extensions.*` 写法，且**平铺字段优先级更高**。
- 未配置 `event_modules` 时，`RoleGAgent` 不会额外装配 event modules（保持旧行为）。

---

## 三、内置模块一览

| 类别 | YAML type | 模块 | 说明 |
|------|-----------|------|------|
| **引擎** | N/A | `WorkflowExecutionKernel` | 按步骤顺序派发，收到完成事件后推进下一步或结束 |
| **执行** | `llm_call` | `LLMCallModule` | 向目标 RoleGAgent 发 `ChatRequestEvent`，等回复转 `StepCompletedEvent` |
| | `tool_call` | `ToolCallModule` | 调用已注册的 Agent 工具（MCP/Skills） |
| | `connector_call` | `ConnectorCallModule` | 按名称调用配置好的 HTTP/CLI/MCP connector |
| **并行** | `parallel` | `ParallelFanOutModule` | 拆 N 个子步骤并行发给不同 role，收齐后合并，可选触发投票 |
| **共识** | `vote` | `VoteConsensusModule` | 对多个候选结果做共识选择 |
| **迭代** | `foreach` | `ForEachModule` | 按分隔符拆分输入，逐项执行子步骤 |
| **流程** | `conditional` | `ConditionalModule` | 条件分支 |
| | `while` | `WhileModule` | 循环执行（别名 `loop`） |
| | `workflow_call` | `WorkflowCallModule` | 调用子工作流（别名 `sub_workflow`，支持 `lifecycle=singleton/transient/scope`） |
| | `dynamic_workflow` | `DynamicWorkflowModule` | 从 LLM 输出提取 YAML，动态重配后继续执行 |
| | `assign` | `AssignModule` | 变量赋值 |
| | `checkpoint` | `CheckpointModule` | 检查点 |
| **数据** | `transform` | `TransformModule` | 纯函数变换（count/take/join/split/distinct 等） |
| | `retrieve_facts` | `RetrieveFactsModule` | 按关键词检索事实片段 |

每个原语的作用、参数和 YAML sample，见 [WORKFLOW_PRIMITIVES.md](./WORKFLOW_PRIMITIVES.md)。
如果要把 `human_input` / `human_approval` / `wait_signal` 接到真实应用交互，优先参考该文档中的“实际应用集成模式”小节。

`workflow_call` 关联规则补充：

- invocation id 统一由共享工厂生成，格式为 `<parent_run_id>:workflow_call:<parent_step_id>:<guidN>`；
- `parent_step_id` 必须非空；缺失时直接失败，不再生成兜底 step token；
- `WorkflowCallModule` 与 `WorkflowGAgent` 共用同一规则，避免双点实现漂移；
- 子流程 run id 复用 invocation id，便于父子流程关联追踪。

### 从 Foundation Orchestration 迁移

`Aevatar.Foundation.Core/Orchestration` 已移除，原能力统一收敛到 workflow 模块：

| 原类 | 推荐替代 |
|------|------|
| `SequentialOrchestration` | 线性 `steps`（由 `WorkflowLoopModule` 推进） |
| `ConcurrentOrchestration` | `type: parallel`（`ParallelFanOutModule`） |
| `VoteOrchestration` | `parallel + vote`（`VoteConsensusModule`） |
| `HandoffOrchestration` | `type: conditional` / `type: switch` + 分支推进 |

最小迁移示例（并行 + 投票）：

```yaml
steps:
  - id: parallel_analysis
    type: parallel
    parameters:
      workers: "agent_a,agent_b,agent_c"
      vote_step_type: "vote"
```

---

## 四、运行链路（从请求到结果）

```
POST /api/chat { prompt, workflow?, workflowYaml?, agentId? }
  │
  ├── ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>.ExecuteAsync
  │     ├── WorkflowRunCommandTargetResolver: workflowYaml 优先；否则按 workflow 名查 registry；仅当 workflow/workflowYaml 同时为空时走默认 workflow（默认 direct，可配置为 auto）
  │     ├── WorkflowRunCommandTargetBinder: 建立 projection lease + live sink + accepted receipt
  │     └── DefaultCommandDispatchPipeline / ActorCommandTargetDispatcher: 将 `ChatRequestEvent` 包装为 `EventEnvelope`，由 `IActorDispatchPort` 投递到 run actor；目标 actor 的获取/创建仍由 `IActorRuntime` 负责
  │
  ├── WorkflowRunGAgent 收到 `ChatRequestEvent` envelope
  │     ├── EnsureAgentTreeAsync: 按 roles 创建子 RoleGAgent
  │     └── 发布 StartWorkflowEvent (BroadcastDirection.Self)
  │
  ├── WorkflowExecutionKernel 收到 StartWorkflowEvent
  │     └── 取第一个步骤，发布 StepRequestEvent
  │
  ├── 对应模块处理 StepRequestEvent
  │     ├── LLMCallModule: 转 ChatRequestEvent → SendTo RoleGAgent → 等 TextMessageEndEvent → StepCompletedEvent
  │     ├── ConnectorCallModule: 查 registry → 执行 connector → StepCompletedEvent
  │     ├── ParallelFanOutModule: 拆子步骤 → 收齐合并 → 可选投票 → StepCompletedEvent
  │     └── ...其他模块同理
  │
  ├── WorkflowExecutionKernel 收到 StepCompletedEvent
  │     ├── 有下一步 → 再发 StepRequestEvent（循环）
  │     └── 无下一步 → 发布 WorkflowCompletedEvent
  │
  ├── run actor envelope 流进入统一 Projection Pipeline（一对多分发）
  │     ├── WorkflowExecutionReadModelProjector: reducer 链更新 ReadModel
  │     └── WorkflowExecutionAGUIEventProjector: 映射 AGUI 事件 → run event sink
  │
  ├── DefaultEventOutputStream + IdentityEventFrameMapper: 从 sink 读事件 → 透传 WorkflowRunEventEnvelope → emitAsync
  └── SSE 流返回客户端
```

关键点：**流程控制由模块完成，不写死在单个 Agent 的方法里。**

### `/api/chat` 入参矩阵（推荐）

| 场景 | 推荐请求体 | 说明 |
|------|------------|------|
| 新建 Actor，按名称加载已注册 workflow | `{ "prompt": "...", "workflow": "direct" }` | `workflow` 按名称从 registry 查 YAML。 |
| 新建 Actor，`workflow/workflowYaml` 都不传 | `{ "prompt": "..." }` | 默认走 `direct`；如开启 `UseAutoAsDefaultWhenWorkflowUnspecified`，则默认走 `auto`。 |
| 复用已绑定 workflow 的 Actor | `{ "prompt": "...", "agentId": "actor-123" }` | 只传 `prompt + agentId` 即可，`workflow/workflowYaml` 可留空。 |
| 新建 Actor，直接提交 inline YAML | `{ "prompt": "...", "workflowYaml": "name: demo\\nroles: ...\\nsteps: ..." }` | 不依赖预存文件，服务端先解析 `workflowYaml`。 |
| 给指定 Actor 传 inline YAML | `{ "prompt": "...", "agentId": "actor-123", "workflowYaml": "..." }` | 仅允许“未绑定 actor 首次绑定”或“同名 workflow 更新”；不允许切换到其它 workflow 名。 |
| 同时传 `workflow` + `workflowYaml` | `{ "prompt": "...", "workflow": "demo", "workflowYaml": "name: demo\\n..." }` | 两者名称必须一致；不一致返回 `WORKFLOW_NAME_MISMATCH`（400）。 |

错误码要点：

- `INVALID_WORKFLOW_YAML`（400）：`workflowYaml` 解析/校验失败。
- `WORKFLOW_NAME_MISMATCH`（400）：`workflow` 与 `workflowYaml.name` 不一致。
- `WORKFLOW_BINDING_MISMATCH`（409）：目标 actor 已绑定其它 workflow。
- `AGENT_WORKFLOW_NOT_CONFIGURED`（409）：传了 `agentId`，但 actor 未绑定且未提供 `workflowYaml`。

异常回退语义：

- 应用层仅在“白名单 workflow + 白名单异常类型”命中时尝试一次 `direct` 回退执行。
- inline `workflowYaml` 与显式 `direct` 请求默认不触发自动回退；已进入回退阶段也不再二次回退（防循环）。

最小可用示例（复用已有 Actor）：

```json
{
  "prompt": "继续上一次分析，给我三条行动建议",
  "agentId": "actor-123"
}
```

### 和 CQRS 投影的关系

- 同一条 `EventEnvelope` 并行进入多个 projector：ReadModel 分支（查询用）和 AGUI 分支（实时输出用）
- 投影管线统一入口、一对多分发，不搞双轨实现
- ReadModel 是事件投影的结果，不是 Agent State 的直接镜像
- 需要列表/统计等读模型时，扩展 reducer/projector + read-only store，通过 Query API 暴露

---

## 五、模块装配机制

`WorkflowRunGAgent` 不硬编码“哪个 workflow 需要哪些模块”，而是通过组合策略自动推导：

### 1. 依赖推导（`IWorkflowModuleDependencyExpander`）

按 Order 排序，依次调用，累积出所需模块名集合：

| Expander | 逻辑 |
|----------|------|
| `WorkflowLoopModuleDependencyExpander` | 现已等价为“确保执行内核存在”；兼容命名仍保留在依赖推导层 |
| `WorkflowStepTypeModuleDependencyExpander` | 遍历 steps，按 `type` 加入对应模块 |
| `WorkflowImplicitModuleDependencyExpander` | 补齐隐式依赖（如 `parallel` 隐式需要 `llm_call`） |

### 2. 实例配置（`IWorkflowModuleConfigurator`）

模块创建后，由 configurator 做初始化：

| Configurator | 逻辑 |
|--------------|------|
| `WorkflowLoopModuleConfigurator` | 历史命名；当前配置目标是 `WorkflowExecutionKernel` 相关执行上下文 |

### 扩展方式

新增模块不改 `WorkflowRunGAgent`，只需：

```csharp
// 1. 实现 IEventModule<IWorkflowExecutionContext>
public sealed class MyStepModule : IEventModule<IWorkflowExecutionContext> { ... }

// 2. DI 注册
services.AddWorkflowModule<MyStepModule>("my_step", "my_alias");

// 3. （可选）如果需要自定义推导或配置，新增 expander/configurator
services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowModuleDependencyExpander, MyExpander>());
```

---

## 六、Connector 机制

`connector_call` 把外部能力（HTTP / CLI / MCP）收敛到统一契约：

| 组件 | 位置 |
|------|------|
| 契约 | `Aevatar.Foundation.Abstractions/Connectors/IConnector.cs` |
| 注册表 | `Aevatar.Workflow.Core/Connectors/InMemoryConnectorRegistry.cs` |
| 执行模块 | `Aevatar.Workflow.Core/Modules/ConnectorCallModule.cs` |
| 配置加载 | `Aevatar.Configuration/AevatarConnectorConfig.cs` → `~/.aevatar/connectors.json` |

### 安全策略

HTTP 和 CLI connector 都采用白名单：
- **HTTP**：`allowedMethods`、`allowedPaths`、`allowedInputKeys`
- **CLI**：`allowedOperations`、`allowedInputKeys`

### 角色级授权

YAML 中角色可声明 `connectors` 列表，`ConnectorCallModule` 执行时校验：步骤指定的 connector 名称必须在角色允许列表内。

```yaml
roles:
  - id: coordinator
    connectors: [my_api, my_mcp]  # 只允许调这两个

steps:
  - id: call_api
    type: connector_call
    role: coordinator
    parameters:
      connector: my_api           # 必须在 coordinator.connectors 内
```

### 容错参数

| 参数 | 说明 |
|------|------|
| `retry` | 失败重试次数（0-5） |
| `timeout_ms` | 超时（100-300000ms） |
| `on_missing` | connector 不存在时：`fail`（默认）/ `skip` |
| `on_error` | 执行失败时：`fail`（默认）/ `continue` |
| `optional` | `true` 等价于 `on_missing: skip` |

---

## 七、示例

### 示例 1：最简单的工作流（单步 LLM 调用）

```yaml
name: simple_qa
roles:
  - id: assistant
    name: Assistant
    system_prompt: "You are a helpful assistant."
steps:
  - id: answer
    type: llm_call
    role: assistant
```

一个角色、一个步骤。用户输入直接发给 assistant 角色的 LLM，回复即为工作流输出。

### 示例 2：顺序多步

```yaml
name: research_then_summarize
roles:
  - id: researcher
    name: Researcher
    system_prompt: "You gather and organize information."
  - id: writer
    name: Writer
    system_prompt: "You write clear, concise summaries."
steps:
  - id: research
    type: llm_call
    role: researcher
  - id: summarize
    type: llm_call
    role: writer
```

先让 researcher 调研，输出传给 writer 做总结。

### 示例 3：并行 + 投票

```yaml
name: multi_perspective
roles:
  - id: analyst_a
    name: Analyst A
    system_prompt: "You analyze from a technical perspective."
  - id: analyst_b
    name: Analyst B
    system_prompt: "You analyze from a business perspective."
  - id: analyst_c
    name: Analyst C
    system_prompt: "You analyze from a user experience perspective."
steps:
  - id: parallel_analysis
    type: parallel
    parameters:
      workers: "analyst_a,analyst_b,analyst_c"
      vote_step_type: "vote"
```

三个分析师并行工作，结果经投票选出最佳。

### 示例 4：LLM + Connector 调外部 API

```yaml
name: analyze_and_post
roles:
  - id: coordinator
    name: Coordinator
    system_prompt: "You coordinate analysis tasks."
    connectors: [my_api]
steps:
  - id: analyze
    type: llm_call
    role: coordinator
  - id: post_result
    type: connector_call
    role: coordinator
    parameters:
      connector: my_api
      timeout_ms: "10000"
```

先用 LLM 分析，再把结果发到外部 API。

### 示例 5：循环 + 条件

```yaml
name: iterative_refinement
roles:
  - id: writer
    name: Writer
    system_prompt: "You write and refine content. When satisfied, include DONE in your response."
steps:
  - id: draft
    type: llm_call
    role: writer
  - id: refine_loop
    type: while
    parameters:
      max_iterations: "5"
    children:
      - id: refine
        type: llm_call
        role: writer
```

写初稿后循环打磨，直到回复中包含 "DONE" 或达到最大迭代次数。

---

## 八、代码定位（阅读顺序建议）

| 顺序 | 文件 | 看什么 |
|------|------|--------|
| 1 | `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs` | 入口、YAML 编译、模块装配、子 Agent 创建 |
| 2 | `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs` | 引擎主循环：步骤派发与推进 |
| 3 | `src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs` | LLM 调用：请求/响应关联、点对点发送 |
| 4 | `src/workflow/Aevatar.Workflow.Core/Modules/ParallelFanOutModule.cs` | 并行：扇出/收集/合并/投票 |
| 5 | `src/workflow/Aevatar.Workflow.Core/Modules/ConnectorCallModule.cs` | Connector：安全校验、重试、容错 |
| 6 | `src/workflow/Aevatar.Workflow.Core/Composition/` | 模块装配策略：expander + configurator |
| 7 | `src/Aevatar.CQRS.Core/Interactions/DefaultCommandInteractionService.cs` | 通用交互编排：dispatch → stream → finalize |
| 8 | `src/workflow/Aevatar.Workflow.Projection/` | 投影管线：reducer → ReadModel、AGUI 输出 |
| 9 | `src/Aevatar.Foundation.Core/GAgentBase.cs` | 模块如何进入统一事件管线 |

各项目的详细结构见 [src/workflow/README.md](../src/workflow/README.md)。

---

## 九、优缺点

### 优点

- **可配置**：流程从代码移到 YAML，业务人员可调整
- **可复用**：控制原语模块跨项目复用
- **可演进**：新增能力多数只需新增模块，不改 WorkflowGAgent
- **可治理**：模块统一做日志、容错、元数据记录

### 代价

- 调试链路变长（事件驱动 + 模块分发）
- 需要理解事件驱动思维
- 模块间通过事件通信，隐式依赖需要文档说明

建议：先从 1-2 个步骤的 workflow 开始，确保链路通了再逐步增加复杂度。

---

## 十、开发建议

- 每引入一个新模块，单独做用例验证
- 对关键模块加结构化日志（stepId、runId、duration）
- 不要在模块里藏隐式状态，状态尽量显式放在 workflow vars 或事件里
- 模块保持单一职责：一个模块处理一种 step type
- YAML 只写 connector 名称和调用意图，连接细节与安全策略放配置
- 每次 connector 调用的运行注解会写入 `StepCompletedEvent.Annotations`，便于回放与审计

---

## 十一、FAQ

### Q1：什么时候该用工作流？

当你需要以下任一能力时：

- 流程可配置（不改代码调整步骤）
- 复杂分支和循环
- 多 Agent 并行协作与结果汇总
- 业务团队希望通过 YAML 调整流程

### Q2：所有 Agent 都要改成 WorkflowGAgent 吗？

不需要。固定流程、简单任务型 Agent，用普通 `GAgentBase` + `[EventHandler]` 更直接。

### Q3：模块失败会怎样？

取决于模块实现和步骤配置。`WorkflowLoopModule` 收到 `Success=false` 的 `StepCompletedEvent` 后会直接发布 `WorkflowCompletedEvent(Success=false)`，终止整个 workflow。`ConnectorCallModule` 支持 `on_error: continue` 降级策略。

### Q4：怎么新增一种步骤类型？

三步：

1. 实现 `IEventModule`（`CanHandle` 过滤 `StepRequestEvent.StepType`，`HandleAsync` 执行逻辑，完成后发布 `StepCompletedEvent`）
2. DI 注册：`services.AddWorkflowModule<MyModule>("my_type")`
3. YAML 里写 `type: my_type`

### Q5：怎么替换投影存储？

默认是内存存储（`InMemoryWorkflowExecutionReadModelStore`）。替换为持久化实现：

```csharp
services.AddWorkflowExecutionProjectionReadModelStore<MyPersistentStore>();
```
