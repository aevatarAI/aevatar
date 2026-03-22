using Aevatar.Configuration;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Reporting;

internal sealed class FileSystemWorkflowRunReportExporter : IWorkflowRunReportExportPort
{
    private readonly IOptions<WorkflowRunReportExportOptions> _options;
    private readonly ILogger<FileSystemWorkflowRunReportExporter> _logger;

    public FileSystemWorkflowRunReportExporter(
        IOptions<WorkflowRunReportExportOptions> options,
        ILogger<FileSystemWorkflowRunReportExporter> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task ExportAsync(WorkflowRunExportDocument exportDocument, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exportDocument);

        if (!_options.Value.Enabled)
            return;

        ct.ThrowIfCancellationRequested();

        var outputDir = ResolveOutputDirectory();
        var (jsonPath, htmlPath) = WorkflowRunReportExportWriter.BuildDefaultPaths(outputDir);
        await WorkflowRunReportExportWriter.WriteAsync(exportDocument, jsonPath, htmlPath);

        _logger.LogInformation("Chat run report saved: json={JsonPath}, html={HtmlPath}", jsonPath, htmlPath);
    }

    private string ResolveOutputDirectory()
    {
        var configured = _options.Value.OutputDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return Path.Combine(AevatarPaths.RepoRoot, "artifacts", "workflow-executions");
    }
}
