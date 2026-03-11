// ─────────────────────────────────────────────────────────────
// GAgentBase - stateless base class for GAgent.
//
// Responsibilities:
// 1. Unified event pipeline ([EventHandler] + IEventModule<IEventHandlerContext> interleaved by priority)
// 2. Module management APIs (RegisterModule / SetModules)
// 3. Dual hook channels (virtual methods + IGAgentExecutionHook pipeline)
// 4. Publishing helpers (PublishAsync / SendToAsync)
// ─────────────────────────────────────────────────────────────

using System.Diagnostics;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.Pipeline;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Core;

/// <summary>
/// Stateless GAgent base class with unified event pipeline, module management,
/// and dual hook channels (virtual methods + IGAgentExecutionHook pipeline).
/// </summary>
public abstract class GAgentBase : IAgent
{
    private EventHandlerMetadata[]? _staticHandlers;
    private volatile IEventModule<IEventHandlerContext>[] _modules = [];
    private volatile IGAgentExecutionHook[] _hooks = [];
    private EventEnvelope? _activeInboundEnvelope;

    // Identity

    /// <summary>Unique agent identifier.</summary>
    public string Id { get; private set; } = string.Empty;

    // Framework-injected dependencies (set by Runtime)

    /// <summary>Event publisher injected by actor/runtime.</summary>
    public IEventPublisher EventPublisher { get; set; } = NullEventPublisher.Instance;

    /// <summary>Logger injected by runtime.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>DI service provider injected by runtime.</summary>
    public IServiceProvider Services { get; set; } = EmptyServiceProvider.Instance;

    // IAgent implementation

    /// <summary>Returns the agent description string.</summary>
    public virtual Task<string> GetDescriptionAsync() =>
        Task.FromResult($"{GetType().Name}:{Id}");

    /// <summary>Returns all event types this agent can handle.</summary>
    public virtual Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync()
    {
        var types = GetStaticHandlers()
            .Where(h => !h.IsAllEventHandler)
            .Select(h => h.ParameterType).Distinct().ToList();
        return Task.FromResult<IReadOnlyList<Type>>(types);
    }

    /// <summary>Activates agent and loads hooks.</summary>
    public virtual async Task ActivateAsync(CancellationToken ct = default)
    {
        using var guard = StateGuard.BeginWriteScope();
        ct.ThrowIfCancellationRequested();
        LoadHooksFromDI();
    }

    /// <summary>Deactivates agent.</summary>
    public virtual Task DeactivateAsync(CancellationToken ct = default) =>
        Task.CompletedTask;

    // Unified event dispatch (with dual hook channels)

    /// <summary>
    /// Unified event dispatch entry. For each matching handler:
    /// 1. Calls virtual OnEventHandlerStartAsync (subclass extension point)
    /// 2. Calls IGAgentExecutionHook pipeline OnEventHandlerStartAsync (DI-injected hooks)
    /// 3. Executes handler
    /// 4. Calls IGAgentExecutionHook pipeline OnEventHandlerEndAsync
    /// 5. Calls virtual OnEventHandlerEndAsync
    ///
    /// Default behavior is fail-fast: handler exceptions are rethrown after hook callbacks.
    /// Subclasses can override <see cref="ShouldSuppressHandlerException"/> to opt into best-effort continuation.
    /// </summary>
    public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        using var guard = StateGuard.BeginWriteScope();
        var previousEnvelope = _activeInboundEnvelope;
        _activeInboundEnvelope = envelope;
        try
        {
            var ctx = CreateHandlerContext(envelope);
            var pipeline = EventPipelineBuilder.Build(GetStaticHandlers(), _modules, this);

            foreach (var handler in pipeline)
            {
                ct.ThrowIfCancellationRequested();
                if (!handler.CanHandle(envelope)) continue;

                var hookCtx = new GAgentExecutionHookContext
                {
                    AgentId = Id,
                    AgentType = GetType().Name,
                    EventId = envelope.Id,
                    EventType = envelope.Payload?.TypeUrl,
                    HandlerName = handler.Name,
                };

                var sw = Stopwatch.StartNew();
                Exception? error = null;
                try
                {
                    // Dual hook channels: Start
                    await OnEventHandlerStartAsync(envelope, handler.Name, null, ct);
                    await RunHooksAsync(h => h.OnEventHandlerStartAsync(hookCtx, ct), "OnEventHandlerStart");

                    await handler.HandleAsync(envelope, ctx, ct);
                }
                catch (Exception ex)
                {
                    error = ex;
                    hookCtx.Exception = ex;
                    Logger.LogError(ex, "Handler {Name} failed", handler.Name);
                    await RunHooksAsync(h => h.OnErrorAsync(hookCtx, ex, ct), "OnError");

                    if (!ShouldSuppressHandlerException(envelope, handler.Name, ex))
                        throw;
                }
                finally
                {
                    sw.Stop();
                    hookCtx.Duration = sw.Elapsed;

                    // Dual hook channels: End
                    await RunHooksAsync(h => h.OnEventHandlerEndAsync(hookCtx, ct), "OnEventHandlerEnd");
                    await OnEventHandlerEndAsync(envelope, handler.Name, null, sw.Elapsed, error, ct);
                }
            }
        }
        finally
        {
            _activeInboundEnvelope = previousEnvelope;
        }
    }

    // Dual hook channels #1: virtual methods (subclasses may override)

    /// <summary>Virtual hook before handler execution. Subclasses may override.</summary>
    protected virtual Task OnEventHandlerStartAsync(
        EventEnvelope envelope, string handlerName, object? payload, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Controls whether a handler exception should be suppressed.
    /// Default is <c>false</c> (rethrow).
    /// </summary>
    protected virtual bool ShouldSuppressHandlerException(
        EventEnvelope envelope,
        string handlerName,
        Exception exception)
    {
        _ = envelope;
        _ = handlerName;
        _ = exception;
        return false;
    }

    /// <summary>Virtual hook after handler execution. Subclasses may override.</summary>
    protected virtual Task OnEventHandlerEndAsync(
        EventEnvelope envelope, string handlerName, object? payload,
        TimeSpan duration, Exception? exception, CancellationToken ct)
        => Task.CompletedTask;

    // Dual hook channels #2: IGAgentExecutionHook pipeline (DI-injected)

    /// <summary>Registers a foundation-level hook.</summary>
    public void RegisterHook(IGAgentExecutionHook hook)
    {
        var current = _hooks;
        var next = new IGAgentExecutionHook[current.Length + 1];
        current.CopyTo(next, 0);
        next[current.Length] = hook;
        Array.Sort(next, (a, b) => a.Priority.CompareTo(b.Priority));
        _hooks = next;
    }

    /// <summary>Gets all currently registered hooks.</summary>
    public IReadOnlyList<IGAgentExecutionHook> GetHooks() => _hooks;

    // Module management APIs

    /// <summary>Registers a dynamic event module.</summary>
    public void RegisterModule(IEventModule<IEventHandlerContext> module)
    {
        var current = _modules;
        var next = new IEventModule<IEventHandlerContext>[current.Length + 1];
        current.CopyTo(next, 0);
        next[current.Length] = module;
        _modules = next;
    }

    /// <summary>Replaces dynamic event modules in batch.</summary>
    public void SetModules(IEnumerable<IEventModule<IEventHandlerContext>> modules)
    {
        _modules = modules.ToArray();
    }

    /// <summary>Gets all currently registered dynamic modules.</summary>
    public IReadOnlyList<IEventModule<IEventHandlerContext>> GetModules() => _modules;

    // Publishing helper methods

    /// <summary>Publishes an event with direction.</summary>
    protected Task PublishAsync<TEvent>(TEvent evt,
        EventDirection direction = EventDirection.Down,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null) where TEvent : Google.Protobuf.IMessage =>
        EventPublisher.PublishAsync(evt, direction, ct, _activeInboundEnvelope, options);

    /// <summary>Sends an event to a target actor.</summary>
    protected Task SendToAsync<TEvent>(string targetActorId, TEvent evt,
        CancellationToken ct = default,
        EventEnvelopePublishOptions? options = null) where TEvent : Google.Protobuf.IMessage =>
        EventPublisher.SendToAsync(targetActorId, evt, ct, _activeInboundEnvelope, options);

    protected Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dueTime, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(evt);

        return Services.GetRequiredService<IActorRuntimeCallbackScheduler>()
            .ScheduleTimeoutAsync(
                new RuntimeCallbackTimeoutRequest
                {
                    ActorId = Id,
                    CallbackId = callbackId,
                    TriggerEnvelope = SelfEventEnvelopeFactory.Create(Id, evt, _activeInboundEnvelope, options),
                    DueTime = dueTime,
                },
                ct);
    }

    protected Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
        string callbackId,
        TimeSpan dueTime,
        TimeSpan period,
        IMessage evt,
        EventEnvelopePublishOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dueTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(evt);

        return Services.GetRequiredService<IActorRuntimeCallbackScheduler>()
            .ScheduleTimerAsync(
                new RuntimeCallbackTimerRequest
                {
                    ActorId = Id,
                    CallbackId = callbackId,
                    TriggerEnvelope = SelfEventEnvelopeFactory.Create(Id, evt, _activeInboundEnvelope, options),
                    DueTime = dueTime,
                    Period = period,
                },
                ct);
    }

    protected Task CancelDurableCallbackAsync(
        RuntimeCallbackLease lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return Services.GetRequiredService<IActorRuntimeCallbackScheduler>()
            .CancelAsync(lease, ct);
    }

    // Internal methods

    /// <summary>Sets agent ID (called by runtime).</summary>
    internal void SetId(string id) => Id = id;

    /// <summary>Generates and sets a default ID.</summary>
    protected void InitializeId() => Id = AgentId.New(GetType());

    /// <summary>Loads registered IGAgentExecutionHook instances from DI.</summary>
    private void LoadHooksFromDI()
    {
        var hooks = Services.GetServices<IGAgentExecutionHook>().ToList();
        if (hooks.Count > 0)
        {
            hooks.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            _hooks = hooks.ToArray();
        }
    }

    /// <summary>Runs hook pipeline in best-effort mode.</summary>
    private async Task RunHooksAsync(Func<IGAgentExecutionHook, Task> action, string phase)
    {
        foreach (var hook in _hooks)
        {
            try { await action(hook); }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Hook {Hook} failed during phase {Phase} (best-effort)", hook.Name, phase);
            }
        }
    }

    private EventHandlerMetadata[] GetStaticHandlers() =>
        _staticHandlers ??= EventHandlerDiscoverer.Discover(GetType());

    private EventHandlerContext CreateHandlerContext(EventEnvelope envelope) =>
        new(
            this,
            EventPublisher,
            Services.GetRequiredService<IActorRuntimeCallbackScheduler>(),
            Services,
            Logger,
            envelope);

    // Null implementations

    private sealed class NullEventPublisher : IEventPublisher
    {
        public static readonly NullEventPublisher Instance = new();
        public Task PublishAsync<T>(T e, EventDirection d, CancellationToken c, EventEnvelope? sourceEnvelope, EventEnvelopePublishOptions? options) where T : Google.Protobuf.IMessage => Task.CompletedTask;
        public Task SendToAsync<T>(string t, T e, CancellationToken c, EventEnvelope? sourceEnvelope, EventEnvelopePublishOptions? options) where T : Google.Protobuf.IMessage => Task.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
