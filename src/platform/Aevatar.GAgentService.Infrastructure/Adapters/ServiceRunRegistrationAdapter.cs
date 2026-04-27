using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Core.GAgents;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

/// <summary>
/// Infrastructure adapter that registers and updates service runs by dispatching
/// commands to <see cref="ServiceRunGAgent"/> actors. The actor commits the events
/// and the current-state projection materializes them into the durable readmodel.
/// </summary>
public sealed class ServiceRunRegistrationAdapter : IServiceRunRegistrationPort
{
    private const string PublisherId = "gagent-service.runs";

    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IServiceRunCurrentStateProjectionPort _projectionPort;

    public ServiceRunRegistrationAdapter(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IServiceRunCurrentStateProjectionPort projectionPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<ServiceRunRegistrationResult> RegisterAsync(
        ServiceRunRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.RunId))
            throw new InvalidOperationException("run_id is required.");

        var actorId = BuildRunActorId(record.RunId);
        var actor = await _runtime.CreateAsync<ServiceRunGAgent>(actorId, ct: ct);
        await _projectionPort.EnsureProjectionAsync(actor.Id, ct);

        var prepared = record.Clone();
        if (prepared.CreatedAt == null)
            prepared.CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        prepared.UpdatedAt = prepared.CreatedAt;
        if (prepared.Status == ServiceRunStatus.Unspecified)
            prepared.Status = ServiceRunStatus.Accepted;

        var envelope = CreateEnvelope(actor.Id, Any.Pack(new RegisterServiceRunRequested
        {
            Record = prepared,
        }), prepared.CommandId, prepared.CorrelationId);

        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
        return new ServiceRunRegistrationResult(actor.Id, prepared.RunId);
    }

    public async Task UpdateStatusAsync(
        string runActorId,
        string runId,
        ServiceRunStatus status,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runActorId))
            throw new ArgumentException("runActorId is required.", nameof(runActorId));
        if (status == ServiceRunStatus.Unspecified)
            return;

        var commandId = Guid.NewGuid().ToString("N");
        var envelope = CreateEnvelope(
            runActorId,
            Any.Pack(new UpdateServiceRunStatusRequested
            {
                RunId = runId ?? string.Empty,
                Status = status,
            }),
            commandId,
            commandId);
        await _dispatchPort.DispatchAsync(runActorId, envelope, ct);
    }

    private static string BuildRunActorId(string runId) => $"service-run:{runId}";

    private static EventEnvelope CreateEnvelope(
        string actorId,
        Any payload,
        string commandId,
        string correlationId) =>
        new()
        {
            Id = string.IsNullOrWhiteSpace(commandId) ? Guid.NewGuid().ToString("N") : commandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = payload,
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherId, actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? commandId : correlationId,
            },
        };
}
