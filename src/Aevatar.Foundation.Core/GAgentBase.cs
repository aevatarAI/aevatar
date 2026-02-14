// ─────────────────────────────────────────────────────────────
// GAgentBase - stateless base class for GAgent.
//
// Responsibilities:
// 1. Unified event pipeline ([EventHandler] + IEventModule interleaved by priority)
// 2. Module management APIs (RegisterModule / SetModules / manifest persistence)
// 3. Dual hook channels (virtual methods + IGAgentExecutionHook pipeline)
// 4. Publishing helpers (PublishAsync / SendToAsync)
// ─────────────────────────────────────────────────────────────

using System.Diagnostics;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.Pipeline;
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
    private volatile IEventModule[] _modules = [];
    private volatile IGAgentExecutionHook[] _hooks = [];

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

    /// <summary>Manifest persistence store injected by runtime.</summary>
    public IAgentManifestStore? ManifestStore { get; set; }

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

    /// <summary>Activates agent: restores modules and loads hooks.</summary>
    public virtual async Task ActivateAsync(CancellationToken ct = default)
    {
        using var guard = StateGuard.BeginWriteScope();
        await RestoreModulesAsync(ct);
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
    /// </summary>
    public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        using var guard = StateGuard.BeginWriteScope();
        var ctx = CreateHandlerContext();
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

    // Dual hook channels #1: virtual methods (subclasses may override)

    /// <summary>Virtual hook before handler execution. Subclasses may override.</summary>
    protected virtual Task OnEventHandlerStartAsync(
        EventEnvelope envelope, string handlerName, object? payload, CancellationToken ct)
        => Task.CompletedTask;

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

    /// <summary>Registers a dynamic event module and persists it to manifest.</summary>
    public void RegisterModule(IEventModule module)
    {
        var current = _modules;
        var next = new IEventModule[current.Length + 1];
        current.CopyTo(next, 0);
        next[current.Length] = module;
        _modules = next;
        SchedulePersistModules();
    }

    /// <summary>Replaces dynamic event modules in batch and persists them.</summary>
    public void SetModules(IEnumerable<IEventModule> modules)
    {
        _modules = modules.ToArray();
        SchedulePersistModules();
    }

    /// <summary>Gets all currently registered dynamic modules.</summary>
    public IReadOnlyList<IEventModule> GetModules() => _modules;

    // Publishing helper methods

    /// <summary>Publishes an event with direction.</summary>
    protected Task PublishAsync<TEvent>(TEvent evt,
        EventDirection direction = EventDirection.Down,
        CancellationToken ct = default) where TEvent : Google.Protobuf.IMessage =>
        EventPublisher.PublishAsync(evt, direction, ct);

    /// <summary>Sends an event to a target actor.</summary>
    protected Task SendToAsync<TEvent>(string targetActorId, TEvent evt,
        CancellationToken ct = default) where TEvent : Google.Protobuf.IMessage =>
        EventPublisher.SendToAsync(targetActorId, evt, ct);

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

    /// <summary>Restores registered modules from ManifestStore.</summary>
    private async Task RestoreModulesAsync(CancellationToken ct)
    {
        if (ManifestStore == null) return;
        var manifest = await ManifestStore.LoadAsync(Id, ct);
        if (manifest?.ModuleNames is not { Count: > 0 }) return;

        var factories = Services.GetServices<IEventModuleFactory>().ToList();
        var modules = new List<IEventModule>();
        foreach (var name in manifest.ModuleNames)
            foreach (var factory in factories)
                if (factory.TryCreate(name, out var m) && m != null)
                { modules.Add(m); break; }
        _modules = modules.ToArray();
    }

    /// <summary>Persists current module names to ManifestStore.</summary>
    private async Task PersistModulesAsync()
    {
        if (ManifestStore == null) return;
        var manifest = await ManifestStore.LoadAsync(Id) ?? new AgentManifest { AgentId = Id };
        manifest.ModuleNames = _modules.Select(m => m.Name).ToList();
        await ManifestStore.SaveAsync(Id, manifest);
    }

    private void SchedulePersistModules()
    {
        _ = PersistModulesSafeAsync();
    }

    private async Task PersistModulesSafeAsync()
    {
        try
        {
            await PersistModulesAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to persist module manifest for agent {AgentId}", Id);
        }
    }

    private EventHandlerMetadata[] GetStaticHandlers() =>
        _staticHandlers ??= EventHandlerDiscoverer.Discover(GetType());

    private EventHandlerContext CreateHandlerContext() =>
        new(this, EventPublisher, Services, Logger);

    // Null implementations

    private sealed class NullEventPublisher : IEventPublisher
    {
        public static readonly NullEventPublisher Instance = new();
        public Task PublishAsync<T>(T e, EventDirection d, CancellationToken c) where T : Google.Protobuf.IMessage => Task.CompletedTask;
        public Task SendToAsync<T>(string t, T e, CancellationToken c) where T : Google.Protobuf.IMessage => Task.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
