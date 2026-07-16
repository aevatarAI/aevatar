using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.AI.ToolProviders.AgentCatalog;

public sealed class AgentDeliveryTargetToolSource : IAgentToolSource
{
    // Refactor (iter83/cluster-083-agent-tool-source-root-provider-locator):
    //   Old pattern: tool source captures root IServiceProvider; tools resolve business ports via service locator in ExecuteAsync
    //   New principle: tool source + tools constructor-inject typed contracts; no root provider lookup
    private readonly IUserAgentCatalogQueryPort _queryPort;
    private readonly IUserAgentCatalogCommandPort _commandPort;
    private readonly ICallerScopeResolver _callerScopeResolver;
    private readonly ISecretVault _secretVault;
    private readonly IScheduledAgentApiKeyIssuer? _apiKeyIssuer;
    private readonly IScheduledInvocationAuthorizationPlanner? _authorizationPlanner;
    private readonly IScheduledInvocationAuthorizationRevalidator? _authorizationRevalidator;
    private readonly ScheduledAgentCreatorOptions? _authorizationOptions;

    public AgentDeliveryTargetToolSource(
        IUserAgentCatalogQueryPort queryPort,
        IUserAgentCatalogCommandPort commandPort,
        ICallerScopeResolver callerScopeResolver,
        ISecretVault secretVault,
        IScheduledAgentApiKeyIssuer? apiKeyIssuer = null,
        IScheduledInvocationAuthorizationPlanner? authorizationPlanner = null,
        IScheduledInvocationAuthorizationRevalidator? authorizationRevalidator = null,
        ScheduledAgentCreatorOptions? authorizationOptions = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _callerScopeResolver = callerScopeResolver ?? throw new ArgumentNullException(nameof(callerScopeResolver));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _apiKeyIssuer = apiKeyIssuer;
        _authorizationPlanner = authorizationPlanner;
        _authorizationRevalidator = authorizationRevalidator;
        _authorizationOptions = authorizationOptions;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<IAgentTool> tools = [new AgentDeliveryTargetTool(
            _queryPort,
            _commandPort,
            _callerScopeResolver,
            _secretVault,
            _apiKeyIssuer,
            _authorizationPlanner,
            _authorizationRevalidator,
            _authorizationOptions)];
        return Task.FromResult(tools);
    }
}
