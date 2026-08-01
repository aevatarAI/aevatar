// ─────────────────────────────────────────────────────────────
// IEventDeduplicator - event de-duplication contract.
// Best-effort duplicate filtering for runtime envelope delivery.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions.Deduplication;

/// <summary>
/// Best-effort duplicate-filter contract for stream delivery.
/// This contract does not provide durable business idempotency.
/// </summary>
public interface IEventDeduplicator
{
    /// <summary>
    /// Attempts to reserve an event ID before handling. Returns true if first-seen, false if duplicate.
    /// A failed attempt which will be redelivered must release the reservation with
    /// <see cref="ForgetAsync"/>.
    /// </summary>
    Task<bool> TryRecordAsync(string eventId);

    /// <summary>Releases a provisional reservation so the same delivery attempt can be handled again.</summary>
    Task ForgetAsync(string eventId);
}
