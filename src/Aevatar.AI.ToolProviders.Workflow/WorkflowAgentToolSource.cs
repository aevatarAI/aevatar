using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Workflow.Ports;
using Aevatar.AI.ToolProviders.Workflow.Tools;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Workflow;

/// <summary>
/// Workflow tool source. Provides tools for inspecting workflow executions,
/// workflow actor current state (via readmodel), and event timelines.
/// </summary>
public sealed class WorkflowAgentToolSource : IAgentToolSource
{
    private readonly IWorkflowExecutionQueryApplicationService _queryService;
    private readonly WorkflowToolOptions _options;
    private readonly IWorkflowDefinitionCommandAdapter? _definitionCommand;
    private readonly ILogger _logger;

    public WorkflowAgentToolSource(
        IWorkflowExecutionQueryApplicationService queryService,
        WorkflowToolOptions options,
        ILogger<WorkflowAgentToolSource>? logger = null,
        IWorkflowDefinitionCommandAdapter? definitionCommand = null)
    {
        _queryService = queryService;
        _options = options;
        _definitionCommand = definitionCommand;
        _logger = logger ?? NullLogger<WorkflowAgentToolSource>.Instance;
    }

    // Refactor (iter105/cluster-105-workflow-artifact-query-still-actor-shaped):
    //   Old pattern: Workflow artifact/report/graph query surfaces still sit under actor inspection and actor-query enablement, even after documents were renamed as artifacts/exports.
    //   New principle: Workflow artifacts have an explicit artifact/export query surface separate from actor current-state query and tool names — graph-only workflow_artifact_query tool on existing execution facade; delete actor-shaped graph wrapper and aliases; rename artifact gate away from actor query.
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        var tools = new List<IAgentTool>
        {
            new WorkflowStatusTool(_queryService, _options),
            new WorkflowArtifactQueryTool(_queryService, _options),
            new WorkflowActorCurrentStateTool(_queryService, _options),
            new EventQueryTool(_queryService, _options),
        };

        if (_definitionCommand is not null)
        {
            tools.Add(new WorkflowCreateDefTool(_definitionCommand, _options));
            tools.Add(new WorkflowUpdateDefTool(_definitionCommand, _options));
        }

        _logger.LogInformation(
            "Workflow tools registered ({Count} tools, definition command: {DefAvailable})",
            tools.Count, _definitionCommand is not null);

        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}
