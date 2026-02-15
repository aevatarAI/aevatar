namespace Aevatar.Demos.CaseProjections.Extensions.Sla.Reducers;

/// <summary>
/// External extension reducer: no core modification needed.
/// </summary>
public sealed class CaseEscalatedEventReducer : CaseProjectionEventReducerBase<CaseEscalatedEvent>
{
    public override int Order => 30;

    protected override void Reduce(
        CaseProjectionReadModel readModel,
        CaseProjectionContext context,
        EventEnvelope envelope,
        CaseEscalatedEvent evt,
        DateTimeOffset now)
    {
        if (evt.Level > readModel.EscalationLevel)
            readModel.EscalationLevel = evt.Level;

        readModel.Timeline.Add(new CaseProjectionTimelineItem
        {
            Timestamp = now,
            Stage = "case.escalated",
            Message = $"level={evt.Level}, reason={evt.Reason}",
            EventType = envelope.Payload?.TypeUrl ?? "",
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["level"] = evt.Level.ToString(),
                ["reason"] = evt.Reason,
            },
        });
    }
}
