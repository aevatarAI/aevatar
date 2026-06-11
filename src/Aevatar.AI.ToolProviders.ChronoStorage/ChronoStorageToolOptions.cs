namespace Aevatar.AI.ToolProviders.ChronoStorage;

/// <summary>Configuration for ChronoStorage agent tools.</summary>
public sealed class ChronoStorageToolOptions
{
    // Refactor (iter8/cluster-018): keep SDK examples on the repo-approved local API port.
    /// <summary>Base URL of the Explorer API (e.g. http://localhost:5100).</summary>
    public string? ApiBaseUrl { get; set; }
}
