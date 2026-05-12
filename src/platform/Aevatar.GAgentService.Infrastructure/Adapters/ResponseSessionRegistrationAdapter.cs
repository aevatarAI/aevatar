using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Core.GAgents;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

/// <summary>
/// Registers response sessions through their owning actor and lets the current-state
/// projection materialize the queryable response_id lookup. JSON payloads from the
/// HTTP boundary are parsed into protobuf values here so the actor state never
/// holds JSON strings.
/// </summary>
public sealed class ResponseSessionRegistrationAdapter : IResponseSessionRegistrationPort
{
    private const string PublisherId = "gagent-service.response-sessions";

    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IResponseSessionCurrentStateProjectionPort _projectionPort;

    public ResponseSessionRegistrationAdapter(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IResponseSessionCurrentStateProjectionPort projectionPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<ResponseSessionRegistrationResult> RegisterAsync(
        ResponseSessionRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.ResponseId))
            throw new InvalidOperationException("response_id is required.");
        if (string.IsNullOrWhiteSpace(record.ScopeId))
            throw new InvalidOperationException("scope_id is required.");
        if (string.IsNullOrWhiteSpace(record.OwnerSubject))
            throw new InvalidOperationException("owner_subject is required.");

        var actorId = ResponseSessionIds.NewActorId();
        var actor = await _runtime.CreateAsync<ResponseSessionGAgent>(actorId, ct: ct);
        await _projectionPort.EnsureProjectionAsync(actor.Id, ct);

        var prepared = record.Clone();
        if (prepared.CreatedAt == null)
            prepared.CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        prepared.UpdatedAt = prepared.CreatedAt.Clone();
        if (prepared.Status == ResponseSessionStatus.Unspecified)
            prepared.Status = ResponseSessionStatus.Accepted;

        var envelope = CreateEnvelope(
            actor.Id,
            Any.Pack(new RegisterResponseSessionRequested
            {
                Record = prepared,
            }),
            prepared.ResponseId);

        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
        return new ResponseSessionRegistrationResult(actor.Id, prepared.ResponseId);
    }

    public async Task UpdateStatusAsync(
        string sessionActorId,
        string responseId,
        ResponseSessionStatus status,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionActorId))
            throw new ArgumentException("sessionActorId is required.", nameof(sessionActorId));
        if (string.IsNullOrWhiteSpace(responseId))
            throw new ArgumentException("responseId is required.", nameof(responseId));
        if (status == ResponseSessionStatus.Unspecified)
            return;

        var envelopeId = $"{responseId}:{(int)status}:{Guid.NewGuid():N}";
        var envelope = CreateEnvelope(
            sessionActorId,
            Any.Pack(new UpdateResponseSessionStatusRequested
            {
                ResponseId = responseId.Trim(),
                Status = status,
                UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            }),
            envelopeId);

        await _dispatchPort.DispatchAsync(sessionActorId, envelope, ct);
    }

    public async Task RecordForwardedToolCallAsync(
        string sessionActorId,
        string responseId,
        ResponseSessionForwardedToolCall call,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionActorId))
            throw new ArgumentException("sessionActorId is required.", nameof(sessionActorId));
        if (string.IsNullOrWhiteSpace(responseId))
            throw new ArgumentException("responseId is required.", nameof(responseId));
        ArgumentNullException.ThrowIfNull(call);
        if (string.IsNullOrWhiteSpace(call.CallId))
            throw new InvalidOperationException("call_id is required.");

        var prepared = call.Clone();
        if (prepared.EmittedAt == null)
            prepared.EmittedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        if (prepared.Status == ResponseSessionForwardedToolCallStatus.Unspecified)
            prepared.Status = ResponseSessionForwardedToolCallStatus.Pending;
        var envelopeId = $"{responseId}:tool:{prepared.CallId}:emitted";
        var envelope = CreateEnvelope(
            sessionActorId,
            Any.Pack(new RecordForwardedToolCallRequested
            {
                ResponseId = responseId.Trim(),
                Call = prepared,
            }),
            envelopeId);

        await _dispatchPort.DispatchAsync(sessionActorId, envelope, ct);
    }

    public async Task ReceiveForwardedToolResultAsync(
        string sessionActorId,
        string responseId,
        string callId,
        string schemaHash,
        string resultJson,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionActorId))
            throw new ArgumentException("sessionActorId is required.", nameof(sessionActorId));
        if (string.IsNullOrWhiteSpace(responseId))
            throw new ArgumentException("responseId is required.", nameof(responseId));
        if (string.IsNullOrWhiteSpace(callId))
            throw new ArgumentException("callId is required.", nameof(callId));
        if (string.IsNullOrWhiteSpace(schemaHash))
            throw new ArgumentException("schemaHash is required.", nameof(schemaHash));

        var envelopeId = $"{responseId}:tool:{callId}:received";
        var envelope = CreateEnvelope(
            sessionActorId,
            Any.Pack(new ReceiveForwardedToolResultRequested
            {
                ResponseId = responseId.Trim(),
                CallId = callId.Trim(),
                SchemaHash = schemaHash.Trim(),
                Result = ResponsesJsonValues.ParseBoundaryPayload(resultJson),
                ReceivedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            }),
            envelopeId);

        await _dispatchPort.DispatchAsync(sessionActorId, envelope, ct);
    }

    public async Task ResolveForwardedToolResultAsync(
        string sessionActorId,
        string responseId,
        string callId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionActorId))
            throw new ArgumentException("sessionActorId is required.", nameof(sessionActorId));
        if (string.IsNullOrWhiteSpace(responseId))
            throw new ArgumentException("responseId is required.", nameof(responseId));
        if (string.IsNullOrWhiteSpace(callId))
            throw new ArgumentException("callId is required.", nameof(callId));

        var envelopeId = $"{responseId}:tool:{callId}:resolved";
        var envelope = CreateEnvelope(
            sessionActorId,
            Any.Pack(new ResolveForwardedToolResultRequested
            {
                ResponseId = responseId.Trim(),
                CallId = callId.Trim(),
                ResolvedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            }),
            envelopeId);

        await _dispatchPort.DispatchAsync(sessionActorId, envelope, ct);
    }

    private static EventEnvelope CreateEnvelope(
        string actorId,
        Any payload,
        string commandId) =>
        new()
        {
            Id = string.IsNullOrWhiteSpace(commandId) ? Guid.NewGuid().ToString("N") : commandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = payload,
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherId, actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = commandId,
            },
        };
}
