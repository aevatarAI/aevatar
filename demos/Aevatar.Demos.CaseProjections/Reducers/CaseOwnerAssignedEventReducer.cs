namespace Aevatar.Demos.CaseProjections.Reducers;

public sealed class CaseOwnerAssignedEventReducer : CaseProjectionEventReducerBase<CaseOwnerAssignedEvent>
{
    public override int Order => 10;

    protected override void Reduce(
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
    }
}
