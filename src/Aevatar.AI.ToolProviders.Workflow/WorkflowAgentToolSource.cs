using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Workflow.Ports;
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

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        var tools = new List<IAgentTool>
        {
            new WorkflowStatusTool(_queryService, _options),
            new ActorInspectTool(_queryService, _options),
            new EventQueryTool(_queryService, _options),
        };

        if (_definitionCommand is not null)
        {
            tools.Add(new WorkflowListDefsTool(_definitionCommand));
            tools.Add(new WorkflowReadDefTool(_definitionCommand));
            tools.Add(new WorkflowCreateDefTool(_definitionCommand, _options));
            tools.Add(new WorkflowUpdateDefTool(_definitionCommand, _options));
        }

        _logger.LogInformation(
            "Workflow tools registered ({Count} tools, definition command: {DefAvailable})",
            tools.Count, _definitionCommand is not null);

        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}
