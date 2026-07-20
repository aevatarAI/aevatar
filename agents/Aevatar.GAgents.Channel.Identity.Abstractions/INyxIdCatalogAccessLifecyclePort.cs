using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Invalidates the secret-free NyxID catalog snapshot when authenticated access is lost.
/// </summary>
public interface INyxIdCatalogAccessLifecyclePort
{
    /// <summary>
    /// Records that the subject can no longer use its previously observed catalog.
    /// </summary>
    Task InvalidateAsync(ExternalSubjectRef subject, string reason, CancellationToken ct = default);
}
