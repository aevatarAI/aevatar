using Aevatar.Cqrs.Projections.Abstractions.ReadModels;
using AIEvents = Aevatar.AI.Abstractions;

namespace Aevatar.Cqrs.Projections.Reducers;

public sealed class TextMessageEndEventReducer : ChatRunEventReducerBase<AIEvents.TextMessageEndEvent>
{
    public override int Order => 30;

    protected override void Reduce(
        ChatRunReport report,
        ChatProjectionContext context,
        EventEnvelope envelope,
        AIEvents.TextMessageEndEvent evt,
        DateTimeOffset now)
    {
        var publisher = string.IsNullOrWhiteSpace(envelope.PublisherId) ? "(unknown)" : envelope.PublisherId;
        if (!string.Equals(publisher, context.RootActorId, StringComparison.Ordinal))
        {
            report.RoleReplies.Add(new ChatRoleReply
            {
                Timestamp = now,
                RoleId = publisher,
                SessionId = evt.SessionId ?? "",
                Content = evt.Content ?? "",
                ContentLength = (evt.Content ?? "").Length,
            });
        }

        ChatRunProjectionMutations.AddTimeline(
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
