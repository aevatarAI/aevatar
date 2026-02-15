using Aevatar.Workflows.Core;

namespace Aevatar.CQRS.Projections.Orchestration;

/// <summary>
/// Marks projection completion when the envelope payload is <see cref="WorkflowCompletedEvent" />.
/// </summary>
public sealed class WorkflowCompletedEventProjectionCompletionDetector<TContext>
    : IProjectionCompletionDetector<TContext>
{
    private static readonly string WorkflowCompletedTypeUrl =
        Google.Protobuf.WellKnownTypes.Any.Pack(new WorkflowCompletedEvent()).TypeUrl;

    public bool IsProjectionCompleted(TContext context, EventEnvelope envelope) =>
        string.Equals(envelope.Payload?.TypeUrl, WorkflowCompletedTypeUrl, StringComparison.Ordinal);
}
