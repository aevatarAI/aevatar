using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity.Slash;

/// <summary>
/// /unbind — revoke the active NyxID binding for the inbound sender. Routes
/// the revoke through NyxID first (source of truth — see ADR-0018 §Decision)
/// and event-sources the local actor to flip the projection to inactive.
/// </summary>
public sealed class UnbindChannelSlashCommandHandler : IChannelSlashCommandHandler
{
    private readonly INyxIdCapabilityBroker _broker;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly ILogger<UnbindChannelSlashCommandHandler> _logger;
    private readonly INyxIdCatalogAccessLifecyclePort? _catalogLifecyclePort;

    public UnbindChannelSlashCommandHandler(
        INyxIdCapabilityBroker broker,
        IActorDispatchPort actorDispatchPort,
        ILogger<UnbindChannelSlashCommandHandler> logger,
        INyxIdCatalogAccessLifecyclePort? catalogLifecyclePort = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _catalogLifecyclePort = catalogLifecyclePort;
    }

    public string Name => "unbind";

    public bool RequiresBinding => false;

    public ChannelSlashCommandUsage Usage => new(
        Name,
        string.Empty,
        "解绑当前聊天用户的 NyxID 账号");

    public async Task<MessageContent?> HandleAsync(ChannelSlashCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrEmpty(context.BindingIdValue))
        {
            return new MessageContent { Text = "当前未绑定 NyxID 账号。" };
        }

        try
        {
            await _broker.RevokeBindingAsync(context.Subject, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "/unbind failed at NyxID for subject={Platform}:{Tenant}:{Sender}",
                context.Subject.Platform, context.Subject.Tenant, context.Subject.ExternalUserId);
            return new MessageContent { Text = "解绑 NyxID 账号时遇到内部错误,请稍后重试 /unbind。" };
        }

        if (_catalogLifecyclePort != null)
            await _catalogLifecyclePort.InvalidateAsync(context.Subject, "user_unbind", ct).ConfigureAwait(false);

        // 2) Event-source local revoke so the projection flips to inactive
        //    independently of any subsequent NyxID CAE webhook. Retry once
        //    on a transient failure before giving up — without it a one-off
        //    dispatch hiccup leaves the user thinking they're unbound while
        //    the readmodel still says they're bound, blocking re-/init for
        //    however long the CAE webhook takes (PR #521 review v4-pro).
        var actorId = context.Subject.ToActorId();
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
                        ExternalSubject = context.Subject.Clone(),
                        Reason = "user_unbind",
                    }),
                    Route = EnvelopeRouteSemantics.CreateDirect("channel.identity.unbind", actorId),
                };
                await _actorDispatchPort.DispatchAsync(actorId, envelope, ct).ConfigureAwait(false);
                localDispatchError = null;
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                localDispatchError = ex;
                _logger.LogWarning(ex,
                    "/unbind: local actor revoke dispatch failed on attempt {Attempt}/2 for actor={ActorId}",
                    attempt,
                    actorId);
            }
        }

        if (localDispatchError is not null)
        {
            // NyxID has the truth (revoked); the local projection is stale.
            // Surface the partial state so the user knows the binding may
            // appear active for a few minutes and they may need to /unbind
            // again if the CAE webhook does not arrive.
            return new MessageContent
            {
                Text = "已在 NyxID 取消绑定,但本地状态同步失败。如几分钟后仍未生效,请重试 /unbind。",
            };
        }

        return new MessageContent { Text = "已解绑 NyxID 账号。如需重新绑定,发送 /init。" };
    }
}
