using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf.WellKnownTypes;
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
    private readonly AevatarOAuthClientProjectionPort _projectionPort;
    private readonly IActorRuntime _actorRuntime;
    private readonly ILogger<AevatarOAuthClientBootstrapService> _logger;
    private readonly CancellationTokenSource _stoppingCts = new();
    private Task? _bootstrapTask;

    public AevatarOAuthClientBootstrapService(
        IAevatarOAuthClientProvider clientProvider,
        AevatarOAuthClientProjectionPort projectionPort,
        IActorRuntime actorRuntime,
        ILogger<AevatarOAuthClientBootstrapService> logger)
    {
        // Provider is registered as a singleton (so are its transitive deps);
        // injecting it directly avoids the brittle "resolve from the root
        // IServiceProvider" pattern, which would silently mask any future
        // scoped dep being added to the provider chain (ValidateScopes
        // catches scoped → singleton at resolve time, not at AddHostedService
        // wiring time).
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
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

    private async Task EnsureProvisionedAsync(CancellationToken ct)
    {
        var authority = NyxIdAuthorityResolver.Resolve(_logger);

        // Activate the projection scope FIRST so the projector subscribes
        // to the actor's committed events before we dispatch the
        // provisioning command. Without this the AevatarOAuthClient
        // readmodel never materializes and IAevatarOAuthClientProvider
        // keeps throwing AevatarOAuthClientNotProvisionedException long
        // after DCR succeeded (production regression observed
        // 2026-04-30 in aismart-app-mainnet — the bootstrap log showed
        // "Provisioned aevatar OAuth client via DCR" + "Seeded HMAC key"
        // immediately after the silo started, but every /init still
        // returned "正在初始化" because no consumer was watching the
        // event stream).
        await _projectionPort
            .EnsureProjectionForActorAsync(AevatarOAuthClientGAgent.WellKnownId, ct)
            .ConfigureAwait(false);

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
        if (cached is not null
            && string.Equals(cached.NyxIdAuthority, authority, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(cached.ClientId)
            && !redirectDrifted)
        {
            _logger.LogInformation(
                "Aevatar OAuth client already provisioned at NyxID: client_id={ClientId}, authority={Authority}, redirect_uri={RedirectUri}, broker_capability_observed={BrokerObserved}",
                cached.ClientId,
                cached.NyxIdAuthority,
                cached.RedirectUri ?? "<unrecorded>",
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
        var actor = await _actorRuntime
            .CreateAsync<AevatarOAuthClientGAgent>(AevatarOAuthClientGAgent.WellKnownId, ct)
            .ConfigureAwait(false);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new EnsureAevatarOAuthClientProvisionedCommand
            {
                NyxidAuthority = authority,
                RedirectUri = redirectUri,
                ClientName = ClientName,
            }),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = AevatarOAuthClientGAgent.WellKnownId },
            },
        };
        await actor.HandleEventAsync(envelope, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Aevatar OAuth client EnsureProvisioned dispatched to {ActorId} (authority={Authority}). " +
            "Production deployments must enable broker_capability_enabled on this client at NyxID admin (one-time per cluster).",
            AevatarOAuthClientGAgent.WellKnownId,
            authority);
    }

    /// <summary>
    /// True only when the snapshot has a recorded redirect URI AND it does
    /// not match the current resolver output. Empty / null stored
    /// redirect_uri is treated as "match anything" so legacy event-store
    /// snapshots that predate the redirect-uri-tracking field do not force
    /// a spurious re-DCR on first redeploy of this code.
    /// </summary>
    private static bool RedirectUriDrifted(string? stored, string resolved) =>
        !string.IsNullOrEmpty(stored)
        && !string.Equals(stored, resolved, StringComparison.Ordinal);
}
