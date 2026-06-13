using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class DefaultUserLlmSelectionService : IUserLlmSelectionService
{
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
        var option = UserLlmPreferenceWriteCore.FindOption(view.Available, serviceId.Trim());
        if (option is null)
            throw new InvalidOperationException($"LLM service '{serviceId}' is not available for this user.");

        await SaveSelectedOptionAsync(
            context,
            option,
            UserLlmPreferenceWriteCore.NormalizeOptional(modelOverride) ?? option.DefaultModel,
            preserveCurrentModelWhenMissing: false,
            ct).ConfigureAwait(false);
    }

    public async Task SetModelOverrideAsync(
        UserLlmSelectionContext context,
        string model,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var normalized = model.Trim();
        var bearerToken = UserConfigLlmModel.TryParseRouteModel(normalized) is null
            ? null
            : await IssueRequiredAccessTokenAsync(context.Subject, AevatarOAuthClientScopes.Proxy, ct)
                .ConfigureAwait(false);

        await SavePreferenceAsync(
            context,
            bearerToken,
            new SaveUserLlmPreferenceCommand(Model: normalized),
            ct).ConfigureAwait(false);
    }

    public async Task ApplyPresetAsync(
        UserLlmSelectionContext context,
        string presetId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);

        var statusToken = await IssueRequiredAccessTokenAsync(
                context.Subject,
                AevatarOAuthClientScopes.Proxy,
                ct)
            .ConfigureAwait(false);

        await SavePreferenceAsync(
            context,
            statusToken,
            new SaveUserLlmPreferenceCommand(PresetId: presetId.Trim()),
            ct).ConfigureAwait(false);
    }

    private async Task<string> IssueRequiredAccessTokenAsync(ExternalSubjectRef subject, string scope, CancellationToken ct)
    {
        if (_broker is null)
            throw new InvalidOperationException("Channel LLM catalog writes require a NyxID capability broker.");

        var handle = await _broker
            .IssueShortLivedAsync(subject, new CapabilityScope { Value = scope }, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(handle.AccessToken))
            throw new InvalidOperationException("NyxID capability broker returned an empty access token.");

        return handle.AccessToken;
    }

    public Task ResetAsync(UserLlmSelectionContext context, CancellationToken ct) =>
        SavePreferenceAsync(
            context,
            bearerToken: null,
            new SaveUserLlmPreferenceCommand(Reset: true),
            ct);

    private async Task SavePreferenceAsync(
        UserLlmSelectionContext context,
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct)
    {
        if (_scopeFactory is null)
            throw new InvalidOperationException("User LLM preference writes are not enabled in this deployment.");

        using var scope = _scopeFactory.CreateScope();
        var preferencePort = scope.ServiceProvider.GetService<IChannelUserLlmPreferencePort>();
        if (preferencePort is null)
            throw new InvalidOperationException("User LLM preference writes are not enabled in this deployment.");

        try
        {
            await preferencePort
                .SaveAsync(RequireBindingId(context), bearerToken, command, ct)
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
                "Failed to write LLM selection for binding {BindingId}",
                context.BindingId.Value);
            throw;
        }
    }

    private async Task SaveSelectedOptionAsync(
        UserLlmSelectionContext context,
        UserLlmOption option,
        string? model,
        bool preserveCurrentModelWhenMissing,
        CancellationToken ct)
    {
        if (_scopeFactory is null)
            throw new InvalidOperationException("User LLM preference writes are not enabled in this deployment.");

        using var scope = _scopeFactory.CreateScope();
        var preferencePort = scope.ServiceProvider.GetService<IChannelUserLlmPreferencePort>();
        if (preferencePort is null)
            throw new InvalidOperationException("User LLM preference writes are not enabled in this deployment.");

        try
        {
            await preferencePort
                .SaveSelectedOptionAsync(
                    RequireBindingId(context),
                    option,
                    model,
                    preserveCurrentModelWhenMissing,
                    ct)
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
                "Failed to write LLM selection for binding {BindingId}",
                context.BindingId.Value);
            throw;
        }
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

}
