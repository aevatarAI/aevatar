using Aevatar.Configuration;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Application.Reporting;

internal sealed class FileSystemWorkflowExecutionReportArtifactSink : IWorkflowExecutionReportArtifactSink
{
    private readonly IWorkflowExecutionProjectionService _projectionService;
    private readonly ILogger<FileSystemWorkflowExecutionReportArtifactSink> _logger;

    public FileSystemWorkflowExecutionReportArtifactSink(
        IWorkflowExecutionProjectionService projectionService,
        ILogger<FileSystemWorkflowExecutionReportArtifactSink> logger)
    {
        _projectionService = projectionService;
        _logger = logger;
    }

    public async Task PersistAsync(WorkflowExecutionReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!_projectionService.EnableRunReportArtifacts)
            return;

        ct.ThrowIfCancellationRequested();

        var outputDir = Path.Combine(AevatarPaths.RepoRoot, "artifacts", "workflow-executions");
        var (jsonPath, htmlPath) = WorkflowExecutionReportWriter.BuildDefaultPaths(outputDir);
        await WorkflowExecutionReportWriter.WriteAsync(report, jsonPath, htmlPath);
        _logger.LogInformation("Chat run report saved: json={JsonPath}, html={HtmlPath}", jsonPath, htmlPath);
    }
}
