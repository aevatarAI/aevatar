// ─────────────────────────────────────────────────────────────
// IEventModule - event module contract.
// Pluggable pipeline component with priority and filtering.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.EventModules;

/// <summary>
/// Event module contract for pluggable event processing.
/// </summary>
public interface IEventModule
{
    /// <summary>Module name.</summary>
    string Name { get; }

    /// <summary>Execution priority. Lower values run first.</summary>
    int Priority { get; }

    /// <summary>Determines whether the module can handle the event.</summary>
    bool CanHandle(EventEnvelope envelope);

    /// <summary>Handles the event asynchronously.</summary>
    Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct);
}

/// <summary>
/// Marker interface: bypasses routing filters and always participates in the pipeline.
/// </summary>
public interface IRouteBypassModule;
