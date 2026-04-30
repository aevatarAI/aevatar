using System.Security.Cryptography;
using Aevatar.Foundation.Abstractions.Attributes;
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
    protected override AevatarOAuthClientState TransitionState(AevatarOAuthClientState current, IMessage evt)
    {
        if (evt is not null
            && evt is not AevatarOAuthClientProvisionedEvent
            && evt is not AevatarOAuthClientHmacKeyRotatedEvent
            && evt is not AevatarOAuthClientBrokerCapabilityObservedEvent
            && evt is not AevatarOAuthClientProjectionRebuildRequestedEvent)
        {
            Logger.LogWarning(
                "AevatarOAuthClientGAgent received unrecognised event type {EventType}; state unchanged",
                evt.GetType().FullName);
        }

        return StateTransitionMatcher
            .Match(current, evt)
            .On<AevatarOAuthClientProvisionedEvent>(ApplyProvisioned)
            .On<AevatarOAuthClientHmacKeyRotatedEvent>(ApplyHmacKeyRotated)
            .On<AevatarOAuthClientBrokerCapabilityObservedEvent>(ApplyBrokerCapabilityObserved)
            .On<AevatarOAuthClientProjectionRebuildRequestedEvent>(static (state, _) => state)
            .OrCurrent();
    }

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

        var alreadyProvisioned = !string.IsNullOrEmpty(State.ClientId)
            && string.Equals(State.NyxidAuthority, cmd.NyxidAuthority, StringComparison.Ordinal);
        if (alreadyProvisioned)
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

        await PersistDomainEventAsync(new AevatarOAuthClientProvisionedEvent
        {
            ClientId = registration.ClientId,
            ClientIdIssuedAtUnix = registration.IssuedAt.ToUnixTimeSeconds(),
            NyxidAuthority = cmd.NyxidAuthority,
            PersistedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        Logger.LogInformation(
            "Provisioned aevatar OAuth client via DCR: client_id={ClientId}, authority={Authority}",
            registration.ClientId,
            cmd.NyxidAuthority);

        if (State.HmacKey.Length == 0)
        {
            await PersistDomainEventAsync(BuildHmacKeyRotatedEvent());
            Logger.LogInformation("Seeded HMAC key for aevatar OAuth client");
        }
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
