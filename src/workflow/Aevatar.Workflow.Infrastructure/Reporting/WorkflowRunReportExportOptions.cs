namespace Aevatar.Workflow.Infrastructure.Reporting;

public sealed class WorkflowRunReportExportOptions
{
    public bool Enabled { get; set; } = true;

    public string? OutputDirectory { get; set; }
}
