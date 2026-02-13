// ─────────────────────────────────────────────────────────────
// GAgentBase<TState, TConfig> - configurable GAgent base class.
// Config is persisted to ManifestStore.
// ─────────────────────────────────────────────────────────────

using Aevatar.Persistence;
using Google.Protobuf;
using System.Text.Json;

namespace Aevatar;

/// <summary>
/// Configurable GAgent base class. Config is persisted as JSON in ManifestStore and restored on activation.
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
        await PersistConfigAsync(ct);
        await OnConfigChangedAsync(config, ct);
    }

    /// <summary>Hook triggered after config changes. Subclasses may override.</summary>
    protected virtual Task OnConfigChangedAsync(TConfig config, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>Activates agent after restoring config from manifest.</summary>
    public override async Task ActivateAsync(CancellationToken ct = default)
    {
        // Restore config from manifest
        if (ManifestStore != null)
        {
            var manifest = await ManifestStore.LoadAsync(Id, ct);
            if (manifest?.ConfigJson != null)
            {
                try { Config = JsonSerializer.Deserialize<TConfig>(manifest.ConfigJson) ?? new(); }
                catch { /* Fallback to default config when deserialization fails */ }
            }
        }
        await base.ActivateAsync(ct);
    }

    private async Task PersistConfigAsync(CancellationToken ct)
    {
        if (ManifestStore == null) return;
        var manifest = await ManifestStore.LoadAsync(Id, ct) ?? new AgentManifest { AgentId = Id };
        manifest.ConfigJson = JsonSerializer.Serialize(Config);
        await ManifestStore.SaveAsync(Id, manifest, ct);
    }
}
