using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.ProjectionRecovery;

internal sealed class ActorDispatchAevatarOAuthClientProjectionRepublishPort
    : IAevatarOAuthClientProjectionRepublishPort
{
    private readonly ICommandDispatchService<
        RepairAevatarOAuthClientProjectionCommand,
        ChannelIdentityOAuthAcceptedReceipt,
        ChannelIdentityOAuthDispatchError> _dispatch;

    public ActorDispatchAevatarOAuthClientProjectionRepublishPort(
        ICommandDispatchService<
            RepairAevatarOAuthClientProjectionCommand,
            ChannelIdentityOAuthAcceptedReceipt,
            ChannelIdentityOAuthDispatchError> dispatch)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    public async Task<AevatarOAuthClientProjectionRepublishReceipt> DispatchAsync(
        long expectedStateVersion,
        string repairRequestId,
        CancellationToken ct = default)
    {
        if (expectedStateVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedStateVersion),
                "Expected state version must be positive.");
        }

        var normalizedRepairRequestId = repairRequestId?.Trim() ?? string.Empty;
        if (normalizedRepairRequestId.Length == 0)
            throw new ArgumentException("repairRequestId is required.", nameof(repairRequestId));

        var accepted = await _dispatch
            .DispatchAsync(
                new RepairAevatarOAuthClientProjectionCommand
                {
                    ExpectedStateVersion = expectedStateVersion,
                    RepairRequestId = normalizedRepairRequestId,
                },
                ct)
            .ConfigureAwait(false);
        if (!accepted.Succeeded || accepted.Receipt is null)
        {
            throw new InvalidOperationException(
                $"OAuth client projection repair dispatch rejected: {accepted.Error}.");
        }

        return new AevatarOAuthClientProjectionRepublishReceipt(
            accepted.Receipt.ActorId,
            accepted.Receipt.CommandId,
            accepted.Receipt.CorrelationId);
    }
}
