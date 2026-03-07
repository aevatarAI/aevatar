# Runtime Phase-7 Thin Owner / Event Module Retirement 重构蓝图（Delivered, Historical Snapshot）

## 1. 文档元信息

1. 状态：`Delivered`
2. 版本：`v1`
3. 日期：`2026-03-07`
4. 决策级别：`Architecture Breaking Change`
5. 适用范围：
   - `src/workflow/Aevatar.Workflow.Core`
   - `src/workflow/Aevatar.Workflow.Application*`
   - `src/workflow/Aevatar.Workflow.Infrastructure*`
   - `src/workflow/Aevatar.Workflow.Host.Api`
   - `src/Aevatar.AI.Abstractions`
   - `src/Aevatar.AI.Core`
   - `src/Aevatar.Foundation.Abstractions/EventModules`
   - `src/Aevatar.Foundation.Core`
   - `demos/Aevatar.Demos.Workflow*`
   - `docs/WORKFLOW.md`
   - `src/workflow/Aevatar.Workflow.Core/README.md`
   - `src/Aevatar.AI.Core/README.md`
   - `src/Aevatar.Foundation.Abstractions/README.md`
   - `src/Aevatar.Foundation.Core/README.md`
   - `test/Aevatar.Workflow.*`
   - `test/Aevatar.AI.Tests`
   - `test/Aevatar.Foundation.Core.Tests`
   - `test/Aevatar.Integration.Tests`
6. 非范围：
   - mixed-version 升级验证链删除或弱化
   - Orleans durable callback reminder-only 主策略回退
   - CQRS projection 主协议重写
   - AI provider failover 产品语义调整
   - Workflow DSL 产品语法新增能力设计
7. 本版结论：
   - phase-7 记录的是 `2026-03-07` 当天的终局重构决策与问题快照。
   - 其中 `EventModules` 从 `Workflow -> Role -> Foundation` 主链中彻底删除，已经在后续交付中完成。
   - `WorkflowRunGAgent` 的持续薄化在后续 phase-8 之后继续推进；本文件保留原始问题快照，作为历史证据而非当前待办。

## 2. 最佳实践基线

这份蓝图采用下面 10 条硬约束，作为 phase-7 的设计基线：

1. 一个子系统只保留一套权威扩展模型，不能同时保留 `primitive` 和 `event module` 两套运行时扩展面。
2. Actor owner 只负责事实持有、状态转换、效果调度入口，不负责承载成片的业务分支实现。
3. Workflow DSL 只表达业务语义，不承载 Role/AI 内部事件管线配置。
4. Role/AI 的扩展必须走 AI 自己的 typed middleware / hook，不再借用 Foundation 的通用事件模块。
5. Foundation 基类只提供稳定通用机制，不为上层业务保留动态“第二调度器”。
6. 任何可恢复业务事实都必须有显式 state integration contract；没有 `TState/reducer/domain event/replay` 接口的扩展面不得承载跨事件运行态。
7. 命名必须与语义一致；如果类型实现的是 primitive executor，就不能继续叫 `Module`。
8. 传输协议字段只保留单一语义；删除 `event_modules/event_routes` 这类“字符串承载运行时策略”的字段。
9. Demo 不能继续展示已经判定为错误的架构模式。
10. 删除优于兼容；旧抽象一旦被替换，直接删除，不留空壳和桥接。
11. 架构治理必须前置为静态门禁和测试约束，而不是靠口头约定。

## 3. 原始问题快照（Historical, 2026-03-07）

### P1. `WorkflowRunGAgent` 仍然是偏大的单 run orchestrator

截至 `2026-03-07`，`WorkflowRunGAgent` 相关切片的实际行数大致为：

1. `WorkflowRunGAgent.cs`：`367`
2. `WorkflowRunGAgent.Infrastructure.cs`：`535`
3. `WorkflowRunGAgent.Callbacks.cs`：`392`
4. `WorkflowRunGAgent.StatefulCompletions.cs`：`335`
5. `WorkflowRunGAgent.Composition.cs`：`272`
6. `WorkflowRunGAgent.AI.cs`：`244`
7. `WorkflowRunGAgent.ControlFlow.cs`：`218`
8. `WorkflowRunGAgent.Dispatch.cs`：`218`
9. `WorkflowRunGAgent.Lifecycle.cs`：`186`
10. `WorkflowRunGAgent.ExternalInteractions.cs`：`119`
11. `WorkflowRunGAgent.HumanInteraction.cs`：`56`

总量约 `2942` 行。

问题本质：

1. shell 已经变薄，但 owner 仍直接掌握大量 family-specific helper 与 branch logic。
2. partial file 拆分更多是“文件拆开”，还不是“职责彻底拆开”。
3. `WorkflowRunGAgent` 仍然同时承担：
   - run ingress
   - callback reconcile
   - stateful primitive orchestration
   - role init envelope 组装
   - child actor tree helper
   - 变量求值与运行时工具方法

### P2. Workflow 主链已经不用 `IEventModule`，但命名仍在制造旧心智

当前 `src/workflow/Aevatar.Workflow.Core/Modules/*.cs` 中的类型，例如：

1. `ConnectorCallModule`
2. `DynamicWorkflowModule`
3. `ToolCallModule`
4. `AssignModule`

都已经实现 `IWorkflowPrimitiveHandler`，而不是 `IEventModule`。

问题本质：

1. 目录名仍叫 `Modules/`，类型名仍叫 `*Module`，会持续误导团队把它理解成旧 event-module 体系的一部分。
2. `WorkflowCoreModulePack`、`WorkflowModuleRegistration`、`IWorkflowModulePack` 这些名字也在延续旧语义。
3. 当前代码实际上已经是 “primitive executor registry”，但命名还停在“module pack”。

### P3. Workflow DSL 仍携带 `EventModules/EventRoutes`

当前仍有这些真实代码路径：

1. `WorkflowRawDefinition` 持有 `EventModules/EventRoutes`
2. `WorkflowDefinition` 持有 `EventModules/EventRoutes`
3. `WorkflowDefinitionNormalizer` 仍在归一化这两个字段
4. `WorkflowRunGAgent.Infrastructure.cs` 创建 `InitializeRoleAgentEvent` 时继续透传这两个字段

问题本质：

1. Workflow DSL 本应只描述 workflow/role 的稳定业务配置。
2. `event_modules/event_routes` 实际上是在把 Role 内部事件管线策略偷渡进 workflow DSL。
3. 这等于让 workflow 挂着第二套、非业务语义的扩展入口。

### P4. `RoleGAgent` 仍依赖 `IEventModuleFactory + RoutedEventModule`

当前真实接入链路：

1. `RoleGAgent.HandleInitializeRoleAgent()` 调 `RoleGAgentFactory.ApplyModuleExtensions(...)`
2. `RoleGAgentFactory` 根据字符串 `EventModules` 从 `IEventModuleFactory` 动态创建模块
3. `RoleGAgentFactory` 根据 `EventRoutes` 用 `RoutedEventModule` 做过滤包装
4. `RoleGAgent` 最终通过 `SetModules(...)` 把这些动态模块挂到 agent 上

问题本质：

1. AI/Role 层明明已经有 `IAgentRunMiddleware`、`ILLMCallMiddleware`、`IToolCallMiddleware`、`IAIGAgentExecutionHook` 这些 typed 扩展点。
2. 但当前仍绕回 Foundation 的 `IEventModule` 体系，形成重复扩展模型。
3. `event_routes` 这种字符串 DSL 也让 role 运行时行为变成了“配置字符串驱动的隐藏分支”。
4. 更关键的是，这条链路和 `RoleGAgentState` 之间没有显式 state integration contract，无法安全承载任何需要 replay/reactivation 收敛的运行事实。

### P5. Foundation 事件管线仍把静态 handler 和动态 module 混成统一调度器

当前真实实现：

1. `EventPipelineBuilder` 会把静态 handler 和动态 `IEventModule` 合并排序
2. `StaticHandlerAdapter` 自己也实现 `IEventModule`
3. `GAgentBase` 公开 `RegisterModule / SetModules / GetModules`
4. `EventHandlerAttribute` 注释仍写着“Handlers and IEventModule execute interleaved”

问题本质：

1. Foundation Base Class 仍把 “typed static event handler” 和 “dynamic generic event module” 当成同一类东西。
2. 这让所有上层都天然带着“我还可以再挂一条动态事件处理链”的认知。
3. 只要这层还在，`EventModules` 就会不断通过新的入口回流。
4. `IEventModule` / `IEventHandlerContext` 只提供 `CanHandle/HandleAsync/Publish/Send/Callback`，并不提供 `TState`、reducer、domain-event ownership 或 replay 协议，所以它天然不是 stateful business fact 的正规承载面。

### P6. Demo 和文档仍保留旧 event-module 叙事

当前残留包括：

1. `demos/Aevatar.Demos.Workflow.Web/Program.cs` 仍有 role event module 相关演示口径
2. `demos/Aevatar.Demos.Workflow/workflows/33_role_event_module_no_routes_template.yaml` 等示例仍存在
3. `src/Aevatar.AI.Core/README.md` 仍显式介绍 `EventRoutes`
4. `src/Aevatar.Foundation.Abstractions/README.md` 仍将 `IEventModule` 作为主扩展模型描述

问题本质：

1. 代码和文档如果继续同时保留旧叙事，未来就会把已经删除的架构又引回来。
2. Demo 的错误示范比代码残留更危险，因为它会变成新需求的默认复制模板。

## 4. 根因分析

这些问题的共同根因不是“某几个文件太长”，而是下面 5 个架构偏差没有彻底清掉：

1. `Workflow` 和 `Role` 之间的边界还不够硬。
   - workflow 还在传 role 内部事件管线配置。
2. `Role` 和 `Foundation` 之间的边界也不够硬。
   - role 扩展没有完全落到 AI 自己的 typed middleware/hook，而是继续借用通用 event module。
3. `EventModule` 从设计上就缺少和 `GAgent state` 的正规集成契约。
   - 没有 `TState`
   - 没有 reducer 入口
   - 没有 domain-event ownership
   - 没有 replay/reactivation contract
   - 一旦要和状态关联，就只会退化成私有易失字段或隐藏 cast 耦合
4. Foundation 还保留“静态 handler + 动态 module”双轨调度模型。
   - 只要这个能力还在，就会不断诱导上层把业务语义塞进动态模块。
5. 命名没有跟着语义一起完成清理。
   - `primitive` 已经成了新主链，但大量名字仍然暗示“module”。

## 5. 终局目标

phase-7 完成后，系统必须满足下面 8 条终局约束：

1. `WorkflowRunGAgent` 只保留 run owner、event ingress、state transition 调用、effect dispatch 触发，不再直接承载 primitive family 细节。
2. Workflow 只保留一套扩展模型：`primitive executor`。
3. `WorkflowDefinition`、`WorkflowRawDefinition`、`InitializeRoleAgentEvent`、`RoleConfigurationNormalizer` 中不再出现 `EventModules/EventRoutes`。
4. `RoleGAgent` 不再调用 `SetModules()`，也不再依赖 `IEventModuleFactory`、`RoutedEventModule`。
5. AI 扩展只允许走 typed middleware / hook：
   - `IAgentRunMiddleware`
   - `ILLMCallMiddleware`
   - `IToolCallMiddleware`
   - `IAIGAgentExecutionHook`
6. 任何需要跨事件收敛的运行事实，都只能进入 actor state + reducer + domain event 主链，不能再落到 `EventModule` 一类无状态契约里。
7. Foundation `GAgentBase` 不再支持动态 `IEventModule` 管线；事件处理只剩 typed static handlers。
8. Workflow Core 中不再保留 `*Module` 这一命名；统一改成 `*PrimitiveExecutor`。
9. Demo、README、守卫和测试都不再展示或默许 `event_modules/event_routes` 旧模型。

## 6. 目标架构

### 6.1 Workflow 终局模型

1. `WorkflowGAgent`
   - 只负责 definition/binding。
2. `WorkflowRunGAgent`
   - 只负责 run owner、event ingress、domain-event persistence、result commit/fail。
3. `WorkflowRunReducer`
   - 只负责 `WorkflowRunState` 转换。
4. `WorkflowPrimitiveExecutorRegistry`
   - 只负责 `step_type -> executor` 解析。
5. `WorkflowPrimitiveExecutionPlanner`
   - 只负责把 step request 变成 primitive execution plan。
6. `WorkflowRunEffectAssembler`
   - 只负责把 executor 结果变成 effect plan。
7. `WorkflowRunEffectDispatcher`
   - 只负责 send / publish / durable callback / child run / connector invoke。
8. `WorkflowAsyncOperationReconciler`
   - 只负责 async completion / timeout / retry backoff 的唯一对账入口。

### 6.2 Role / AI 终局模型

1. `RoleGAgent`
   - 只接收 typed role config。
   - 不再动态注册 event module。
2. `RoleGAgentFactory`
   - 只做 role config 规范化和 `InitializeRoleAgentEvent` 组装。
   - 不再负责模块创建和字符串 DSL 路由。
3. AI 扩展入口只剩：
   - `IAgentRunMiddleware`
   - `ILLMCallMiddleware`
   - `IToolCallMiddleware`
   - `IAIGAgentExecutionHook`
4. 任何 workflow-local 定制逻辑：
   - 进入 workflow primitive
   - 或进入 projection / host 组合
   - 不再放进 Role 事件模块

### 6.3 Foundation 终局模型

1. `GAgentBase`
   - 只保留静态 handler 调度与 execution hooks。
2. `StaticEventHandlerDispatcher`
   - 负责静态 handler 的排序与调用。
3. `EventHandlerContext`
   - 若仍需要，仅服务于静态 handler 执行，不再暴露为通用 `IEventModule` 上下文。
4. 删除：
   - `IEventModule`
   - `IEventModuleFactory`
   - `IEventHandlerContext`
   - `IRouteBypassModule`
   - `EventPipelineBuilder`
   - `StaticHandlerAdapter`

## 7. 明确的破坏性决策

1. 删除 `event_modules` / `event_routes` 协议字段，不提供兼容桥接。
2. 删除 `IEventModule` 整套基础设施，不提供 adapter。
3. 删除 role event module demo，不保留“legacy role event module”示例。
4. Workflow Core 命名全部改成 primitive 语义，不保留 `Module` 命名别名。
5. 若某个旧 event module 场景仍有业务价值，必须按下面三条之一重入：
   - workflow primitive
   - AI typed middleware/hook
   - projection / host 层能力
6. 不接受“先留着字符串字段，内部不用”的空兼容壳。

## 8. 详细重构设计

### 8.1 WP1: Workflow 命名与扩展面统一

#### 8.1.1 目标

把 Workflow 主链从“语义已变、命名未变”的状态，收敛到真正一致的 primitive executor 模型。

#### 8.1.2 设计

1. 重命名接口和注册抽象：
   - `IWorkflowPrimitiveHandler` -> `IWorkflowPrimitiveExecutor`
   - `IWorkflowModulePack` -> `IWorkflowPrimitivePack`
   - `WorkflowModuleRegistration` -> `WorkflowPrimitiveRegistration`
   - `WorkflowPrimitiveRegistry` -> `WorkflowPrimitiveExecutorRegistry`
2. 重命名目录：
   - `src/workflow/Aevatar.Workflow.Core/Modules` -> `PrimitiveExecutors`
3. 重命名实现类：
   - `ConnectorCallModule` -> `ConnectorCallPrimitiveExecutor`
   - `DynamicWorkflowModule` -> `DynamicWorkflowPrimitiveExecutor`
   - 其它 `*Module` 同理
4. `WorkflowCoreModulePack` -> `WorkflowCorePrimitivePack`

#### 8.1.3 主要影响文件

1. `src/workflow/Aevatar.Workflow.Core/IWorkflowPrimitiveHandler.cs`
2. `src/workflow/Aevatar.Workflow.Core/WorkflowPrimitiveRegistry.cs`
3. `src/workflow/Aevatar.Workflow.Core/WorkflowModuleRegistration.cs`
4. `src/workflow/Aevatar.Workflow.Core/WorkflowCoreModulePack.cs`
5. `src/workflow/Aevatar.Workflow.Core/Modules/*`
6. `src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/*`
7. `test/Aevatar.Workflow.*`
8. `test/Aevatar.Integration.Tests/*Workflow*`

#### 8.1.4 验收标准

1. Workflow Core 不再存在实现 primitive 的 `*Module` 类型。
2. Workflow Core 不再存在 `IWorkflowModulePack` / `WorkflowModuleRegistration` 名称。
3. 所有活跃文档只使用 `primitive executor` 口径。

### 8.2 WP2: `WorkflowRunGAgent` 最终去壳化

#### 8.2.1 目标

把 `WorkflowRunGAgent` 从“大 orchestrator”压成真正的 thin owner。

#### 8.2.2 设计

1. `WorkflowRunGAgent` 只保留：
   - `[EventHandler]` ingress
   - `TransitionState`
   - 调用 reducer / planner / reconciler / dispatcher
2. 把剩余 helper 继续外提为协作者：
   - `WorkflowRunVariableResolver`
   - `WorkflowRoleInitializationFactory`
   - `WorkflowChildActorCoordinator`
   - `WorkflowRunAsyncCompletionHandlers`
   - `WorkflowRunControlFlowHandlers`
   - `WorkflowRunAIHandlers`
   - `WorkflowRunCompositionHandlers`
3. owner 文件中不允许再出现 primitive-specific private method。
4. `Infrastructure.cs` 中剩余的 role init / actor tree / callback helper 全部收敛到 service 协作者。

#### 8.2.3 主要影响文件

1. `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs`
2. `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.Infrastructure.cs`
3. `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.Callbacks.cs`
4. `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.StatefulCompletions.cs`
5. `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.AI.cs`
6. `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.Composition.cs`
7. `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.ControlFlow.cs`
8. `src/workflow/Aevatar.Workflow.Core/WorkflowPrimitiveExecutionPlanner.cs`
9. `src/workflow/Aevatar.Workflow.Core/WorkflowAsyncOperationReconciler.cs`
10. `test/Aevatar.Workflow.Core.Tests/*`

#### 8.2.4 验收标准

1. `WorkflowRunGAgent.cs` 控制在 `250` 行以内。
2. `WorkflowRunGAgent.*` 总量控制在 `1800` 行以内。
3. 任一切片都不再同时包含：
   - actor owner ingress
   - primitive family business logic
   - low-level helper 工具函数

### 8.3 WP3: 从 Workflow DSL 删除 `EventModules/EventRoutes`

#### 8.3.1 目标

让 workflow DSL 只表达 workflow/role 的稳定业务语义，不再表达 Role 内部事件管线。

#### 8.3.2 设计

1. 删除 workflow 角色定义中的：
   - `event_modules`
   - `event_routes`
2. 删除对应内存模型字段：
   - `WorkflowRawDefinition`
   - `WorkflowDefinition`
3. 删除 normalizer 合并逻辑：
   - `WorkflowDefinitionNormalizer`
4. 删除 role init envelope 透传：
   - `WorkflowRunGAgent.Infrastructure.cs`
5. 更新 YAML 示例和说明文档：
   - role event module 示例全部删除

#### 8.3.3 主要影响文件

1. `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowRawDefinition.cs`
2. `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowDefinition.cs`
3. `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowDefinitionNormalizer.cs`
4. `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.Infrastructure.cs`
5. `docs/WORKFLOW.md`
6. `demos/Aevatar.Demos.Workflow/workflows/*`

#### 8.3.4 验收标准

1. Workflow DSL 中不再出现 `event_modules` / `event_routes`。
2. Workflow Core 解析和运行路径不再出现这两个字段。
3. 原有 role event module demo 全部删除或改写成 primitive 版本。

### 8.4 WP4: 从 AI/Role 删除 event-module 运行链

#### 8.4.1 目标

让 Role 扩展回到 AI 自己的 typed middleware/hook，不再借用 Foundation `IEventModule`。

#### 8.4.2 设计

1. 修改 `InitializeRoleAgentEvent`：
   - 删除 `event_modules`
   - 删除 `event_routes`
2. 修改 `RoleConfigurationInput / RoleConfigurationNormalized`：
   - 删除 `EventModules`
   - 删除 `EventRoutes`
3. 修改 `RoleGAgentFactory`：
   - 删除 `ApplyModuleExtensions(...)`
   - 删除字符串解析和路由包装逻辑
4. 修改 `RoleGAgent`：
   - `HandleInitializeRoleAgent()` 不再调用 `SetModules()`
5. 删除 AI 路由包装器：
   - `RoutedEventModule`
6. 删除基于 role event module 的测试与 demo
7. surviving use cases 显式迁移：
   - workflow-local 格式化/选择逻辑 -> workflow primitive
   - LLM/tool cross-cutting -> `IAgentRunMiddleware` / `ILLMCallMiddleware` / `IToolCallMiddleware`
   - tracing / observability -> `IAIGAgentExecutionHook`

#### 8.4.3 主要影响文件

1. `src/Aevatar.AI.Abstractions/ai_messages.proto`
2. `src/Aevatar.AI.Abstractions/Agents/RoleConfigurationNormalizer.cs`
3. `src/Aevatar.AI.Core/RoleGAgent.cs`
4. `src/Aevatar.AI.Core/RoleGAgentFactory.cs`
5. `src/Aevatar.AI.Core/Routing/RoutedEventModule.cs`
6. `src/Aevatar.AI.Core/README.md`
7. `test/Aevatar.AI.Tests/*`
8. `test/Aevatar.Integration.Tests/EventRoutingTests.cs`
9. `demos/Aevatar.Demos.Workflow.Web/*`

#### 8.4.4 验收标准

1. `RoleGAgent` 不再调用 `SetModules()`。
2. AI 主路径不再出现 `IEventModuleFactory`。
3. `RoleConfigurationNormalizer` 不再暴露 `EventModules/EventRoutes`。
4. AI README 不再介绍 role event module/event routes。

### 8.5 WP5: 从 Foundation 删除 `IEventModule` 抽象

#### 8.5.1 目标

让 Foundation 只保留 typed static event handler 和 execution hook，不再保留动态通用事件模块体系。

#### 8.5.2 设计

1. 删除抽象：
   - `IEventModule`
   - `IEventModuleFactory`
   - `IEventHandlerContext`
   - `IRouteBypassModule`
2. 删除调度桥：
   - `EventPipelineBuilder`
   - `StaticHandlerAdapter`
3. 重写 `GAgentBase.HandleEventAsync(...)`
   - 直接遍历静态 handler metadata
   - 按 priority 排序执行
   - 保持 hooks 行为不变
4. 删除 `GAgentBase` 中的动态 module API：
   - `RegisterModule`
   - `SetModules`
   - `GetModules`
5. 更新 `EventHandlerAttribute` 注释与 README

#### 8.5.3 主要影响文件

1. `src/Aevatar.Foundation.Abstractions/EventModules/IEventModule.cs`
2. `src/Aevatar.Foundation.Abstractions/EventModules/IEventModuleFactory.cs`
3. `src/Aevatar.Foundation.Abstractions/EventModules/IEventHandlerContext.cs`
4. `src/Aevatar.Foundation.Abstractions/Attributes/EventHandlerAttribute.cs`
5. `src/Aevatar.Foundation.Core/GAgentBase.cs`
6. `src/Aevatar.Foundation.Core/Pipeline/EventPipelineBuilder.cs`
7. `src/Aevatar.Foundation.Core/Pipeline/StaticHandlerAdapter.cs`
8. `src/Aevatar.Foundation.Core/Pipeline/EventHandlerContext.cs`
9. `src/Aevatar.Foundation.Abstractions/README.md`
10. `src/Aevatar.Foundation.Core/README.md`
11. `test/Aevatar.Foundation.Core.Tests/*`

#### 8.5.4 验收标准

1. Foundation `src/` 中不再存在 `EventModules/` 目录。
2. `GAgentBase` 不再暴露 module 管理 API。
3. `EventHandlerAttribute` 与 `README` 不再提 “IEventModule interleaved pipeline”。

### 8.6 WP6: Demo / Docs / Guards 全量收口

#### 8.6.1 目标

防止旧 event-module 架构在文档、示例和守卫层面回流。

#### 8.6.2 设计

1. 删除所有 role event module demo：
   - `33_role_event_module_no_routes_template.yaml`
   - 其它同类示例
2. 把仍有业务价值的 demo，改写成：
   - workflow primitive
   - prompt engineering
   - middleware/hook 示例
3. 新增静态门禁：
   - 禁止 `event_modules` / `event_routes`
   - 禁止 `IEventModule` / `IEventModuleFactory`
   - 禁止 `RegisterModule` / `SetModules`
   - 禁止 Workflow Core 出现实现 primitive 的 `*Module` 类型
4. 同步所有活跃文档与 README

#### 8.6.3 主要影响文件

1. `tools/ci/architecture_guards.sh`
2. `docs/WORKFLOW.md`
3. `src/workflow/Aevatar.Workflow.Core/README.md`
4. `src/Aevatar.AI.Core/README.md`
5. `src/Aevatar.Foundation.Abstractions/README.md`
6. `src/Aevatar.Foundation.Core/README.md`
7. `demos/Aevatar.Demos.Workflow*`

#### 8.6.4 验收标准

1. 活跃文档中不再出现 `event_modules` / `event_routes` / `IEventModule` 现行说明。
2. Demo 不再展示 role event module。
3. 守卫能阻止旧模型回流。

## 9. 旧模型到新模型的映射

| 旧概念 | 新概念 |
|---|---|
| `IWorkflowPrimitiveHandler` | `IWorkflowPrimitiveExecutor` |
| `IWorkflowModulePack` | `IWorkflowPrimitivePack` |
| `WorkflowModuleRegistration` | `WorkflowPrimitiveRegistration` |
| `WorkflowCoreModulePack` | `WorkflowCorePrimitivePack` |
| `ConnectorCallModule` | `ConnectorCallPrimitiveExecutor` |
| `DynamicWorkflowModule` | `DynamicWorkflowPrimitiveExecutor` |
| `event_modules` | 删除，无替代字符串字段 |
| `event_routes` | 删除，无替代字符串字段 |
| `IEventModuleFactory` | 删除 |
| `RoutedEventModule` | 删除 |
| `GAgentBase.SetModules()` | 删除 |

## 10. 实施顺序

1. `WP1`：先完成 Workflow 命名统一，防止后续设计继续建立在错误命名上。
2. `WP2`：再把 `WorkflowRunGAgent` 继续压薄到 thin owner。
3. `WP3`：删除 workflow DSL 中的 `EventModules/EventRoutes`。
4. `WP4`：删除 AI/Role 的 event-module 运行链。
5. `WP5`：删除 Foundation 的通用 event-module 抽象。
6. `WP6`：最后统一 demo / docs / guards。

为什么这样排序：

1. 如果先删 Foundation `IEventModule`，workflow/AI 侧会同时爆出大量编译面，回归面太大。
2. 先做 Workflow 命名和 thin-owner，能把主链边界先冻住。
3. 再删 `EventModules/EventRoutes`，可以让 AI/Role 和 Foundation 的删除动作一次性彻底。

## 11. 测试与门禁矩阵

### 11.1 必跑构建

1. `dotnet build aevatar.slnx --nologo`
2. `bash tools/ci/solution_split_guards.sh`

### 11.2 必跑测试

1. `dotnet test aevatar.slnx --nologo`
2. `bash tools/ci/solution_split_test_guards.sh`
3. `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo`
4. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo`
5. `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo`
6. `dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj --nologo`
7. `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~Workflow|FullyQualifiedName~Role|FullyQualifiedName~EventRouting"`

### 11.3 必跑守卫

1. `bash tools/ci/architecture_guards.sh`
2. `bash tools/ci/test_stability_guards.sh`
3. `bash tools/ci/projection_route_mapping_guard.sh`

### 11.4 新增守卫要求

1. 禁止 `WorkflowRawDefinition` / `WorkflowDefinition` / `RoleConfigurationNormalizer` / `InitializeRoleAgentEvent` 中出现 `EventModules/EventRoutes`。
2. 禁止 Foundation `src/` 出现 `IEventModule` / `IEventModuleFactory` / `SetModules` / `RegisterModule`。
3. 禁止 Workflow Core 中存在实现 primitive executor 的 `*Module` 类型。
4. 禁止 demo 中出现 `role_event_module` 样例名。

## 12. 风险与应对

### R1. 删除 `EventModules` 可能使部分 demo 语义消失

应对：

1. 先梳理每个 demo 的真实业务价值。
2. 能转成 workflow primitive 的，直接迁移。
3. 只是展示旧架构的，直接删除。

### R2. `GAgentBase` 去掉动态 module 后，少量测试桩会失效

应对：

1. Foundation 单测改成直接使用静态 `[EventHandler]`。
2. 若确实需要跨 handler 的观测点，进入 hook 模型而不是 module 模型。

### R3. 命名重构面大，可能短期影响搜索与认知

应对：

1. 一次性完成 rename，不留双名并存。
2. README、蓝图、guards 同步更新。

## 13. Definition of Done

只有同时满足下面 12 条，phase-7 才算完成：

1. `WorkflowRunGAgent` 达到 thin owner 目标。
2. Workflow Core 不再存在 `*Module` primitive 实现。
3. Workflow Core 不再存在 `IWorkflowModulePack` 等旧命名。
4. `WorkflowDefinition` 不再包含 `EventModules/EventRoutes`。
5. `InitializeRoleAgentEvent` 不再包含 `event_modules/event_routes`。
6. `RoleGAgent` 不再调用 `SetModules()`。
7. `RoleGAgentFactory` 不再解析 module 字符串和 route DSL。
8. Foundation 不再存在 `IEventModule` 抽象。
9. `GAgentBase` 不再支持动态 module API。
10. role event module demo 全部删除或改写。
11. 活跃文档和 README 不再把 event-module 当成现行模型。
12. build / test / guards 全部通过。

## 14. 一句话结论

phase-7 的本质不是“再把几个文件拆小一点”，而是完成最后一层架构净化：

`Workflow` 彻底收敛为 `definition actor + thin run owner + primitive executor`，`Role` 彻底收敛为 typed AI middleware/hook，`Foundation` 彻底退出通用 event-module 体系。
