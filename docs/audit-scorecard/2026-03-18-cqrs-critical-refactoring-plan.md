# CQRS Framework Critical Refactoring Plan

> **日期**：2026-03-18
> **基于**：[2026-03-18-cqrs-framework-software-engineering-audit.md](./2026-03-18-cqrs-framework-software-engineering-audit.md)
> **目标**：消除审计发现的 CRITICAL / HIGH 级问题，保持 `build + test + CI guard` 全通过

---

## Phase 1：CRITICAL 级修复

### R-1: 消除 DefaultDetachedCommandDispatchService Fire-and-Forget

**问题**：`Task.Run` 后台线程监控 live sink、解析 completion、执行 cleanup，违反 Actor 哲学。

**当前代码**（`src/Aevatar.CQRS.Core/Commands/DefaultDetachedCommandDispatchService.cs:48-131`）：
```csharp
private void StartDetachedDrain(TTarget target, TReceipt receipt)
{
    _ = Task.Run(async () =>
    {
        // 后台线程：PumpAsync → DurableCompletion → ReleaseAfterInteraction
        // 全部使用 CancellationToken.None
    }, CancellationToken.None);
}
```

**重构方案：纯 Dispatch 语义 + Projection 完成态物化**

Detached dispatch 的本意是"只保证投递，不等待完成"。当前设计在后台偷偷监控完成态并执行 cleanup，违反了这一语义。改为：

1. **删除 `StartDetachedDrain` 方法和所有后台线程逻辑**。
2. **DispatchAsync 只负责 dispatch + 返回 receipt**，不创建 live sink、不监控 stream。
3. **Cleanup 责任迁移到 target actor 自身**：target actor 在处理完命令后通过 committed event 通知完成，projection 负责物化完成态。
4. **如需 detached cleanup**：由 target 的 `ICommandDispatchCleanupAware` 在 dispatch 成功后异步清理，而不是另起线程。

**具体改动**：

**文件 1：`src/Aevatar.CQRS.Core/Commands/DefaultDetachedCommandDispatchService.cs`**

重写为纯 dispatch 服务：

```csharp
public sealed class DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError>
    : ICommandDispatchService<TCommand, TReceipt, TError>
    where TTarget : class, ICommandDispatchTarget
{
    private readonly ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> _dispatchPipeline;

    public DefaultDetachedCommandDispatchService(
        ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> dispatchPipeline)
    {
        _dispatchPipeline = dispatchPipeline
            ?? throw new ArgumentNullException(nameof(dispatchPipeline));
    }

    public async Task<CommandDispatchResult<TReceipt, TError>> DispatchAsync(
        TCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var dispatch = await _dispatchPipeline.DispatchAsync(command, ct);
        if (!dispatch.Succeeded || dispatch.Target == null)
            return CommandDispatchResult<TReceipt, TError>.Failure(dispatch.Error);
        return CommandDispatchResult<TReceipt, TError>.Success(dispatch.Target.Receipt);
    }
}
```

**关键变化**：
- 泛型参数从 7 个降到 4 个（删除 `TEvent`、`TFrame`、`TCompletion`）。
- 不再依赖 `IEventOutputStream`、`ICommandCompletionPolicy`、`ICommandDurableCompletionResolver`。
- 不再需要 `TTarget : ICommandEventTarget<TEvent>, ICommandInteractionCleanupTarget<TReceipt, TCompletion>` 约束。
- 完成态通过 projection pipeline 异步物化到 readmodel，调用方通过 query 或 SSE/WS 观察。

**文件 2：调用方适配**

当前只有 Workflow 使用 detached dispatch：
- `src/workflow/Aevatar.Workflow.Projection/DependencyInjection/ServiceCollectionExtensions.cs`

调整 DI 注册，去除 `TEvent`/`TFrame`/`TCompletion` 参数。Workflow 的完成态已通过 `WorkflowExecutionCurrentStateProjector` 物化到 readmodel，无需 detached drain 二次推导。

**验证**：
```bash
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
bash tools/ci/architecture_guards.sh
```

---

### R-2: 合并 Session / Materialization 激活释放接口

**问题**：4 个接口（2 activation + 2 release）签名完全相同，4 个实现类逻辑几乎一致，仅 `ProjectionRuntimeMode` 和 request DTO 不同。

**当前结构**：
```
IProjectionSessionActivationService<TLease>       ← EnsureAsync(SessionStartRequest)
IProjectionMaterializationActivationService<TLease> ← EnsureAsync(MaterializationStartRequest)
IProjectionSessionReleaseService<TLease>            ← ReleaseIfIdleAsync(TLease)
IProjectionMaterializationReleaseService<TLease>    ← ReleaseIfIdleAsync(TLease)
```

**重构方案：统一为 2 个接口 + 1 个统一 request + Mode 枚举区分**

#### Step 1：定义统一 request

**新文件：`src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/ProjectionScopeStartRequest.cs`**

```csharp
namespace Aevatar.CQRS.Projection.Core.Abstractions;

public sealed record ProjectionScopeStartRequest(
    string RootActorId,
    string ProjectionKind,
    ProjectionRuntimeMode Mode,
    string SessionId = "");
```

#### Step 2：统一接口

**修改文件：`src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/`**

删除 4 个接口，替换为 2 个：

```csharp
// IProjectionScopeActivationService.cs
public interface IProjectionScopeActivationService<TLease>
    where TLease : class, IProjectionRuntimeLease
{
    Task<TLease> EnsureAsync(ProjectionScopeStartRequest request, CancellationToken ct = default);
}

// IProjectionScopeReleaseService.cs
public interface IProjectionScopeReleaseService<TLease>
    where TLease : class, IProjectionRuntimeLease
{
    Task ReleaseIfIdleAsync(TLease lease, CancellationToken ct = default);
}
```

#### Step 3：统一实现

**删除**：
- `ProjectionSessionScopeActivationService.cs`
- `ProjectionMaterializationScopeActivationService.cs`

**替换为**：`ProjectionScopeActivationService.cs`

```csharp
public sealed class ProjectionScopeActivationService<TLease, TContext, TScopeAgent>
    : IProjectionScopeActivationService<TLease>
    where TLease : class, IProjectionRuntimeLease
    where TContext : class, IProjectionMaterializationContext
    where TScopeAgent : IAgent
{
    private readonly ProjectionScopeActorRuntime<TScopeAgent> _scopeRuntime;
    private readonly Func<ProjectionScopeStartRequest, TContext> _contextFactory;
    private readonly Func<ProjectionRuntimeScopeKey, TContext, TLease> _leaseFactory;

    public ProjectionScopeActivationService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        Func<ProjectionScopeStartRequest, TContext> contextFactory,
        Func<ProjectionRuntimeScopeKey, TContext, TLease> leaseFactory,
        IAgentTypeVerifier? agentTypeVerifier = null)
    {
        _scopeRuntime = new ProjectionScopeActorRuntime<TScopeAgent>(runtime, dispatchPort, agentTypeVerifier);
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _leaseFactory = leaseFactory ?? throw new ArgumentNullException(nameof(leaseFactory));
    }

    public async Task<TLease> EnsureAsync(ProjectionScopeStartRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var context = _contextFactory(request);
        var scopeKey = new ProjectionRuntimeScopeKey(
            context.RootActorId,
            context.ProjectionKind,
            request.Mode,
            request.SessionId);

        await _scopeRuntime.EnsureExistsAsync(scopeKey, ct).ConfigureAwait(false);
        await _scopeRuntime.DispatchAsync(
            scopeKey,
            new EnsureProjectionScopeCommand
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                SessionId = scopeKey.SessionId,
                Mode = ProjectionScopeModeMapper.ToProto(scopeKey.Mode),
            },
            ct).ConfigureAwait(false);

        return _leaseFactory(scopeKey, context);
    }
}
```

**删除**：
- `ProjectionSessionScopeReleaseService.cs`
- `ProjectionMaterializationScopeReleaseService.cs`

**替换为**：`ProjectionScopeReleaseService.cs`

```csharp
public sealed class ProjectionScopeReleaseService<TLease, TScopeAgent>
    : IProjectionScopeReleaseService<TLease>
    where TLease : class, IProjectionRuntimeLease
    where TScopeAgent : IAgent
{
    private readonly ProjectionScopeActorRuntime<TScopeAgent> _scopeRuntime;
    private readonly Func<TLease, ProjectionRuntimeScopeKey> _scopeKeyAccessor;

    public ProjectionScopeReleaseService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        Func<TLease, ProjectionRuntimeScopeKey> scopeKeyAccessor,
        IAgentTypeVerifier? agentTypeVerifier = null)
    {
        _scopeRuntime = new ProjectionScopeActorRuntime<TScopeAgent>(runtime, dispatchPort, agentTypeVerifier);
        _scopeKeyAccessor = scopeKeyAccessor ?? throw new ArgumentNullException(nameof(scopeKeyAccessor));
    }

    public async Task ReleaseIfIdleAsync(TLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();

        var scopeKey = _scopeKeyAccessor(lease);
        if (!await _scopeRuntime.ExistsAsync(scopeKey, ct).ConfigureAwait(false))
            return;

        await _scopeRuntime.DispatchAsync(
            scopeKey,
            new ReleaseProjectionScopeCommand
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                SessionId = scopeKey.SessionId,
                Mode = ProjectionScopeModeMapper.ToProto(scopeKey.Mode),
            },
            ct).ConfigureAwait(false);
    }
}
```

#### Step 4：统一 DI 注册

**合并** `ProjectionMaterializationRuntimeRegistration.cs` 和 `EventSinkProjectionRuntimeRegistration.cs` 为单一入口：

```csharp
public static class ProjectionScopeRuntimeRegistration
{
    public static IServiceCollection AddProjectionScopeRuntime<TContext, TRuntimeLease, TScopeAgent>(
        this IServiceCollection services,
        ProjectionRuntimeMode mode,
        Func<ProjectionRuntimeScopeKey, TContext> contextFactory,
        Func<TContext, TRuntimeLease> leaseFactory)
        where TContext : class, IProjectionMaterializationContext
        where TRuntimeLease : class, IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
        where TScopeAgent : IAgent
    {
        services.TryAddSingleton<IProjectionFailureReplayService, ProjectionFailureReplayService>();
        services.TryAddSingleton<IProjectionFailureAlertSink, LoggingProjectionFailureAlertSink>();
        services.TryAddSingleton<IProjectionScopeContextFactory<TContext>>(
            _ => new ProjectionScopeContextFactory<TContext>(contextFactory));
        services.TryAddSingleton<IProjectionScopeActivationService<TRuntimeLease>>(sp =>
            new ProjectionScopeActivationService<TRuntimeLease, TContext, TScopeAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                request => contextFactory(new ProjectionRuntimeScopeKey(
                    request.RootActorId, request.ProjectionKind, mode, request.SessionId)),
                (_, context) => leaseFactory(context),
                sp.GetService<IAgentTypeVerifier>()));
        services.TryAddSingleton<IProjectionScopeReleaseService<TRuntimeLease>>(sp =>
            new ProjectionScopeReleaseService<TRuntimeLease, TScopeAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                lease => new ProjectionRuntimeScopeKey(
                    lease.Context.RootActorId, lease.Context.ProjectionKind, mode,
                    lease.Context is IProjectionSessionContext sc ? sc.SessionId : ""),
                sp.GetService<IAgentTypeVerifier>()));
        return services;
    }
}
```

#### Step 5：适配消费方

**影响的文件**（替换接口引用）：

| 文件 | 变更 |
|------|------|
| `MaterializationProjectionPortBase.cs` | `IProjectionMaterializationActivationService` → `IProjectionScopeActivationService` |
| `EventSinkProjectionLifecyclePortBase.cs` | `IProjectionSessionActivationService` → `IProjectionScopeActivationService` |
| `WorkflowExecutionProjectionPort.cs` | 同上 |
| `WorkflowExecutionMaterializationPort.cs` | 同上 |
| `ScriptAuthorityProjectionPort.cs` | 同上 |
| `ScriptExecutionProjectionPort.cs` | 同上 |
| `ScriptEvolutionProjectionPort.cs` | 同上 |
| `ScriptExecutionReadModelPort.cs` | 同上 |
| `ServiceProjectionPortBase.cs` | 同上 |
| `ServiceConfigurationProjectionPort.cs` | 同上 |
| 所有 `ServiceCollectionExtensions.cs` 中的 DI 注册 | 替换注册方法名 |

**删除的文件**（6 个）：
- `IProjectionSessionActivationService.cs`
- `IProjectionMaterializationActivationService.cs`
- `IProjectionSessionReleaseService.cs`
- `IProjectionMaterializationReleaseService.cs`
- `ProjectionSessionScopeActivationService.cs` / `ProjectionMaterializationScopeActivationService.cs`
- `ProjectionSessionScopeReleaseService.cs` / `ProjectionMaterializationScopeReleaseService.cs`

**净减少**：~8 个文件，~200 行代码。

---

### R-3: 修复 Cleanup 异常吞没

**问题**：`DefaultCommandInteractionService.cs:125` 中 `catch (Exception ex) when (executionException != null)` 吞没 cleanup 异常。

**当前代码**（`src/Aevatar.CQRS.Core/Interactions/DefaultCommandInteractionService.cs:106-134`）：
```csharp
catch (Exception ex)
{
    executionException = ex;
    throw;
}
finally
{
    try
    {
        // cleanup: durable completion + release
    }
    catch (Exception ex) when (executionException != null)
    {
        _logger.LogWarning(ex, "..."); // 吞没
    }
}
```

**重构方案**：

```csharp
finally
{
    try
    {
        if (!observedCompleted && !durableCompletionAttempted)
        {
            durableCompletion = await _durableCompletionResolver.ResolveAsync(
                receipt, CancellationToken.None);
        }

        await target.ReleaseAfterInteractionAsync(
            receipt,
            new CommandInteractionCleanupContext<TCompletion>(
                observedCompleted, observedCompletion, durableCompletion),
            CancellationToken.None);
    }
    catch (Exception cleanupException)
    {
        if (executionException != null)
        {
            _logger.LogWarning(
                cleanupException,
                "Command interaction cleanup failed after execution failure. command={CommandType}",
                typeof(TCommand).FullName);
            // execution exception 优先抛出，cleanup 异常已记录
        }
        else
        {
            throw; // 执行成功但 cleanup 失败：让调用方知道
        }
    }
}
```

**关键变化**：
- 执行成功 + cleanup 失败：**抛出 cleanup 异常**（而不是让它作为未观察异常逃逸）。
- 执行失败 + cleanup 失败：**记录 cleanup 异常 + 抛出 execution 异常**（保持原语义但显式记录）。

---

## Phase 2：HIGH 级修复

### R-4: 拆分 ProjectionScopeGAgentBase

**问题**：255 行基类承载 7 个职责。

**重构方案：提取 2 个职责对象**

#### Step 1：提取 ProjectionScopeFailureTracker

**新文件：`src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeFailureTracker.cs`**

```csharp
internal sealed class ProjectionScopeFailureTracker
{
    private readonly Func<Google.Protobuf.IMessage, Task> _persistAsync;
    private readonly Func<IProjectionFailureAlertSink?> _alertSinkResolver;
    private readonly Func<ProjectionRuntimeScopeKey> _scopeKeyResolver;
    private readonly Func<int> _failureCountAccessor;

    public ProjectionScopeFailureTracker(
        Func<Google.Protobuf.IMessage, Task> persistAsync,
        Func<IProjectionFailureAlertSink?> alertSinkResolver,
        Func<ProjectionRuntimeScopeKey> scopeKeyResolver,
        Func<int> failureCountAccessor)
    {
        _persistAsync = persistAsync;
        _alertSinkResolver = alertSinkResolver;
        _scopeKeyResolver = scopeKeyResolver;
        _failureCountAccessor = failureCountAccessor;
    }

    public async ValueTask RecordAsync(
        string stage, string eventId, string eventType,
        long sourceVersion, string reason, EventEnvelope envelope,
        ILogger logger)
    {
        var evt = ProjectionScopeFailureLog.BuildFailureEvent(
            stage, eventId, eventType, sourceVersion, reason, envelope);
        await _persistAsync(evt);

        var alertSink = _alertSinkResolver();
        if (alertSink == null) return;

        try
        {
            await alertSink.PublishAsync(
                new ProjectionFailureAlert(
                    _scopeKeyResolver(),
                    evt.FailureId, stage, eventId, eventType, sourceVersion, reason,
                    Math.Min(ProjectionFailureRetentionPolicy.DefaultMaxRetainedFailures,
                        _failureCountAccessor() + 1),
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Projection failure alert publishing failed.");
        }
    }

    public async Task ReplayAsync(
        ProjectionScopeState state,
        int maxItems,
        Func<EventEnvelope, CancellationToken, Task<ProjectionScopeDispatchResult>> dispatchAsync,
        ILogger logger)
    {
        var failures = ProjectionScopeFailureLog.GetPendingFailures(state, maxItems);
        foreach (var failure in failures)
        {
            if (failure.Envelope == null) continue;
            try
            {
                var result = await dispatchAsync(failure.Envelope, CancellationToken.None);
                if (result.Handled)
                    await _persistAsync(ProjectionScopeFailureLog.BuildReplayResultEvent(failure.FailureId, true));
            }
            catch (Exception ex)
            {
                await _persistAsync(ProjectionScopeFailureLog.BuildReplayResultEvent(failure.FailureId, false, ex.Message));
            }
        }
    }
}
```

#### Step 2：简化 ProjectionScopeGAgentBase

从 ~255 行缩减到 ~150 行，只保留：
- Actor 生命周期（`OnActivateAsync` / `OnDeactivateAsync`）
- Scope 命令处理（`HandleEnsureAsync` / `HandleReleaseAsync`）
- 观察信号转发与分发（`HandleObservationAsync` / `DispatchObservationAsync`）
- 水位推进

失败记录、回放、告警委派给 `ProjectionScopeFailureTracker`。

#### Step 3：构造注入 failure tracker

在 `OnActivateAsync` 中创建 tracker 实例并传入所需委托。

---

### R-5: EventSinkProjectionRuntimeLeaseBase 去锁化

**问题**：`lock` + `List<LiveSinkSubscription>` 违反 Actor 模型。

**当前代码**（`src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionRuntimeLeaseBase.cs`）：
```csharp
private readonly object _liveSinkGate = new();
private readonly List<LiveSinkSubscription> _liveSinkSubscriptions = [];

public IAsyncDisposable? AttachOrReplaceLiveSinkSubscription(IEventSink<TEvent> sink, IAsyncDisposable subscription)
{
    lock (_liveSinkGate) { ... }
}
```

**重构方案**：

Live sink subscription 的管理应该发生在调用方（`EventSinkProjectionLifecyclePortBase`）的方法调用链内，本身已是顺序执行。Lease 不应持有可变状态列表。

**方案 A：将 subscription 管理移到 Port 层**

Port 已经有 `AttachLiveSinkAsync` / `DetachLiveSinkAsync` 方法。改为 Port 自身持有 subscription 映射（Port 是 per-module singleton，attach/detach 由 command target 顺序调用）。

```csharp
// EventSinkProjectionLifecyclePortBase.cs 新增
private readonly ConcurrentDictionary<string, IAsyncDisposable> _activeSinkSubscriptions = new();
```

Lease 仅保留 `RootEntityId` + `Context`，不持有 mutable state。

**删除**：`EventSinkProjectionRuntimeLeaseBase<TEvent>` 中的 `_liveSinkGate`、`_liveSinkSubscriptions`、`AttachOrReplaceLiveSinkSubscription`、`DetachLiveSinkSubscription`。

**影响的文件**：
- `EventSinkProjectionLifecyclePortBase.cs`：改为自己管理 subscription
- 3 个具体 lease 类（`WorkflowExecutionRuntimeLease`、`ScriptExecutionRuntimeLease`、`ScriptEvolutionRuntimeLease`）：只继承 `ProjectionRuntimeLeaseBase`

---

### R-6: 合并 Port 基类

**问题**：`MaterializationProjectionPortBase` 和 `EventSinkProjectionLifecyclePortBase` 的 activation/release 逻辑完全一致。

**重构方案**：

在 R-2 合并接口后，两个基类的 activation/release 部分已经统一。进一步合并为：

```csharp
public abstract class ProjectionPortBase<TRuntimeLease>
    where TRuntimeLease : class, IProjectionRuntimeLease
{
    private readonly Func<bool> _projectionEnabledAccessor;
    private readonly IProjectionScopeActivationService<TRuntimeLease> _activationService;
    private readonly IProjectionScopeReleaseService<TRuntimeLease>? _releaseService;

    public bool ProjectionEnabled => _projectionEnabledAccessor();

    protected async Task<TRuntimeLease?> EnsureProjectionAsync(
        ProjectionScopeStartRequest request, CancellationToken ct = default)
    {
        if (!ProjectionEnabled || request == null || string.IsNullOrWhiteSpace(request.RootActorId))
            return null;
        return await _activationService.EnsureAsync(request, ct);
    }

    protected Task ReleaseProjectionAsync(TRuntimeLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!ProjectionEnabled || _releaseService == null) return Task.CompletedTask;
        return _releaseService.ReleaseIfIdleAsync(lease, ct);
    }
}
```

`EventSinkProjectionLifecyclePortBase` 改为继承 `ProjectionPortBase` 并扩展 live sink 管理。`MaterializationProjectionPortBase` 直接删除，消费方改为继承 `ProjectionPortBase`。

**删除**：`MaterializationProjectionPortBase.cs`
**影响**：所有 ~8 个继承 `MaterializationProjectionPortBase` 的 port 改为继承 `ProjectionPortBase`。

---

### R-7: 评估 ProjectionStoreDispatcher Multi-Binding

**问题**：176 行的 multi-binding 重试+补偿框架，实际只有 1 个 binding 实现。

**重构方案（保守方案，保留扩展点但简化）**：

当前 `ProjectionStoreDispatcher` 的核心价值在于：
1. 启动时验证 binding 可用性并日志输出
2. 提供统一 `IProjectionWriteDispatcher<TReadModel>` 接口

**简化**：删除 multi-binding 补偿逻辑（`CompensateAsync`）和重试逻辑（`UpsertWithRetryAsync`），保留单 binding 直通：

```csharp
public sealed class ProjectionStoreDispatcher<TReadModel>
    : IProjectionWriteDispatcher<TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    private readonly IProjectionWriteSink<TReadModel> _binding;

    public ProjectionStoreDispatcher(IEnumerable<IProjectionWriteSink<TReadModel>> bindings, ...)
    {
        var enabled = bindings.Where(b => b.IsEnabled).ToList();
        if (enabled.Count == 0) throw ...;
        if (enabled.Count > 1)
            throw new InvalidOperationException(
                $"Multiple bindings for '{typeof(TReadModel).FullName}' not supported. Found: {string.Join(", ", enabled.Select(b => b.SinkName))}");
        _binding = enabled[0];
    }

    public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        return _binding.UpsertAsync(readModel, ct);
    }
}
```

**净减少**：~120 行。如果未来真正需要 multi-binding，再加回来（YAGNI）。

---

## Phase 3：MEDIUM 级修复

### R-8: 删除 ProjectionScopeContextFactory 空转发

**问题**：28 行类仅包装一个 `Func<ProjectionRuntimeScopeKey, TContext>`。

**重构方案**：

删除 `IProjectionScopeContextFactory<TContext>` 接口和 `ProjectionScopeContextFactory<TContext>` 实现。

在 DI 中直接注册 `Func<ProjectionRuntimeScopeKey, TContext>`：

```csharp
services.TryAddSingleton<Func<ProjectionRuntimeScopeKey, TContext>>(_ => contextFactory);
```

在 `ProjectionScopeGAgentBase` 中：

```csharp
private TContext ResolveScopeContext()
{
    var factory = Services.GetRequiredService<Func<ProjectionRuntimeScopeKey, TContext>>();
    return factory(BuildScopeKey());
}
```

**删除的文件**：
- `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/IProjectionScopeContextFactory.cs`
- `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeContextFactory.cs`

---

### R-9: 清理 ProjectionScopeDispatchResult 冗余字段

**问题**：`LastSuccessfulVersion` 始终等于 `LastObservedVersion`。

**重构方案**：

```csharp
public readonly record struct ProjectionScopeDispatchResult(
    bool Handled,
    long LastObservedVersion,
    string EventType)
{
    public static ProjectionScopeDispatchResult Skip(string eventType = "") =>
        new(false, 0, eventType);

    public static ProjectionScopeDispatchResult Success(long observedVersion, string eventType) =>
        new(true, observedVersion, eventType);
}
```

**影响**：
- `ProjectionScopeGAgentBase.DispatchObservationAsync`：`WatermarkAdvancedEvent` 改用 `result.LastObservedVersion` 作为 `LastSuccessfulVersion`。
- `ProjectionMaterializationScopeGAgentBase.ProcessObservationCoreAsync`：`Success()` 调用去除重复参数。
- `ProjectionSessionScopeGAgentBase.ProcessObservationCoreAsync`：同上。

---

### R-10: ProjectionScopeModeMapper 使用 Protobuf 枚举值

**问题**：硬编码魔数 1 和 2。

**重构方案**：

```csharp
internal static class ProjectionScopeModeMapper
{
    public static ProjectionScopeMode ToProto(ProjectionRuntimeMode mode) =>
        mode switch
        {
            ProjectionRuntimeMode.DurableMaterialization => ProjectionScopeMode.DurableMaterialization,
            ProjectionRuntimeMode.SessionObservation => ProjectionScopeMode.SessionObservation,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

    public static ProjectionRuntimeMode ToRuntime(ProjectionScopeMode mode) =>
        mode switch
        {
            ProjectionScopeMode.DurableMaterialization => ProjectionRuntimeMode.DurableMaterialization,
            ProjectionScopeMode.SessionObservation => ProjectionRuntimeMode.SessionObservation,
            _ => ProjectionRuntimeMode.SessionObservation,
        };
}
```

需同步确认 `projection_scope_messages.proto` 中 `ProjectionScopeMode` 枚举值命名。如果枚举值名称不匹配，先更新 proto 定义。

---

## Phase 4：CancellationToken 修复

### R-11: Cleanup 路径传播 CancellationToken

**问题**：7 处 cleanup 使用 `CancellationToken.None`。

**重构原则**：
- `finally` 块中的 cleanup 可以使用 `CancellationToken.None`（这是正确的——cleanup 应该尝试完成，即使请求被取消）。
- 但 cleanup **之前**的操作（如 `DurableCompletionResolver`）应使用关联 token 或 `CancellationTokenSource.CreateLinkedTokenSource` + 合理超时。

**具体改动**（`DefaultCommandInteractionService.cs`）：

```csharp
finally
{
    try
    {
        if (!observedCompleted && !durableCompletionAttempted)
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            durableCompletion = await _durableCompletionResolver.ResolveAsync(
                receipt, cleanupCts.Token);
        }

        await target.ReleaseAfterInteractionAsync(
            receipt,
            new CommandInteractionCleanupContext<TCompletion>(...),
            CancellationToken.None); // cleanup 本身仍使用 None
    }
    catch (...) { ... }
}
```

---

## 影响范围汇总

```
Phase 1 (CRITICAL)
├── R-1: 2 文件改动，净减 ~80 行
├── R-2: 10 文件删除 + 15 文件适配，净减 ~200 行
└── R-3: 1 文件改动，净增 ~5 行

Phase 2 (HIGH)
├── R-4: 1 新文件 + 1 文件重构，净增 ~20 行（但职责分离）
├── R-5: 3 文件改动，净减 ~40 行
├── R-6: 1 文件删除 + 8 文件适配，净减 ~30 行
└── R-7: 1 文件重构，净减 ~120 行

Phase 3 (MEDIUM)
├── R-8: 2 文件删除，净减 ~40 行
├── R-9: 3 文件改动，净减 ~5 行
└── R-10: 1 文件改动，净变 0 行

Phase 4
└── R-11: 2 文件改动，净增 ~5 行

总计：~10 文件删除，~30 文件修改，净减 ~490 行
```

---

## 验证清单

每个 Phase 完成后必须全部通过：

```bash
# 编译
dotnet build aevatar.slnx --nologo

# 全量测试
dotnet test aevatar.slnx --nologo

# CI 门禁
bash tools/ci/architecture_guards.sh
bash tools/ci/test_stability_guards.sh
bash tools/ci/projection_route_mapping_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/solution_split_guards.sh
bash tools/ci/solution_split_test_guards.sh
```

---

## 分支策略

每个 Phase 独立分支，按 CLAUDE.md 分支命名规范：

```
refactor/2026-03-18_cqrs-eliminate-fire-and-forget       (R-1)
refactor/2026-03-18_cqrs-unify-activation-release        (R-2 + R-3)
refactor/2026-03-18_cqrs-scope-agent-srp                 (R-4 + R-5 + R-6 + R-7)
refactor/2026-03-18_cqrs-cleanup-medium                  (R-8 + R-9 + R-10 + R-11)
```
