using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Workflow.Tools;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Workflow;

/// <summary>
/// Workflow tool source. Provides tools for inspecting workflow executions,
/// actor state (via readmodel), and event timelines.
/// </summary>
public sealed class WorkflowAgentToolSource : IAgentToolSource
{
    private readonly IWorkflowExecutionQueryApplicationService _queryService;
    private readonly WorkflowToolOptions _options;
    private readonly ILogger _logger;

    public WorkflowAgentToolSource(
        IWorkflowExecutionQueryApplicationService queryService,
        WorkflowToolOptions options,
        ILogger<WorkflowAgentToolSource>? logger = null)
    {
        _queryService = queryService;
        _options = options;
        _logger = logger ?? NullLogger<WorkflowAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<IAgentTool> tools =
        [
            new WorkflowStatusTool(_queryService, _options),
            new ActorInspectTool(_queryService, _options),
            new EventQueryTool(_queryService, _options),
        ];

        _logger.LogInformation(
            "Workflow tools registered ({Count} tools, actor query enabled: {Enabled})",
            tools.Count, _queryService.ActorQueryEnabled);

        return Task.FromResult(tools);
    }
}
