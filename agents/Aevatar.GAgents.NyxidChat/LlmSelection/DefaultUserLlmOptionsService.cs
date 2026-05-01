using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using StudioUserConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class DefaultUserLlmOptionsService : IUserLlmOptionsService
{
    private const string StatusScope = "llm:status";

    private readonly INyxIdLlmServiceCatalogClient _catalogClient;
    private readonly INyxIdCapabilityBroker? _broker;
    private readonly IUserConfigQueryPort? _queryPort;

    public DefaultUserLlmOptionsService(
        INyxIdLlmServiceCatalogClient catalogClient,
        IUserConfigQueryPort? queryPort = null,
        INyxIdCapabilityBroker? broker = null)
    {
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _queryPort = queryPort;
        _broker = broker;
    }

    public async Task<UserLlmOptionsView> GetOptionsAsync(UserLlmOptionsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var accessToken = await IssueAccessTokenAsync(query, ct).ConfigureAwait(false);
        var catalog = await _catalogClient.GetServicesAsync(query, accessToken, ct).ConfigureAwait(false);
        var available = catalog.Services.Select(ToOption).ToArray();
        var current = await ResolveCurrentAsync(query, available, ct).ConfigureAwait(false);
        var setupHint = available.Length == 0
            ? catalog.SetupHint ?? await _catalogClient.GetSetupHintAsync(query, accessToken, ct).ConfigureAwait(false)
            : null;

        return new UserLlmOptionsView(query.BindingId, current, available, setupHint);
    }

    private async Task<string> IssueAccessTokenAsync(UserLlmOptionsQuery query, CancellationToken ct)
    {
        if (_broker is null)
            return string.Empty;

        var handle = await _broker
            .IssueShortLivedAsync(query.Subject, new CapabilityScope { Value = StatusScope }, ct)
            .ConfigureAwait(false);
        return handle.AccessToken;
    }

    private async Task<UserLlmOption?> ResolveCurrentAsync(
        UserLlmOptionsQuery query,
        IReadOnlyList<UserLlmOption> available,
        CancellationToken ct)
    {
        if (_queryPort is null || string.IsNullOrWhiteSpace(query.BindingId.Value))
            return null;

        StudioUserConfig config;
        try
        {
            config = await _queryPort.GetAsync(query.BindingId.Value.Trim(), ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        var route = UserConfigLlmRoute.Normalize(config.PreferredLlmRoute);
        if (string.IsNullOrWhiteSpace(route))
            return null;

        return available.FirstOrDefault(option =>
            string.Equals(option.RouteValue, route, StringComparison.OrdinalIgnoreCase));
    }

    private static UserLlmOption ToOption(NyxIdLlmService service) => new(
        ServiceId: NormalizeRequired(service.UserServiceId, nameof(service.UserServiceId)),
        ServiceSlug: NormalizeRequired(service.ServiceSlug, nameof(service.ServiceSlug)),
        DisplayName: NormalizeRequired(service.DisplayName, nameof(service.DisplayName)),
        RouteValue: NormalizeRequired(service.RouteValue, nameof(service.RouteValue)),
        DefaultModel: NormalizeOptional(service.DefaultModel),
        AvailableModels: service.Models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        Status: NormalizeRequired(service.Status, nameof(service.Status)),
        Source: NormalizeRequired(service.Source, nameof(service.Source)),
        Allowed: service.Allowed,
        Description: NormalizeOptional(service.Description));

    private static string NormalizeRequired(string value, string name)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{name} must not be empty.");
        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
