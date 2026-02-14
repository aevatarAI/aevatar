# COGNITIVE 设计与实践

这份文档回答三个问题：

1. 为什么需要 `Aevatar.Workflows.Core` 和 `Event Modules`？
2. 它们在代码里是怎么实现的？
3. 实际开发时应该怎么用（附示例）？

---

## 一、先建立直觉：它解决了什么问题？

在普通 Agent 模式里，我们常见写法是：

- 收到一个事件
- 写一段固定代码处理
- 发布下一个事件

这种方式很直接，但有两个限制：

- **流程变更成本高**：改一次步骤顺序就要改代码
- **复用性差**：`if/while/并行/投票` 这些控制逻辑会重复出现在很多 Agent 里

`Aevatar.Workflows.Core` 的思路是：

- 把“流程控制能力”做成通用模块（Event Modules）
- 把“业务流程”写成 YAML（可配置）
- 让 `WorkflowGAgent` 在运行时装配模块并驱动流程

简单说：  
**硬编码 Agent 适合固定逻辑，Cognitive 适合可编排、可调整、可复用的流程逻辑。**

---

## 二、核心概念

### 1) WorkflowGAgent

`WorkflowGAgent` 是工作流入口 Agent（根 Actor）：

- 持有 `workflow yaml`（保存在 `State.WorkflowYaml`）
- 激活时解析并校验 workflow
- 动态创建子 `RoleGAgent`
- 安装认知模块（如 `workflow_loop`、`parallel_fanout`）
- 接收用户请求并触发执行

你可以把它理解成“工作流 orchestrator”。

### 2) Event Module

`Event Module` 是一类可插拔处理器（实现 `IEventModule`）：

- 有名字（`Name`）
- 有优先级（`Priority`）
- 能判断自己是否处理某事件（`CanHandle`）
- 真正处理事件（`HandleAsync`）

模块和静态 `[EventHandler]` 一起进入统一 pipeline。  
这意味着你可以在不改业务代码的情况下替换流程行为。

### 3) WorkflowModuleFactory

`WorkflowModuleFactory` 负责“按名字创建模块实例”，例如：

- `workflow_loop`
- `conditional`
- `while`
- `parallel_fanout`
- `vote_consensus`
- `llm_call`
- `tool_call`
- `connector_call`
- `transform`
- `retrieve_facts`

YAML 里写模块名，运行时通过工厂拿到实现对象。

---

## 三、运行链路（从请求到结果）

下面用一条常见链路说明执行过程：

1. 用户调用 API（例如 `/api/chat`）
2. API 把请求转成 `ChatRequestEvent`
3. `WorkflowGAgent` 收到事件
4. `WorkflowGAgent` 确认 workflow 已编译，必要时创建子 Agent 树
5. `WorkflowGAgent` 发布 `StartWorkflowEvent`
6. 各认知模块在 pipeline 中接力处理（循环、分支、并行、调用 LLM/工具/connector 等）
7. 完成后发布 `WorkflowCompletedEvent`
8. 投影层把事件转换为前端可读事件（例如 AG-UI SSE）

这个流程的关键点是：  
**流程控制由模块完成，不再写死在单个 Agent 的方法里。**

### 这条链路和 CQRS 的关系

- 当前默认读侧是“在线事件投影”：`EventEnvelope` -> AG-UI 事件流（SSE）。
- 它本质是 **事件读模型**，不是直接投影 Agent `State`。
- 若需要业务查询（列表、统计、检索），建议在此链路旁路增加自定义 projector，把事件投影到独立 read-only model（数据库或缓存）并暴露 Query API。

---

## 四、为什么要用 Event Modules（而不是全写 EventHandler）？

### 优点

- **可配置**：流程从代码移到 YAML
- **可复用**：控制原语模块可跨项目复用
- **可演进**：新增能力时，多数场景只需新增模块，不改核心 Agent
- **便于治理**：模块可以统一做日志、预算、容错策略

### 代价

- 调试复杂度上升（因为执行链变长）
- 需要规范模块命名和版本管理
- 对初学者来说，需要先理解事件驱动思维

建议实践：  
先从“硬编码 + 少量模块”开始，逐步模块化，不要一次性全重构。

---

## 五、示例：从简单到复杂

## 示例 1：最小可运行 workflow（顺序执行）

```yaml
name: "simple_flow"
roles:
  - id: "researcher"
    name: "Researcher"
    system_prompt: "You gather facts."
steps:
  - id: "s1"
    type: "llm_call"
    role: "researcher"
```

这个流程只做一件事：调用一次 LLM。

---

## 示例 2：带分支（conditional）

```yaml
name: "conditional_flow"
steps:
  - id: "classify"
    type: "llm_call"
  - id: "route"
    type: "conditional"
    when: "${vars.intent == 'finance'}"
    then:
      - id: "finance_path"
        type: "tool_call"
    else:
      - id: "general_path"
        type: "llm_call"
```

适合“先判断，再走不同路径”的场景。

---

## 示例 3：并行 + 投票（fanout + consensus）

```yaml
name: "consensus_flow"
steps:
  - id: "parallel_reasoning"
    type: "parallel_fanout"
    workers: ["role_a", "role_b", "role_c"]
  - id: "vote"
    type: "vote_consensus"
```

适合多 Agent 讨论后合并结论。

---

## 六、代码定位（你该从哪看起）

建议阅读顺序：

1. `src/Aevatar.Workflows.Core/WorkflowGAgent.cs`  
   看入口、编译、模块装配、子 Agent 创建
2. `src/Aevatar.Workflows.Core/WorkflowModuleFactory.cs`  
   看“模块名 -> 模块实现”映射
3. `src/Aevatar.Workflows.Core/Modules/*`  
   看每个原语的具体行为
4. `src/Aevatar.Foundation.Core/GAgentBase.cs` 与 `EventPipelineBuilder`  
   看模块如何进入统一 pipeline

---

## Connector 扩展（框架内置）

`connector_call` 把外部能力（MCP / HTTP / CLI）收敛到统一契约：

- 契约层：`src/Aevatar.Foundation.Abstractions/Connectors/IConnector.cs`
- 默认注册表：`src/Aevatar.Workflows.Core/Connectors/InMemoryConnectorRegistry.cs`
- 执行模块：`src/Aevatar.Workflows.Core/Modules/ConnectorCallModule.cs`
- 配置模型：`src/Aevatar.Configuration/AevatarConnectorConfig.cs`（读取 `~/.aevatar/connectors.json`）

推荐约定：

- YAML 只写 `connector` 名称和调用意图，不放密钥。
- 连接细节与安全策略放配置（allowlist、timeout、retry）。
- 每次调用把 `connector.*` 字段写入 `StepCompletedEvent.Metadata`，便于回放与审计。

---

## AI Routing 和 Cognitive 的关系

这里容易混淆两种“路由”：

- Runtime Routing（Actor 层级路由）：`Up/Down/Both/Self`
- AI Routing（模块级路由）：`event_routes` 决定事件进哪个模块

`Aevatar.AI` 的 AI Routing 发生在 `RoleGAgentFactory`：

1. 解析 `extensions.event_routes`
2. 用 `RoutedEventModule` 包装目标模块
3. 只有匹配规则的事件才会流入该模块

这套机制对 Cognitive 很有用：  
你可以让同一个 RoleGAgent 挂多个模块，但通过路由只在对应步骤触发对应模块。

示例：

```yaml
extensions:
  event_modules: "llm_handler,tool_handler"
  event_routes: |
    - when: event.step_type == "llm_call"
      to: llm_handler
    - when: event.step_type == "tool_call"
      to: tool_handler
```

---

## 七、开发建议（给新同学）

- 先写一个只有 1~2 个步骤的 workflow，确保链路通
- 每引入一个新模块，都单独做用例验证
- 对关键模块加结构化日志（stepId、runId、duration）
- 不要在模块里藏太多隐式状态，状态尽量显式放在 workflow vars 或事件里
- 优先保持模块“单一职责”，避免一个模块做太多事

---

## 八、常见问题

### Q1：我什么时候该用 Cognitive？

当你需要以下任一能力时，优先考虑：

- 流程可配置
- 复杂分支和循环
- 并行协作与结果汇总
- 业务团队希望不改代码就能调整流程

### Q2：所有 Agent 都要改成 WorkflowGAgent 吗？

不需要。  
固定流程、简单任务型 Agent，用普通 `GAgentBase` 往往更直接。

### Q3：模块失败会怎样？

取决于模块内部实现和 pipeline 策略。  
建议在模块里明确错误语义（重试、降级、终止），并通过事件把失败信息显式抛出。

---

## 九、总结

`Aevatar.Workflows.Core` 的核心价值不是“更复杂”，而是“把复杂流程结构化”。

- `WorkflowGAgent` 负责编排入口
- `Event Modules` 负责流程原语执行
- YAML 负责流程描述

这三层分开后，系统会更容易扩展、复用和维护。
