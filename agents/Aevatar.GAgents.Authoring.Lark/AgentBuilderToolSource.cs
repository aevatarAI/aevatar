using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgents.Scheduled;
using Aevatar.Studio.Application.Authorization;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

public sealed class AgentBuilderToolSource : IAgentToolSource
{
    // Refactor (iter83/cluster-083-agent-tool-source-root-provider-locator):
    //   Old pattern: tool source captures root IServiceProvider; tools resolve business ports via service locator in ExecuteAsync
    //   New principle: tool source + tools constructor-inject typed contracts; no root provider lookup
    private readonly IUserAgentCatalogQueryPort _queryPort;
    private readonly ISkillRunnerExecutionQueryPort _executionQueryPort;
    private readonly ISkillRunnerCommandPort _skillRunnerPort;
    private readonly IScheduledDispatchApplicationService _scheduledDispatchService;
    private readonly IScheduledWorkflowAgentCreationPort _scheduledWorkflowAgentCreationPort;
    private readonly IUserAgentCatalogCommandPort _catalogCommandPort;
    private readonly ICallerScopeResolver _callerScopeResolver;
    private readonly ScheduledAgentCreateRequestMapper _scheduledAgentMapper;
    private readonly ScheduledAgentApiKeyIssuer _scheduledAgentApiKeyIssuer;
    private readonly IScheduledInvocationAuthorizationPlanner _scheduledInvocationAuthorizationPlanner;
    private readonly ScheduledAgentCreatorOptions _scheduledAgentCreatorOptions;
    private readonly ILogger<AgentBuilderTool>? _toolLogger;
    private readonly ILogger<ScheduledAgentCreatorTool>? _creatorToolLogger;

    internal AgentBuilderToolSource(
        IUserAgentCatalogQueryPort queryPort,
        ISkillRunnerExecutionQueryPort executionQueryPort,
        ISkillRunnerCommandPort skillRunnerPort,
        IScheduledDispatchApplicationService scheduledDispatchService,
        IScheduledWorkflowAgentCreationPort scheduledWorkflowAgentCreationPort,
        IUserAgentCatalogCommandPort catalogCommandPort,
        ICallerScopeResolver callerScopeResolver,
        ScheduledAgentCreateRequestMapper scheduledAgentMapper,
        ScheduledAgentApiKeyIssuer scheduledAgentApiKeyIssuer,
        IScheduledInvocationAuthorizationPlanner? scheduledInvocationAuthorizationPlanner = null,
        ScheduledAgentCreatorOptions? scheduledAgentCreatorOptions = null,
        ILogger<AgentBuilderTool>? toolLogger = null,
        ILogger<ScheduledAgentCreatorTool>? creatorToolLogger = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _executionQueryPort = executionQueryPort ?? throw new ArgumentNullException(nameof(executionQueryPort));
        _skillRunnerPort = skillRunnerPort ?? throw new ArgumentNullException(nameof(skillRunnerPort));
        _scheduledDispatchService = scheduledDispatchService ?? throw new ArgumentNullException(nameof(scheduledDispatchService));
        _scheduledWorkflowAgentCreationPort = scheduledWorkflowAgentCreationPort ?? throw new ArgumentNullException(nameof(scheduledWorkflowAgentCreationPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        _callerScopeResolver = callerScopeResolver ?? throw new ArgumentNullException(nameof(callerScopeResolver));
        _scheduledAgentMapper = scheduledAgentMapper ?? throw new ArgumentNullException(nameof(scheduledAgentMapper));
        _scheduledAgentApiKeyIssuer = scheduledAgentApiKeyIssuer ?? throw new ArgumentNullException(nameof(scheduledAgentApiKeyIssuer));
        _scheduledInvocationAuthorizationPlanner = scheduledInvocationAuthorizationPlanner ?? UnavailableAuthorizationPlanner.Instance;
        _scheduledAgentCreatorOptions = scheduledAgentCreatorOptions ?? new ScheduledAgentCreatorOptions();
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
                _scheduledDispatchService,
                _catalogCommandPort,
                _callerScopeResolver,
                _scheduledAgentApiKeyIssuer,
                _toolLogger),
            new ScheduledAgentCreatorTool(
                _scheduledWorkflowAgentCreationPort,
                _callerScopeResolver,
                _scheduledAgentMapper,
                _scheduledAgentApiKeyIssuer,
                _scheduledInvocationAuthorizationPlanner,
                _scheduledAgentCreatorOptions,
                _creatorToolLogger),
        ];
        return Task.FromResult(tools);
    }

    private sealed class UnavailableAuthorizationPlanner : IScheduledInvocationAuthorizationPlanner
    {
        public static readonly UnavailableAuthorizationPlanner Instance = new();

        public Task<ScheduledInvocationAuthorizationPlanResult> PlanAsync(
            ScheduledInvocationAuthorizationRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(ScheduledInvocationAuthorizationPlanResult.Failed(
                ScheduledInvocationAuthorizationFailureCode.SnapshotNotFound,
                "scheduled_invocation_authorization_planner_unavailable"));
    }
}
