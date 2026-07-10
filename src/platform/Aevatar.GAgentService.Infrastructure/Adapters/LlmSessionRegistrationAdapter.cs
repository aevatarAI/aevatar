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
// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
public sealed class LlmSessionRegistrationAdapter : ILlmSessionRegistrationPort
{
    private const string PublisherId = "gagent-service.response-sessions";

    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public LlmSessionRegistrationAdapter(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<LlmSessionRegistrationResult> RegisterAsync(
        LlmSessionRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.ResponseId))
            throw new InvalidOperationException("response_id is required.");
        if (string.IsNullOrWhiteSpace(record.ScopeId))
            throw new InvalidOperationException("scope_id is required.");
        if (string.IsNullOrWhiteSpace(record.OwnerSubject))
            throw new InvalidOperationException("owner_subject is required.");

        var actorId = LlmSessionIds.NewActorId();
        var actor = await _runtime.CreateAsync<LlmSessionGAgent>(actorId, ct: ct);

        var prepared = record.Clone();
        if (prepared.CreatedAt == null)
            prepared.CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        prepared.UpdatedAt = prepared.CreatedAt.Clone();
        if (prepared.Status == LlmSessionStatus.Unspecified)
            prepared.Status = LlmSessionStatus.Accepted;

        // Refactor (iter18/cluster-006):
        //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
        //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
        var envelope = CreateEnvelope(
            actor.Id,
            Any.Pack(new RegisterResponseSessionRequested
            {
                Record = prepared,
            }),
            prepared.ResponseId);

        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
        return new LlmSessionRegistrationResult(actor.Id, prepared.ResponseId);
    }

    public async Task UpdateStatusAsync(
        string sessionActorId,
        string responseId,
        LlmSessionStatus status,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionActorId))
            throw new ArgumentException("sessionActorId is required.", nameof(sessionActorId));
        if (string.IsNullOrWhiteSpace(responseId))
            throw new ArgumentException("responseId is required.", nameof(responseId));
        if (status == LlmSessionStatus.Unspecified)
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

    public async Task CancelRunAsync(
        string sessionActorId,
        string responseId,
        string runId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionActorId))
            throw new ArgumentException("sessionActorId is required.", nameof(sessionActorId));
        if (string.IsNullOrWhiteSpace(responseId))
            throw new ArgumentException("responseId is required.", nameof(responseId));
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("runId is required.", nameof(runId));

        var trimmedResponseId = responseId.Trim();
        var trimmedRunId = runId.Trim();
        var envelopeId = $"{trimmedResponseId}:run:{trimmedRunId}:cancelled";
        var envelope = CreateEnvelope(
            sessionActorId,
            Any.Pack(new CancelLlmRunRequested
            {
                ResponseId = trimmedResponseId,
                RunId = trimmedRunId,
                CancelledAt = Timestamp.FromDateTime(DateTime.UtcNow),
            }),
            envelopeId);

        await _dispatchPort.DispatchAsync(sessionActorId, envelope, ct);
    }

    public async Task RecordForwardedToolCallAsync(
        string sessionActorId,
        string responseId,
        LlmSessionForwardedToolCall call,
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
        if (prepared.Status == LlmSessionForwardedToolCallStatus.Unspecified)
            prepared.Status = LlmSessionForwardedToolCallStatus.Pending;
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

    // Refactor (iter75/cluster-075-responses-agui-host-completion-state):
    //   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
    //   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
    public async Task RecordCompletionAsync(
        string sessionActorId,
        string responseId,
        LlmSessionCompletion completion,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionActorId))
            throw new ArgumentException("sessionActorId is required.", nameof(sessionActorId));
        if (string.IsNullOrWhiteSpace(responseId))
            throw new ArgumentException("responseId is required.", nameof(responseId));
        ArgumentNullException.ThrowIfNull(completion);

        var prepared = completion.Clone();
        if (prepared.CompletedAt == null)
            prepared.CompletedAt = Timestamp.FromDateTime(DateTime.UtcNow);

        var envelopeId = $"{responseId}:completion";
        var envelope = CreateEnvelope(
            sessionActorId,
            Any.Pack(new RecordResponseSessionCompletionRequested
            {
                ResponseId = responseId.Trim(),
                Completion = prepared,
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
