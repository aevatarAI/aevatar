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
    private readonly ILogger<AgentBuilderTool>? _toolLogger;

    public AgentBuilderToolSource(
        IUserAgentCatalogQueryPort queryPort,
        ISkillRunnerExecutionQueryPort executionQueryPort,
        INyxIdApiClientFactory nyxClientFactory,
        ISkillRunnerCommandPort skillRunnerPort,
        IUserAgentCatalogCommandPort catalogCommandPort,
        ICallerScopeResolver callerScopeResolver,
        ILogger<AgentBuilderTool>? toolLogger = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _executionQueryPort = executionQueryPort ?? throw new ArgumentNullException(nameof(executionQueryPort));
        _nyxClientFactory = nyxClientFactory ?? throw new ArgumentNullException(nameof(nyxClientFactory));
        _skillRunnerPort = skillRunnerPort ?? throw new ArgumentNullException(nameof(skillRunnerPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        _callerScopeResolver = callerScopeResolver ?? throw new ArgumentNullException(nameof(callerScopeResolver));
        _toolLogger = toolLogger;
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
        ];
        return Task.FromResult(tools);
    }
}
