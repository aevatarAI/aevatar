using Aevatar.CQRS.Projections.Abstractions.ReadModels;
using AIEvents = Aevatar.AI.Abstractions;

namespace Aevatar.CQRS.Projections.Reducers;

public sealed class TextMessageEndEventReducer : WorkflowExecutionEventReducerBase<AIEvents.TextMessageEndEvent>
{
    public override int Order => 30;

    protected override void Reduce(
        WorkflowExecutionReport report,
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        AIEvents.TextMessageEndEvent evt,
        DateTimeOffset now)
    {
        var publisher = string.IsNullOrWhiteSpace(envelope.PublisherId) ? "(unknown)" : envelope.PublisherId;
        if (!string.Equals(publisher, context.RootActorId, StringComparison.Ordinal))
        {
            report.RoleReplies.Add(new WorkflowExecutionRoleReply
            {
                Timestamp = now,
                RoleId = publisher,
                SessionId = evt.SessionId ?? "",
                Content = evt.Content ?? "",
                ContentLength = (evt.Content ?? "").Length,
            });
        }

        WorkflowExecutionProjectionMutations.AddTimeline(
            report,
            now,
            "llm.end",
            $"agent={publisher}, chars={(evt.Content ?? "").Length}",
            publisher,
            null,
            null,
            envelope.Payload?.TypeUrl ?? "",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = evt.SessionId ?? "",
            });
    }
}
