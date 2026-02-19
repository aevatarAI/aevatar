namespace Aevatar.Demos.CaseProjection.Reducers;

public sealed class CaseOwnerAssignedEventReducer : CaseProjectionEventReducerBase<CaseOwnerAssignedEvent>
{
    protected override bool Reduce(
        CaseProjectionReadModel readModel,
        CaseProjectionContext context,
        EventEnvelope envelope,
        CaseOwnerAssignedEvent evt,
        DateTimeOffset now)
    {
        readModel.OwnerId = evt.OwnerId;

        CaseProjectionMutations.AddTimeline(
            readModel,
            now,
            "case.owner.assigned",
            $"owner={evt.OwnerId}",
            envelope.Payload?.TypeUrl ?? "",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["owner_id"] = evt.OwnerId,
            });

        return true;
    }
}
