using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowDefinitionBindObservationSessionEventHub
    : IProjectionSessionEventHub<EventEnvelope>
{
    private readonly ProjectionSessionEventHub<EventEnvelope> _inner;

    public WorkflowDefinitionBindObservationSessionEventHub(
        IStreamProvider streamProvider,
        WorkflowBindingSessionEventCodec codec,
        ILogger<ProjectionSessionEventHub<EventEnvelope>>? logger = null)
    {
        _inner = new ProjectionSessionEventHub<EventEnvelope>(streamProvider, codec, logger);
    }

    public Task PublishAsync(
        string rootActorId,
        string sessionId,
        EventEnvelope evt,
        CancellationToken ct = default) =>
        _inner.PublishAsync(rootActorId, sessionId, evt, ct);

    public Task<IAsyncDisposable> SubscribeAsync(
        string rootActorId,
        string sessionId,
        Func<EventEnvelope, ValueTask> handler,
        CancellationToken ct = default) =>
        _inner.SubscribeAsync(rootActorId, sessionId, handler, ct);
}
