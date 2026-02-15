namespace Aevatar.Demos.CaseProjection.Configuration;

/// <summary>
/// Feature switches for case projection demo pipeline.
/// </summary>
public sealed class CaseProjectionOptions : IProjectionRuntimeOptions
{
    public bool Enabled { get; set; } = true;

    public bool EnableRunQueryEndpoints { get; set; } = true;

    public bool EnableRunReportArtifacts { get; set; } = false;

    public int RunProjectionCompletionWaitTimeoutMs { get; set; } = 3000;
}
