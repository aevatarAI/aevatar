using System.Security.Cryptography;
using System.Text;
using Aevatar.ContentArtifacts.Abstractions;
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
public sealed class ServiceRunRegistrationAdapter :
    IServiceRunRegistrationPort,
    IServiceRunResultArtifactAttachmentPort
{
    private const string PublisherId = "gagent-service.runs";

    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public ServiceRunRegistrationAdapter(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<ServiceRunRegistrationResult> RegisterAsync(
        ServiceRunRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.RunId))
            throw new InvalidOperationException("run_id is required.");
        if (string.IsNullOrWhiteSpace(record.ScopeId))
            throw new InvalidOperationException("scope_id is required.");
        if (string.IsNullOrWhiteSpace(record.ServiceId))
            throw new InvalidOperationException("service_id is required.");

        var actorId = ServiceRunIds.BuildActorId(record.ScopeId, record.ServiceId, record.RunId);
        var actor = await _runtime.CreateAsync<ServiceRunGAgent>(actorId, ct: ct);

        var prepared = record.Clone();
        if (prepared.CreatedAt == null)
            prepared.CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        prepared.UpdatedAt = prepared.CreatedAt;
        if (prepared.Status == ServiceRunStatus.Unspecified)
            prepared.Status = ServiceRunStatus.Accepted;

        // Refactor (iter18/cluster-006):
        //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
        //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
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
        CancellationToken ct = default) =>
        await UpdateStatusAsync(runActorId, runId, status, null, null, ct);

    public async Task UpdateStatusAsync(
        string runActorId,
        string runId,
        ServiceRunStatus status,
        string? lastOutput,
        string? lastError,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runActorId))
            throw new ArgumentException("runActorId is required.", nameof(runActorId));
        if (status == ServiceRunStatus.Unspecified)
            return;

        var command = new UpdateServiceRunStatusRequested
        {
            RunId = runId ?? string.Empty,
            Status = status,
        };
        if (lastOutput != null)
            command.LastOutput = lastOutput;
        if (lastError != null)
            command.LastError = lastError;

        var commandId = Guid.NewGuid().ToString("N");
        var envelope = CreateEnvelope(
            runActorId,
            Any.Pack(command),
            commandId,
            commandId);
        await _dispatchPort.DispatchAsync(runActorId, envelope, ct);
    }

    public async Task<ServiceRunArtifactAttachmentResult> AttachResultArtifactsAsync(
        string runActorId,
        string runId,
        long expectedStateVersion,
        IReadOnlyList<ContentArtifactReference> resultArtifacts,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runActorId))
            throw new ArgumentException("runActorId is required.", nameof(runActorId));
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("runId is required.", nameof(runId));
        ArgumentNullException.ThrowIfNull(resultArtifacts);
        if (resultArtifacts.Count == 0)
            throw new InvalidOperationException("At least one result artifact reference is required.");

        var command = new AttachServiceRunResultArtifactsRequested
        {
            RunId = runId.Trim(),
            ExpectedStateVersion = expectedStateVersion,
        };
        command.ResultArtifacts.Add(resultArtifacts.Select(static artifact => artifact.Clone()));
        var commandId = BuildResultArtifactCommandId(command);
        await _dispatchPort.DispatchAsync(
            runActorId,
            CreateEnvelope(runActorId, Any.Pack(command), commandId, commandId),
            ct);
        return new ServiceRunArtifactAttachmentResult(
            command.RunId,
            commandId,
            commandId,
            DateTimeOffset.UtcNow);
    }

    private static string BuildResultArtifactCommandId(AttachServiceRunResultArtifactsRequested command)
    {
        var identity = string.Join(
            "\n",
            command.ResultArtifacts
                .OrderBy(static artifact => artifact.ArtifactId, StringComparer.Ordinal)
                .ThenBy(static artifact => artifact.RevisionId, StringComparer.Ordinal)
                .Select(static artifact =>
                    $"{artifact.ArtifactId}\t{artifact.RevisionId}\t{artifact.ContentHash}\t{artifact.MediaType}"));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $"service-run-artifacts-{command.RunId}-v{command.ExpectedStateVersion}-{digest}";
    }

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
