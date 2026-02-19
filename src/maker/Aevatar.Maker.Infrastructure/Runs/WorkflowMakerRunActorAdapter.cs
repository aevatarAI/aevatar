using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Maker.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Maker.Infrastructure.Runs;

public sealed class WorkflowMakerRunActorAdapter : IMakerRunActorAdapter
{
    public async Task<MakerResolvedActor> ResolveOrCreateAsync(
        IActorRuntime runtime,
        string? actorId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (!string.IsNullOrWhiteSpace(actorId))
        {
            var actor = await runtime.GetAsync(actorId)
                ?? throw new InvalidOperationException($"Actor '{actorId}' not found.");
            EnsureWorkflowAgent(actor);
            return new MakerResolvedActor(actor, Created: false);
        }

        var created = await runtime.CreateAsync<WorkflowGAgent>(ct: ct);
        EnsureWorkflowAgent(created);
        return new MakerResolvedActor(created, Created: true);
    }

    public Task ConfigureAsync(
        IActor actor,
        MakerRunRequest request,
        CancellationToken ct = default)
    {
        _ = ct;
        var workflow = EnsureWorkflowAgent(actor);
        workflow.ConfigureWorkflow(request.WorkflowYaml, request.WorkflowName);
        return Task.CompletedTask;
    }

    public EventEnvelope CreateStartEnvelope(MakerRunRequest request, string correlationId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = request.Input,
                SessionId = correlationId,
            }),
            PublisherId = "maker.application",
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
        };

    public bool TryResolveCompletion(EventEnvelope envelope, out MakerRunCompletion completion)
    {
        completion = default!;
        var payload = envelope.Payload;
        if (payload is null || !payload.Is(WorkflowCompletedEvent.Descriptor))
            return false;

        var evt = payload.Unpack<WorkflowCompletedEvent>();
        completion = new MakerRunCompletion(
            Output: evt.Output ?? string.Empty,
            Success: evt.Success,
            Error: evt.Error);
        return true;
    }

    private static WorkflowGAgent EnsureWorkflowAgent(IActor actor)
    {
        if (actor.Agent is WorkflowGAgent workflow)
            return workflow;

        throw new InvalidOperationException("Current actor adapter requires WorkflowGAgent.");
    }
}
