namespace Aevatar.Demos.CaseProjections.Orchestration;

public sealed class CaseResolvedProjectionCompletionDetector
    : IProjectionCompletionDetector<CaseProjectionContext>
{
    public bool IsProjectionCompleted(CaseProjectionContext context, EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        if (payload == null || !payload.Is(CaseResolvedEvent.Descriptor))
            return false;

        var resolved = payload.Unpack<CaseResolvedEvent>();
        return string.Equals(resolved.RunId, context.RunId, StringComparison.Ordinal);
    }
}
