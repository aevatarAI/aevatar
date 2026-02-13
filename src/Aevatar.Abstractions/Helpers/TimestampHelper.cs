// ─────────────────────────────────────────────────────────────
// TimestampHelper — Protobuf Timestamp utility
// ─────────────────────────────────────────────────────────────

using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Helpers;

/// <summary>
/// Protobuf Timestamp convenience utility.
/// </summary>
public static class TimestampHelper
{
    /// <summary>Protobuf Timestamp for current UTC time.</summary>
    public static Timestamp Now() => Timestamp.FromDateTime(DateTime.UtcNow);
}