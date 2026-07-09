using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// On host startup, publishes one bootstrap intent to the cluster-singleton
/// OAuth client actor. The actor owns DCR, drift reconciliation, retry, and
/// backoff.
/// </summary>
/// <remarks>
/// Refactor (iter53/issue-906-oauth-bootstrap):
///   Old pattern: Hosted service ran a Task.Run + Task.Delay retry loop driving OAuth client provisioning lifecycle from outside the actor turn.
///   New principle: Bootstrap is one-shot signal publisher; AevatarOAuthClientGAgent owns retry/backoff via durable self-callbacks and drift reconciliation in actor turn.
/// </remarks>
public sealed class AevatarOAuthClientBootstrapService : IHostedService
{
    private const string ClientName = "aevatar";
    public const string ForceDcrOnStartupEnvVar = "AEVATAR_OAUTH_FORCE_DCR_ON_STARTUP";

    private readonly ICommandDispatchService<EnsureAevatarOAuthClientProvisionedCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> _provisioningDispatch;
    private readonly IOptions<AevatarOAuthClientDefaultServiceOptions>? _defaultServiceOptions;
    private readonly ILogger<AevatarOAuthClientBootstrapService> _logger;

    public AevatarOAuthClientBootstrapService(
        ICommandDispatchService<EnsureAevatarOAuthClientProvisionedCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> provisioningDispatch,
        ILogger<AevatarOAuthClientBootstrapService> logger,
        IOptions<AevatarOAuthClientDefaultServiceOptions>? defaultServiceOptions = null)
    {
        _provisioningDispatch = provisioningDispatch ?? throw new ArgumentNullException(nameof(provisioningDispatch));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultServiceOptions = defaultServiceOptions;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        DispatchBootstrapIntentAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    internal async Task DispatchBootstrapIntentAsync(CancellationToken ct)
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        // Refactor (iter53/issue-906-oauth-bootstrap):
        //   Old pattern: Hosted service ran a Task.Run + Task.Delay retry loop driving OAuth client provisioning lifecycle from outside the actor turn.
        //   New principle: Bootstrap is one-shot signal publisher; AevatarOAuthClientGAgent owns retry/backoff via durable self-callbacks and drift reconciliation in actor turn.
        var authority = NyxIdAuthorityResolver.Resolve(_logger);

        // Cold-boot DCR is mediated by the well-known actor (PR #521 review):
        // every silo broadcasts EnsureAevatarOAuthClientProvisionedCommand,
        // and the actor's single-threaded handler turns the broadcast into
        // exactly one DCR HTTP call. Without this seam the bootstrap path
        // races on the projection readmodel and creates orphan OAuth clients
        // at NyxID. The redirect URI must match what the broker sends at
        // authorize / token time — both call sites use NyxIdRedirectUriResolver.
        var redirectUri = NyxIdRedirectUriResolver.Resolve(_logger);
        var redirectUris = NyxIdRedirectUriResolver.ResolveRegisteredRedirectUris(_logger);
        ValidateStartupProvisioningBoundary(authority, redirectUri);
        var defaultServiceSlugs = AevatarOAuthClientDefaultServices.Resolve(_defaultServiceOptions, _logger);
        var forceReprovision = string.Equals(
            Environment.GetEnvironmentVariable(ForceDcrOnStartupEnvVar),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (forceReprovision)
        {
            _logger.LogWarning(
                "Aevatar OAuth client force DCR is enabled by {EnvVar}; disable it after one successful startup to avoid creating a new NyxID client on every restart.",
                ForceDcrOnStartupEnvVar);
        }

        var command = new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = authority,
            RedirectUri = redirectUri,
            ClientName = ClientName,
            ForceReprovision = forceReprovision,
        };
        command.RedirectUris.AddRange(redirectUris);
        command.DefaultServiceSlugs.AddRange(defaultServiceSlugs);

        var accepted = await _provisioningDispatch
            .DispatchAsync(command, ct)
            .ConfigureAwait(false);
        if (!accepted.Succeeded || accepted.Receipt is null)
            throw new InvalidOperationException($"Aevatar OAuth client bootstrap dispatch rejected: {accepted.Error}.");

        _logger.LogInformation(
            "Aevatar OAuth client EnsureProvisioned accepted for {ActorId} (authority={Authority}, command_id={CommandId}). " +
            "Production deployments must enable broker_capability_enabled on this client at NyxID admin (one-time per cluster).",
            AevatarOAuthClientGAgent.WellKnownId,
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
            "Aevatar OAuth client bootstrap blocked: environment '" + environmentName + "' requires explicit " +
            string.Join(" and ", missing) +
            " before Dynamic Client Registration can run. This prevents local or non-production startup from silently registering a NyxID OAuth client against production defaults.";
        _logger.LogError(
            "Aevatar OAuth client bootstrap blocked in environment {Environment}: explicit {MissingEnvVars} required before Dynamic Client Registration can run.",
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
