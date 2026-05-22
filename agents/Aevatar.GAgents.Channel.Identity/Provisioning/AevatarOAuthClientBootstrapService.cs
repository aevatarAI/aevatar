using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Identity.Abstractions;
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
/// <remarks>
/// Bootstrap runs as a non-blocking background task with retry: a transient
/// NyxID/DCR outage during host startup must not leave the cluster
/// permanently unprovisioned (PR #521 Codex P1). The retry loop continues
/// until either provisioning succeeds, the host shuts down, or the back-off
/// reaches the configured ceiling (~30 min); the status endpoint surfaces
/// the gap to ops while the loop runs.
/// </remarks>
public sealed class AevatarOAuthClientBootstrapService : IHostedService
{
    private const string ClientName = "aevatar";

    /// <summary>
    /// First retry delay after a failed provisioning attempt (5s). Doubles
    /// on each failure up to <see cref="MaxRetryDelay"/>.
    /// </summary>
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Upper bound on the back-off interval (30 min). At this point the
    /// loop stops doubling and keeps retrying at this cadence — the cluster
    /// is dead enough that ops attention is required, but we still self-heal
    /// when NyxID returns.
    /// </summary>
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IAevatarOAuthClientProvider _clientProvider;
    private readonly ICommandDispatchService<EnsureAevatarOAuthClientProvisionedCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> _provisioningDispatch;
    private readonly ILogger<AevatarOAuthClientBootstrapService> _logger;
    private readonly CancellationTokenSource _stoppingCts = new();
    private Task? _bootstrapTask;

    public AevatarOAuthClientBootstrapService(
        IAevatarOAuthClientProvider clientProvider,
        ICommandDispatchService<EnsureAevatarOAuthClientProvisionedCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> provisioningDispatch,
        ILogger<AevatarOAuthClientBootstrapService> logger)
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        // Provider is registered as a singleton (so are its transitive deps);
        // injecting it directly avoids the brittle "resolve from the root
        // IServiceProvider" pattern, which would silently mask any future
        // scoped dep being added to the provider chain (ValidateScopes
        // catches scoped → singleton at resolve time, not at AddHostedService
        // wiring time).
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _provisioningDispatch = provisioningDispatch ?? throw new ArgumentNullException(nameof(provisioningDispatch));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Run the bootstrap as a background task so a transient NyxID
        // outage does not block host startup, but DO retry indefinitely
        // (capped backoff) so the cluster self-heals when NyxID returns.
        // Wrap RunWithRetryAsync in a top-level try/catch so any escape
        // (e.g. ObjectDisposed on _stoppingCts after race-y shutdown) is
        // logged and observed rather than swallowed by the unobserved-task
        // exception sink.
        _bootstrapTask = Task.Run(RunSafelyAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunSafelyAsync()
    {
        try
        {
            await RunWithRetryAsync(_stoppingCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stoppingCts.IsCancellationRequested)
        {
            // expected when host shutdown cancels mid-flight
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Aevatar OAuth client bootstrap loop exited unexpectedly; broker mode unavailable until host restart.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stoppingCts.CancelAsync().ConfigureAwait(false);
        if (_bootstrapTask is null)
            return;

        try
        {
            await _bootstrapTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected when the host's shutdown CT fires before the bootstrap
            // task observes its own _stoppingCts cancellation.
        }
        catch (TimeoutException)
        {
            // WaitAsync(TimeSpan)-shaped overloads can throw TimeoutException;
            // host shutdown timeout (Host:ShutdownTimeoutSeconds) is the path
            // here. Log + continue — the task has already been cancelled via
            // _stoppingCts so it will self-terminate even after we return.
            _logger.LogInformation(
                "Aevatar OAuth client bootstrap did not complete within host shutdown timeout; continuing in background.");
        }
    }

    private async Task RunWithRetryAsync(CancellationToken ct)
    {
        var delay = InitialRetryDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureProvisionedAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Aevatar OAuth client bootstrap failed; retrying in {DelaySeconds}s. Broker mode unavailable until the next successful attempt.",
                    (int)delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Exponential backoff with a 30-minute ceiling. Stays self-healing
            // forever without spamming the log on a long outage.
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
        }
    }

    internal async Task EnsureProvisionedAsync(CancellationToken ct)
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        var authority = NyxIdAuthorityResolver.Resolve(_logger);

        // Cold-boot DCR is mediated by the well-known actor (PR #521 review):
        // every silo broadcasts EnsureAevatarOAuthClientProvisionedCommand,
        // and the actor's single-threaded handler turns the broadcast into
        // exactly one DCR HTTP call. Without this seam the bootstrap path
        // races on the projection readmodel and creates orphan OAuth clients
        // at NyxID. The redirect URI must match what the broker sends at
        // authorize / token time — both call sites use NyxIdRedirectUriResolver.
        var redirectUri = NyxIdRedirectUriResolver.Resolve(_logger);

        AevatarOAuthClientSnapshot? cached = null;
        try
        {
            cached = await _clientProvider.GetAsync(ct).ConfigureAwait(false);
        }
        catch (AevatarOAuthClientNotProvisionedException)
        {
            // expected on the first run
        }

        var redirectDrifted = cached is not null && RedirectUriDrifted(cached.RedirectUri, redirectUri);
        var oauthScopeDrifted = cached is not null &&
                                !AevatarOAuthClientScopes.ContainsRequiredScopes(cached.OauthScope);
        if (cached is not null
            && string.Equals(cached.NyxIdAuthority, authority, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(cached.ClientId)
            && !redirectDrifted
            && !oauthScopeDrifted)
        {
            _logger.LogInformation(
                "Aevatar OAuth client already provisioned at NyxID: client_id={ClientId}, authority={Authority}, redirect_uri={RedirectUri}, oauth_scope={OauthScope}, broker_capability_observed={BrokerObserved}",
                cached.ClientId,
                cached.NyxIdAuthority,
                cached.RedirectUri ?? "<unrecorded>",
                cached.OauthScope ?? "<unrecorded>",
                cached.BrokerCapabilityObserved);
            return;
        }

        if (redirectDrifted)
        {
            _logger.LogWarning(
                "Aevatar OAuth client redirect URI drifted (stored='{Stored}', resolved='{Resolved}'); dispatching EnsureProvisioned so the actor re-runs DCR.",
                cached!.RedirectUri,
                redirectUri);
        }
        if (oauthScopeDrifted)
        {
            _logger.LogWarning(
                "Aevatar OAuth client scope drifted (stored='{Stored}', required='{Required}'); dispatching EnsureProvisioned so the actor re-runs DCR.",
                cached!.OauthScope ?? "<unrecorded>",
                AevatarOAuthClientScopes.AuthorizationScope);
        }
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

    /// <summary>
    /// True when the snapshot either predates redirect-uri tracking or no
    /// longer matches the current resolver output. Legacy empty redirect_uri
    /// is unknown, not trustworthy: the production incident this code heals
    /// already has a persisted client_id at NyxID with no recorded callback
    /// in our state, so treating empty as "match anything" would keep the
    /// broken client forever.
    /// </summary>
    private static bool RedirectUriDrifted(string? stored, string resolved) =>
        string.IsNullOrEmpty(stored)
        || !string.Equals(stored, resolved, StringComparison.Ordinal);

}
