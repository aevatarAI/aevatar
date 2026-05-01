using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StudioUserConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class DefaultUserLlmSelectionService : IUserLlmSelectionService
{
    private const string StatusScope = "llm:status";
    private const string ProxyScope = "llm:proxy";

    private readonly IUserLlmOptionsService _optionsService;
    private readonly INyxIdLlmServiceCatalogClient _catalogClient;
    private readonly INyxIdCapabilityBroker? _broker;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<DefaultUserLlmSelectionService> _logger;

    public DefaultUserLlmSelectionService(
        IUserLlmOptionsService optionsService,
        INyxIdLlmServiceCatalogClient catalogClient,
        IServiceScopeFactory? scopeFactory = null,
        INyxIdCapabilityBroker? broker = null,
        ILogger<DefaultUserLlmSelectionService>? logger = null)
    {
        _optionsService = optionsService ?? throw new ArgumentNullException(nameof(optionsService));
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _scopeFactory = scopeFactory;
        _broker = broker;
        _logger = logger ?? NullLogger<DefaultUserLlmSelectionService>.Instance;
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
            preserveCurrentModelWhenMissing: false,
            ct: ct).ConfigureAwait(false);
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
                    existing.DefaultModel,
                    preserveCurrentModelWhenMissing: true,
                    ct: ct).ConfigureAwait(false);
                break;
            case ProvisionThenUse provisioning:
                var proxyToken = await IssueAccessTokenAsync(context.Subject, ProxyScope, ct).ConfigureAwait(false);
                var provisioned = await _catalogClient
                    .ProvisionAsync(context, proxyToken, provisioning.ProvisionEndpointId, ct)
                    .ConfigureAwait(false);
                var provisionedOption = NyxIdLlmServiceMapping.ToOption(provisioned);
                EnsureSelectable(provisionedOption);
                await SaveAsync(
                    context,
                    provisionedOption.RouteValue,
                    provisionedOption.DefaultModel,
                    preserveCurrentModelWhenMissing: true,
                    ct: ct).ConfigureAwait(false);
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
        string? defaultModel,
        bool preserveCurrentModelWhenMissing,
        CancellationToken ct)
    {
        var current = await ReadCurrentAsync(context, ct).ConfigureAwait(false);
        var resolvedDefaultModel = NormalizeOptional(defaultModel) ??
                                   (preserveCurrentModelWhenMissing ? current.DefaultModel : string.Empty);
        var merged = current with
        {
            DefaultModel = resolvedDefaultModel,
            PreferredLlmRoute = preferredRoute.Trim(),
        };
        await SaveConfigAsync(context, merged, ct).ConfigureAwait(false);
    }

    private async Task<StudioUserConfig> ReadCurrentAsync(UserLlmSelectionContext context, CancellationToken ct)
    {
        if (_scopeFactory is null)
            return new StudioUserConfig(string.Empty);

        using var scope = _scopeFactory.CreateScope();
        var queryPort = scope.ServiceProvider.GetService<IUserConfigQueryPort>();
        if (queryPort is null)
            return new StudioUserConfig(string.Empty);

        try
        {
            return await queryPort.GetAsync(RequireBindingId(context), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read current LLM selection for binding {BindingId}",
                context.BindingId.Value);
            return new StudioUserConfig(string.Empty);
        }
    }

    private async Task SaveConfigAsync(UserLlmSelectionContext context, StudioUserConfig config, CancellationToken ct)
    {
        if (_scopeFactory is null)
            throw new InvalidOperationException("User LLM preference writes are not enabled in this deployment.");

        using var scope = _scopeFactory.CreateScope();
        var commandService = scope.ServiceProvider.GetService<IUserConfigCommandService>();
        if (commandService is null)
            throw new InvalidOperationException("User LLM preference writes are not enabled in this deployment.");

        await commandService.SaveAsync(RequireBindingId(context), config, ct).ConfigureAwait(false);
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

}
