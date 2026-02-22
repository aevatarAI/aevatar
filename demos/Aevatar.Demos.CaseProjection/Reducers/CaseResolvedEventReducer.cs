namespace Aevatar.Demos.CaseProjection.Reducers;

public sealed class CaseResolvedEventReducer : CaseProjectionEventReducerBase<CaseResolvedEvent>
{
    protected override bool Reduce(
        CaseProjectionReadModel readModel,
        CaseProjectionContext context,
        EventEnvelope envelope,
        CaseResolvedEvent evt,
        DateTimeOffset now)
    {
        readModel.Status = "closed";
        readModel.Resolution = evt.Resolution;
        readModel.EndedAt = now;

        CaseProjectionMutations.AddTimeline(
            readModel,
            now,
            "case.resolved",
            $"resolved={evt.Resolved}",
            envelope.Payload?.TypeUrl ?? "",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resolved"] = evt.Resolved.ToString(),
            });

        return true;
    }
}
