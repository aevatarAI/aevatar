// ─────────────────────────────────────────────────────────────
// GAgentBase<TState, TConfig> - configurable GAgent base class.
// Runtime class defaults + state overrides => effective config.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Core.Configurations;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

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
    private IAgentClassDefaultsProvider<TConfig>? _classDefaultsProvider;
    private long _appliedClassDefaultsVersion = long.MinValue;

    /// <summary>Current effective config (class defaults merged with state overrides).</summary>
    public TConfig Config { get; private set; } = new();

    /// <summary>Current class defaults version that produced <see cref="Config"/>.</summary>
    protected long AppliedClassDefaultsVersion => _appliedClassDefaultsVersion;

    /// <summary>Hook triggered after config changes. Subclasses may override.</summary>
    protected virtual Task OnConfigChangedAsync(TConfig config, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>
    /// Hook executed after config recompute for state-change path.
    /// </summary>
    protected virtual Task OnStateChangedAfterConfigAppliedAsync(TState state, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>
    /// Merges class defaults and state into effective config.
    /// </summary>
    protected abstract TConfig MergeConfig(TConfig classDefaults, TState state);

    /// <summary>
    /// Recomputes effective config from current state and latest class defaults.
    /// </summary>
    protected Task RecomputeEffectiveConfigAsync(CancellationToken ct = default) =>
        RecomputeEffectiveConfigAsync(State, ct);

    protected sealed override async Task OnStateChangedAsync(TState state, CancellationToken ct)
    {
        await RecomputeEffectiveConfigAsync(state, ct);
        await OnStateChangedAfterConfigAppliedAsync(state, ct);
    }

    protected override async Task OnEventHandlerStartAsync(
        EventEnvelope envelope,
        string handlerName,
        object? payload,
        CancellationToken ct)
    {
        await EnsureClassDefaultsVersionCurrentAsync(ct);
        await base.OnEventHandlerStartAsync(envelope, handlerName, payload, ct);
    }

    private async Task EnsureClassDefaultsVersionCurrentAsync(CancellationToken ct)
    {
        var classDefaults = await ResolveClassDefaultsAsync(ct);
        if (classDefaults.Version == _appliedClassDefaultsVersion)
            return;

        await RecomputeEffectiveConfigAsync(State, ct, classDefaults);
    }

    private async Task RecomputeEffectiveConfigAsync(
        TState state,
        CancellationToken ct,
        AgentClassDefaultsSnapshot<TConfig>? classDefaultsSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var classDefaults = classDefaultsSnapshot ?? await ResolveClassDefaultsAsync(ct);
        var merged = MergeConfig(classDefaults.Defaults, state);
        if (merged == null)
        {
            throw new InvalidOperationException(
                $"{GetType().FullName} merge returned null config. " +
                $"MergeConfig must return a non-null effective config.");
        }

        await ApplyEffectiveConfigAsync(merged, classDefaults.Version, ct);
    }

    private async Task ApplyEffectiveConfigAsync(
        TConfig config,
        long classDefaultsVersion,
        CancellationToken ct)
    {
        Config = config;
        _appliedClassDefaultsVersion = classDefaultsVersion;
        await OnConfigChangedAsync(config, ct);
    }

    private ValueTask<AgentClassDefaultsSnapshot<TConfig>> ResolveClassDefaultsAsync(CancellationToken ct)
    {
        _classDefaultsProvider ??= Services.GetService<IAgentClassDefaultsProvider<TConfig>>()
            ?? NullAgentClassDefaultsProvider<TConfig>.Instance;
        return _classDefaultsProvider.GetSnapshotAsync(GetType(), ct);
    }
}
