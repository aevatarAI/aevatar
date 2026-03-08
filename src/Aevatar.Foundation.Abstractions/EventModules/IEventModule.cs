// ─────────────────────────────────────────────────────────────
// IEventModule<TContext> - event module contract.
// Pluggable pipeline component with priority and filtering.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Abstractions.EventModules;

/// <summary>
/// Generic event module contract for pluggable event processing.
/// </summary>
public interface IEventModule<in TContext>
    where TContext : IEventContext
{
    /// <summary>Module name.</summary>
    string Name { get; }

    /// <summary>Execution priority. Lower values run first.</summary>
    int Priority { get; }

    /// <summary>Determines whether the module can handle the event.</summary>
    bool CanHandle(EventEnvelope envelope);

    /// <summary>Handles the event asynchronously.</summary>
    Task HandleAsync(EventEnvelope envelope, TContext ctx, CancellationToken ct);
}

/// <summary>
/// Marker interface: bypasses routing filters and always participates in the pipeline.
/// </summary>
public interface IRouteBypassModule;
