using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StudioUserConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class DefaultUserLlmOptionsService : IUserLlmOptionsService
{
    private readonly INyxIdLlmServiceCatalogClient _catalogClient;
    private readonly INyxIdCapabilityBroker? _broker;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<DefaultUserLlmOptionsService> _logger;

    public DefaultUserLlmOptionsService(
        INyxIdLlmServiceCatalogClient catalogClient,
        IServiceScopeFactory? scopeFactory = null,
        INyxIdCapabilityBroker? broker = null,
        ILogger<DefaultUserLlmOptionsService>? logger = null)
    {
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _scopeFactory = scopeFactory;
        _broker = broker;
        _logger = logger ?? NullLogger<DefaultUserLlmOptionsService>.Instance;
    }

    public async Task<UserLlmOptionsView> GetOptionsAsync(UserLlmOptionsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var accessToken = await IssueAccessTokenAsync(query, ct).ConfigureAwait(false);
        var catalog = await _catalogClient.GetServicesAsync(query, accessToken, ct).ConfigureAwait(false);
        var available = catalog.Services.Select(NyxIdLlmServiceMapping.ToOption).ToArray();
        var current = await ResolveCurrentAsync(query, available, ct).ConfigureAwait(false);
        var setupHint = available.Length == 0 ? catalog.SetupHint : null;

        return new UserLlmOptionsView(current, available, setupHint);
    }

    private async Task<string> IssueAccessTokenAsync(UserLlmOptionsQuery query, CancellationToken ct)
    {
        if (_broker is null)
            return string.Empty;

        var handle = await _broker
            .IssueShortLivedAsync(query.Subject, new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy }, ct)
            .ConfigureAwait(false);
        return handle.AccessToken;
    }

    private async Task<UserLlmOption?> ResolveCurrentAsync(
        UserLlmOptionsQuery query,
        IReadOnlyList<UserLlmOption> available,
        CancellationToken ct)
    {
        if (_scopeFactory is null || string.IsNullOrWhiteSpace(query.BindingId.Value))
            return null;

        using var scope = _scopeFactory.CreateScope();
        var queryPort = scope.ServiceProvider.GetService<IUserConfigQueryPort>();
        if (queryPort is null)
            return null;

        StudioUserConfig config;
        try
        {
            config = await queryPort.GetAsync(query.BindingId.Value.Trim(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve current LLM selection for binding {BindingId}",
                query.BindingId.Value);
            return null;
        }

        var route = UserConfigLlmRoute.Normalize(config.PreferredLlmRoute);
        if (string.IsNullOrWhiteSpace(route))
            return null;

        return available.FirstOrDefault(option =>
            string.Equals(option.RouteValue, route, StringComparison.OrdinalIgnoreCase));
    }

}
