namespace Aevatar.Foundation.Projection.ReadModels;

public interface IHasProjectionTimeline
{
    void AddTimeline(ProjectionTimelineEvent timelineEvent);
}
