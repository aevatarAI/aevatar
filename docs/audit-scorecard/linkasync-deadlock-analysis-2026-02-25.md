# `LinkAsync` 潜在死锁分析与最小改动方案（2026-02-25）

## 1. 背景与范围

本分析聚焦 Orleans 运行时下的 `LinkAsync` 调用链，目标是回答两个问题：

1. 当前实现是否存在死锁风险。
2. 在不破坏现有分层与抽象边界的前提下，最小改动如何消除风险。

涉及代码：

- `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorRuntime.cs`
- `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`
- `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`

---

## 2. 结论（先给结论）

`LinkAsync` 在“普通外部调用（非 Grain 执行上下文）”下通常不会死锁；但在“Grain 内部调用且 `parentId` 指向当前激活”的场景下，存在明显的调度死锁风险（更准确地说是请求互等直到超时）。

风险等级：**中-高**（取决于该路径在生产中的触发频率）。

---

## 3. 触发条件与死锁机理

### 3.1 触发条件

- 当前正在执行的激活为 `RuntimeActorGrain(parentId)`。
- 该激活的业务逻辑中触发 `IActorRuntime.LinkAsync(parentId, childId)`。
- `LinkAsync` 内部再次通过 Grain 调用访问同一个 `parentId` 激活（例如 `parent.IsInitializedAsync()` / `parent.AddChildAsync()`）。

### 3.2 机理（简化时序）

1. `RuntimeActorGrain(parent)` 正在处理请求（例如 workflow 触发建树）。
2. 处理过程中调用 `_runtime.LinkAsync(parentId, childId)`。
3. `LinkAsync` 再次发起对 `parent` 激活的方法调用。
4. `parent` 激活当前请求未完成，默认不可重入时无法接收该回调调用。
5. 外层等待内层，内层等待外层释放，形成互等并最终超时。

---

## 4. 现状证据（代码路径）

### 4.1 `WorkflowGAgent` 在激活处理流中调用 `LinkAsync`

`WorkflowGAgent.EnsureAgentTreeAsync()` 中创建子 actor 后立即执行：

- `_runtime.LinkAsync(Id, actor.Id)`

该调用发生在 actor 事件处理链上，`Id` 即当前 actor 标识。

### 4.2 `OrleansActorRuntime.LinkAsync` 会反向调用 parent grain

`LinkAsync` 当前执行顺序：

1. `parent.IsInitializedAsync()`
2. `child.IsInitializedAsync()`
3. `parent.AddChildAsync(childId)`
4. `child.SetParentAsync(parentId)`
5. `stream.UpsertRelayAsync(...)`

其中第 1、3 步会命中 `parent` 激活本身（当 `parentId` 是当前激活时）。

### 4.3 `RuntimeActorGrain` 未声明 reentrant

当前 `RuntimeActorGrain` 没有 `[Reentrant]` 或等效的可重入策略声明，默认调度模型下存在上述互等风险。

---

## 5. 最小改动方案（推荐）

### 5.1 方案描述

在 `OrleansActorRuntime.LinkAsync` 方法内部增加调用链重入作用域：

- 在进入 grain 调用前创建 `AllowCallChainReentrancy` 作用域；
- 作用域仅覆盖当前 `LinkAsync` 这条调用链。

设计意图：

- 不把 Orleans 依赖上移到 `Workflow.Core`。
- 不把整个 `RuntimeActorGrain` 改成全局可重入。
- 只对需要的调用链做精确放开，影响面最小。

### 5.2 建议改动点

文件：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorRuntime.cs`

1. 增加 `using Orleans.Runtime;`
2. 在 `LinkAsync` 方法开头添加：

```csharp
using var reentrancyScope = RequestContext.AllowCallChainReentrancy();
```

> 说明：命名可简化为 `_`，保留具名变量便于审查时识别目的。

### 5.3 为什么这是“最小改动”

- **改动行数少**：单文件、单方法级别。
- **不改契约**：`IActorRuntime` / `IRuntimeActorGrain` 接口保持不变。
- **不改业务语义**：仍是 `AddChild -> SetParent -> UpsertRelay` 的链路。
- **不扩散依赖**：Orleans 细节留在 Orleans 实现层。

---

## 6. 备选方案与不推荐原因

### 6.1 在 `WorkflowGAgent` 调用点开启重入

不推荐原因：会把 Orleans 运行时细节渗透到 `Workflow.Core`，违反分层边界。

### 6.2 给 `RuntimeActorGrain` 加 `[Reentrant]`

不推荐作为最小方案：影响所有请求交错行为，风险面大于问题面。

### 6.3 把 `AddChild/SetParent` 改成 out-of-band 异步命令

属于架构级改造，不是“最小改动”。

---

## 7. 验证方案（建议）

### 7.1 必做验证

1. 新增/补充一个 Orleans 集成测试，覆盖“grain 内部调用 `LinkAsync`”路径，断言在超时时间内成功返回。
2. 回归现有 runtime forwarding 相关测试，确保行为不退化。

建议命令：

- `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo`

### 7.2 观察项

- 无新增超时异常（尤其是 Orleans invocation timeout）。
- Parent/child 拓扑状态保持一致。
- 转发绑定正常落库（`ListBySourceAsync(parentId)` 可读到 child binding）。

---

## 8. 风险与后续改进

### 8.1 本次最小方案仍未覆盖的点

`LinkAsync` 仍是“多步骤写入”：

1. `parent.AddChildAsync`
2. `child.SetParentAsync`
3. `UpsertRelayAsync`

若中间失败，可能出现短暂不一致（这不是死锁问题，但属于一致性问题）。

### 8.2 后续可选增强（非本次最小改动范围）

- 增加补偿式回滚（例如 `SetParent` 失败时回滚 `AddChild`）。
- 或引入单写事实源 + 异步投影更新，避免链路内多点写入。

---

## 9. 执行清单（可直接用于实施）

1. 在 `OrleansActorRuntime.LinkAsync` 增加 `AllowCallChainReentrancy` 作用域。
2. 新增一个集成测试覆盖 grain 内部 `LinkAsync`。
3. 跑 runtime hosting 测试集并确认无超时/回归。
4. 评估是否追加“一致性补偿”任务（后续迭代）。

