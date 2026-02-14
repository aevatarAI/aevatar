namespace Aevatar.Cqrs.Projections.Configuration;

/// <summary>
/// Feature flags for chat projection pipeline.
/// </summary>
public sealed class ChatProjectionOptions
{
    /// <summary>
    /// Enables projection pipeline registration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Exposes read-side run query endpoints.
    /// </summary>
    public bool EnableRunQueryEndpoints { get; set; } = true;

    /// <summary>
    /// Writes run report artifacts (json/html).
    /// </summary>
    public bool EnableRunReportArtifacts { get; set; } = true;

    /// <summary>
    /// Max wait time for one run projection completion signal.
    /// </summary>
    public int RunProjectionCompletionWaitTimeoutMs { get; set; } = 3000;
}
