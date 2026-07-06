using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
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

    public AgentDeliveryTargetToolSource(
        IUserAgentCatalogQueryPort queryPort,
        IUserAgentCatalogCommandPort commandPort,
        ICallerScopeResolver callerScopeResolver,
        ISecretVault secretVault,
        IScheduledAgentApiKeyIssuer? apiKeyIssuer = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _callerScopeResolver = callerScopeResolver ?? throw new ArgumentNullException(nameof(callerScopeResolver));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _apiKeyIssuer = apiKeyIssuer;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<IAgentTool> tools = [new AgentDeliveryTargetTool(_queryPort, _commandPort, _callerScopeResolver, _secretVault, _apiKeyIssuer)];
        return Task.FromResult(tools);
    }
}
