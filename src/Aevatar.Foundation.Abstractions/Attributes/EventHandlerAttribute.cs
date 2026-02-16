// ─────────────────────────────────────────────────────────────
// EventHandlerAttribute — Marks handler methods for specific event types
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions.Attributes;

/// <summary>
/// Marks methods on Agent classes as event handlers.
/// Method signature: async Task HandleXxx(TEvent evt) where TEvent : IMessage.
/// Handlers and IEventModule execute interleaved in the same pipeline by Priority.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EventHandlerAttribute : Attribute
{
    /// <summary>
    /// Execution priority. Lower values execute first. Default is 0.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Whether to handle events published by itself. Default is false.
    /// </summary>
    public bool AllowSelfHandling { get; set; }

    /// <summary>
    /// Only handle events published by itself (EventDirection.Self). Default is false.
    /// </summary>
    public bool OnlySelfHandling { get; set; }
}