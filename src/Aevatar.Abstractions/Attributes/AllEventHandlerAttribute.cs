// ─────────────────────────────────────────────────────────────
// AllEventHandlerAttribute — Marks methods that handle all event types
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Attributes;

/// <summary>
/// Marks methods on Agent classes as all-event handlers.
/// Method signature: async Task HandleXxx(EventEnvelope envelope).
/// Receives all types of events without type filtering.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AllEventHandlerAttribute : Attribute
{
    /// <summary>
    /// Execution priority. Default is int.MaxValue (lowest priority).
    /// </summary>
    public int Priority { get; set; } = int.MaxValue;

    /// <summary>
    /// Whether to handle events published by itself. Default is false.
    /// </summary>
    public bool AllowSelfHandling { get; set; }
}