namespace Aevatar.Foundation.VoicePresence.Abstractions;

/// <summary>
/// Narrow discovery port that exposes structured tool definitions to voice sessions.
/// </summary>
public interface IVoiceToolCatalog
{
    /// <summary>
    /// Discovers all currently available voice-callable tools.
    /// </summary>
    Task<IReadOnlyList<VoiceToolDefinition>> DiscoverAsync(CancellationToken ct = default);
}
