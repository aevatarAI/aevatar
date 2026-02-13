// ─────────────────────────────────────────────────────────────
// GAgentBase<TState> - stateful base class for GAgent.
// State + StateStore + OnStateChanged Hook
// ─────────────────────────────────────────────────────────────

using Aevatar.Persistence;
using Google.Protobuf;

namespace Aevatar;

/// <summary>
/// Stateful GAgent base class with Protobuf state storage, StateStore persistence, and lifecycle hooks.
/// </summary>
/// <typeparam name="TState">Protobuf-generated state type.</typeparam>
public abstract class GAgentBase<TState> : GAgentBase, IAgent<TState>
    where TState : class, IMessage<TState>, new()
{
    private TState _state = new();

    /// <summary>Mutable agent state, writable only in EventHandler/OnActivateAsync scopes.</summary>
    public TState State
    {
        get => _state;
        protected set { StateGuard.EnsureWritable(); _state = value; }
    }

    /// <summary>State persistence store injected by runtime.</summary>
    public IStateStore<TState>? StateStore { get; set; }

    /// <summary>Activates agent, restores state from StateStore, then calls OnActivateAsync.</summary>
    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await base.ActivateAsync(ct); // Restore modules
        using var guard = StateGuard.BeginWriteScope();
        if (StateStore != null)
        {
            var loaded = await StateStore.LoadAsync(Id, ct);
            if (loaded != null) _state = loaded;
        }
        await OnActivateAsync(ct);
    }

    /// <summary>Deactivates agent, calls OnDeactivateAsync, and persists state.</summary>
    public override async Task DeactivateAsync(CancellationToken ct = default)
    {
        await OnDeactivateAsync(ct);
        if (StateStore != null)
            await StateStore.SaveAsync(Id, _state, ct);
    }

    /// <summary>Hook invoked after state changes, useful for CQRS projection.</summary>
    protected virtual Task OnStateChangedAsync(TState state, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>Activation hook for subclass initialization.</summary>
    protected virtual Task OnActivateAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Deactivation hook for subclass cleanup.</summary>
    protected virtual Task OnDeactivateAsync(CancellationToken ct) => Task.CompletedTask;
}
