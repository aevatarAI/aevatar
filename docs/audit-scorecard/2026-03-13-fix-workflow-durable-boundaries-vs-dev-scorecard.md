# `fix/workflow-durable-boundaries-20260310` vs `dev` 审计评分卡

## 1. 审计范围与方法

- 审计对象：当前分支 `fix/workflow-durable-boundaries-20260310` 相对 `dev` 的差异。
- 范围说明：`git diff --stat dev...HEAD` 显示本分支相对 `dev` 已累计 `1087 files changed, 111416 insertions(+), 17259 deletions(-)`，属于大体量堆叠分支。
- 方法：采用风险导向审计，不做 1087 个文件的逐文件穷举；重点审查了本分支尾部直接相关的 durable cleanup / projection cleanup / command dispatch 改动，以及这些改动触达的测试与公共抽象。
- 重点提交：
  - `c6c09336 Fix workflow projection cleanup semantics`
  - `c34bced2 Fix detached workflow cleanup durability`

## 2. 客观验证结果

### 2.1 通过

1. `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo`
   - `Passed: 160, Failed: 0`
2. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo`
   - `Passed: 317, Failed: 0`
3. `bash tools/ci/test_stability_guards.sh`
   - 通过
4. `bash tools/ci/architecture_guards.sh`
   - 通过

### 2.2 结论

- 当前测试与 guard 没有阻止本次发现的问题。
- 问题集中在“异常时序 + durable cleanup 回放”的未覆盖路径，而不是常规 happy path。

## 3. 总体评分

**总分：83/100（B+）**

| 维度 | 分数 | 结论 |
|---|---:|---|
| 分层与依赖反转 | 18/20 | 方向正确，detached cleanup 通过应用层端口接 projection/runtime 能力，没有明显反向依赖回退 |
| CQRS 与统一投影链路 | 17/20 | 命令、事件、projection 主链路保持统一，但 detached cleanup 的 durable 补偿分支仍有缺口 |
| Projection 编排与状态约束 | 14/20 | 继续朝 actorized orchestration 演进，但 release / replay 时序还有真实回收漏洞 |
| 读写分离与会话语义 | 13/15 | 会话语义更清晰，但“accepted -> durable cleanup guaranteed”在异常时序下并不成立 |
| 命名语义与冗余清理 | 9/10 | 命名与职责大体一致 |
| 可验证性（门禁/构建/测试） | 12/15 | 现有测试与 guards 全绿，但未覆盖关键 crash-window/race-window |

## 4. 主要发现

### 4.1 [P1] cleanup 记录在真正 dispatch 前入 outbox，但 replay 端无法处理“根本没投递成功”的记录

- 证据：
  - `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunDetachedDispatchService.cs:31`
  - `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunDetachedDispatchService.cs:36`
  - `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunDetachedDispatchService.cs:45`
  - `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowRunDetachedCleanupOutboxGAgent.cs:168`
  - `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowRunDetachedCleanupOutboxGAgent.cs:181`
  - `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowProjectionQueryReader.cs:26`
- 事实：
  - 当前实现先 `PrepareAsync`，再 `ScheduleAsync` durable cleanup，之后才 `DispatchPreparedAsync`。
  - 一旦进程在“已 enqueue cleanup，但尚未真正 dispatch 到 run actor”这个窗口崩溃，outbox replay 读到的 snapshot 会是 `null`。
  - `WorkflowRunDetachedCleanupOutboxGAgent` 对 `snapshot == null` 不视为终态，也不重试/不清理，只会保留记录并持续轮询。
- 影响：
  - 新创建的 workflow run actor 及其 `CreatedActorIds` 会永久泄漏。
  - 这正好击穿了这次改动要补的 durability 目标：`dispatch 未真正发生` 的异常窗口并没有被自愈。
- 评价：
  - 这是 merge 前应修复的问题。

### 4.2 [P1] projection release listener 是异步后台订阅，outbox 立即 replay 时存在 release control 丢失窗口

- 证据：
  - `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowProjectionActivationService.cs:80`
  - `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionRuntimeLease.cs:59`
  - `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionRuntimeLease.cs:150`
  - `src/workflow/Aevatar.Workflow.Projection/Orchestration/ActorWorkflowRunDetachedCleanupOutbox.cs:37`
  - `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowRunDetachedCleanupOutboxGAgent.cs:212`
  - `test/Aevatar.Workflow.Host.Api.Tests/WorkflowRunDetachedCleanupOutboxTests.cs:115`
- 事实：
  - `WorkflowExecutionRuntimeLease` 在构造函数里启动 `RunProjectionReleaseListenerAsync(...)`，但不会等待订阅完成就把 lease 返回给上层。
  - `ActorWorkflowRunDetachedCleanupOutbox.ScheduleAsync(...)` 在 enqueue 后立即 `TriggerReplayAsync(batchSize: 1)`。
  - 测试为了稳定通过，显式等待了 `SubscriptionStarted`，说明订阅建立本身就是异步时序点；生产路径没有等价屏障。
- 影响：
  - 如果 run 很快完成，outbox 可能在 listener 尚未真正订阅时就发布 `ReleaseRequested`。
  - 当前 session event hub 基于普通 stream，不保证后订阅者能回放之前的消息；一旦该控制事件丢失，projection 可能继续存活，而 outbox 已经把 read model 标成 stopped 并释放 ownership。
- 评价：
  - 这是一个真实 race，不是理论问题。

### 4.3 [P2] 成功 cleanup 的 outbox entry 永不删除，会导致 actor state 长期累积

- 证据：
  - `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowRunDetachedCleanupOutboxGAgent.cs:73`
  - `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowRunDetachedCleanupOutboxGAgent.cs:139`
  - `src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowRunDetachedCleanupOutboxGAgent.cs:150`
- 事实：
  - replay 只处理 `CompletedAtUtc == null` 的 entry。
  - 成功路径只把 entry 标记为 `CompletedAtUtc != null`，并不移除。
  - 仓库内没有额外的 retention / compaction 逻辑来清理这些已完成 entry。
- 影响：
  - 分支引入的 detached cleanup outbox 会随运行次数单调增长，最终把无价值历史留在 actor state 中。
- 评价：
  - 不是立即阻断，但会成为长期运行下的状态膨胀问题。

## 5. 正向观察

1. detached cleanup 的职责边界比旧实现清晰：应用层负责 schedule / discard，projection 层负责 durable outbox 与 release orchestration。
2. ownership heartbeat、projection release listener、cleanup replay 已经开始向 actorized runtime state 收敛，方向与仓库架构规则一致。
3. 相关测试、stability guard、architecture guard 都能通过，基础回归面没有明显退化。

## 6. 建议的合并准入条件

### 合并前应修复

1. 明确处理 “cleanup 已入队，但 dispatch 从未真正发生” 的 replay 终态策略。
2. 为 projection release listener 引入 readiness barrier，或把 release control 改成可重放/可确认的 durable 通道，避免一次性控制消息丢失。

### 可在下一迭代修复

1. 给 detached cleanup outbox 增加成功后删除或保留期清理策略。
2. 新增 crash-window / listener-race 的测试，避免当前仅靠 happy path 测试掩盖问题。

## 7. 最终结论

- 这组改动的方向是对的，也明显朝着“detached workflow cleanup durable 化”推进了一步。
- 但目前仍未闭合两个关键异常窗口：`pre-dispatch crash window` 与 `release-listener race window`。
- 因此本次评分维持在 **83/100（B+）**，结论是：**不建议按当前状态直接合并**。
