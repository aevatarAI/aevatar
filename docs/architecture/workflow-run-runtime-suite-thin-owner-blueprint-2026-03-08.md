# Workflow Run Runtime Suite / Thin Owner 重构蓝图

## 1. 文档元信息

1. 状态：`Proposed`
2. 版本：`v1`
3. 日期：`2026-03-08`
4. 决策级别：`Architecture Breaking Change`
5. 主范围：
   - `src/workflow/Aevatar.Workflow.Core`
   - `docs/WORKFLOW.md`
   - `src/workflow/Aevatar.Workflow.Core/README.md`
   - `test/Aevatar.Workflow.Core.Tests`
   - `test/Aevatar.Workflow.Host.Api.Tests`
   - `test/Aevatar.Integration.Tests`
6. 替代文档：
   - 已删除 `runtime-phase6-final-authority-hardening-and-shell-thinning-blueprint-2026-03-07.md`
   - 已删除 `runtime-phase7-thin-owner-and-event-module-retirement-blueprint-2026-03-07.md`
7. 本文定位：
   - 作为 `WorkflowRunGAgent` 后续终局重构的唯一权威蓝图
   - 不再拆成 phase-6 / phase-7 两份重叠文档

## 2. 背景与关键决策

当前 `WorkflowRunGAgent` 已经从“大单文件”收敛成：

1. 一个较薄的 actor shell。
2. 一组共享 `WorkflowRunRuntimeContext` 的 runtime 协作者。
3. `WorkflowPrimitiveExecutionPlanner + WorkflowAsyncOperationReconciler` 两条主调度链。

这一步是对的，但还没达到最佳实践终局。当前剩余问题不是“文件名不优雅”，而是 actor owner 仍然自己持有过多 runtime 字段，并在构造函数内亲自完成大部分 family wiring。

本蓝图明确 4 个关键决策：

1. 不使用继承树解决 runtime 膨胀问题。
   - `Llm / Evaluate / Reflect / Cache / FanOut / SubWorkflow / Timeout / Completion` 是并列协作者，不是 `is-a` 关系。
   - 如果引入 `WorkflowRunRuntimeBase` 一类抽象父类，只会把 `State/Publish/Send/Persist` 变成一大组 `protected` 能力，扩大可写表面。
2. 使用组合，不使用继承。
   - 共享能力通过 `WorkflowRunRuntimeContext` 暴露。
   - family dispatch 通过 interface + registry + suite 组合。
3. owner 只持有组合根，不持有一长串具体 runtime 字段。
4. actor-thread state/effect 边界继续保持严格。
   - 任何协作者都只能通过 `WorkflowRunRuntimeContext` 访问状态与 effect。
   - 不允许新引入共享可变对象或跨 turn 缓存。

## 3. 重构目标

1. 把 `WorkflowRunGAgent` 收敛成真正的 thin owner，而不是“薄主文件 + 厚装配构造器”。
2. 把 runtime family 组织方式从“owner 持有一串字段”改成“suite 组合 + interface 分组 + registry 调度”。
3. 明确禁止用继承树承载 runtime 行为复用，统一改为：
   - `Context`
   - `Handler Interface`
   - `Dispatch Table`
   - `Runtime Suite`
4. 保持现有 actor 事实源、callback/reconcile、pending state 模型不回退。
5. 让后续新增 primitive family 时，不再需要修改 owner 的字段区和大构造器。

## 4. 范围与非范围

### 4.1 范围

1. `WorkflowRunGAgent` 的 runtime 装配模型。
2. `WorkflowPrimitiveExecutionPlanner` 的 family dispatch 模型。
3. `WorkflowAsyncOperationReconciler` 的 completion / signal handler 接入方式。
4. `WorkflowRunRuntimeContext` 的职责边界。
5. `docs/WORKFLOW.md` 与 `Aevatar.Workflow.Core/README.md` 的说明口径。

### 4.2 非范围

1. `WorkflowRunState` 事实模型回退或重写。
2. `EventModule` 回流。
3. `Scripting` / `Projection` 统一内核重构。
4. mixed-version 验证链调整。
5. DSL 语法层新增产品能力。

## 5. 架构硬约束

1. `WorkflowRunGAgent` 不得再新增 primitive-family 具体字段。
2. 新增 family 行为时，必须注册到 suite / registry，不得直接塞进 owner 构造器分支。
3. 不得引入 `WorkflowRunRuntimeBase`、`WorkflowRunHandlerBase` 之类暴露大量 `protected` state/effect 方法的继承基类。
4. runtime 协作者不得持久缓存 `WorkflowRunRuntimeContext` 之外的可变状态。
5. planner / reconciler 只依赖接口集合或注册表，不依赖具体 runtime 类名。
6. async signal / callback / completion 仍只能在 actor 事件流内推进业务。
7. 若某能力需要跨事件事实，仍必须落入 `WorkflowRunState`，不能借 suite 或 handler 私有字段保存。
8. 文档、README、测试命名要统一使用 `runtime suite / handler / registry / dispatch table` 语义，不再回到 `module` 或继承式运行时叙事。

## 6. 当前基线（代码事实）

截至 `2026-03-08`，当前热点如下：

1. [WorkflowRunGAgent.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs) `358` 行。
2. [WorkflowRunGAgent.Lifecycle.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.Lifecycle.cs) `233` 行。
3. [WorkflowRunGAgent.Infrastructure.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.Infrastructure.cs) `187` 行。
4. [WorkflowRunDispatchRuntime.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunDispatchRuntime.cs) `178` 行。
5. [WorkflowRunControlFlowRuntime.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunControlFlowRuntime.cs) `230` 行。
6. [WorkflowRunLlmRuntime.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunLlmRuntime.cs) `192` 行。
7. [WorkflowRunFanOutRuntime.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunFanOutRuntime.cs) `196` 行。
8. [WorkflowRunAggregationCompletionRuntime.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunAggregationCompletionRuntime.cs) `207` 行。

更关键的结构事实：

1. [WorkflowRunGAgent.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs) 仍持有十余个 runtime 协作者字段。
2. `WorkflowRunRuntimeContext` 已存在并收敛了 `State / CompiledWorkflow / Persist / Publish / Send / Effect`。
3. [WorkflowPrimitiveExecutionPlanner.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowPrimitiveExecutionPlanner.cs) 仍由 owner 显式拼装各 family planner。
4. [WorkflowAsyncOperationReconciler.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowAsyncOperationReconciler.cs) 仍由 owner 显式传入 completion/signal handler。

结论：

1. 当前已经从“owner 直接写业务分支”升级到“owner 装配大量协作者”。
2. 下一步该削的是“装配复杂度”和“直接依赖具体 runtime 类型”，不是再发明更多 helper。

## 7. 需求分解与状态矩阵

| ID | 需求 | 验收标准 | 当前状态 | 证据 | 差距 |
|---|---|---|---|---|---|
| R1 | owner 不再持有成串 runtime 字段 | `WorkflowRunGAgent` 只保留 `Context + Suite + Planner + Reconciler + Lifecycle helpers` | 未完成 | [WorkflowRunGAgent.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs) | owner 仍直接依赖具体 runtime 类 |
| R2 | family 行为通过接口分组 | 存在 `step family / completion / internal signal` 三类接口 | 未完成 | [WorkflowPrimitiveExecutionPlanner.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowPrimitiveExecutionPlanner.cs) | 仍是显式 concrete wiring |
| R3 | 使用组合根统一装配 | 新增 `WorkflowRunRuntimeSuite` 并成为 owner 唯一运行时组合入口 | 未完成 | 当前无该文件 | 当前装配分散在 owner 构造器 |
| R4 | 明确禁止继承式 runtime 复用 | 新文档、README、代码中不存在 `RuntimeBase/HandlerBase` 设计 | 部分完成 | 当前代码无基类，但缺少明确架构约束 | 需要在蓝图和活跃文档写死 |
| R5 | planner / reconciler 依赖 registry | owner 不再手工把每个 runtime 委托一一传入 | 未完成 | [WorkflowRunGAgent.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs) | 仍是手工 wiring |
| R6 | 文档只有一份权威蓝图 | phase-6/7 文档删除，新文档成为唯一来源 | 本次交付 | 本文件 | 需删除旧文档并切换引用 |

## 8. 差距详解

### 8.1 当前为什么还不够好

当前实现已经做对了三件事：

1. 把 state/effect 访问收进 `WorkflowRunRuntimeContext`。
2. 把大部分 family 逻辑从 owner 主文件移走。
3. 把 callback/completion 从直接业务分支变成 reconcile 主链。

但还有三个结构问题：

1. owner 仍知道太多具体 family 类型。
2. owner 构造器仍是唯一运行时装配中心。
3. planner / reconciler 的装配协议还是“一个一个传方法”。

### 8.2 为什么不用继承

这里如果改成继承，通常会走向下面的坏结果：

1. 引入 `WorkflowRunRuntimeBase`。
2. 把 `State / Publish / Send / Persist / Logger` 做成 `protected`。
3. 各 runtime 从基类继承，再在子类里随意调用这些能力。

这会带来：

1. state/effect 可写面扩大。
2. family 间共享逻辑变成隐式耦合。
3. actor-thread 约束靠约定而不是靠接口收敛。
4. 后续测试更依赖基类脚手架，不利于替换和裁剪。

因此这里正确的设计模式不是继承，而是：

1. `Facade`：`WorkflowRunRuntimeContext`
2. `Strategy`：各类 handler interface
3. `Registry / Dispatch Table`：family 路由
4. `Composition Root`：`WorkflowRunRuntimeSuite`

## 9. 目标架构

### 9.1 Owner 终局形态

`WorkflowRunGAgent` 只保留：

1. `WorkflowRunRuntimeContext`
2. `WorkflowRunRuntimeSuite`
3. `WorkflowPrimitiveExecutionPlanner`
4. `WorkflowAsyncOperationReconciler`
5. `Lifecycle / binding / finalization` 少量 owner 职责

owner 不再直接持有：

1. `WorkflowRunLlmRuntime`
2. `WorkflowRunEvaluationRuntime`
3. `WorkflowRunReflectRuntime`
4. `WorkflowRunCacheRuntime`
5. `WorkflowRunFanOutRuntime`
6. `WorkflowRunSubWorkflowRuntime`
7. `WorkflowRunTimeoutCallbackRuntime`
8. `WorkflowRunAggregationCompletionRuntime`
9. `WorkflowRunProgressionCompletionRuntime`
10. `WorkflowRunAsyncPolicyRuntime`

### 9.2 Runtime Suite

新增：

1. `WorkflowRunRuntimeSuite`
   - 作为唯一运行时组合根
   - 内部持有所有 family handler 集合
   - 向 planner / reconciler 暴露 registry 视图

建议结构：

```text
WorkflowRunRuntimeSuite
├── StepFamilyHandlers
├── CompletionHandlers
├── InternalSignalHandlers
├── ResponseHandlers
└── RuntimeServices
```

### 9.3 接口分组

建议至少拆成下面几类接口：

1. `IWorkflowStepFamilyHandler`
   - 声明自己处理哪些 canonical step types
   - 处理 `StepRequestEvent`
2. `IWorkflowCompletionHandler`
   - 处理 `StepCompletedEvent` 的 stateful completion 对账
3. `IWorkflowInternalSignalHandler`
   - 处理 timeout / retry backoff / watchdog / internal fired signals
4. `IWorkflowResponseHandler`
   - 处理 LLM / evaluate / reflect 这类外部响应事件

接口上只暴露“处理能力”和“匹配能力”，不暴露 owner 细节。

### 9.4 Dispatch Table / Registry

新增：

1. `WorkflowStepFamilyDispatchTable`
   - `canonical step type -> IWorkflowStepFamilyHandler`
2. `WorkflowCompletionHandlerRegistry`
   - 一组 `IWorkflowCompletionHandler`
3. `WorkflowInternalSignalRegistry`
   - 一组 `IWorkflowInternalSignalHandler`
4. `WorkflowResponseHandlerRegistry`
   - 一组 `IWorkflowResponseHandler`

最终效果：

1. planner 从 `dispatch table` 取 family handler。
2. reconciler 从各 registry 取候选处理器。
3. owner 不再需要知道某个 step family 具体由哪一个 runtime 类处理。

## 10. 重构工作包（WBS）

### WP1. 建立 Runtime Suite 组合根

目标：

1. 新增 `WorkflowRunRuntimeSuite.cs`
2. 让 owner 只保留一个 suite 字段

产物：

1. `WorkflowRunRuntimeSuite.cs`
2. `WorkflowRunRuntimeSuiteBuilder.cs` 或等价构造入口

DoD：

1. owner 构造器不再逐个 `new WorkflowRun*Runtime`
2. 运行时对象装配集中到 suite builder

### WP2. 定义 handler interfaces

目标：

1. 用接口收窄 family 行为边界
2. 让 planner / reconciler 依赖抽象集合

产物：

1. `IWorkflowStepFamilyHandler.cs`
2. `IWorkflowCompletionHandler.cs`
3. `IWorkflowInternalSignalHandler.cs`
4. `IWorkflowResponseHandler.cs`

DoD：

1. 不再把具体 runtime 方法直接当 delegate 传入 planner / reconciler

### WP3. 构建 dispatch table / registries

目标：

1. 把 canonical step type、completion、internal signal 的路由表显式化

产物：

1. `WorkflowStepFamilyDispatchTable.cs`
2. `WorkflowCompletionHandlerRegistry.cs`
3. `WorkflowInternalSignalRegistry.cs`
4. `WorkflowResponseHandlerRegistry.cs`

DoD：

1. `WorkflowPrimitiveExecutionPlanner` 不再知道具体 runtime 类名
2. `WorkflowAsyncOperationReconciler` 不再知道具体 runtime 类名

### WP4. 收薄 owner shell

目标：

1. 让 [WorkflowRunGAgent.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs) 只保留 owner 入口

产物：

1. 收薄后的 `WorkflowRunGAgent.cs`
2. 必要时把 owner 构造逻辑迁移到单独 composition helper

DoD：

1. owner runtime 字段数量降到 `0-2`
2. 主文件不再承担大块运行时 wiring

### WP5. 文档与测试同步

目标：

1. 活跃文档统一改为 suite/registry 语义
2. 增加针对 dispatch table / registry 的单元测试

产物：

1. 更新 `docs/WORKFLOW.md`
2. 更新 `Aevatar.Workflow.Core/README.md`
3. 新增 suite/registry tests

DoD：

1. 仓库活跃文档不再写“owner 持有一串 runtime 字段”
2. 对 dispatch/registry 的行为有单测覆盖

## 11. 里程碑与依赖

1. M1：删除旧蓝图并冻结新蓝图
   - 交付件：本文件
2. M2：引入 `RuntimeSuite + handler interfaces`
   - 依赖：无
3. M3：planner / reconciler 切换到 registry
   - 依赖：M2
4. M4：owner 字段与构造器收薄
   - 依赖：M3
5. M5：文档、测试、门禁同步
   - 依赖：M4

## 12. 验证矩阵（需求 -> 命令 -> 通过标准）

| 需求 | 命令 | 通过标准 |
|---|---|---|
| suite/registry 代码编译通过 | `dotnet build aevatar.slnx --nologo` | 退出 `0` |
| workflow 领域测试通过 | `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo` | 退出 `0` |
| API 与集成回归通过 | `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo` | 退出 `0` |
| 整解无回归 | `dotnet test aevatar.slnx --nologo` | 退出 `0` |
| 架构门禁通过 | `bash tools/ci/architecture_guards.sh` | 退出 `0` |
| 测试稳定性门禁通过 | `bash tools/ci/test_stability_guards.sh` | 退出 `0` |
| 分片构建通过 | `bash tools/ci/solution_split_guards.sh` | 退出 `0` |
| 分片测试通过 | `bash tools/ci/solution_split_test_guards.sh` | 退出 `0` |

## 13. 完成定义（Final DoD）

以下 8 条同时满足，才算这条重构线完成：

1. `WorkflowRunGAgent` 不再直接持有一长串具体 runtime 字段。
2. `WorkflowRunRuntimeSuite` 成为唯一运行时组合根。
3. planner 只依赖 step-family dispatch table。
4. reconciler 只依赖 completion/signal/response registries。
5. 没有引入继承式 runtime base class。
6. 所有运行态访问仍经由 `WorkflowRunRuntimeContext`。
7. 文档和 README 全部切到 suite/registry 语义。
8. build/test/guards 全部通过。

## 14. 风险与应对

### 风险 1：suite 变成新的 God Object

应对：

1. suite 只负责组合与暴露 registry 视图。
2. suite 不直接承载业务逻辑。

### 风险 2：接口拆太细，复杂度转移

应对：

1. 只按调度语义分 4 类接口，不做过度微接口化。
2. 以 planner / reconciler 真实依赖为准设计接口。

### 风险 3：开发者回退到继承基类

应对：

1. 文档明确禁止。
2. review 与门禁检查 `*RuntimeBase`、`*HandlerBase` 等新引入模式。

## 15. 执行清单

- [x] 删除旧 phase-6 蓝图
- [x] 删除旧 phase-7 蓝图
- [x] 生成新的单一权威蓝图
- [ ] 引入 `WorkflowRunRuntimeSuite`
- [ ] 引入 handler interfaces
- [ ] 引入 dispatch table / registries
- [ ] 收薄 owner 字段与构造器
- [ ] 增加 suite/registry 单测
- [ ] 跑完 build/test/guards

## 16. 当前执行快照（2026-03-08）

已完成：

1. 删除了旧的 phase-6 / phase-7 重构文档。
2. 生成了新的统一蓝图。
3. 活跃说明文档将切换为引用本蓝图。

未完成：

1. `RuntimeSuite + handler interface + registry` 代码尚未落地。
2. owner 仍然直接装配 runtime。

## 17. 变更纪律

1. 本蓝图是 `WorkflowRunGAgent` 后续重构的唯一权威文档。
2. 后续如需继续拆分，不再新增 phase-6/7 同类平行蓝图，而是在本文件内推进版本和状态。
3. 任何尝试引入继承式 runtime base 的设计，都应默认视为违反本蓝图，除非有新的独立 ADR 明确推翻此决策。
