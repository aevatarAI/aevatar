using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

public sealed class AgentBuilderToolSource : IAgentToolSource
{
    // Refactor (iter83/cluster-083-agent-tool-source-root-provider-locator):
    //   Old pattern: tool source captures root IServiceProvider; tools resolve business ports via service locator in ExecuteAsync
    //   New principle: tool source + tools constructor-inject typed contracts; no root provider lookup
    private readonly IUserAgentCatalogQueryPort _queryPort;
    private readonly IScheduledDispatchApplicationService _scheduledDispatchService;
    private readonly IScheduledWorkflowAgentCreationPort _scheduledWorkflowAgentCreationPort;
    private readonly IUserAgentCatalogCommandPort _catalogCommandPort;
    private readonly ICallerScopeResolver _callerScopeResolver;
    private readonly ScheduledAgentCreateRequestMapper _scheduledAgentMapper;
    private readonly IScheduledAgentCredentialLifecycle _scheduledAgentCredentialLifecycle;
    private readonly IScheduledInvocationAuthorizationPlanner _scheduledInvocationAuthorizationPlanner;
    private readonly IScheduledInvocationAuthorizationRevalidator _scheduledInvocationAuthorizationRevalidator;
    private readonly ScheduledAgentCreatorOptions _scheduledAgentCreatorOptions;
    private readonly ILogger<AgentBuilderTool>? _toolLogger;
    private readonly ILogger<ScheduledAgentCreatorTool>? _creatorToolLogger;

    internal AgentBuilderToolSource(
        IUserAgentCatalogQueryPort queryPort,
        IScheduledDispatchApplicationService scheduledDispatchService,
        IScheduledWorkflowAgentCreationPort scheduledWorkflowAgentCreationPort,
        IUserAgentCatalogCommandPort catalogCommandPort,
        ICallerScopeResolver callerScopeResolver,
        ScheduledAgentCreateRequestMapper scheduledAgentMapper,
        IScheduledAgentCredentialLifecycle scheduledAgentCredentialLifecycle,
        IScheduledInvocationAuthorizationPlanner scheduledInvocationAuthorizationPlanner,
        IScheduledInvocationAuthorizationRevalidator scheduledInvocationAuthorizationRevalidator,
        ScheduledAgentCreatorOptions? scheduledAgentCreatorOptions = null,
        ILogger<AgentBuilderTool>? toolLogger = null,
        ILogger<ScheduledAgentCreatorTool>? creatorToolLogger = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _scheduledDispatchService = scheduledDispatchService ?? throw new ArgumentNullException(nameof(scheduledDispatchService));
        _scheduledWorkflowAgentCreationPort = scheduledWorkflowAgentCreationPort ?? throw new ArgumentNullException(nameof(scheduledWorkflowAgentCreationPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        _callerScopeResolver = callerScopeResolver ?? throw new ArgumentNullException(nameof(callerScopeResolver));
        _scheduledAgentMapper = scheduledAgentMapper ?? throw new ArgumentNullException(nameof(scheduledAgentMapper));
        _scheduledAgentCredentialLifecycle = scheduledAgentCredentialLifecycle ?? throw new ArgumentNullException(nameof(scheduledAgentCredentialLifecycle));
        _scheduledInvocationAuthorizationPlanner = scheduledInvocationAuthorizationPlanner ??
                                                   throw new ArgumentNullException(nameof(scheduledInvocationAuthorizationPlanner));
        _scheduledInvocationAuthorizationRevalidator = scheduledInvocationAuthorizationRevalidator ??
                                                       throw new ArgumentNullException(nameof(scheduledInvocationAuthorizationRevalidator));
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
                _scheduledDispatchService,
                _catalogCommandPort,
                _callerScopeResolver,
                _toolLogger),
            new ScheduledAgentCreatorTool(
                _scheduledWorkflowAgentCreationPort,
                _callerScopeResolver,
                _scheduledAgentMapper,
                _scheduledAgentCredentialLifecycle,
                _scheduledInvocationAuthorizationPlanner,
                _scheduledInvocationAuthorizationRevalidator,
                _scheduledAgentCreatorOptions,
                _creatorToolLogger),
        ];
        return Task.FromResult(tools);
    }
}
