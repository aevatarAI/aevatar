using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

public sealed class AgentBuilderToolSource : IAgentToolSource
{
    // Refactor (iter83/cluster-083-agent-tool-source-root-provider-locator):
    //   Old pattern: tool source captures root IServiceProvider; tools resolve business ports via service locator in ExecuteAsync
    //   New principle: tool source + tools constructor-inject typed contracts; no root provider lookup
    private readonly IUserAgentCatalogQueryPort _queryPort;
    private readonly ISkillRunnerExecutionQueryPort _executionQueryPort;
    private readonly INyxIdApiClientFactory _nyxClientFactory;
    private readonly ISkillRunnerCommandPort _skillRunnerPort;
    private readonly IUserAgentCatalogCommandPort _catalogCommandPort;
    private readonly ICallerScopeResolver _callerScopeResolver;
    private readonly ScheduledAgentCreateRequestMapper _scheduledAgentMapper;
    private readonly ScheduledAgentApiKeyIssuer _scheduledAgentApiKeyIssuer;
    private readonly ILogger<AgentBuilderTool>? _toolLogger;
    private readonly ILogger<ScheduledAgentCreatorTool>? _creatorToolLogger;

    internal AgentBuilderToolSource(
        IUserAgentCatalogQueryPort queryPort,
        ISkillRunnerExecutionQueryPort executionQueryPort,
        INyxIdApiClientFactory nyxClientFactory,
        ISkillRunnerCommandPort skillRunnerPort,
        IUserAgentCatalogCommandPort catalogCommandPort,
        ICallerScopeResolver callerScopeResolver,
        ScheduledAgentCreateRequestMapper scheduledAgentMapper,
        ScheduledAgentApiKeyIssuer scheduledAgentApiKeyIssuer,
        ILogger<AgentBuilderTool>? toolLogger = null,
        ILogger<ScheduledAgentCreatorTool>? creatorToolLogger = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _executionQueryPort = executionQueryPort ?? throw new ArgumentNullException(nameof(executionQueryPort));
        _nyxClientFactory = nyxClientFactory ?? throw new ArgumentNullException(nameof(nyxClientFactory));
        _skillRunnerPort = skillRunnerPort ?? throw new ArgumentNullException(nameof(skillRunnerPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        _callerScopeResolver = callerScopeResolver ?? throw new ArgumentNullException(nameof(callerScopeResolver));
        _scheduledAgentMapper = scheduledAgentMapper ?? throw new ArgumentNullException(nameof(scheduledAgentMapper));
        _scheduledAgentApiKeyIssuer = scheduledAgentApiKeyIssuer ?? throw new ArgumentNullException(nameof(scheduledAgentApiKeyIssuer));
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
                _nyxClientFactory,
                _skillRunnerPort,
                _catalogCommandPort,
                _callerScopeResolver,
                _toolLogger),
            new ScheduledAgentCreatorTool(
                _skillRunnerPort,
                _callerScopeResolver,
                _scheduledAgentMapper,
                _scheduledAgentApiKeyIssuer,
                _creatorToolLogger),
        ];
        return Task.FromResult(tools);
    }
}
