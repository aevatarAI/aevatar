using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Interop.A2A.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Interop.A2A.Application;

/// <summary>Dispatches A2A task lifecycle commands through the actor-owned task boundary.</summary>
// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: adapter owned task actor provisioning with direct IActorRuntime calls.
//   New principle: task command port owns runtime provisioning; adapter only normalizes DTOs and submits typed commands.
public interface IA2ATaskCommandPort
{
    Task<string> SubmitAsync(A2ATaskSubmitCommand command, CancellationToken ct = default);

    Task<string> CancelAsync(A2ATaskCancelCommand command, CancellationToken ct = default);
}

public sealed class A2ATaskCommandPort : IA2ATaskCommandPort
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public A2ATaskCommandPort(IActorRuntime runtime, IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<string> SubmitAsync(A2ATaskSubmitCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var taskActorId = A2ATaskActorId.Build(command.TaskId);
        await EnsureTaskActorAsync(taskActorId, ct);
        await _dispatchPort.DispatchAsync(taskActorId, BuildEnvelope(command, command.CommandId, taskActorId), ct);
        return taskActorId;
    }

    public async Task<string> CancelAsync(A2ATaskCancelCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var taskActorId = A2ATaskActorId.Build(command.TaskId);
        await _dispatchPort.DispatchAsync(taskActorId, BuildEnvelope(command, command.CommandId, taskActorId), ct);
        return taskActorId;
    }

    private Task EnsureTaskActorAsync(string taskActorId, CancellationToken ct) =>
        _runtime.CreateAsync<A2ATaskGAgent>(taskActorId, ct);

    private static EventEnvelope BuildEnvelope(IMessage payload, string commandId, string targetActorId)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("a2a-adapter", targetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = commandId,
            },
        };
    }
}
