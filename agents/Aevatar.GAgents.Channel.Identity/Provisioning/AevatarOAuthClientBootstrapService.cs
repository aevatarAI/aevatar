using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

    private readonly ICommandDispatchService<EnsureAevatarOAuthClientProvisionedCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> _provisioningDispatch;
    private readonly ILogger<AevatarOAuthClientBootstrapService> _logger;

    public AevatarOAuthClientBootstrapService(
        ICommandDispatchService<EnsureAevatarOAuthClientProvisionedCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> provisioningDispatch,
        ILogger<AevatarOAuthClientBootstrapService> logger)
    {
        _provisioningDispatch = provisioningDispatch ?? throw new ArgumentNullException(nameof(provisioningDispatch));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        var accepted = await _provisioningDispatch
            .DispatchAsync(new EnsureAevatarOAuthClientProvisionedCommand
            {
                NyxidAuthority = authority,
                RedirectUri = redirectUri,
                ClientName = ClientName,
            }, ct)
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
}
