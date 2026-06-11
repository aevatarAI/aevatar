namespace Aevatar.Foundation.Abstractions.Persistence;

/// <summary>
/// Maintenance-only event stream operations.
/// </summary>
public interface IEventStoreMaintenance
{
    /// <summary>
    /// Deletes the event stream and resets its version watermark.
    /// </summary>
    Task<bool> ResetStreamAsync(string agentId, CancellationToken ct = default);
}
