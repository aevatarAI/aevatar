// ─────────────────────────────────────────────────────────────
// GAgentBase<TState, TConfig> - configurable GAgent base class.
// Runtime class defaults + instance overrides are applied by agent events/state.
// ─────────────────────────────────────────────────────────────

using Google.Protobuf;

namespace Aevatar.Foundation.Core;

/// <summary>
/// Configurable GAgent base class.
/// </summary>
/// <typeparam name="TState">Protobuf-generated state type.</typeparam>
/// <typeparam name="TConfig">Configuration type with a parameterless constructor.</typeparam>
public abstract class GAgentBase<TState, TConfig> : GAgentBase<TState>
    where TState : class, IMessage<TState>, new()
    where TConfig : class, new()
{
    /// <summary>Agent config, set via ConfigureAsync or ActivateAsync.</summary>
    public TConfig Config { get; private set; } = new();

    /// <summary>Updates and persists config, then calls OnConfigChangedAsync.</summary>
    public async Task ConfigureAsync(TConfig config, CancellationToken ct = default)
    {
        Config = config;
        await OnConfigChangedAsync(config, ct);
    }

    /// <summary>Hook triggered after config changes. Subclasses may override.</summary>
    protected virtual Task OnConfigChangedAsync(TConfig config, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>Activates agent.</summary>
    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        await base.ActivateAsync(ct);
    }
}
