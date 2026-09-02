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

    /// <summary>
    /// Identity + budget knobs for the real/authenticated probes. The paid LLM canary is only
    /// emitted when <see cref="StatusProbeOptions.CanaryBearer"/> resolves to a real
    /// NyxID-recognized credential; absent it the canary is simply not probed.
    /// </summary>
    public StatusProbeOptions Probe { get; set; } = new();
}

/// <summary>
/// Knobs for the deep/credentialed probes. Bound from <c>Aevatar:Status:Probe</c>. All values
/// are optional: when a secret is missing, the corresponding probe is omitted rather than
/// reported as down.
/// </summary>
public sealed class StatusProbeOptions
{
    /// <summary>Default cheap model for the LLM canary when none is configured.</summary>
    public const string DefaultCanaryModel = "deepseek/deepseek-v4-flash";

    /// <summary>
    /// Configuration key the probe executor reads the canary bearer from at probe time
    /// (static_bearer mode). Kept in sync with the bound <see cref="CanaryBearer"/> path.
    /// </summary>
    public const string CanaryBearerConfigurationKey = "Aevatar:Status:Probe:CanaryBearer";

    /// <summary>
    /// A real NyxID-recognized bearer used only by the paid LLM canary (POST /v1/chat/completions).
    /// Empty disables the canary. Provision a dedicated low-privilege canary credential in prod.
    /// </summary>
    public string CanaryBearer { get; set; } = string.Empty;

    /// <summary>Cheapest model id for the canary completion.</summary>
    public string CanaryModel { get; set; } = DefaultCanaryModel;

    /// <summary>Canary probe interval. Defaults to 15 minutes to keep spend negligible.</summary>
    public int CanaryIntervalSeconds { get; set; } = 900;

    /// <summary>Hard output cap for the canary completion.</summary>
    public int CanaryMaxTokens { get; set; } = 8;

    /// <summary>
    /// Probe scope id for credentialed scope_service_token reads (orchestration + observatory).
    /// Empty omits those probes. Requires <c>Aevatar:Authentication:ScopeServiceTokens:Enabled=true</c>
    /// to actually mint tokens; when disabled the probes degrade to <c>unknown</c>, never a false down.
    /// </summary>
    public string ScopeId { get; set; } = string.Empty;
}

public sealed class StatusProbeTargetConfig
{
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Layer: <c>self</c>, <c>llm</c>, <c>orchestration</c>, <c>studio</c>, <c>upstream</c>, <c>feature</c>.</summary>
    public string Category { get; set; } = "upstream";

    /// <summary>
    /// Aggregation weight: <c>critical</c>, <c>standard</c>, or <c>canary</c>. A down/degraded
    /// <c>critical</c> target forces overall down/degraded; <c>canary</c> targets only ever
    /// degrade overall, and an <c>unknown</c> (unconfigured) canary is excluded entirely.
    /// </summary>
    public string Severity { get; set; } = "standard";

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
