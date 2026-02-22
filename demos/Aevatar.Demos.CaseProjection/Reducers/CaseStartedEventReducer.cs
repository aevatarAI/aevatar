namespace Aevatar.Demos.CaseProjection.Reducers;

public sealed class CaseStartedEventReducer : CaseProjectionEventReducerBase<CaseStartedEvent>
{
    protected override bool Reduce(
        CaseProjectionReadModel readModel,
        CaseProjectionContext context,
        EventEnvelope envelope,
        CaseStartedEvent evt,
        DateTimeOffset now)
    {
        readModel.CaseId = evt.CaseId;
        readModel.CaseType = string.IsNullOrWhiteSpace(evt.CaseType) ? context.CaseType : evt.CaseType;
        readModel.Title = evt.Title;
        readModel.Input = evt.Input;
        readModel.Status = "open";

        CaseProjectionMutations.AddTimeline(
            readModel,
            now,
            "case.started",
            $"case={evt.CaseId}",
            envelope.Payload?.TypeUrl ?? "");

        return true;
    }
}
