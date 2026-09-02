namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Narrow infrastructure port used by the binding actor to retire a specific
/// superseded or unadopted NyxID broker binding after the local replacement
/// fact has committed.
/// </summary>
public interface INyxIdBindingRetirementPort
{
    /// <summary>
    /// Revokes <paramref name="bindingId"/> at NyxID. Implementations must be
    /// idempotent and treat an already missing/revoked binding as success.
    /// </summary>
    Task RetireAsync(string bindingId, CancellationToken ct = default);
}
