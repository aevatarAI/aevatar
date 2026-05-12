# 题 01 — 硬规则甄别（PR 评审模式）

> 满分：10 分（每小题 2 分）
> 必读：根目录 `CLAUDE.md` 全文，重点 `Actor 设计原则` / `Actor 执行模型` / `权威状态 / ReadModel / Projection` / `中间层状态约束` / `序列化` / `测试与质量门禁`。

## 题面

下面是 5 个**伪 PR diff 片段**，由不同新同事提交。每段 diff 都至少违反 **CLAUDE.md** 中的一条**强制约束**（标记为"强制"或"硬约束"的条目）。

请对每段 diff：

1. 指出**违反了哪条规则**（必须把 CLAUDE.md 中**对应小节标题**写出，例如"§Actor 执行模型 — 单线程事实源"），并**引一句**该小节里能直接证明"这就是违反"的原文（不少于 8 个汉字）。
2. **改写**该 diff（≤8 行伪代码）让它合规。
3. 如果该违规直接对应仓库里**已经存在**的某条 CI 门禁脚本（在 `tools/ci/` 下），写出脚本文件名；**不是每条规则都有专门的 CI 脚本**——如果你 `grep` 一圈没找到，直接写 *"无对应 CI 脚本，规则仅在 CLAUDE.md 文字层"* 即可，不扣分。

任一小步缺失或答错扣 0.5 分。

---

### A. 在 `Aevatar.CQRS.Projection.Core` 中新增

```csharp
public sealed class WorkflowRunIndex
{
    private readonly ConcurrentDictionary<string, WorkflowRunContext> _runs = new();

    public void Track(string runId, WorkflowRunContext ctx) =>
        _runs[runId] = ctx;

    public WorkflowRunContext? Find(string runId) =>
        _runs.TryGetValue(runId, out var ctx) ? ctx : null;
}
```

### B. 在 `RoleGAgent` 内的工具回调里

```csharp
private async Task OnHttpToolFinishedAsync(ToolResult result)
{
    var shouldComplete = false;
    lock (_pendingToolsLock)
    {
        _pendingToolCount--;
        if (_pendingToolCount == 0)
        {
            State.Stage = ChatTurnStage.Completed;
            shouldComplete = true;
        }
    }

    if (shouldComplete)
    {
        await PersistDomainEventAsync(new TurnCompletedEvent());
    }
}
```

### C. 在新写的集成测试里

```csharp
await client.PostAsync("/api/scopes/x/workflows/y/runs", body);
await Task.Delay(2000); // 等投影
var snap = await client.GetFromJsonAsync<RunSnapshot>("/api/.../runs/last");
snap.Status.Should().Be("Completed");
```

### D. 在 Application 层新增

```csharp
public sealed class WorkflowRunQueryService
{
    public async Task<RunDto> GetAsync(string runId, CancellationToken ct)
    {
        var events = await _eventStore.LoadAsync(runId, ct);
        var state = WorkflowRunState.ReplayFrom(events);
        return _mapper.ToDto(state);
    }
}
```

### E. 在新增的领域事件契约里

```csharp
public sealed record ChatTurnAppendedEvent
{
    public string RunId { get; init; } = "";
    public string PayloadJson { get; init; } = "";
    // 后续阶段把它解析成 typed payload
}
```

## 答题区

### A

违规规则：§中间层状态约束（强制）。`CLAUDE.md:115` 原文：`禁止中间层维护 entity/actor/workflow-run/session 等 ID → 上下文/事实状态的进程内映射（Dictionary<>/ConcurrentDictionary<>/HashSet<>/Queue<>）。` 这个 `WorkflowRunIndex` 正是在 `Aevatar.CQRS.Projection.Core` 中用 `_runs: runId -> WorkflowRunContext` 做进程内事实索引。

合规改写：

```csharp
public sealed class WorkflowRunProjectionActor : GAgentBase<WorkflowRunProjectionState>
{
    public Task HandleAsync(TrackWorkflowRun cmd) =>
        PersistDomainEventAsync(new WorkflowRunTrackedEvent(cmd.RunId, cmd.Context));
}

await _dispatchPort.SendAsync(WorkflowRunActorId.From(runId), new TrackWorkflowRun(runId, ctx), ct);
var ctx = await _queryPort.GetRunContextAsync(runId, ct);
```

CI 门禁：`tools/ci/architecture_guards.sh`。它扫描 `src/Aevatar.CQRS.Projection.Core` 等目录里的 `actor/entity/run/session` ID 映射字典字段（见 `architecture_guards.sh:783-829`）。

### B

违规规则：§Actor 执行模型（强制）-- 单线程事实源 / 回调只发信号。`CLAUDE.md:105` 原文：`运行态只在事件处理主线程修改；禁止 lock/Monitor/ConcurrentDictionary 作为并发补丁维护事实状态。` `CLAUDE.md:106` 也写了：`线程池回调不直接读写运行态或推进业务；只发布内部触发事件`。该 diff 在工具回调里 `lock`、改 `State.Stage`、再提交领域事件，业务推进没有回到 actor inbox。

合规改写：

```csharp
private Task OnHttpToolFinishedAsync(ToolResult result) =>
    PublishAsync(
        new ToolFinishedSignal(result.RunId, result.TurnId, result.ToolCallId),
        TopologyAudience.Self,
        default);

[EventHandler]
public Task HandleToolFinishedAsync(ToolFinishedSignal signal)
{
    if (!State.ActiveTurnId.Equals(signal.TurnId)) return Task.CompletedTask;
    State.PendingToolIds.Remove(signal.ToolCallId);
    return State.PendingToolIds.Count == 0 ? PersistDomainEventAsync(new TurnCompletedEvent()) : Task.CompletedTask;
}
```

CI 门禁：无对应 CI 脚本，规则仅在 `CLAUDE.md` 文字层。现有脚本能扫部分 `ConcurrentDictionary` 模式，但没有看到针对 `lock` 或“回调直接改 Actor State”的通用扫描。

### C

违规规则：§测试与质量门禁 -- 轮询等待门禁。`CLAUDE.md:187` 原文：`禁止随意 Task.Delay(...)/WaitUntilAsync(...)。确属跨进程最终一致性探测且无法改为确定性同步时，须加入 tools/ci/test_polling_allowlist.txt 并说明原因。` 这里用 `Task.Delay(2000)` 等投影，是不确定的轮询等待。

合规改写：

```csharp
var completed = new TaskCompletionSource<RunSnapshot>();
await using var sub = await observer.SubscribeRunAsync(runId, s =>
    s.Status == "Completed" ? completed.TrySetResult(s) : false, ct);

await client.PostAsync("/api/scopes/x/workflows/y/runs", body, ct);
var snap = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
snap.Status.Should().Be("Completed");
```

CI 门禁：`tools/ci/test_stability_guards.sh`。它直接扫描测试中的 `Task.Delay(` / `WaitUntilAsync(`，且要求不在 allowlist 的命中失败（见 `test_stability_guards.sh:16-35`）。

### D

违规规则：§权威状态 / ReadModel / Projection（强制）-- 读写边界。`CLAUDE.md:62` 原文：`对外查询只读 readmodel；不暴露 actor 内部状态、state mirror payload 或 event replay 为查询主路径。` `CLAUDE.md:65` 还明确：`QueryPort/QueryService/ApplicationService 不得在请求路径读 IEventStore、重放 events、临时重建 state mirror`。该 `WorkflowRunQueryService` 在 Application 层从 event store replay 出状态后返回 DTO，正是 query-time replay。

合规改写：

```csharp
public sealed class WorkflowRunQueryService
{
    public Task<RunDto?> GetAsync(string runId, CancellationToken ct) =>
        _workflowRunQueryPort.GetCurrentAsync(runId, ct);
}

// IWorkflowRunQueryPort 的实现只读 workflow_run_current_state readmodel，并返回 stateVersion。
```

CI 门禁：`tools/ci/cqrs_eventsourcing_boundary_guard.sh`，并由 `tools/ci/architecture_guards.sh` 调用（见 `architecture_guards.sh:964-965`）。该脚本扫描 read/query 路径里的 `IEventStore` 等用法，报错文案是 `Read/query paths must not read or replay committed facts from IEventStore`（见 `cqrs_eventsourcing_boundary_guard.sh:16-44`）。

### E

违规规则：§序列化（强制）。`CLAUDE.md:123` 原文：`State、领域事件、命令、回调载荷、快照、缓存载荷、跨 Actor/跨节点内部传输对象全部使用 Protobuf。` `CLAUDE.md:126` 还要求：`新增状态/事件/持久化载荷：先定义 .proto 并生成类型，再接入实现；禁止先写临时结构后补 Protobuf。` `PayloadJson` 把领域事件载荷先塞进 JSON 字符串，后续再解析，违反了事件契约强类型和 Protobuf 优先。

合规改写：

```proto
message ChatTurnAppendedEvent {
  string run_id = 1;
  ChatTurnPayload payload = 2;
}

message ChatTurnPayload {
  string role = 1;
  repeated ChatContentPart parts = 2;
}
```

CI 门禁：无对应 CI 脚本，规则仅在 `CLAUDE.md` 文字层。没有找到针对领域事件里 `PayloadJson` / JSON 字符串载荷的通用门禁。
