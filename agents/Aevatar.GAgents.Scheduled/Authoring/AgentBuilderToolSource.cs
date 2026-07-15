using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;
using Aevatar.Workflow.Application.Abstractions.Schedules;

namespace Aevatar.GAgents.Scheduled;

public sealed class AgentBuilderToolSource : IAgentToolSource
{
    // Refactor (iter83/cluster-083-agent-tool-source-root-provider-locator):
    //   Old pattern: tool source captures root IServiceProvider; tools resolve business ports via service locator in ExecuteAsync
    //   New principle: tool source + tools constructor-inject typed contracts; no root provider lookup
    private readonly IUserAgentCatalogQueryPort _queryPort;
    private readonly ISkillRunnerExecutionQueryPort _executionQueryPort;
    private readonly ISkillRunnerCommandPort _skillRunnerPort;
    private readonly IWorkflowScheduleApplicationService _workflowScheduleService;
    private readonly IScheduledWorkflowAgentCreationPort _scheduledWorkflowAgentCreationPort;
    private readonly IUserAgentCatalogCommandPort _catalogCommandPort;
    private readonly ICallerScopeResolver _callerScopeResolver;
    private readonly ScheduledAgentCreateRequestMapper _scheduledAgentMapper;
    private readonly IScheduledAgentCredentialLifecycle _scheduledAgentCredentialLifecycle;
    private readonly ILogger<AgentBuilderTool>? _toolLogger;
    private readonly ILogger<ScheduledAgentCreatorTool>? _creatorToolLogger;

    internal AgentBuilderToolSource(
        IUserAgentCatalogQueryPort queryPort,
        ISkillRunnerExecutionQueryPort executionQueryPort,
        ISkillRunnerCommandPort skillRunnerPort,
        IWorkflowScheduleApplicationService workflowScheduleService,
        IScheduledWorkflowAgentCreationPort scheduledWorkflowAgentCreationPort,
        IUserAgentCatalogCommandPort catalogCommandPort,
        ICallerScopeResolver callerScopeResolver,
        ScheduledAgentCreateRequestMapper scheduledAgentMapper,
        IScheduledAgentCredentialLifecycle scheduledAgentCredentialLifecycle,
        ILogger<AgentBuilderTool>? toolLogger = null,
        ILogger<ScheduledAgentCreatorTool>? creatorToolLogger = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _executionQueryPort = executionQueryPort ?? throw new ArgumentNullException(nameof(executionQueryPort));
        _skillRunnerPort = skillRunnerPort ?? throw new ArgumentNullException(nameof(skillRunnerPort));
        _workflowScheduleService = workflowScheduleService ?? throw new ArgumentNullException(nameof(workflowScheduleService));
        _scheduledWorkflowAgentCreationPort = scheduledWorkflowAgentCreationPort ?? throw new ArgumentNullException(nameof(scheduledWorkflowAgentCreationPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        _callerScopeResolver = callerScopeResolver ?? throw new ArgumentNullException(nameof(callerScopeResolver));
        _scheduledAgentMapper = scheduledAgentMapper ?? throw new ArgumentNullException(nameof(scheduledAgentMapper));
        _scheduledAgentCredentialLifecycle = scheduledAgentCredentialLifecycle ??
            throw new ArgumentNullException(nameof(scheduledAgentCredentialLifecycle));
        _toolLogger = toolLogger;
        _creatorToolLogger = creatorToolLogger;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<IAgentTool> tools =
        [
            new AgentBuilderTool(
                _queryPort,
                _executionQueryPort,
                _skillRunnerPort,
                _workflowScheduleService,
                _catalogCommandPort,
                _callerScopeResolver,
                _toolLogger),
            new ScheduledAgentCreatorTool(
                _scheduledWorkflowAgentCreationPort,
                _callerScopeResolver,
                _scheduledAgentMapper,
                _scheduledAgentCredentialLifecycle,
                _creatorToolLogger),
        ];
        return Task.FromResult(tools);
    }
}
