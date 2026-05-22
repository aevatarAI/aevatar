namespace Aevatar.GAgents.StatusDashboard.Configuration;

/// <summary>
/// Bound to the <c>Aevatar:Status</c> configuration section. The host owns
/// the choice of probe targets; the agent module just executes whatever is
/// declared here.
/// </summary>
public sealed class StatusDashboardOptions
{
    public const string SectionName = "Aevatar:Status";

    /// <summary>
    /// Default probe interval used when a target omits <c>IntervalSeconds</c>.
    /// </summary>
    public int DefaultIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Default probe timeout used when a target omits <c>TimeoutMs</c>.
    /// </summary>
    public int DefaultTimeoutMs { get; set; } = 5_000;

    /// <summary>
    /// Base URL used by built-in self probes. Production containers usually
    /// listen on 8080, while local development can override this to 5080.
    /// </summary>
    public string SelfBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Use the built-in mainnet probe set when <see cref="Targets"/> is empty.
    /// This prevents production config trimming from turning /status into an
    /// empty board.
    /// </summary>
    public bool UseBuiltInTargets { get; set; } = true;

    /// <summary>
    /// Probe target manifest. Order is preserved; the API surfaces targets in
    /// declared order.
    /// </summary>
    public List<StatusProbeTargetConfig> Targets { get; set; } = new();
}

public sealed class StatusProbeTargetConfig
{
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>One of: <c>upstream</c>, <c>self</c>, <c>feature</c>.</summary>
    public string Category { get; set; } = "upstream";

    /// <summary>Executor strategy key (matches <c>IHealthProbeExecutor.Kind</c>).</summary>
    public string Probe { get; set; } = string.Empty;

    public int? IntervalSeconds { get; set; }

    public int? TimeoutMs { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Free-form parameters consumed by the chosen executor. Keys and value
    /// semantics are documented per executor implementation.
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
