using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// On host startup, provisions the cluster-singleton OAuth client at NyxID
/// (RFC 7591 DCR) when the binding readmodel reports no registered client.
/// Idempotent: subsequent silos boot, see the cached <c>client_id</c>, and
/// skip the call. The actor seeds its own HMAC key on first activation —
/// no operator step needed beyond enabling <c>broker_capability_enabled</c>
/// at NyxID admin once per cluster (see /api/oauth/aevatar-client/status
/// for the post-boot ops handoff).
/// </summary>
public sealed class AevatarOAuthClientBootstrapService : IHostedService
{
    private const string ClientName = "aevatar";

    private readonly IServiceProvider _services;
    private readonly NyxIdDynamicClientRegistrationClient _registrar;
    private readonly IActorRuntime _actorRuntime;
    private readonly ILogger<AevatarOAuthClientBootstrapService> _logger;
    private readonly IConfiguration _configuration;

    public AevatarOAuthClientBootstrapService(
        IServiceProvider services,
        NyxIdDynamicClientRegistrationClient registrar,
        IActorRuntime actorRuntime,
        IConfiguration configuration,
        ILogger<AevatarOAuthClientBootstrapService> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureProvisionedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Bootstrap failure must not block the host startup — other
            // surfaces (legacy bot-owner-shared mode) still work without a
            // provisioned broker. Log loudly and move on; the status endpoint
            // surfaces the gap to ops, and a subsequent /init exercises the
            // path again.
            _logger.LogError(
                ex,
                "Aevatar OAuth client bootstrap failed; broker mode will be unavailable until the next successful provisioning attempt");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureProvisionedAsync(CancellationToken ct)
    {
        var authority = NyxIdAuthorityResolver.Resolve();
        AevatarOAuthClientSnapshot? cached = null;
        try
        {
            cached = await _services.GetRequiredService<IAevatarOAuthClientProvider>().GetAsync(ct).ConfigureAwait(false);
        }
        catch (AevatarOAuthClientNotProvisionedException)
        {
            // expected on the first run
        }

        if (cached is not null
            && string.Equals(cached.NyxIdAuthority, authority, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(cached.ClientId))
        {
            _logger.LogInformation(
                "Aevatar OAuth client already provisioned at NyxID: client_id={ClientId}, authority={Authority}, broker_capability_observed={BrokerObserved}",
                cached.ClientId,
                cached.NyxIdAuthority,
                cached.BrokerCapabilityObserved);
            return;
        }

        var redirectUri = ResolveRedirectUri();
        var registration = await _registrar
            .RegisterPublicClientAsync(authority, ClientName, redirectUri, ct)
            .ConfigureAwait(false);

        var actor = await _actorRuntime
            .CreateAsync<AevatarOAuthClientGAgent>(AevatarOAuthClientGAgent.WellKnownId, ct)
            .ConfigureAwait(false);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new ProvisionAevatarOAuthClientCommand
            {
                ClientId = registration.ClientId,
                ClientIdIssuedAtUnix = registration.IssuedAt.ToUnixTimeSeconds(),
                NyxidAuthority = authority,
            }),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = AevatarOAuthClientGAgent.WellKnownId },
            },
        };
        await actor.HandleEventAsync(envelope, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Aevatar OAuth client provisioned at NyxID: client_id={ClientId}. " +
            "Production deployments must enable broker_capability_enabled on this client at NyxID admin (one-time per cluster).",
            registration.ClientId);
    }

    private string ResolveRedirectUri()
    {
        var serverUrls = _configuration[WebHostDefaults.ServerUrlsKey];
        var firstUrl = serverUrls?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(firstUrl))
            firstUrl = "http://127.0.0.1:5080";
        return $"{firstUrl.TrimEnd('/')}/api/oauth/nyxid-callback";
    }
}
