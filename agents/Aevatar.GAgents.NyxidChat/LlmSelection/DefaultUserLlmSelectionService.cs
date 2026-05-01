using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using StudioUserConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class DefaultUserLlmSelectionService : IUserLlmSelectionService
{
    private const string StatusScope = "llm:status";
    private const string ProxyScope = "llm:proxy";

    private readonly IUserLlmOptionsService _optionsService;
    private readonly INyxIdLlmServiceCatalogClient _catalogClient;
    private readonly INyxIdCapabilityBroker? _broker;
    private readonly IUserConfigQueryPort? _queryPort;
    private readonly IUserConfigCommandService? _commandService;

    public DefaultUserLlmSelectionService(
        IUserLlmOptionsService optionsService,
        INyxIdLlmServiceCatalogClient catalogClient,
        IUserConfigQueryPort? queryPort = null,
        IUserConfigCommandService? commandService = null,
        INyxIdCapabilityBroker? broker = null)
    {
        _optionsService = optionsService ?? throw new ArgumentNullException(nameof(optionsService));
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _queryPort = queryPort;
        _commandService = commandService;
        _broker = broker;
    }

    public async Task SetByServiceAsync(
        UserLlmSelectionContext context,
        string serviceId,
        string? modelOverride,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        var view = await _optionsService.GetOptionsAsync(ToQuery(context), ct).ConfigureAwait(false);
        var option = view.Available.FirstOrDefault(candidate =>
            string.Equals(candidate.ServiceId, serviceId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (option is null)
            throw new InvalidOperationException($"LLM service '{serviceId}' is not available for this user.");
        EnsureSelectable(option);

        await SaveAsync(
            context,
            option.RouteValue,
            NormalizeOptional(modelOverride) ?? option.DefaultModel ?? string.Empty,
            ct).ConfigureAwait(false);
    }

    public Task SetModelOverrideAsync(
        UserLlmSelectionContext context,
        string model,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return SaveModelOnlyAsync(context, model.Trim(), ct);
    }

    public async Task ApplyPresetAsync(
        UserLlmSelectionContext context,
        string presetId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);

        var query = ToQuery(context);
        var statusToken = await IssueAccessTokenAsync(context.Subject, StatusScope, ct).ConfigureAwait(false);
        var hint = await _catalogClient.GetSetupHintAsync(query, statusToken, ct).ConfigureAwait(false);
        var preset = hint.Presets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, presetId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset is null)
            throw new InvalidOperationException($"LLM preset '{presetId}' is not available.");

        switch (preset.Activation)
        {
            case UseExistingService existing:
                await SaveAsync(
                    context,
                    existing.RouteValue,
                    existing.DefaultModel ?? string.Empty,
                    ct).ConfigureAwait(false);
                break;
            case ProvisionThenUse provisioning:
                var proxyToken = await IssueAccessTokenAsync(context.Subject, ProxyScope, ct).ConfigureAwait(false);
                var provisioned = await _catalogClient
                    .ProvisionAsync(context, proxyToken, provisioning.ProvisionEndpointId, ct)
                    .ConfigureAwait(false);
                var provisionedOption = ToOption(provisioned);
                EnsureSelectable(provisionedOption);
                await SaveAsync(
                    context,
                    provisionedOption.RouteValue,
                    provisionedOption.DefaultModel ?? string.Empty,
                    ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unsupported LLM preset activation for '{preset.Id}'.");
        }
    }

    private async Task<string> IssueAccessTokenAsync(ExternalSubjectRef subject, string scope, CancellationToken ct)
    {
        if (_broker is null)
            return string.Empty;

        var handle = await _broker
            .IssueShortLivedAsync(subject, new CapabilityScope { Value = scope }, ct)
            .ConfigureAwait(false);
        return handle.AccessToken;
    }

    private static void EnsureSelectable(UserLlmOption option)
    {
        if (!option.Allowed)
            throw new InvalidOperationException($"LLM service '{option.DisplayName}' is not allowed for this user.");
        if (!string.Equals(option.Status, "ready", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"LLM service '{option.DisplayName}' is not ready: {option.Status}.");
    }

    public async Task ResetAsync(UserLlmSelectionContext context, CancellationToken ct)
    {
        var current = await ReadCurrentAsync(context, ct).ConfigureAwait(false);
        var cleared = current with
        {
            DefaultModel = string.Empty,
            PreferredLlmRoute = UserConfigLlmRouteDefaults.Gateway,
        };
        await SaveConfigAsync(context, cleared, ct).ConfigureAwait(false);
    }

    private async Task SaveModelOnlyAsync(UserLlmSelectionContext context, string model, CancellationToken ct)
    {
        var current = await ReadCurrentAsync(context, ct).ConfigureAwait(false);
        var merged = current with { DefaultModel = model };
        await SaveConfigAsync(context, merged, ct).ConfigureAwait(false);
    }

    private async Task SaveAsync(
        UserLlmSelectionContext context,
        string preferredRoute,
        string defaultModel,
        CancellationToken ct)
    {
        var current = await ReadCurrentAsync(context, ct).ConfigureAwait(false);
        var merged = current with
        {
            DefaultModel = defaultModel.Trim(),
            PreferredLlmRoute = preferredRoute.Trim(),
        };
        await SaveConfigAsync(context, merged, ct).ConfigureAwait(false);
    }

    private async Task<StudioUserConfig> ReadCurrentAsync(UserLlmSelectionContext context, CancellationToken ct)
    {
        if (_queryPort is null)
            return new StudioUserConfig(string.Empty);

        try
        {
            return await _queryPort.GetAsync(RequireBindingId(context), ct).ConfigureAwait(false);
        }
        catch
        {
            return new StudioUserConfig(string.Empty);
        }
    }

    private Task SaveConfigAsync(UserLlmSelectionContext context, StudioUserConfig config, CancellationToken ct)
    {
        if (_commandService is null)
            throw new InvalidOperationException("User LLM preference writes are not enabled in this deployment.");

        return _commandService.SaveAsync(RequireBindingId(context), config, ct);
    }

    private static UserLlmOptionsQuery ToQuery(UserLlmSelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new UserLlmOptionsQuery(
            context.BindingId.Clone(),
            context.Subject.Clone(),
            context.RegistrationScopeId);
    }

    private static string RequireBindingId(UserLlmSelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bindingId = context.BindingId?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(bindingId))
            throw new BindingNotFoundException(context.Subject);
        return bindingId;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
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
}
