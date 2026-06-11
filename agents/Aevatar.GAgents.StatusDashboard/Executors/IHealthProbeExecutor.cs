namespace Aevatar.GAgents.StatusDashboard.Executors;

/// <summary>
/// Strategy contract for one health probe kind. Resolved by
/// <see cref="IHealthProbeExecutorRegistry"/> using the descriptor's
/// <c>probe_kind</c> key. Implementations must not throw — they translate any
/// failure into a <c>down</c>/<c>degraded</c> outcome with a diagnostic message.
/// </summary>
public interface IHealthProbeExecutor
{
    /// <summary>Stable kind key matched against <c>HealthProbeTargetDescriptor.ProbeKind</c>.</summary>
    string Kind { get; }

    Task<HealthProbeOutcome> ProbeAsync(
        HealthProbeTargetDescriptor descriptor,
        CancellationToken ct);
}

/// <summary>
/// Lookup over the registered executors. Returns <c>null</c> when no executor
/// is registered for the descriptor kind — the GAgent records a configuration
/// failure outcome rather than throwing.
/// </summary>
public interface IHealthProbeExecutorRegistry
{
    IHealthProbeExecutor? Resolve(string kind);

    IReadOnlyCollection<string> KnownKinds { get; }
}
