# Workflow Execution 销毁竞态备忘（Bug 6）

## 目的

记录当前 `WorkflowExecutionGAgent` 销毁时序中的竞态风险、影响面和后续可选方案，作为后续评审与实现的上下文基线。

这份文档是备忘，不是最终改造方案。

## 问题一句话

当前链路在 run 收尾后会立即 `DestroyExecutionActor`。大多数场景是安全的，但在极端时序下，仍存在“非投影消费方的尾部事件尚在路上，执行 actor 已销毁”的理论窗口。

## 当前收尾链路（代码语义）

`WorkflowChatRunApplicationService.ExecuteAsync(...)` 的主流程是：

1. 启动 `processingTask = ProcessEnvelopeAsync(...)`。
2. `StreamAsync(...)` 从 `IWorkflowRunEventSink` 读事件，直到 `RUN_FINISHED` 或 `RUN_ERROR`。
3. `FinalizeAsync(...)` 等待投影完成状态并产出报告。
4. `JoinProcessingTaskAsync(...)` 等待请求处理任务退出。
5. 结束阶段：
   - 正常分支：`DisposeSinkSafeAsync(...)`
   - 异常分支：`AbortCoreAsync(...)`（内部也会 `JoinProcessingTaskAsync` + `DisposeSinkSafeAsync`）
6. `finally` 中无条件 `DestroyExecutionActorSafeAsync(executionActorId)`。

关键点在第 6 步：销毁是“当前应用层链路完成后立即发生”，没有额外 grace period。

## 竞态窗口定义

这里的“销毁竞态”不是指主流程内明显漏 await，而是指跨组件时序：

- 我们等待的是“投影完成信号”和“本地处理任务结束”。
- 但系统里可能存在不受这两个信号严格约束的异步消费方（例如某些实时推送或外部订阅扩展）。
- 如果这些消费方仍依赖 execution actor 存活，立即销毁就有机会把尾部事件截断。

换句话说：当前链路对“投影闭环”已经有等待，但对“所有潜在旁路消费者都完成”没有全局确认。

## 影响面（如果竞态触发）

- 终态推送偶发缺尾（例如最后一帧或结束信号迟到）。
- 个别观察端看到 run 已结束，但补充事件未到齐。
- 排障时会出现“报告已完成但实时流尾部不完整”的不一致体感。

目前没有证据显示这是高频问题，更接近低概率时序缺口。

## 为什么当前优先级可以放 P3

现有实现已经有几层保护，实际风险被压低：

- `WorkflowExecutionRunOrchestrator.FinalizeAsync(...)` 先等投影完成，超时后还有一次 grace wait。
- `WorkflowChatRunApplicationService` 在销毁前会 `JoinProcessingTaskAsync(...)`。
- sink 在结束时 `Complete + Dispose`，避免无限写入。
- `DestroyExecutionActorSafeAsync(...)` 做了异常兜底，不会反向拖垮主流程。

所以它不是“当前链路必现 bug”，更像“高负载或复杂订阅拓扑下的稳健性增强点”。

## 后续可选方案（按侵入性从低到高）

### 方案 A：固定 grace period 后再销毁

- 做法：`finally` 里先 `Task.Delay(xxx)` 再 `DestroyAsync`。
- 优点：实现最简单，改动小。
- 缺点：拍脑袋参数，不同负载下效果不稳定。

### 方案 B：soft-destroy（标记回收）+ 延迟清理

- 做法：先把 execution actor 标记为“待回收”，短窗口后统一清理。
- 优点：语义清晰，可观察性好，便于灰度。
- 缺点：需要 runtime/生命周期扩展，改动面较大。

### 方案 C：显式 ack 的收口协议

- 做法：以 run 终态事件为边界，等待关键消费者 ack 后再销毁。
- 优点：语义最严格，可证明“不会截尾”。
- 缺点：实现复杂，需要定义 ack 协议与超时策略。

## 建议的落地顺序

1. 先补观测：统计“run 完成到 actor 销毁”的耗时、销毁失败率、尾帧缺失率。
2. 如果观测到真实尾部缺失，再优先尝试方案 A 做低风险缓解。
3. 若 A 仍不足，再评估方案 B（soft-destroy）作为正式收口方案。

## 建议补充的回归用例

- 人工注入慢消费（延迟投递/延迟推送）后，验证是否仍稳定拿到终态帧。
- 高并发 run 下，检查 `RUN_FINISHED` 与最后 `TEXT_MESSAGE_END` 的到达完整率。
- 销毁时机压测：缩短/拉长 grace window，对比尾帧完整率和资源占用。

## 代码锚点

- `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowChatRunApplicationService.cs`
- `src/workflow/Aevatar.Workflow.Application/Orchestration/WorkflowExecutionRunOrchestrator.cs`
- `src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowRunEventContracts.cs`
- `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunOutputStreamer.cs`
- `src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/WorkflowExecutionAGUIEventProjector.cs`
