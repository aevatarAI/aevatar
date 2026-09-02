using System.Security.Cryptography;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
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
/// Cluster-singleton actor that materializes the configured OAuth client and
/// owns its HMAC key plus observed broker capability. Deployment configuration
/// owns the client id; committed actor state keeps the fleet coherent and
/// auditable.
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

    /// <summary>
    /// True when the actor already holds an HMAC key in either shape: a vault
    /// reference (new writes) or legacy plaintext bytes (pre-migration state).
    /// The seed guards must treat a ref-backed key as "present" or they would
    /// re-seed on every command since ref-backed writes leave [hmac_key] empty.
    /// </summary>
    private bool HasHmacKey => State.HmacKey.Length > 0 || State.HmacKeyRef is not null;

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
            .On<AevatarOAuthClientForceReprovisionConsumedEvent>(ApplyForceReprovisionConsumed)
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

        if (!cmd.ForceReprovision && State.ForceReprovisionConsumed)
        {
            await PersistDomainEventAsync(new AevatarOAuthClientForceReprovisionConsumedEvent
            {
                Consumed = false,
                PersistedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }).ConfigureAwait(false);
        }

        var expectedRedirectUris = NormalizeProvisioningRedirectUris(cmd.RedirectUris, cmd.RedirectUri);
        var sameClient = !string.IsNullOrEmpty(State.ClientId)
            && string.Equals(State.NyxidAuthority, cmd.NyxidAuthority, StringComparison.Ordinal);
        var forceReprovision = cmd.ForceReprovision && !State.ForceReprovisionConsumed;

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
        var redirectUriListDrifted = sameClient
            && (State.RedirectUris.Count == 0
                || !RedirectUriListsEqual(State.RedirectUris, expectedRedirectUris));
        var oauthScopeDrifted = sameClient
            && !AevatarOAuthClientScopes.ContainsRequiredAuthorizationScopes(State.OauthScope);

        if (sameClient && !forceReprovision && !redirectUriDrifted && !redirectUriListDrifted && !oauthScopeDrifted)
        {
            // Seed HMAC key on first activation against an existing client_id
            // (defence-in-depth against partial state loaded from snapshots).
            // Returning here is intentional: HmacKeyRotatedEvent itself
            // publishes the committed state root, so the projector materializes the
            // readmodel without needing an additional rebuild trigger.
            if (!HasHmacKey)
            {
                await PersistDomainEventAsync(await BuildHmacKeyRotatedEventAsync());
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
        if (redirectUriListDrifted)
        {
            Logger.LogWarning(
                "Aevatar OAuth client redirect URI allowlist drifted: stored='{Stored}', resolved='{Resolved}'. " +
                "Re-running DCR to register a new client_id at NyxID with the configured callback targets.",
                string.Join(",", State.RedirectUris),
                string.Join(",", expectedRedirectUris));
        }
        if (oauthScopeDrifted)
        {
            Logger.LogWarning(
                "Aevatar OAuth client scope drifted: stored='{Stored}', required='{Required}'. " +
                "Re-running DCR to register a new client_id at NyxID with proxy-capable scopes.",
                State.OauthScope,
                AevatarOAuthClientScopes.AuthorizationScope);
        }
        if (forceReprovision)
        {
            Logger.LogWarning(
                "Aevatar OAuth client force DCR requested: stored_client_id={ClientId}, authority={Authority}, redirect_uris={RedirectUris}.",
                State.ClientId,
                cmd.NyxidAuthority,
                string.Join(",", expectedRedirectUris));
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
            .RegisterPublicClientAsync(cmd.NyxidAuthority, clientName, expectedRedirectUris, CancellationToken.None)
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
        var previousRedirectUris = State.RedirectUris.ToArray();
        var previousOauthScope = State.OauthScope;
        var provisioned = new AevatarOAuthClientProvisionedEvent
        {
            ClientId = registration.ClientId,
            ClientIdIssuedAtUnix = registration.IssuedAt.ToUnixTimeSeconds(),
            NyxidAuthority = cmd.NyxidAuthority,
            PersistedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            RedirectUri = cmd.RedirectUri,
            OauthScope = AevatarOAuthClientScopes.AuthorizationScope,
        };
        provisioned.RedirectUris.AddRange(expectedRedirectUris);
        await PersistDomainEventAsync(
            provisioned,
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
            redirectUriListDrifted,
            oauthScopeDrifted,
            previousRedirectUri,
            previousRedirectUris,
            previousOauthScope);

        Logger.LogInformation(
            "Provisioned aevatar OAuth client via DCR: client_id={ClientId}, authority={Authority}, redirect_uris={RedirectUris}",
            registration.ClientId,
            cmd.NyxidAuthority,
            string.Join(",", expectedRedirectUris));

        if (!HasHmacKey)
        {
            // Distinct race shape from the DCR commit OCC: this handler
            // ALREADY successfully committed Provisioned, so
            // registration.ClientId is the active cluster client — not
            // an orphan. A peer wrote at v+2 between our Provisioned
            // commit and this seed (e.g. their own HMAC seed). Absorb
            // without orphan messaging when the post-replay state has a
            // non-empty HMAC.
            await PersistDomainEventAsync(
                await BuildHmacKeyRotatedEventAsync(),
                onOptimisticConcurrencyConflict: AbsorbPeerHmacSeedAsync);
            Logger.LogInformation("Seeded HMAC key for aevatar OAuth client");
        }

        if (cmd.ForceReprovision)
        {
            await PersistDomainEventAsync(new AevatarOAuthClientForceReprovisionConsumedEvent
            {
                Consumed = true,
                PersistedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }).ConfigureAwait(false);
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

        var command = new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = State.ProvisioningRetryAuthority,
            RedirectUri = State.ProvisioningRetryRedirectUri,
            ClientName = State.ProvisioningRetryClientName,
            ForceReprovision = State.ProvisioningRetryForceReprovision,
        };
        command.RedirectUris.AddRange(State.ProvisioningRetryRedirectUris);
        await HandleEnsureProvisionedAsync(command, allowPendingRetryBypass: true).ConfigureAwait(false);
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
            || !string.Equals(evt.ClientName, State.ProvisioningRetryClientName, StringComparison.Ordinal)
            || evt.ForceReprovision != State.ProvisioningRetryForceReprovision
            || !RedirectUriListsEqual(
                NormalizeProvisioningRedirectUris(evt.RedirectUris, evt.RedirectUri),
                State.ProvisioningRetryRedirectUris))
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
        && RedirectUriListsEqual(State.RedirectUris, NormalizeProvisioningRedirectUris(cmd.RedirectUris, cmd.RedirectUri))
        && AevatarOAuthClientScopes.ContainsRequiredAuthorizationScopes(State.OauthScope)
        && HasHmacKey;

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
            ClientName = normalized.ClientName,
            CallbackId = callbackId,
            FiredAtUnixMs = due.ToUnixTimeMilliseconds(),
            ForceReprovision = normalized.ForceReprovision,
        };
        callbackPayload.RedirectUris.AddRange(normalized.RedirectUris);
        var lease = await ScheduleSelfDurableTimeoutAsync(
                callbackId,
                delay,
                callbackPayload,
                ct: CancellationToken.None)
            .ConfigureAwait(false);

        var scheduled = new AevatarOAuthClientProvisioningRetryScheduledEvent
        {
            Attempt = nextAttempt,
            DueUnixMs = due.ToUnixTimeMilliseconds(),
            NyxidAuthority = normalized.NyxidAuthority,
            RedirectUri = normalized.RedirectUri,
            ClientName = normalized.ClientName,
            CallbackId = lease.CallbackId,
            CallbackGeneration = lease.Generation,
            LastError = error.Message,
            ScheduledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ForceReprovision = normalized.ForceReprovision,
        };
        scheduled.RedirectUris.AddRange(normalized.RedirectUris);
        await PersistDomainEventAsync(scheduled).ConfigureAwait(false);

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
        bool redirectUriListDrifted,
        bool oauthScopeDrifted,
        string previousRedirectUri,
        IReadOnlyCollection<string> previousRedirectUris,
        string previousOauthScope)
    {
        var events = new List<IMessage>(capacity: 3);
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
        if (redirectUriListDrifted)
        {
            events.Add(new AevatarOAuthClientDriftReconciledEvent
            {
                DriftKind = "redirect_uris",
                PreviousValue = string.Join(",", previousRedirectUris),
                ExpectedValue = string.Join(",", State.RedirectUris),
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

        if (events.Count > 0)
            await PersistDomainEventsAsync(events).ConfigureAwait(false);
    }

    private static EnsureAevatarOAuthClientProvisionedCommand? NormalizeEnsureProvisionedCommand(
        EnsureAevatarOAuthClientProvisionedCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.NyxidAuthority) || string.IsNullOrWhiteSpace(cmd.RedirectUri))
            return null;

        var normalized = new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = cmd.NyxidAuthority.Trim(),
            RedirectUri = cmd.RedirectUri.Trim(),
            ClientName = string.IsNullOrWhiteSpace(cmd.ClientName) ? "aevatar" : cmd.ClientName.Trim(),
            ForceReprovision = cmd.ForceReprovision,
        };
        normalized.RedirectUris.AddRange(NormalizeProvisioningRedirectUris(cmd.RedirectUris, normalized.RedirectUri));
        return normalized;
    }

    private static string[] NormalizeProvisioningRedirectUris(
        IEnumerable<string> redirectUris,
        string fallbackRedirectUri)
    {
        var values = NyxIdRedirectUriResolver.NormalizeRedirectUris(redirectUris);
        if (values.Count > 0)
            return values.ToArray();

        var fallback = fallbackRedirectUri?.Trim();
        return string.IsNullOrWhiteSpace(fallback) ? Array.Empty<string>() : [fallback];
    }

    private static bool RedirectUriListsEqual(
        IEnumerable<string> stored,
        IReadOnlyCollection<string> expected)
    {
        var storedValues = NyxIdRedirectUriResolver.NormalizeRedirectUris(stored);
        return storedValues.Count == expected.Count
            && storedValues.SequenceEqual(expected, StringComparer.Ordinal);
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
        var expectedRedirectUris = NormalizeProvisioningRedirectUris(cmd.RedirectUris, cmd.RedirectUri);
        var peerHealed = !string.IsNullOrEmpty(State.ClientId)
            && string.Equals(State.NyxidAuthority, cmd.NyxidAuthority, StringComparison.Ordinal)
            && string.Equals(State.RedirectUri, cmd.RedirectUri, StringComparison.Ordinal)
            && RedirectUriListsEqual(State.RedirectUris, expectedRedirectUris)
            && AevatarOAuthClientScopes.ContainsRequiredAuthorizationScopes(State.OauthScope)
            && HasHmacKey;

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
            + "stored_client_id={StoredClientId}, stored_redirect_uris={StoredRedirectUris}, expected_redirect_uris={ExpectedRedirectUris}, "
            + "orphan_client_id={OrphanClientId}, expected_version={Expected}, actual_version={Actual}.",
            State.ClientId,
            string.Join(",", State.RedirectUris),
            string.Join(",", expectedRedirectUris),
            orphanClientId,
            occ.ExpectedVersion,
            occ.ActualVersion);
        return Task.FromResult(false);
    }

    private Task<bool> AbsorbPeerHmacSeedAsync(EventStoreOptimisticConcurrencyException occ)
    {
        if (HasHmacKey)
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
    /// Persists the configured client_id without calling NyxID DCR. Production
    /// bootstrap and the operator rebuild endpoint both use this command.
    /// Idempotent: re-issuing the same
    /// snapshot (client_id + authority + redirect_uri + oauth_scope) is a
    /// no-op. Always seeds a fresh HMAC key when the state has none —
    /// bootstrap and provisioning are single-step.
    /// </summary>
    /// <remarks>
    /// The same-snapshot check covers redirect_uri + oauth_scope on top of
    /// client_id + authority so configured bootstrap and operator reconcile
    /// can repair a partial snapshot without appending duplicate events.
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
        // "". Empty means "not supplied", not "clear the configured fact".
        var redirectUri = string.IsNullOrEmpty(cmd.RedirectUri) ? State.RedirectUri : cmd.RedirectUri;
        var oauthScope = string.IsNullOrEmpty(cmd.OauthScope) ? State.OauthScope : cmd.OauthScope;
        var redirectUris = cmd.RedirectUris.Count == 0
            ? State.RedirectUris.Count == 0 ? NormalizeProvisioningRedirectUris(Array.Empty<string>(), redirectUri) : State.RedirectUris.ToArray()
            : NormalizeProvisioningRedirectUris(cmd.RedirectUris, redirectUri);
        var sameSnapshot = string.Equals(State.ClientId, cmd.ClientId, StringComparison.Ordinal)
            && string.Equals(State.NyxidAuthority, cmd.NyxidAuthority, StringComparison.Ordinal)
            && string.Equals(State.RedirectUri, redirectUri, StringComparison.Ordinal)
            && RedirectUriListsEqual(State.RedirectUris, redirectUris)
            && string.Equals(State.OauthScope, oauthScope, StringComparison.Ordinal);
        if (!sameSnapshot)
        {
            var provisioned = new AevatarOAuthClientProvisionedEvent
            {
                ClientId = cmd.ClientId,
                ClientIdIssuedAtUnix = cmd.ClientIdIssuedAtUnix,
                NyxidAuthority = cmd.NyxidAuthority,
                PersistedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                OauthScope = oauthScope,
                RedirectUri = redirectUri,
            };
            provisioned.RedirectUris.AddRange(redirectUris);
            await PersistDomainEventAsync(provisioned);
            Logger.LogInformation(
                "Provisioned aevatar OAuth client: client_id={ClientId}, authority={Authority}, redirect_uris={RedirectUris}",
                cmd.ClientId,
                cmd.NyxidAuthority,
                redirectUris.Length == 0 ? "<unspecified>" : string.Join(",", redirectUris));
        }

        if (!HasHmacKey)
        {
            await PersistDomainEventAsync(await BuildHmacKeyRotatedEventAsync());
            Logger.LogInformation("Seeded HMAC key for aevatar OAuth client");
        }

        await ClearProvisioningRetryAsync("configured_client_provisioned");
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
        var idempotencyKey = cmd.IdempotencyKey?.Trim() ?? string.Empty;
        var expectedCurrentKid = cmd.ExpectedCurrentKid?.Trim() ?? string.Empty;
        if (idempotencyKey.Length == 0 || expectedCurrentKid.Length == 0)
        {
            Logger.LogWarning("Ignored HMAC rotation without an idempotency key and expected current kid");
            return;
        }
        if (string.Equals(
                State.LastHmacRotationIdempotencyKey,
                idempotencyKey,
                StringComparison.Ordinal))
        {
            Logger.LogInformation("Ignored already-applied HMAC rotation request");
            return;
        }
        if (!string.Equals(State.HmacKid, expectedCurrentKid, StringComparison.Ordinal))
        {
            Logger.LogWarning(
                "Ignored HMAC rotation because expected kid does not match committed state. expected={ExpectedKid} current={CurrentKid}",
                expectedCurrentKid,
                State.HmacKid);
            return;
        }

        await PersistDomainEventAsync(await BuildHmacKeyRotatedEventAsync(idempotencyKey));
        Logger.LogInformation("Rotated HMAC key for aevatar OAuth client");
    }

    /// <summary>
    /// Maintenance / disaster-recovery: re-materialize the cluster OAuth client
    /// current-state readmodel from the surviving authoritative actor state.
    /// Appends no event and changes no OAuth client fact — it re-emits the
    /// current committed state so a projection store that was wiped/reset
    /// (while the actor state in the event store survived) rebuilds the
    /// document. No-op when the actor holds no provisioned client.
    /// </summary>
    [EventHandler]
    public async Task HandleRebuildProjection(RebuildAevatarOAuthClientProjectionCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (string.IsNullOrEmpty(State.ClientId))
        {
            Logger.LogInformation(
                "RebuildAevatarOAuthClientProjection found no provisioned client; nothing to rebuild");
            return;
        }

        // Routing payload only: the activation plan provider recognizes the
        // Provisioned descriptor; the materialized document comes from the
        // current state snapshot, not this reconstructed event.
        var provisioned = new AevatarOAuthClientProvisionedEvent
        {
            ClientId = State.ClientId,
            ClientIdIssuedAtUnix = State.ClientIdIssuedAtUnix,
            NyxidAuthority = State.NyxidAuthority,
            RedirectUri = State.RedirectUri,
            OauthScope = State.OauthScope,
        };
        provisioned.RedirectUris.AddRange(State.RedirectUris);
        await RepublishCommittedStateAsync(provisioned);

        Logger.LogInformation(
            "Rebuilt aevatar OAuth client readmodel from surviving actor state: client_id={ClientId}",
            State.ClientId);
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

    private async Task<AevatarOAuthClientHmacKeyRotatedEvent> BuildHmacKeyRotatedEventAsync(
        string idempotencyKey = "")
    {
        var keyBytes = new byte[HmacKeyBytes];
        RandomNumberGenerator.Fill(keyBytes);
        var now = DateTimeOffset.UtcNow;
        var nextKid = NextKid(State.HmacKid, now);

        // Store the new key material in the secret vault and persist only a
        // reference. The plaintext [hmac_key] field stays empty for new
        // writes; the projection provider resolves the bytes from the vault
        // at read time (dual-read migration keeps legacy plaintext readable).
        var secretVault = Services.GetRequiredService<ISecretVault>();
        var stored = await secretVault.PutAsync(
            new StoreSecretRequest(
                CredentialSecretPurposes.OAuthStateTokenHmacKey,
                WellKnownId,
                WellKnownId,
                Convert.ToBase64String(keyBytes),
                "identity.oauth.hmac-rotate"))
            .ConfigureAwait(false);

        // Demote the current key to the grace-window slot so any state token
        // signed with it (TTL ≤ 5 min) keeps verifying. Initial seed has no
        // current key yet, so the demoted fields stay empty. Demotion carries
        // both the ref (for ref-backed current keys) and the legacy bytes (for
        // legacy plaintext current keys) so either shape keeps verifying.
        var demotingExisting = State.HmacKey.Length > 0 || State.HmacKeyRef is not null;
        var evt = new AevatarOAuthClientHmacKeyRotatedEvent
        {
            HmacKeyRef = stored.Reference,
            HmacKid = nextKid,
            RotatedAtUnix = now.ToUnixTimeSeconds(),
            PersistedAt = Timestamp.FromDateTimeOffset(now),
            PreviousHmacKey = demotingExisting ? State.HmacKey : ByteString.Empty,
            PreviousHmacKid = demotingExisting ? State.HmacKid : string.Empty,
            PreviousHmacDemotedAtUnix = demotingExisting ? now.ToUnixTimeSeconds() : 0,
            IdempotencyKey = idempotencyKey,
        };
        if (demotingExisting && State.HmacKeyRef is not null)
            evt.PreviousHmacKeyRef = State.HmacKeyRef;
        return evt;
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
        next.RedirectUris.Clear();
        next.RedirectUris.AddRange(NormalizeProvisioningRedirectUris(evt.RedirectUris, next.RedirectUri));
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
        // Copy both the ref (new writes) and legacy bytes (empty for new,
        // populated for replayed legacy events) so the reducer stays pure and
        // replay-safe — no vault call happens here; the Put already ran in the
        // async builder at command time.
        next.HmacKeyRef = evt.HmacKeyRef;
        next.HmacKey = evt.HmacKey ?? ByteString.Empty;
        next.HmacKeyRotatedAtUnix = evt.RotatedAtUnix;
        next.HmacKid = string.IsNullOrEmpty(evt.HmacKid) ? InitialHmacKid : evt.HmacKid;
        next.PreviousHmacKeyRef = evt.PreviousHmacKeyRef;
        next.PreviousHmacKey = evt.PreviousHmacKey ?? ByteString.Empty;
        next.PreviousHmacKid = evt.PreviousHmacKid ?? string.Empty;
        next.PreviousHmacDemotedAtUnix = evt.PreviousHmacDemotedAtUnix;
        next.LastHmacRotationIdempotencyKey = evt.IdempotencyKey ?? string.Empty;
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
        next.ProvisioningRetryRedirectUris.Clear();
        next.ProvisioningRetryRedirectUris.AddRange(NormalizeProvisioningRedirectUris(evt.RedirectUris, next.ProvisioningRetryRedirectUri));
        next.ProvisioningRetryClientName = evt.ClientName ?? string.Empty;
        next.ProvisioningRetryForceReprovision = evt.ForceReprovision;
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
        next.ProvisioningRetryRedirectUris.Clear();
        next.ProvisioningRetryClientName = string.Empty;
        next.ProvisioningRetryForceReprovision = false;
        next.ProvisioningRetryCallbackId = string.Empty;
        next.ProvisioningRetryCallbackGeneration = 0;
        next.ProvisioningRetryLastError = string.Empty;
        return next;
    }

    private static AevatarOAuthClientState ApplyForceReprovisionConsumed(
        AevatarOAuthClientState current,
        AevatarOAuthClientForceReprovisionConsumedEvent evt)
    {
        var next = current.Clone();
        next.ForceReprovisionConsumed = evt.Consumed;
        return next;
    }
}
