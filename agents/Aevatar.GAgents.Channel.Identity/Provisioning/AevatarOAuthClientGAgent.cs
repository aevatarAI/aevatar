using System.Security.Cryptography;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Cluster-singleton actor that owns the aevatar host's OAuth client
/// registration against NyxID. Holds <see cref="AevatarOAuthClientState"/>
/// (client_id + HMAC key + observed broker capability) so the entire silo
/// fleet shares one provisioning record — no IConfiguration / appsettings /
/// secrets store needed. See cluster bootstrap service for the startup
/// signal wiring.
/// </summary>
[GAgent("channel.identity.aevatar-oauth-client")]
public sealed class AevatarOAuthClientGAgent : GAgentBase<AevatarOAuthClientState>
{
    // Refactor (iter71/cluster-071-identity-projection-rebuild-events):
    //   Old pattern: emit no-op ProjectionRebuildRequested event in command handler to trigger projection materialization
    //   New principle: Identity actor only persists real identity facts; projection materialization owned by projection lifecycle/materializer/bootstrap
    /// <summary>
    /// Well-known actor id. There is exactly one of these per cluster.
    /// </summary>
    public const string WellKnownId = "aevatar-oauth-client";

    private const int HmacKeyBytes = 32; // 256-bit
    /// <summary>
    /// Initial kid assigned on first HMAC key seed. Subsequent rotations
    /// produce <c>"v2"</c>, <c>"v3"</c>, etc. — the integer suffix is parsed
    /// + incremented from the current kid; if parsing fails the rotation
    /// falls back to <c>"v{rotated_at_unix}"</c>.
    /// </summary>
    public const string InitialHmacKid = "v1";
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private const string ProvisioningRetryCallbackPrefix = "aevatar-oauth-client-provisioning-retry";

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="StateTransitionMatcher"/> handles <c>Any</c>-wrapped payloads
    /// transparently via <c>ProtobufContractCompatibility.TryUnpack</c>, so the
    /// event-store's wrapped form ("type.googleapis.com/...") is matched the
    /// same as a directly-typed instance. No "unrecognised event type"
    /// pre-check fires here — the earlier guard incorrectly classified every
    /// Any-wrapped event as unknown and produced noisy warnings on every
    /// activation replay (one warning per persisted event).
    /// </remarks>
    protected override AevatarOAuthClientState TransitionState(AevatarOAuthClientState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<AevatarOAuthClientProvisionedEvent>(ApplyProvisioned)
            .On<AevatarOAuthClientHmacKeyRotatedEvent>(ApplyHmacKeyRotated)
            .On<AevatarOAuthClientBrokerCapabilityObservedEvent>(ApplyBrokerCapabilityObserved)
            .On<AevatarOAuthClientProvisioningRetryScheduledEvent>(ApplyProvisioningRetryScheduled)
            .On<AevatarOAuthClientProvisioningRetryClearedEvent>(ApplyProvisioningRetryCleared)
            .On<AevatarOAuthClientDriftReconciledEvent>(static (state, _) => state)
            .OrCurrent();

    // ─── Commands ───

    /// <summary>
    /// Bootstrap entry-point. Cold-boot in a multi-silo cluster: every silo
    /// sends this command to the well-known actor; the actor's single-
    /// threaded handler turns the broadcast into exactly one DCR call (the
    /// first command serialized) and a no-op for the rest. This serializes
    /// the external side-effect at NyxID and prevents the orphan-clients
    /// race that earlier versions exhibited (PR #521 review consensus).
    /// </summary>
    [EventHandler]
    public async Task HandleEnsureProvisioned(EnsureAevatarOAuthClientProvisionedCommand cmd)
    {
        await HandleEnsureProvisionedAsync(cmd, allowPendingRetryBypass: false).ConfigureAwait(false);
    }

    private async Task HandleEnsureProvisionedAsync(
        EnsureAevatarOAuthClientProvisionedCommand cmd,
        bool allowPendingRetryBypass)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (!allowPendingRetryBypass && HasPendingProvisioningRetryNotDue(DateTimeOffset.UtcNow))
        {
            Logger.LogInformation(
                "Ignoring duplicate external aevatar OAuth client provisioning ensure while actor-owned retry is pending: attempt={Attempt}, due_unix_ms={DueUnixMs}",
                State.ProvisioningRetryAttempt,
                State.ProvisioningRetryDueUnixMs);
            return;
        }

        try
        {
            await TryEnsureProvisionedAsync(cmd).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ScheduleProvisioningRetryAsync(cmd, ex).ConfigureAwait(false);
        }
    }

    private async Task TryEnsureProvisionedAsync(EnsureAevatarOAuthClientProvisionedCommand cmd)
    {
        // Refactor (iter53/issue-906-oauth-bootstrap):
        //   Old pattern: Hosted service ran a Task.Run + Task.Delay retry loop driving OAuth client provisioning lifecycle from outside the actor turn.
        //   New principle: Bootstrap is one-shot signal publisher; AevatarOAuthClientGAgent owns retry/backoff via durable self-callbacks and drift reconciliation in actor turn.
        if (string.IsNullOrWhiteSpace(cmd.NyxidAuthority))
        {
            Logger.LogWarning("EnsureProvisioned rejected: nyxid_authority is required");
            return;
        }
        if (string.IsNullOrWhiteSpace(cmd.RedirectUri))
        {
            Logger.LogWarning("EnsureProvisioned rejected: redirect_uri is required");
            return;
        }
        if (string.IsNullOrWhiteSpace(cmd.StudioLoginRedirectUri))
        {
            Logger.LogWarning("EnsureProvisioned rejected: studio_login_redirect_uri is required");
            return;
        }

        var sameClient = !string.IsNullOrEmpty(State.ClientId)
            && string.Equals(State.NyxidAuthority, cmd.NyxidAuthority, StringComparison.Ordinal);

        // Redirect URI drift: re-DCR when the persisted callback no longer
        // matches what the resolver hands us. Original prod incident
        // (aismart-app-mainnet 2026-04-30): the cluster registered against
        // NyxID with the Kestrel wildcard `http://+:8080/...` because the
        // resolver mistakenly read ASPNETCORE_URLS. After the resolver fix
        // the env now produces the correct public URL, but the actor's
        // existing client_id at NyxID is still bound to the wrong callback
        // — every /init authorizes to a non-routable host. Empty stored
        // redirect_uri is legacy/unknown, not a valid match: the broken
        // production state already has a client_id and no recorded callback,
        // so we must re-DCR once and persist the public redirect URI.
        var redirectUriDrifted = sameClient
            && (string.IsNullOrEmpty(State.RedirectUri)
                || !string.Equals(State.RedirectUri, cmd.RedirectUri, StringComparison.Ordinal));
        var studioLoginRedirectUriDrifted = sameClient
            && (string.IsNullOrEmpty(State.StudioLoginRedirectUri)
                || !string.Equals(State.StudioLoginRedirectUri, cmd.StudioLoginRedirectUri, StringComparison.Ordinal));
        var oauthScopeDrifted = sameClient && !AevatarOAuthClientScopes.ContainsRequiredScopes(State.OauthScope);

        if (sameClient && !redirectUriDrifted && !studioLoginRedirectUriDrifted && !oauthScopeDrifted)
        {
            // Seed HMAC key on first activation against an existing client_id
            // (defence-in-depth against partial state loaded from snapshots).
            // Returning here is intentional: HmacKeyRotatedEvent itself
            // publishes the committed state root, so the projector materializes the
            // readmodel without needing an additional rebuild trigger.
            if (State.HmacKey.Length == 0)
            {
                await PersistDomainEventAsync(BuildHmacKeyRotatedEvent());
                await ClearProvisioningRetryAsync("hmac_seeded_existing_client");
                Logger.LogInformation("Seeded HMAC key for aevatar OAuth client (existing client_id)");
                return;
            }

            await ClearProvisioningRetryAsync("ensure_already_provisioned");
            Logger.LogInformation(
                "Aevatar OAuth client already provisioned: actorId={ActorId}, authority={Authority}; no OAuth client fact changed",
                Id,
                cmd.NyxidAuthority);
            return;
        }

        if (redirectUriDrifted)
        {
            Logger.LogWarning(
                "Aevatar OAuth client redirect URI drifted: stored='{Stored}', resolved='{Resolved}'. " +
                "Re-running DCR to register a new client_id at NyxID with the corrected callback target.",
                State.RedirectUri,
                cmd.RedirectUri);
        }
        if (oauthScopeDrifted)
        {
            Logger.LogWarning(
                "Aevatar OAuth client scope drifted: stored='{Stored}', required='{Required}'. " +
                "Re-running DCR to register a new client_id at NyxID with proxy-capable scopes.",
                State.OauthScope,
                AevatarOAuthClientScopes.AuthorizationScope);
        }
        if (studioLoginRedirectUriDrifted)
        {
            Logger.LogWarning(
                "Aevatar OAuth client Studio login redirect URI drifted: stored='{Stored}', resolved='{Resolved}'. " +
                "Re-running DCR to register a new client_id at NyxID with the Console callback target.",
                State.StudioLoginRedirectUri,
                cmd.StudioLoginRedirectUri);
        }

        var registrar = Services.GetService<NyxIdDynamicClientRegistrationClient>();
        if (registrar is null)
        {
            throw new InvalidOperationException(
                "EnsureProvisioned cannot resolve NyxIdDynamicClientRegistrationClient; DI is missing the registrar.");
        }

        // CancellationToken.None is the contract here: the framework's
        // [EventHandler] dispatcher (EventHandlerDiscoverer.TryBuild requires
        // parameters.Length == 1) does not surface a turn-scoped CT, so the
        // actor takes ownership of completing this single external side-
        // effect atomically. The named HTTP client's per-request timeout
        // (default 100s) bounds the worst case during silo shutdown — the
        // DCR call itself is bounded.
        var clientName = string.IsNullOrWhiteSpace(cmd.ClientName) ? "aevatar" : cmd.ClientName;
        var registration = await registrar
            .RegisterPublicClientAsync(
                cmd.NyxidAuthority,
                clientName,
                [cmd.RedirectUri, cmd.StudioLoginRedirectUri],
                CancellationToken.None)
            .ConfigureAwait(false);

        // Cluster-shared Garnet event store + brief two-pod overlap during
        // a K8s rolling deploy lets two grain activations of this well-
        // known actor each replay v=N, each call DCR (each getting its own
        // client_id from NyxID), and each try to commit Provisioned at
        // expectedVersion=N. One wins, one sees OCC. See issue #549.
        //
        // Pass an absorber callback to PersistDomainEventAsync rather than
        // catching OCC ourselves: the framework refreshes State from the
        // store before invoking the callback, and there is no protected
        // "replay state" helper a future handler could misuse outside an
        // active commit path (PR #552 review codex/glm-5.1).
        var previousRedirectUri = State.RedirectUri;
        var previousOauthScope = State.OauthScope;
        var previousStudioLoginRedirectUri = State.StudioLoginRedirectUri;
        await PersistDomainEventAsync(
            new AevatarOAuthClientProvisionedEvent
            {
                ClientId = registration.ClientId,
                ClientIdIssuedAtUnix = registration.IssuedAt.ToUnixTimeSeconds(),
                NyxidAuthority = cmd.NyxidAuthority,
                PersistedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                RedirectUri = cmd.RedirectUri,
                OauthScope = AevatarOAuthClientScopes.AuthorizationScope,
                StudioLoginRedirectUri = cmd.StudioLoginRedirectUri,
            },
            onOptimisticConcurrencyConflict: occ => AbsorbPeerDcrProvisioningAsync(cmd, registration.ClientId, occ));

        // The loser absorber path returned (peer committed the same
        // shape). Detect that by comparing State.ClientId — if it is the
        // peer's id, this handler must NOT continue with the post-
        // Provisioned HMAC seed against state we did not produce.
        if (!string.Equals(State.ClientId, registration.ClientId, StringComparison.Ordinal))
        {
            if (StateMatchesProvisioningIntent(cmd))
                await ClearProvisioningRetryAsync("ensure_provisioned_by_peer").ConfigureAwait(false);
            return;
        }

        await PersistDriftReconciledEventsAsync(
            redirectUriDrifted,
            studioLoginRedirectUriDrifted,
            oauthScopeDrifted,
            previousRedirectUri,
            previousStudioLoginRedirectUri,
            previousOauthScope);

        Logger.LogInformation(
            "Provisioned aevatar OAuth client via DCR: client_id={ClientId}, authority={Authority}, redirect_uri={RedirectUri}, studio_login_redirect_uri={StudioLoginRedirectUri}",
            registration.ClientId,
            cmd.NyxidAuthority,
            cmd.RedirectUri,
            cmd.StudioLoginRedirectUri);

        if (State.HmacKey.Length == 0)
        {
            // Distinct race shape from the DCR commit OCC: this handler
            // ALREADY successfully committed Provisioned, so
            // registration.ClientId is the active cluster client — not
            // an orphan. A peer wrote at v+2 between our Provisioned
            // commit and this seed (e.g. their own HMAC seed). Absorb
            // without orphan messaging when the post-replay state has a
            // non-empty HMAC.
            await PersistDomainEventAsync(
                BuildHmacKeyRotatedEvent(),
                onOptimisticConcurrencyConflict: AbsorbPeerHmacSeedAsync);
            Logger.LogInformation("Seeded HMAC key for aevatar OAuth client");
        }

        await ClearProvisioningRetryAsync("ensure_provisioned");
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleProvisioningRetryFired(AevatarOAuthClientProvisioningRetryFiredEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!RetryFiredMatchesPendingState(evt))
        {
            Logger.LogInformation(
                "Ignoring stale aevatar OAuth client provisioning retry callback: callback_id={CallbackId}, attempt={Attempt}, due_unix_ms={DueUnixMs}",
                evt.CallbackId,
                evt.Attempt,
                evt.DueUnixMs);
            return;
        }

        await HandleEnsureProvisionedAsync(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = State.ProvisioningRetryAuthority,
            RedirectUri = State.ProvisioningRetryRedirectUri,
            StudioLoginRedirectUri = State.ProvisioningRetryStudioLoginRedirectUri,
            ClientName = State.ProvisioningRetryClientName,
        }, allowPendingRetryBypass: true).ConfigureAwait(false);
    }

    private bool HasPendingProvisioningRetryNotDue(DateTimeOffset now) =>
        State.ProvisioningRetryAttempt > 0
        && State.ProvisioningRetryDueUnixMs > now.ToUnixTimeMilliseconds();

    private bool RetryFiredMatchesPendingState(AevatarOAuthClientProvisioningRetryFiredEvent evt)
    {
        if (State.ProvisioningRetryAttempt <= 0)
            return false;

        if (!string.Equals(evt.CallbackId, State.ProvisioningRetryCallbackId, StringComparison.Ordinal)
            || evt.Attempt != State.ProvisioningRetryAttempt
            || evt.DueUnixMs != State.ProvisioningRetryDueUnixMs
            || !string.Equals(evt.NyxidAuthority, State.ProvisioningRetryAuthority, StringComparison.Ordinal)
            || !string.Equals(evt.RedirectUri, State.ProvisioningRetryRedirectUri, StringComparison.Ordinal)
            || !string.Equals(evt.StudioLoginRedirectUri, State.ProvisioningRetryStudioLoginRedirectUri, StringComparison.Ordinal)
            || !string.Equals(evt.ClientName, State.ProvisioningRetryClientName, StringComparison.Ordinal))
        {
            return false;
        }

        if (ActiveInboundEnvelope is not null &&
            RuntimeCallbackEnvelopeStateReader.TryRead(ActiveInboundEnvelope, out var callbackState))
        {
            return string.Equals(callbackState.CallbackId, State.ProvisioningRetryCallbackId, StringComparison.Ordinal)
                   && callbackState.Generation == State.ProvisioningRetryCallbackGeneration;
        }

        return evt.CallbackGeneration <= 0 || evt.CallbackGeneration == State.ProvisioningRetryCallbackGeneration;
    }

    private bool StateMatchesProvisioningIntent(EnsureAevatarOAuthClientProvisionedCommand cmd) =>
        !string.IsNullOrEmpty(State.ClientId)
        && string.Equals(State.NyxidAuthority, cmd.NyxidAuthority, StringComparison.Ordinal)
        && string.Equals(State.RedirectUri, cmd.RedirectUri, StringComparison.Ordinal)
        && string.Equals(State.StudioLoginRedirectUri, cmd.StudioLoginRedirectUri, StringComparison.Ordinal)
        && AevatarOAuthClientScopes.ContainsRequiredScopes(State.OauthScope)
        && State.HmacKey.Length > 0;

    private async Task ScheduleProvisioningRetryAsync(
        EnsureAevatarOAuthClientProvisionedCommand cmd,
        Exception error)
    {
        var normalized = NormalizeEnsureProvisionedCommand(cmd);
        if (normalized is null)
        {
            Logger.LogWarning(
                error,
                "Aevatar OAuth client provisioning failed but retry was not scheduled because command fields are invalid");
            return;
        }

        var nextAttempt = State.ProvisioningRetryAttempt > 0 ? State.ProvisioningRetryAttempt + 1 : 1;
        var delay = ComputeRetryDelay(nextAttempt);
        var due = DateTimeOffset.UtcNow.Add(delay);
        var callbackId = BuildProvisioningRetryCallbackId();
        var callbackPayload = new AevatarOAuthClientProvisioningRetryFiredEvent
        {
            Attempt = nextAttempt,
            DueUnixMs = due.ToUnixTimeMilliseconds(),
            NyxidAuthority = normalized.NyxidAuthority,
            RedirectUri = normalized.RedirectUri,
            StudioLoginRedirectUri = normalized.StudioLoginRedirectUri,
            ClientName = normalized.ClientName,
            CallbackId = callbackId,
            FiredAtUnixMs = due.ToUnixTimeMilliseconds(),
        };
        var lease = await ScheduleSelfDurableTimeoutAsync(
                callbackId,
                delay,
                callbackPayload,
                ct: CancellationToken.None)
            .ConfigureAwait(false);

        await PersistDomainEventAsync(new AevatarOAuthClientProvisioningRetryScheduledEvent
        {
            Attempt = nextAttempt,
            DueUnixMs = due.ToUnixTimeMilliseconds(),
            NyxidAuthority = normalized.NyxidAuthority,
            RedirectUri = normalized.RedirectUri,
            StudioLoginRedirectUri = normalized.StudioLoginRedirectUri,
            ClientName = normalized.ClientName,
            CallbackId = lease.CallbackId,
            CallbackGeneration = lease.Generation,
            LastError = error.Message,
            ScheduledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        }).ConfigureAwait(false);

        Logger.LogWarning(
            error,
            "Aevatar OAuth client provisioning failed; actor-owned retry scheduled in {DelaySeconds}s (attempt={Attempt}, callback_generation={Generation}).",
            (int)delay.TotalSeconds,
            nextAttempt,
            lease.Generation);
    }

    private async Task ClearProvisioningRetryAsync(string reason)
    {
        if (State.ProvisioningRetryAttempt <= 0)
            return;

        await PersistDomainEventAsync(new AevatarOAuthClientProvisioningRetryClearedEvent
        {
            Reason = reason,
            ClearedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        }).ConfigureAwait(false);
    }

    private async Task PersistDriftReconciledEventsAsync(
        bool redirectUriDrifted,
        bool studioLoginRedirectUriDrifted,
        bool oauthScopeDrifted,
        string previousRedirectUri,
        string previousStudioLoginRedirectUri,
        string previousOauthScope)
    {
        var events = new List<IMessage>(capacity: 2);
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        if (redirectUriDrifted)
        {
            events.Add(new AevatarOAuthClientDriftReconciledEvent
            {
                DriftKind = "redirect_uri",
                PreviousValue = previousRedirectUri ?? string.Empty,
                ExpectedValue = State.RedirectUri,
                ActiveClientId = State.ClientId,
                ReconciledAt = now,
            });
        }
        if (oauthScopeDrifted)
        {
            events.Add(new AevatarOAuthClientDriftReconciledEvent
            {
                DriftKind = "oauth_scope",
                PreviousValue = previousOauthScope ?? string.Empty,
                ExpectedValue = State.OauthScope,
                ActiveClientId = State.ClientId,
                ReconciledAt = now,
            });
        }
        if (studioLoginRedirectUriDrifted)
        {
            events.Add(new AevatarOAuthClientDriftReconciledEvent
            {
                DriftKind = "studio_login_redirect_uri",
                PreviousValue = previousStudioLoginRedirectUri ?? string.Empty,
                ExpectedValue = State.StudioLoginRedirectUri,
                ActiveClientId = State.ClientId,
                ReconciledAt = now,
            });
        }

        if (events.Count > 0)
            await PersistDomainEventsAsync(events).ConfigureAwait(false);
    }

    private static EnsureAevatarOAuthClientProvisionedCommand? NormalizeEnsureProvisionedCommand(
        EnsureAevatarOAuthClientProvisionedCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.NyxidAuthority)
            || string.IsNullOrWhiteSpace(cmd.RedirectUri)
            || string.IsNullOrWhiteSpace(cmd.StudioLoginRedirectUri))
        {
            return null;
        }

        return new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = cmd.NyxidAuthority.Trim(),
            RedirectUri = cmd.RedirectUri.Trim(),
            StudioLoginRedirectUri = cmd.StudioLoginRedirectUri.Trim(),
            ClientName = string.IsNullOrWhiteSpace(cmd.ClientName) ? "aevatar" : cmd.ClientName.Trim(),
        };
    }

    private static TimeSpan ComputeRetryDelay(int attempt)
    {
        var exponent = Math.Max(0, Math.Min(attempt - 1, 20));
        var ticks = InitialRetryDelay.Ticks;
        for (var i = 0; i < exponent && ticks < MaxRetryDelay.Ticks; i++)
            ticks = Math.Min(ticks * 2, MaxRetryDelay.Ticks);
        return TimeSpan.FromTicks(ticks);
    }

    private static string BuildProvisioningRetryCallbackId() =>
        RuntimeCallbackKeyComposer.BuildCallbackId(ProvisioningRetryCallbackPrefix, WellKnownId);

    private Task<bool> AbsorbPeerDcrProvisioningAsync(
        EnsureAevatarOAuthClientProvisionedCommand cmd,
        string orphanClientId,
        EventStoreOptimisticConcurrencyException occ)
    {
        // Framework already refreshed State from the store before invoking
        // this callback. A peer healed the drift iff the cluster's stored
        // record now matches the command's intended shape.
        var peerHealed = !string.IsNullOrEmpty(State.ClientId)
            && string.Equals(State.NyxidAuthority, cmd.NyxidAuthority, StringComparison.Ordinal)
            && string.Equals(State.RedirectUri, cmd.RedirectUri, StringComparison.Ordinal)
            && string.Equals(State.StudioLoginRedirectUri, cmd.StudioLoginRedirectUri, StringComparison.Ordinal)
            && AevatarOAuthClientScopes.ContainsRequiredScopes(State.OauthScope)
            && State.HmacKey.Length > 0;

        if (peerHealed)
        {
            Logger.LogWarning(
                "Aevatar OAuth client OCC race resolved by peer commit; absorbing this attempt as a no-op. "
                + "peer_client_id={PeerClientId}, orphan_client_id={OrphanClientId}, "
                + "expected_version={Expected}, actual_version={Actual}. "
                + "Ops should delete the orphan client at NyxID admin so it stops counting against the registration list.",
                State.ClientId,
                orphanClientId,
                occ.ExpectedVersion,
                occ.ActualVersion);
            return Task.FromResult(true);
        }

        Logger.LogError(
            "Aevatar OAuth client OCC race did not converge on the desired shape after replay; actor-owned retry will re-evaluate. "
            + "stored_client_id={StoredClientId}, stored_redirect_uri={StoredRedirect}, expected_redirect_uri={ExpectedRedirect}, "
            + "orphan_client_id={OrphanClientId}, expected_version={Expected}, actual_version={Actual}.",
            State.ClientId,
            State.RedirectUri,
            cmd.RedirectUri,
            orphanClientId,
            occ.ExpectedVersion,
            occ.ActualVersion);
        return Task.FromResult(false);
    }

    private Task<bool> AbsorbPeerHmacSeedAsync(EventStoreOptimisticConcurrencyException occ)
    {
        if (State.HmacKey.Length > 0)
        {
            Logger.LogWarning(
                "Aevatar OAuth client HMAC-seed OCC race absorbed: peer activation already seeded a key. "
                + "active_client_id={ClientId}, expected_version={Expected}, actual_version={Actual}.",
                State.ClientId,
                occ.ExpectedVersion,
                occ.ActualVersion);
            return Task.FromResult(true);
        }

        Logger.LogError(
            "Aevatar OAuth client HMAC-seed OCC fired but the post-replay state has no HMAC key; actor-owned retry will complete the seed. "
            + "active_client_id={ClientId}, expected_version={Expected}, actual_version={Actual}.",
            State.ClientId,
            occ.ExpectedVersion,
            occ.ActualVersion);
        return Task.FromResult(false);
    }

    /// <summary>
    /// Manual override path: persists a caller-supplied client_id without
    /// calling NyxID DCR. Tests + manual operator scripts use this; the
    /// production bootstrap path uses
    /// <see cref="HandleEnsureProvisioned"/> instead so the actor (not the
    /// caller) mediates the DCR call. Idempotent: re-issuing the same
    /// snapshot (client_id + authority + redirect_uri + studio_login_redirect_uri + oauth_scope) is a
    /// no-op. Always seeds a fresh HMAC key when the state has none —
    /// bootstrap and provisioning are single-step.
    /// </summary>
    /// <remarks>
    /// The same-snapshot check covers redirect_uri + studio_login_redirect_uri + oauth_scope on top of
    /// client_id + authority because the operator-rebuild path
    /// (<c>POST /api/oauth/aevatar-client/rebuild</c>, issue #549) must be
    /// able to heal a wedged actor whose state has the right client_id but
    /// stale or empty redirect_uri / oauth_scope — leaving those drifted
    /// would let the next bootstrap re-DCR and replace the operator's
    /// freshly-pinned client_id with a new (orphan-creating) one.
    /// </remarks>
    [EventHandler]
    public async Task HandleProvision(ProvisionAevatarOAuthClientCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (string.IsNullOrWhiteSpace(cmd.ClientId))
        {
            Logger.LogWarning("ProvisionAevatarOAuthClient rejected: client_id is required");
            return;
        }
        if (string.IsNullOrWhiteSpace(cmd.NyxidAuthority))
        {
            Logger.LogWarning("ProvisionAevatarOAuthClient rejected: nyxid_authority is required");
            return;
        }

        // Empty cmd field = "field not supplied by this caller", NOT "set
        // to empty". Otherwise a legacy / pre-redirect_uri caller (e.g.
        // ProvisionAevatarOAuthClientCommand v1 wire-compatibility, manual
        // operator scripts that only know client_id + authority) would
        // overwrite previously-persisted redirect_uri / oauth_scope with
        // "" — and the next bootstrap pass would observe the cleared
        // value, detect drift, re-DCR the freshly-pinned client, and
        // rotate it away. Codex P1 on PR #570.
        var redirectUri = string.IsNullOrEmpty(cmd.RedirectUri) ? State.RedirectUri : cmd.RedirectUri;
        var oauthScope = string.IsNullOrEmpty(cmd.OauthScope) ? State.OauthScope : cmd.OauthScope;
        var studioLoginRedirectUri = string.IsNullOrEmpty(cmd.StudioLoginRedirectUri)
            ? State.StudioLoginRedirectUri
            : cmd.StudioLoginRedirectUri;
        var sameSnapshot = string.Equals(State.ClientId, cmd.ClientId, StringComparison.Ordinal)
            && string.Equals(State.NyxidAuthority, cmd.NyxidAuthority, StringComparison.Ordinal)
            && string.Equals(State.RedirectUri, redirectUri, StringComparison.Ordinal)
            && string.Equals(State.StudioLoginRedirectUri, studioLoginRedirectUri, StringComparison.Ordinal)
            && string.Equals(State.OauthScope, oauthScope, StringComparison.Ordinal);
        if (!sameSnapshot)
        {
            await PersistDomainEventAsync(new AevatarOAuthClientProvisionedEvent
            {
                ClientId = cmd.ClientId,
                ClientIdIssuedAtUnix = cmd.ClientIdIssuedAtUnix,
                NyxidAuthority = cmd.NyxidAuthority,
                PersistedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                OauthScope = oauthScope,
                RedirectUri = redirectUri,
                StudioLoginRedirectUri = studioLoginRedirectUri,
            });
            Logger.LogInformation(
                "Provisioned aevatar OAuth client: client_id={ClientId}, authority={Authority}, redirect_uri={RedirectUri}, studio_login_redirect_uri={StudioLoginRedirectUri}",
                cmd.ClientId,
                cmd.NyxidAuthority,
                string.IsNullOrEmpty(redirectUri) ? "<unspecified>" : redirectUri,
                string.IsNullOrEmpty(studioLoginRedirectUri) ? "<unspecified>" : studioLoginRedirectUri);
        }

        if (State.HmacKey.Length == 0)
        {
            await PersistDomainEventAsync(BuildHmacKeyRotatedEvent());
            Logger.LogInformation("Seeded HMAC key for aevatar OAuth client");
        }
    }

    /// <summary>
    /// Forces an HMAC key rotation. Production grace window: state-token TTL
    /// (≤5 min) — any callback in flight after rotation fails decode and
    /// the user must re-run /init. ops should rotate at low traffic windows.
    /// </summary>
    [EventHandler]
    public async Task HandleRotateHmacKey(RotateAevatarOAuthClientHmacKeyCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        await PersistDomainEventAsync(BuildHmacKeyRotatedEvent());
        Logger.LogInformation("Rotated HMAC key for aevatar OAuth client");
    }

    /// <summary>
    /// Marks broker capability as observed. Called by the OAuth callback
    /// handler the first time it sees a <c>binding_id</c> in the NyxID token
    /// response — proof that an admin enabled
    /// <c>broker_capability_enabled</c> on the client.  Idempotent.
    /// </summary>
    [EventHandler]
    public async Task HandleObserveBrokerCapability(ObserveBrokerCapabilityCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (State.BrokerCapabilityObserved)
            return;

        var now = DateTimeOffset.UtcNow;
        await PersistDomainEventAsync(new AevatarOAuthClientBrokerCapabilityObservedEvent
        {
            ObservedAtUnix = now.ToUnixTimeSeconds(),
            PersistedAt = Timestamp.FromDateTimeOffset(now),
        });
        Logger.LogInformation("Observed broker_capability_enabled on aevatar OAuth client");
    }

    private AevatarOAuthClientHmacKeyRotatedEvent BuildHmacKeyRotatedEvent()
    {
        var keyBytes = new byte[HmacKeyBytes];
        RandomNumberGenerator.Fill(keyBytes);
        var now = DateTimeOffset.UtcNow;
        var nextKid = NextKid(State.HmacKid, now);

        // Demote the current key to the grace-window slot so any state token
        // signed with it (TTL ≤ 5 min) keeps verifying. Initial seed has no
        // current key yet, so the demoted fields stay empty.
        var demotingExisting = State.HmacKey.Length > 0;
        return new AevatarOAuthClientHmacKeyRotatedEvent
        {
            HmacKey = ByteString.CopyFrom(keyBytes),
            HmacKid = nextKid,
            RotatedAtUnix = now.ToUnixTimeSeconds(),
            PersistedAt = Timestamp.FromDateTimeOffset(now),
            PreviousHmacKey = demotingExisting ? State.HmacKey : ByteString.Empty,
            PreviousHmacKid = demotingExisting ? State.HmacKid : string.Empty,
            PreviousHmacDemotedAtUnix = demotingExisting ? now.ToUnixTimeSeconds() : 0,
        };
    }

    private static string NextKid(string? currentKid, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(currentKid))
            return InitialHmacKid;

        // Kid format "vN" where N is the rotation counter. Parse + increment.
        if (currentKid.Length > 1
            && currentKid[0] == 'v'
            && int.TryParse(currentKid.AsSpan(1), out var current))
        {
            return $"v{current + 1}";
        }

        // Fall back to a timestamp-derived kid so rotation still uniquely
        // labels the new key even if state has been hand-edited or migrated.
        return $"v{now.ToUnixTimeSeconds()}";
    }

    // Refactor (iter97/cluster-097): Old pattern: already-provisioned command
    // no-op activated committed-state projection by side-reading the latest
    // event and manually dispatching a projection envelope. New principle:
    // identity commands only commit real facts; committed-state hook +
    // activation plan provider own projection materialization, and drift
    // repair must be an explicit maintenance/admin path.

    // ─── State transitions ───

    private static AevatarOAuthClientState ApplyProvisioned(
        AevatarOAuthClientState current,
        AevatarOAuthClientProvisionedEvent evt)
    {
        var next = current.Clone();
        next.ClientId = evt.ClientId ?? string.Empty;
        next.ClientIdIssuedAtUnix = evt.ClientIdIssuedAtUnix;
        next.NyxidAuthority = evt.NyxidAuthority ?? string.Empty;
        next.RedirectUri = evt.RedirectUri ?? string.Empty;
        next.StudioLoginRedirectUri = evt.StudioLoginRedirectUri ?? string.Empty;
        next.OauthScope = evt.OauthScope ?? string.Empty;
        // Re-provisioning resets the broker observation: a new client_id
        // starts with broker_capability_enabled=false until ops flips it.
        next.BrokerCapabilityObserved = false;
        next.BrokerCapabilityObservedAtUnix = 0;
        return next;
    }

    private static AevatarOAuthClientState ApplyHmacKeyRotated(
        AevatarOAuthClientState current,
        AevatarOAuthClientHmacKeyRotatedEvent evt)
    {
        var next = current.Clone();
        next.HmacKey = evt.HmacKey ?? ByteString.Empty;
        next.HmacKeyRotatedAtUnix = evt.RotatedAtUnix;
        next.HmacKid = string.IsNullOrEmpty(evt.HmacKid) ? InitialHmacKid : evt.HmacKid;
        next.PreviousHmacKey = evt.PreviousHmacKey ?? ByteString.Empty;
        next.PreviousHmacKid = evt.PreviousHmacKid ?? string.Empty;
        next.PreviousHmacDemotedAtUnix = evt.PreviousHmacDemotedAtUnix;
        return next;
    }

    private static AevatarOAuthClientState ApplyBrokerCapabilityObserved(
        AevatarOAuthClientState current,
        AevatarOAuthClientBrokerCapabilityObservedEvent evt)
    {
        var next = current.Clone();
        next.BrokerCapabilityObserved = true;
        next.BrokerCapabilityObservedAtUnix = evt.ObservedAtUnix;
        return next;
    }

    private static AevatarOAuthClientState ApplyProvisioningRetryScheduled(
        AevatarOAuthClientState current,
        AevatarOAuthClientProvisioningRetryScheduledEvent evt)
    {
        var next = current.Clone();
        next.ProvisioningRetryAttempt = evt.Attempt;
        next.ProvisioningRetryDueUnixMs = evt.DueUnixMs;
        next.ProvisioningRetryAuthority = evt.NyxidAuthority ?? string.Empty;
        next.ProvisioningRetryRedirectUri = evt.RedirectUri ?? string.Empty;
        next.ProvisioningRetryStudioLoginRedirectUri = evt.StudioLoginRedirectUri ?? string.Empty;
        next.ProvisioningRetryClientName = evt.ClientName ?? string.Empty;
        next.ProvisioningRetryCallbackId = evt.CallbackId ?? string.Empty;
        next.ProvisioningRetryCallbackGeneration = evt.CallbackGeneration;
        next.ProvisioningRetryLastError = evt.LastError ?? string.Empty;
        return next;
    }

    private static AevatarOAuthClientState ApplyProvisioningRetryCleared(
        AevatarOAuthClientState current,
        AevatarOAuthClientProvisioningRetryClearedEvent evt)
    {
        _ = evt;
        var next = current.Clone();
        next.ProvisioningRetryAttempt = 0;
        next.ProvisioningRetryDueUnixMs = 0;
        next.ProvisioningRetryAuthority = string.Empty;
        next.ProvisioningRetryRedirectUri = string.Empty;
        next.ProvisioningRetryStudioLoginRedirectUri = string.Empty;
        next.ProvisioningRetryClientName = string.Empty;
        next.ProvisioningRetryCallbackId = string.Empty;
        next.ProvisioningRetryCallbackGeneration = 0;
        next.ProvisioningRetryLastError = string.Empty;
        return next;
    }
}
