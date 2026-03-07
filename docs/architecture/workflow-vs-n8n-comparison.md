# Aevatar Workflow 与 n8n 对比说明

更新时间：2026-02-24  
适用范围：用于架构评审、技术选型、对外说明当前 `Aevatar` workflow 能力边界。

## 1. 结论先行

`Aevatar` 当前 workflow 是面向 AI 多 Agent 运行时的一致性执行内核，强调 Actor 语义、事件驱动和 CQRS 统一投影。  
n8n 是面向业务系统集成和低代码自动化编排的平台，强调节点生态、触发器和可视化交付效率。

二者不是“谁替代谁”的关系，而是优化目标不同：

- `Aevatar Workflow`：优先保证 AI 执行链路语义一致、可追踪、可投影、可扩展。
- `n8n`：优先保证集成效率、接入广度、运维可用性和可视化编排效率。

## 2. 对比维度总表

| 维度 | Aevatar Workflow（当前项目） | n8n |
| --- | --- | --- |
| 产品定位 | AI 多 Agent 执行内核 | 通用自动化与系统集成平台 |
| 编排方式 | YAML DSL + 运行时模块装配 | 可视化节点图（Node/Trigger） |
| 运行主语义 | definition actor 负责 binding，accepted run 由独立 `WorkflowRunGAgent` 接收 `ChatRequestEvent` 并通过事件流推进 | 由 Trigger 触发 workflow 执行，按节点依赖推进 |
| 实体边界 | 一个 workflow definition 对应一个 actor；每次 accepted run 对应一个独立 run actor | workflow 与执行实例为平台对象，不是 actor 绑定模型 |
| 状态与一致性 | 强调 `Command -> Event -> Projection -> ReadModel`，统一事实源 | 强调执行记录与任务运行管理 |
| 读侧与实时输出 | CQRS 读模型与 AGUI 输出复用同一投影管线 | 以平台执行日志/历史/节点结果为主 |
| 扩展机制 | `IWorkflowModulePack`、模块依赖推导、DI 组合 | 自定义节点与社区节点扩展 |
| 扩缩容模型 | 面向 Actor/分布式状态架构演进 | 官方支持队列模式（main + Redis + workers） |
| 典型优势 | AI 语义一致性、运行态可观测、架构可治理 | 集成效率高、上手快、连接器生态成熟 |

## 3. Aevatar 当前实现要点（与对比直接相关）

### 3.1 运行语义

- 一次 run 的入口语义是创建并绑定 `WorkflowRunGAgent`，随后由 run actor 接收 `ChatRequestEvent`，执行与输出均由事件流驱动。
- workflow 与 actor 为绑定关系；已绑定 actor 不允许切换到另一个 workflow。
- 带 `actorId` 的请求只能在原 actor 上继续运行，不可借同一 actor 跨 workflow。

### 3.2 统一投影链路

- CQRS ReadModel 与 AGUI 实时输出共享同一投影输入管线。
- 统一入口，一对多分发，不使用双轨实现。
- 运行时输出通过 `workflow-run:{actorId}:{commandId}` 会话事件流隔离。

### 3.3 扩展模型

- workflow 步骤能力通过模块化机制扩展，不把流程逻辑硬编码在单一 Agent 方法内。
- 支持 `parallel`、`vote`、`connector_call`、`workflow_call` 等步骤类型。
- 新增能力可通过模块包和 DI 扩展，不需要修改执行主干。

## 4. 适用场景建议

### 4.1 优先选择 Aevatar Workflow 的场景

- 需要 AI 多角色协作编排，且要求执行语义可追踪、可回放、可投影。
- 需要在同一事件主链路上同时支撑 ReadModel 查询和实时推送。
- 需要围绕分层治理、依赖反转和架构门禁长期演进。

### 4.2 优先选择 n8n 的场景

- 目标是快速打通 SaaS/API/数据库集成流程。
- 团队更依赖可视化节点编排，而不是 Actor 与事件模型。
- 对 AI 运行时语义一致性要求低于“集成效率与上线速度”。

### 4.3 组合使用建议

- 可将 n8n 作为外围系统集成编排层。
- 将 Aevatar workflow 作为 AI 决策与执行内核层。
- 两者通过 API/Webhook/消息总线解耦，避免职责重叠。

## 5. 概念映射（落地迁移参考）

| n8n 概念 | Aevatar 对应概念 | 迁移说明 |
| --- | --- | --- |
| Trigger Node（Webhook/Schedule） | Host API 入口（`/api/chat`、WS）或外部事件入口 | 在 Aevatar 中触发的是 run 命令，而非节点图起点 |
| Workflow Graph | YAML `steps` + `type` | 用步骤类型表达控制流与执行动作 |
| Node Type | Workflow Primitive Handler（如 `conditional`/`connector_call`）或 run-owned primitive（如 `parallel`/`workflow_call`） | 无状态原语走 `IWorkflowPrimitiveHandler`，有状态 run 语义落 `WorkflowRunState` |
| Execution Log | Projection ReadModel + Timeline | 读侧投影统一提供查询与审计视图 |
| Custom Node | 自定义 `IWorkflowPrimitiveHandler` / 模块包 | 扩展走同一主链路，不另起执行体系 |

## 6. 证据与参考

### 6.1 项目内证据

- `src/workflow/README.md`
- `docs/WORKFLOW.md`
- `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`
- `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs`
- `src/workflow/Aevatar.Workflow.Projection/README.md`
- `src/workflow/Aevatar.Workflow.Host.Api/README.md`

### 6.2 n8n 官方参考

- https://docs.n8n.io/workflows/executions/
- https://docs.n8n.io/hosting/scaling/queue-mode/
- https://docs.n8n.io/hosting/architecture/embed/
- https://docs.n8n.io/integrations/creating-nodes/overview/
- https://docs.n8n.io/integrations/builtin/core-nodes/n8n-nodes-base.executeworkflowtrigger/
