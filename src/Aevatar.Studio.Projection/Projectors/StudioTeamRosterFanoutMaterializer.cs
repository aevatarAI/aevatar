using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.Orchestration;
using Google.Protobuf;

namespace Aevatar.Studio.Projection.Projectors;

/// <summary>
/// Durable fanout from committed StudioMember reassignment facts to the
/// affected StudioTeam actors. The enclosing projection scope owns the
/// source-event watermark and retained failure envelopes; Team actor inbox
/// retry owns per-target handler retry.
/// Refactor (iter96/cluster-544):
///   Old: command service dispatch 后顺序 fanout 到 team service(不可靠,无 durable)
///   New: committed state event -> StudioTeamRosterFanoutMaterializer 投递 team actor inbox(durable + actor retry)
/// </summary>
internal sealed class StudioTeamRosterFanoutMaterializer
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private const string PublisherId = "aevatar.studio.projection.team-roster-fanout";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioProjectionActorCommandDispatch _commandDispatch;

    public StudioTeamRosterFanoutMaterializer(
        IStudioActorBootstrap bootstrap,
        StudioProjectionActorCommandDispatch commandDispatch)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
    }

    public async ValueTask ProjectAsync(
        StudioMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpack(envelope, out var published) ||
            published?.StateEvent?.EventData == null)
        {
            return;
        }

        if (published.StateEvent.EventData.Is(StudioMemberReassignedEvent.Descriptor))
        {
            var evt = published.StateEvent.EventData.Unpack<StudioMemberReassignedEvent>();
            if (evt.HasFromTeamId)
                await DispatchToTeamAsync(evt.ScopeId, evt.FromTeamId, evt, ct).ConfigureAwait(false);
            if (evt.HasToTeamId)
                await DispatchToTeamAsync(evt.ScopeId, evt.ToTeamId, evt, ct).ConfigureAwait(false);
            return;
        }

        if (published.StateEvent.EventData.Is(StudioMemberDeletedEvent.Descriptor))
        {
            var evt = published.StateEvent.EventData.Unpack<StudioMemberDeletedEvent>();
            if (!evt.HasPreviousTeamId)
                return;

            var removal = new StudioMemberReassignedEvent
            {
                MemberId = evt.MemberId,
                ScopeId = evt.ScopeId,
                FromTeamId = evt.PreviousTeamId,
                ReassignedAtUtc = evt.DeletedAtUtc,
            };
            await DispatchToTeamAsync(removal.ScopeId, removal.FromTeamId, removal, ct).ConfigureAwait(false);
        }
    }

    private async Task DispatchToTeamAsync(
        string scopeId,
        string teamId,
        IMessage payload,
        CancellationToken ct)
    {
        var actorId = StudioTeamConventions.BuildActorId(scopeId, teamId);
        var actor = await _bootstrap.EnsureAsync<StudioTeamGAgent>(actorId, ct).ConfigureAwait(false);
        await _commandDispatch.DispatchAsync(
            actor,
            payload,
            PublisherId,
            commandId: BuildFanoutCommandId(actorId, payload),
            deliveryOperationId: BuildFanoutDeliveryOperationId(actorId, payload),
            ct: ct).ConfigureAwait(false);
    }

    private static string BuildFanoutCommandId(string actorId, IMessage payload)
    {
        var eventHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(payload.ToByteArray()))
            .ToLowerInvariant();
        return $"{PublisherId}:{actorId}:{eventHash}";
    }

    private static string BuildFanoutDeliveryOperationId(string actorId, IMessage payload) =>
        BuildFanoutCommandId(actorId, payload);
}
