using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Projection.ReadModels;

namespace Aevatar.AI.Projection.Appliers;

public sealed class AITextMessageEndProjectionApplier<TReadModel, TContext>
    : IProjectionEventApplier<TReadModel, TContext, TextMessageEndEvent>
    where TReadModel : class, IHasProjectionTimeline, IHasProjectionRoleReplies
    where TContext : class, IProjectionSessionContext
{
    public bool Apply(
        TReadModel readModel,
        TContext context,
        EventEnvelope envelope,
        TextMessageEndEvent evt,
        DateTimeOffset now)
    {
        var publisher = AIProjectionApplierHelpers.ResolvePublisher(envelope);
        var content = evt.Content ?? string.Empty;
        if (!string.Equals(publisher, context.RootActorId, StringComparison.Ordinal))
        {
            readModel.AddRoleReply(new ProjectionRoleReply
            {
                Timestamp = now,
                RoleId = publisher,
                SessionId = evt.SessionId ?? string.Empty,
                Content = content,
                ContentLength = content.Length,
            });
        }

        readModel.AddTimeline(new ProjectionTimelineEvent
        {
            Timestamp = now,
            Stage = "llm.end",
            Message = $"agent={publisher}, chars={content.Length}",
            AgentId = publisher,
            EventType = AIProjectionApplierHelpers.ResolveEventType(envelope),
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = evt.SessionId ?? string.Empty,
            },
        });

        return true;
    }
}
