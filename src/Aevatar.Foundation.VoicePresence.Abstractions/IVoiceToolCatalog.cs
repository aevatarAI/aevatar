namespace Aevatar.Foundation.VoicePresence.Abstractions;

/// <summary>
/// Narrow discovery port that exposes structured tool definitions to voice sessions.
/// </summary>
public interface IVoiceToolCatalog
{
    /// <summary>
    /// Materializes the immutable, budgeted catalog used by one voice session.
    /// </summary>
    Task<VoiceToolCatalogSnapshot> DiscoverAsync(
        VoiceToolExecutionContext? toolContext = null,
        CancellationToken ct = default);
}
