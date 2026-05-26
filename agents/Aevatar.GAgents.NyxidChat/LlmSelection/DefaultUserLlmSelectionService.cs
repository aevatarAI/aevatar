using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class DefaultUserLlmSelectionService : IUserLlmSelectionService
{
    private const string InternalCatalogToken = "channel-context";

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

        await SavePreferenceAsync(
            context,
            InternalCatalogToken,
            new SaveUserLlmPreferenceCommand(
                ServiceId: option.ServiceId,
                Model: UserLlmPreferenceWriteCore.NormalizeOptional(modelOverride) ?? option.DefaultModel),
            ct: ct).ConfigureAwait(false);
    }

    public Task SetModelOverrideAsync(
        UserLlmSelectionContext context,
        string model,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return SavePreferenceAsync(
            context,
            InternalCatalogToken,
            new SaveUserLlmPreferenceCommand(Model: model.Trim()),
            ct);
    }

    public async Task ApplyPresetAsync(
        UserLlmSelectionContext context,
        string presetId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);

        var statusToken = await IssueAccessTokenAsync(context.Subject, AevatarOAuthClientScopes.Proxy, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(statusToken))
            statusToken = InternalCatalogToken;

        await SavePreferenceAsync(
            context,
            statusToken,
            new SaveUserLlmPreferenceCommand(PresetId: presetId.Trim()),
            ct).ConfigureAwait(false);
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

    public Task ResetAsync(UserLlmSelectionContext context, CancellationToken ct) =>
        SavePreferenceAsync(
            context,
            InternalCatalogToken,
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
        var writer = scope.ServiceProvider.GetService<UserLlmPreferenceWriter>();
        if (writer is null)
        {
            var queryPort = scope.ServiceProvider.GetService<IUserConfigQueryPort>();
            var commandService = scope.ServiceProvider.GetService<IUserConfigCommandService>();
            if (queryPort is null || commandService is null)
                throw new InvalidOperationException("User LLM preference writes are not enabled in this deployment.");

            writer = new UserLlmPreferenceWriter(
                queryPort,
                commandService,
                new ChannelUserLlmCatalogPort(_catalogClient, ToQuery(context), context));
        }

        if (writer is null)
            throw new InvalidOperationException("User LLM preference writes are not enabled in this deployment.");

        try
        {
            await writer
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

    private sealed class ChannelUserLlmCatalogPort(
        INyxIdLlmServiceCatalogClient catalogClient,
        UserLlmOptionsQuery query,
        UserLlmSelectionContext context) : IUserLlmCatalogPort
    {
        public Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct) =>
            catalogClient.GetServicesAsync(query, bearerToken, ct);

        public Task<NyxIdLlmService> ProvisionAsync(string bearerToken, string provisionEndpointId, CancellationToken ct) =>
            catalogClient.ProvisionAsync(context, bearerToken, provisionEndpointId, ct);
    }

}
