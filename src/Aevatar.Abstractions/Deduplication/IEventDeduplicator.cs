// ─────────────────────────────────────────────────────────────
// IEventDeduplicator - event de-duplication contract.
// Prevents idempotency issues caused by duplicate stream deliveries.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Deduplication;

/// <summary>
/// Event de-duplication contract for stream delivery.
/// </summary>
public interface IEventDeduplicator
{
    /// <summary>Attempts to record an event ID. Returns true if first-seen, false if duplicate.</summary>
    Task<bool> TryRecordAsync(string eventId);
}