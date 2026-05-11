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
