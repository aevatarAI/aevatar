namespace Sisyphus.Application.Services;

/// <summary>
/// Configuration for the chrono-graph proxy base URL.
/// </summary>
public sealed class ChronoGraphOptions
{
    public const string SectionName = "Sisyphus:ChronoGraph";

    /// <summary>Base URL of the chrono-graph API (via NyxId proxy).</summary>
    public string? BaseUrl { get; set; }
}
