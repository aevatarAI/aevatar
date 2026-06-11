using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Identity;

// Refactor (iter27/cluster-028-identity-oauth-endpoint):
//   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
//   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
internal sealed class ChannelIdentityOAuthCommandDispatch<TCommand, TAgent>(
    IActorRuntime actorRuntime,
    IActorDispatchPort actorDispatchPort,
    ChannelIdentityOAuthCommandRoute<TCommand> route)
    : ICommandDispatchService<TCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>
    where TCommand : class, IMessage
    where TAgent : IAgent
{
    private readonly IActorRuntime _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
    private readonly IActorDispatchPort _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
    private readonly ChannelIdentityOAuthCommandRoute<TCommand> _route = route ?? throw new ArgumentNullException(nameof(route));

    public async Task<CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        ArgumentNullException.ThrowIfNull(command);

        var target = _route.ResolveTarget(command);
        if (string.IsNullOrWhiteSpace(target.ActorId))
            return CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>.Failure(
                ChannelIdentityOAuthDispatchError.InvalidTarget);

        var commandId = Guid.NewGuid().ToString("N");
        var actor = await _actorRuntime.CreateAsync<TAgent>(target.ActorId, ct).ConfigureAwait(false);
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(target.PublisherActorId, target.ActorId),
        };

        await _actorDispatchPort.DispatchAsync(actor.Id, envelope, ct).ConfigureAwait(false);

        return CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>.Success(
            new ChannelIdentityOAuthAcceptedReceipt(
                ActorId: target.ActorId,
                CommandId: commandId,
                CorrelationId: commandId));
    }
}

// Refactor (iter27/cluster-028-identity-oauth-endpoint):
//   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
//   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
internal sealed class ChannelIdentityOAuthCommandRoute<TCommand>(
    Func<TCommand, ChannelIdentityOAuthCommandTarget> resolveTarget)
{
    private readonly Func<TCommand, ChannelIdentityOAuthCommandTarget> _resolveTarget =
        resolveTarget ?? throw new ArgumentNullException(nameof(resolveTarget));

    public ChannelIdentityOAuthCommandTarget ResolveTarget(TCommand command) => _resolveTarget(command);
}

// Refactor (iter27/cluster-028-identity-oauth-endpoint):
//   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
//   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
internal sealed record ChannelIdentityOAuthCommandTarget(
    string ActorId,
    string PublisherActorId);

// Refactor (iter27/cluster-028-identity-oauth-endpoint):
//   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
//   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
public sealed record ChannelIdentityOAuthAcceptedReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId);

// Refactor (iter27/cluster-028-identity-oauth-endpoint):
//   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
//   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
public enum ChannelIdentityOAuthDispatchError
{
    None = 0,
    InvalidTarget = 1,
}
