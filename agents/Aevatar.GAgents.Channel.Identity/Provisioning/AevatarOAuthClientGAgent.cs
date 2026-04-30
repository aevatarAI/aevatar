using System.Security.Cryptography;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Persistence;
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
/// secrets store needed. See cluster bootstrap service for the
/// caller-side wiring.
/// </summary>
public sealed class AevatarOAuthClientGAgent : GAgentBase<AevatarOAuthClientState>
{
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
            .On<AevatarOAuthClientProjectionRebuildRequestedEvent>(static (state, _) => state)
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
        ArgumentNullException.ThrowIfNull(cmd);
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

        if (sameClient && !redirectUriDrifted)
        {
            // Seed HMAC key on first activation against an existing client_id
            // (defence-in-depth against partial state loaded from snapshots).
            // Returning here is intentional: HmacKeyRotatedEvent itself
            // re-publishes the state root, so the projector materializes the
            // readmodel without needing an additional rebuild trigger.
            if (State.HmacKey.Length == 0)
            {
                await PersistDomainEventAsync(BuildHmacKeyRotatedEvent());
                Logger.LogInformation("Seeded HMAC key for aevatar OAuth client (existing client_id)");
                return;
            }

            // Steady-state branch: nothing changed at NyxID, but a freshly-
            // booted silo may have an empty projection (codex PR #539 P1 —
            // happens after the projection-scope-activation fix is deployed
            // to a cluster whose actor was already provisioned by an earlier
            // build that never activated the scope). Persist a no-op rebuild
            // event so the now-attached projector has a state-root
            // publication to materialize. Apply is identity, so the OAuth
            // client facts are not mutated.
            await PersistDomainEventAsync(new AevatarOAuthClientProjectionRebuildRequestedEvent
            {
                Reason = "ensure_already_provisioned",
                RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });
            Logger.LogInformation(
                "Requested aevatar OAuth client projection rebuild: actorId={ActorId}, authority={Authority}",
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

        var registrar = Services.GetService<NyxIdDynamicClientRegistrationClient>();
        if (registrar is null)
        {
            Logger.LogError(
                "EnsureProvisioned cannot resolve NyxIdDynamicClientRegistrationClient; DI is missing the registrar");
            return;
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
            .RegisterPublicClientAsync(cmd.NyxidAuthority, clientName, cmd.RedirectUri, CancellationToken.None)
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
        await PersistDomainEventAsync(
            new AevatarOAuthClientProvisionedEvent
            {
                ClientId = registration.ClientId,
                ClientIdIssuedAtUnix = registration.IssuedAt.ToUnixTimeSeconds(),
                NyxidAuthority = cmd.NyxidAuthority,
                PersistedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                RedirectUri = cmd.RedirectUri,
            },
            onOptimisticConcurrencyConflict: occ => AbsorbPeerDcrProvisioningAsync(cmd, registration.ClientId, occ));

        // The loser absorber path returned (peer committed the same
        // shape). Detect that by comparing State.ClientId — if it is the
        // peer's id, this handler must NOT continue with the post-
        // Provisioned HMAC seed against state we did not produce.
        if (!string.Equals(State.ClientId, registration.ClientId, StringComparison.Ordinal))
            return;

        Logger.LogInformation(
            "Provisioned aevatar OAuth client via DCR: client_id={ClientId}, authority={Authority}, redirect_uri={RedirectUri}",
            registration.ClientId,
            cmd.NyxidAuthority,
            cmd.RedirectUri);

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
    }

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
            "Aevatar OAuth client OCC race did not converge on the desired shape after replay; rethrowing so the bootstrap retry path can re-evaluate. "
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
            "Aevatar OAuth client HMAC-seed OCC fired but the post-replay state has no HMAC key; rethrowing so the bootstrap retry path can complete the seed. "
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
    /// triple is a no-op. Always seeds a fresh HMAC key when the state has
    /// none — bootstrap and provisioning are single-step.
    /// </summary>
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

        var sameClient = string.Equals(State.ClientId, cmd.ClientId, StringComparison.Ordinal)
            && string.Equals(State.NyxidAuthority, cmd.NyxidAuthority, StringComparison.Ordinal);
        if (!sameClient)
        {
            await PersistDomainEventAsync(new AevatarOAuthClientProvisionedEvent
            {
                ClientId = cmd.ClientId,
                ClientIdIssuedAtUnix = cmd.ClientIdIssuedAtUnix,
                NyxidAuthority = cmd.NyxidAuthority,
                PersistedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });
            Logger.LogInformation(
                "Provisioned aevatar OAuth client: client_id={ClientId}, authority={Authority}",
                cmd.ClientId,
                cmd.NyxidAuthority);
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
}
