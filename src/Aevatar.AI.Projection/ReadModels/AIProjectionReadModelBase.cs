using Aevatar.Foundation.Projection.ReadModels;

namespace Aevatar.AI.Projection.ReadModels;

/// <summary>
/// Shared AI-layer read-model base.
/// Keeps only capability contracts and avoids introducing extra storage fields.
/// </summary>
public abstract class AIProjectionReadModelBase
    : AevatarReadModelBase,
      IHasProjectionTimeline,
      IHasProjectionRoleReplies
{
    public abstract void AddTimeline(ProjectionTimelineEvent timelineEvent);

    public abstract void AddRoleReply(ProjectionRoleReply roleReply);
}
