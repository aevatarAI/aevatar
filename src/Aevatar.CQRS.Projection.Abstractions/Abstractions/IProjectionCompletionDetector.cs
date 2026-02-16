namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Detects whether one envelope marks projection completion for the specified context.
/// </summary>
public interface IProjectionCompletionDetector<in TContext>
{
    bool IsProjectionCompleted(TContext context, EventEnvelope envelope);
}
