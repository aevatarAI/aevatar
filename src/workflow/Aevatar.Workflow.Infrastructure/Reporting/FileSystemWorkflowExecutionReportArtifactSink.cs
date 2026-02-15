using Aevatar.Configuration;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Reporting;

internal sealed class FileSystemWorkflowExecutionReportArtifactSink : IWorkflowExecutionReportArtifactSink
{
    private readonly IOptions<WorkflowExecutionReportArtifactOptions> _options;
    private readonly ILogger<FileSystemWorkflowExecutionReportArtifactSink> _logger;

    public FileSystemWorkflowExecutionReportArtifactSink(
        IOptions<WorkflowExecutionReportArtifactOptions> options,
        ILogger<FileSystemWorkflowExecutionReportArtifactSink> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task PersistAsync(WorkflowRunReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!_options.Value.Enabled)
            return;

        ct.ThrowIfCancellationRequested();

        var outputDir = ResolveOutputDirectory();
        var (jsonPath, htmlPath) = WorkflowExecutionReportWriter.BuildDefaultPaths(outputDir);
        await WorkflowExecutionReportWriter.WriteAsync(report, jsonPath, htmlPath);

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
