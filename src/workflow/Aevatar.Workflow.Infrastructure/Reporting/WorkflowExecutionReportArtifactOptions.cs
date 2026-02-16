namespace Aevatar.Workflow.Infrastructure.Reporting;

public sealed class WorkflowExecutionReportArtifactOptions
{
    public bool Enabled { get; set; } = true;

    public string? OutputDirectory { get; set; }
}
