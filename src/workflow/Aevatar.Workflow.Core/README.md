# Aevatar.Workflow.Core

`Aevatar.Workflow.Core` 是 workflow 子系统的领域内核，当前模型已经收敛为：

1. `WorkflowGAgent` 负责 definition/binding。
2. `WorkflowRunGAgent` 负责单次 run 的持久事实与执行推进。
3. `WorkflowPrimitiveExecutorRegistry` 只解析无状态原语处理器。

## 1. 目录结构

```text
Aevatar.Workflow.Core/
├── WorkflowGAgent.cs
├── WorkflowRunGAgent.cs
├── WorkflowRunGAgent.Lifecycle.cs
├── WorkflowRunGAgent.ExternalInteractions.cs
├── WorkflowRunReducer.cs
├── WorkflowCompilationService.cs
├── WorkflowPrimitiveExecutionPlanner.cs
├── WorkflowAsyncOperationReconciler.cs
├── WorkflowRunEffectDispatcher.cs
├── WorkflowRunStepRequestFactory.cs
├── WorkflowRunSupport.cs
├── WorkflowRunGAgent.StatefulCompletions.cs
├── WorkflowRunGAgent.Callbacks.cs
├── WorkflowRunGAgent.Dispatch.cs
├── WorkflowRunGAgent.Infrastructure.cs
├── workflow_state.proto
├── workflow_run_state.proto
├── WorkflowPrimitiveExecutorRegistry.cs
├── WorkflowPrimitiveExecutionContext.cs
├── WorkflowRunStatePatchSupport.cs
├── WorkflowCorePrimitivePack.cs
├── IWorkflowPrimitivePack.cs
├── Primitives/
├── Validation/
├── Expressions/
├── PrimitiveExecutors/
└── Connectors/
```

## 2. 两个 Actor 的职责

### WorkflowGAgent

负责：

1. 绑定与校验 workflow YAML。
2. 维护 `workflow_name/version/compiled`。
3. 创建并绑定 `WorkflowRunGAgent`。
4. 汇总 definition 级执行计数。

不负责：

1. step 推进。
2. run pending。
3. signal / resume / callback 对账。

### WorkflowRunGAgent

负责：

1. 持有 `WorkflowRunState`。
2. 启动和推进单次 run。
3. 持久化 timeout/retry/delay/wait/human gate/sub-workflow 等 pending facts。
4. reactivation 后重建编译缓存、恢复 slice-level state patch 结果、重发 suspended facts。
5. 统一处理 `WorkflowResumedEvent`、`SignalReceivedEvent`、callback fired、自身 domain events。
6. 通过 `WorkflowRunReducer / WorkflowPrimitiveExecutionPlanner / WorkflowAsyncOperationReconciler / WorkflowRunEffectDispatcher` 把 reducer、planning、对账和 effect 组装从 actor shell 里显式抽出。
7. `WorkflowRunGAgent` 主文件只保留 owner shell；binding/finalization 在 `Lifecycle`，外部恢复/信号/回包入口在 `ExternalInteractions`。
8. DSL compile/validate 已从 actor shell 抽成 `WorkflowCompilationService + Validation/*`。
9. step request 构建、while 条件求值、callback key / parent-step 推导等纯 helper 逻辑继续下沉到 `WorkflowRunStepRequestFactory + WorkflowRunSupport`，避免 owner 切片继续膨胀。

## 3. 状态模型

### workflow_state.proto

只保存 definition facts：

- `workflow_yaml`
- `workflow_name`
- `version`
- `compiled`
- `compilation_error`
- execution counters
- `inline_workflow_yamls`

### workflow_run_state.proto

保存 run facts：

- `run_id`
- `status`
- `active_step_id`
- `variables`
- `step_executions`
- `retry_attempts`
- `pending_timeouts`
- `pending_retry_backoffs`
- `pending_delays`
- `pending_signal_waits`
- `pending_human_gates`
- `pending_llm_calls`
- `pending_evaluations`
- `pending_reflections`
- `pending_parallel_steps`
- `pending_foreach_steps`
- `pending_map_reduce_steps`
- `pending_race_steps`
- `pending_while_steps`
- `pending_sub_workflows`
- `pending_child_run_ids_by_parent_run_id`
- `cache_entries`
- `pending_cache_calls`

## 4. 运行边界

### 4.1 WorkflowCorePrimitivePack 当前只注册无状态 primitive handlers

- `conditional`
- `switch`
- `checkpoint`
- `assign`
- `vote`
- `tool_call`
- `connector_call`
- `transform`
- `retrieve_facts`
- `guard`
- `emit`
- `workflow_yaml_validate`
- `dynamic_workflow`

### 4.2 以下原语仍由 WorkflowRunGAgent 直接拥有

- `workflow_call`
- `delay`
- `wait_signal`
- `human_input`
- `human_approval`
- `llm_call`
- `evaluate`
- `reflect`
- `parallel`
- `foreach`
- `map_reduce`
- `race`
- `while`
- `cache`

这些原语要么存在跨事件 pending，要么存在恢复语义，因此不能回退为 executor 私有状态机。

## 5. 扩展规则

若新增 step type 只做纯函数或事件转换：

1. 实现 `IWorkflowPrimitiveExecutor`
2. 放入 `IWorkflowPrimitivePack.Executors`

若新增 step type 需要以下任意能力，就必须扩展 `WorkflowRunState` 和 `WorkflowRunGAgent`：

1. callback / timeout / retry / delay
2. 外部响应 correlation
3. human gate / signal wait
4. fanout / aggregation
5. sub-workflow lifecycle
6. 跨 reactivation 恢复

## 6. 关键文件

1. `WorkflowGAgent.cs`
2. `WorkflowRunGAgent.cs`
3. `WorkflowRunReducer.cs`
4. `WorkflowPrimitiveExecutionPlanner.cs`
5. `WorkflowAsyncOperationReconciler.cs`
6. `WorkflowRunEffectDispatcher.cs`
7. `WorkflowRunGAgent.Lifecycle.cs`
8. `WorkflowRunGAgent.ExternalInteractions.cs`
9. `WorkflowRunGAgent.StatefulCompletions.cs`
10. `WorkflowRunGAgent.Callbacks.cs`
11. `WorkflowRunGAgent.Dispatch.cs`
12. `WorkflowRunGAgent.Infrastructure.cs`
13. `WorkflowCompilationService.cs`
14. `workflow_state.proto`
15. `workflow_run_state.proto`
