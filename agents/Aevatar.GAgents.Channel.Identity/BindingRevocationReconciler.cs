using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Event-sources a local revoke for an external subject whose NyxID grant has
/// been observed as gone (<c>invalid_grant</c> on token-exchange). Mirrors the
/// local-revoke step of <see cref="Slash.UnbindChannelSlashCommandHandler"/>
/// but deliberately omits the NyxID-side <c>RevokeBindingAsync</c> call: the
/// grant is already revoked upstream, so re-revoking it would be redundant and
/// could surface a spurious error. Flipping the local projection to inactive
/// lets <c>/whoami</c> / <c>/init</c> self-heal without any handler changes.
/// </summary>
public sealed class BindingRevocationReconciler : IBindingRevocationReconciler
{
    private const string DispatchRoutePublisher = "channel.identity.reconcile";

    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly ILogger<BindingRevocationReconciler> _logger;
    private readonly INyxIdCatalogAccessLifecyclePort? _catalogLifecyclePort;

    public BindingRevocationReconciler(
        IActorDispatchPort actorDispatchPort,
        ILogger<BindingRevocationReconciler> logger,
        INyxIdCatalogAccessLifecyclePort? catalogLifecyclePort = null)
    {
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _catalogLifecyclePort = catalogLifecyclePort;
    }

    public async Task ReconcileRevokedAsync(ExternalSubjectRef subject, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (_catalogLifecyclePort != null)
            await _catalogLifecyclePort.InvalidateAsync(subject, reason, ct).ConfigureAwait(false);

        // Event-source the local revoke so the projection flips to inactive
        // independently of any NyxID CAE webhook. Retry once on a transient
        // dispatch failure before giving up — the actor's RevokeBinding handler
        // is idempotent (leaves facts unchanged when no active binding exists),
        // so a duplicate from a retry is safe.
        var actorId = subject.ToActorId();
        Exception? localDispatchError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var envelope = new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Payload = Any.Pack(new RevokeBindingCommand
                    {
                        ExternalSubject = subject.Clone(),
                        Reason = reason,
                    }),
                    Route = EnvelopeRouteSemantics.CreateDirect(DispatchRoutePublisher, actorId),
                };
                await _actorDispatchPort.DispatchAsync(actorId, envelope, ct).ConfigureAwait(false);
                localDispatchError = null;
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                localDispatchError = ex;
                _logger.LogWarning(
                    ex,
                    "Binding revocation reconcile dispatch failed on attempt {Attempt}/2 for actor={ActorId} (reason={Reason})",
                    attempt,
                    actorId,
                    reason);
            }
        }

        if (localDispatchError is not null)
        {
            _logger.LogWarning(
                localDispatchError,
                "Binding revocation reconcile did not flip local projection for actor={ActorId}; readmodel may show the binding active until a NyxID CAE webhook arrives (reason={Reason})",
                actorId,
                reason);
        }
    }
}
