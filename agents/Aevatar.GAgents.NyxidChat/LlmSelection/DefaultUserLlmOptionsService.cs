using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
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
        var currentConfig = await ResolveCurrentConfigAsync(query, ct).ConfigureAwait(false);
        var current = ResolveCurrentOption(currentConfig, available);
        var setupHint = available.Length == 0 ? catalog.SetupHint : null;

        return new UserLlmOptionsView(current, available, setupHint)
        {
            CurrentRouteValue = ResolveCurrentRoute(currentConfig, current),
            CurrentModel = ResolveCurrentModel(currentConfig),
        };
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

    private async Task<StudioUserConfig?> ResolveCurrentConfigAsync(
        UserLlmOptionsQuery query,
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
            config = await queryPort
                .GetAsync(UserConfigResourceKey.ForChannelBinding(query.BindingId.Value), ct)
                .ConfigureAwait(false);
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

        return config;
    }

    private static UserLlmOption? ResolveCurrentOption(
        StudioUserConfig? config,
        IReadOnlyList<UserLlmOption> available)
    {
        if (!HasReadyTypedSelection(config))
            return null;

        return config?.LlmSelection?.RouteKind switch
        {
            LLMRouteKind.Gateway => FindRouteOption(
                UserConfigLlmRouteDefaults.Gateway,
                available),
            LLMRouteKind.NyxIdUserService => FindInventoryOption(
                config.LlmSelection.NyxIdUserServiceId,
                available),
            null or LLMRouteKind.Unspecified => null,
            _ => null,
        };
    }

    private static UserLlmOption? FindInventoryOption(
        string? userServiceId,
        IReadOnlyList<UserLlmOption> available)
    {
        var normalizedId = UserLlmPreferenceWriteCore.NormalizeOptional(userServiceId);
        if (normalizedId is null)
            return null;

        return available.FirstOrDefault(option =>
            option.Identity is
            {
                Authority: UserLlmIdentityAuthority.NyxIdUserServicesInventory,
            } identity &&
            string.Equals(identity.NyxIdUserServiceId, normalizedId, StringComparison.Ordinal));
    }

    private static UserLlmOption? FindRouteOption(
        string? routeValue,
        IReadOnlyList<UserLlmOption> available)
    {
        var route = UserConfigLlmRoute.Normalize(routeValue);

        return available.FirstOrDefault(option =>
            string.Equals(option.RouteValue, route, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveCurrentRoute(StudioUserConfig? config, UserLlmOption? current) =>
        !HasReadyTypedSelection(config)
            ? string.Empty
            : config!.LlmSelection!.RouteKind switch
        {
            LLMRouteKind.Gateway => UserConfigLlmRouteDefaults.Gateway,
            LLMRouteKind.NyxIdUserService => current?.RouteValue ??
                                                     UserConfigLlmRoute.Normalize(config.LlmSelection.RouteValue),
            _ => string.Empty,
        };

    private static string? ResolveCurrentModel(StudioUserConfig? config)
    {
        if (!HasReadyTypedSelection(config))
            return null;

        return config!.LlmSelection!.ModelSelection.Kind == LLMModelSelectionKind.ExplicitModel
            ? config.LlmSelection.ModelSelection.ModelId
            : null;
    }

    private static bool HasReadyTypedSelection(StudioUserConfig? config) =>
        config is not null &&
        LLMSelectionPolicy.ClassifyPersisted(
            config.LlmSelection,
            config.PreferredLlmRoute,
            config.DefaultModel) == LLMSelectionPersistenceStatus.Ready;

}
