using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// On host startup, publishes the configured OAuth client to the
/// cluster-singleton actor. Configuration owns the desired client id; the
/// actor owns the materialized cluster state, HMAC keys, and broker capability
/// observation.
/// </summary>
/// <remarks>
/// Dispatch acceptance is intentionally the only synchronous startup guarantee.
/// Committed actor state and its projection remain asynchronous.
/// </remarks>
public sealed class AevatarOAuthClientBootstrapService : IHostedService
{
    private readonly ICommandDispatchService<ProvisionAevatarOAuthClientCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> _provisioningDispatch;
    private readonly AevatarOAuthClientOptions _clientOptions;
    private readonly AevatarOAuthClientBootstrapOptions _options;
    private readonly ILogger<AevatarOAuthClientBootstrapService> _logger;

    public AevatarOAuthClientBootstrapService(
        ICommandDispatchService<ProvisionAevatarOAuthClientCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> provisioningDispatch,
        IOptions<AevatarOAuthClientOptions> clientOptions,
        IOptions<AevatarOAuthClientBootstrapOptions> options,
        ILogger<AevatarOAuthClientBootstrapService> logger)
    {
        _provisioningDispatch = provisioningDispatch ?? throw new ArgumentNullException(nameof(provisioningDispatch));
        _clientOptions = clientOptions?.Value ?? throw new ArgumentNullException(nameof(clientOptions));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "Aevatar OAuth client bootstrap disabled by {SectionName}:Enabled=false.",
                AevatarOAuthClientBootstrapOptions.SectionName);
            return Task.CompletedTask;
        }

        return DispatchBootstrapIntentAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    internal async Task DispatchBootstrapIntentAsync(CancellationToken ct)
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        if (string.IsNullOrWhiteSpace(_clientOptions.ClientId))
        {
            throw new InvalidOperationException(
                $"Aevatar OAuth client bootstrap requires a non-empty '{AevatarOAuthClientOptions.ClientIdConfigurationKey}'.");
        }

        var authority = NyxIdAuthorityResolver.Resolve(_logger);

        // Every silo may publish the same desired configuration. The actor's
        // idempotent command handler serializes those writes and commits only
        // when client id, authority, callbacks, or scope changed.
        var redirectUri = NyxIdRedirectUriResolver.Resolve(_logger);
        var redirectUris = NyxIdRedirectUriResolver.ResolveRegisteredRedirectUris(_logger);
        ValidateStartupProvisioningBoundary(authority, redirectUri);

        var command = new ProvisionAevatarOAuthClientCommand
        {
            ClientId = _clientOptions.ClientId.Trim(),
            ClientIdIssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            NyxidAuthority = authority,
            RedirectUri = redirectUri,
            OauthScope = AevatarOAuthClientScopes.AuthorizationScope,
        };
        command.RedirectUris.AddRange(redirectUris);

        var accepted = await _provisioningDispatch
            .DispatchAsync(command, ct)
            .ConfigureAwait(false);
        if (!accepted.Succeeded || accepted.Receipt is null)
            throw new InvalidOperationException($"Aevatar OAuth client bootstrap dispatch rejected: {accepted.Error}.");

        _logger.LogInformation(
            "Configured Aevatar OAuth client provisioning accepted for {ActorId} (client_id={ClientId}, authority={Authority}, command_id={CommandId}). " +
            "Production deployments must enable broker_capability_enabled on this client at NyxID admin (one-time per cluster).",
            AevatarOAuthClientGAgent.WellKnownId,
            command.ClientId,
            authority,
            accepted.Receipt.CommandId);
    }

    private void ValidateStartupProvisioningBoundary(string authority, string redirectUri)
    {
        var environmentName = ResolveEnvironmentName();
        if (!RequiresExplicitNyxIdProvisioningConfiguration(environmentName))
            return;

        var authorityOverride = Environment.GetEnvironmentVariable(NyxIdAuthorityResolver.OverrideEnvVar);
        var redirectBaseUrlOverride = Environment.GetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar);
        var authorityCameFromProductionDefault =
            string.IsNullOrWhiteSpace(authorityOverride)
            && string.Equals(
                authority,
                NyxIdAuthorityResolver.DefaultAuthority.TrimEnd('/'),
                StringComparison.Ordinal);
        var redirectCameFromProductionDefault =
            !NyxIdRedirectUriResolver.TryResolveExplicitBaseUrl(redirectBaseUrlOverride, out _)
            && string.Equals(
                redirectUri,
                $"{NyxIdRedirectUriResolver.DefaultPublicBaseUrl}{NyxIdRedirectUriResolver.CallbackPath}",
                StringComparison.Ordinal);

        if (!authorityCameFromProductionDefault && !redirectCameFromProductionDefault)
            return;

        var missing = new List<string>(capacity: 2);
        if (authorityCameFromProductionDefault)
            missing.Add(NyxIdAuthorityResolver.OverrideEnvVar);
        if (redirectCameFromProductionDefault)
            missing.Add(NyxIdRedirectUriResolver.OverrideEnvVar);

        var message =
            "Configured Aevatar OAuth client bootstrap blocked: environment '" + environmentName + "' requires explicit " +
            string.Join(" and ", missing) +
            " before the configured NyxID OAuth client can be activated. This prevents local or non-production startup from using production identity endpoints accidentally.";
        _logger.LogError(
            "Configured Aevatar OAuth client bootstrap blocked in environment {Environment}: explicit {MissingEnvVars} required.",
            environmentName,
            string.Join(",", missing));
        throw new InvalidOperationException(message);
    }

    private static string ResolveEnvironmentName() =>
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? string.Empty;

    private static bool RequiresExplicitNyxIdProvisioningConfiguration(string environmentName) =>
        !string.IsNullOrWhiteSpace(environmentName)
        && !string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
}
