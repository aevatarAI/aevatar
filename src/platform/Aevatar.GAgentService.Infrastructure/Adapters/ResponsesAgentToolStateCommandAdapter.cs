using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Core.GAgents;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

/// <summary>
/// Host-side adapter for the <see cref="IResponsesAgentToolStateCommandPort"/>.
/// JSON payloads arriving at the HTTP boundary are parsed into typed proto here
/// before dispatch; the actor state never stores JSON strings. The shared
/// <see cref="ResponsesTodoItemParser"/> drives both the dispatched command and
/// the preview returned to the caller so there is only one parser implementation.
/// </summary>
public sealed class ResponsesAgentToolStateCommandAdapter : IResponsesAgentToolStateCommandPort
{
    private const string PublisherId = "gagent-service.responses-agent-tools";

    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public ResponsesAgentToolStateCommandAdapter(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<ResponsesTodoWriteResult> ApplyTodoWriteAsync(
        string scopeId,
        string ownerSubject,
        string sourceResponseId,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var actor = await EnsureActorAsync(scopeId, ownerSubject, ct);
        var observedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        var todos = ResponsesTodoItemParser.Parse(argumentsJson, sourceResponseId, observedAt);

        var apply = new ApplyResponsesTodoWriteRequested
        {
            ScopeId = scopeId.Trim(),
            OwnerSubject = ownerSubject.Trim(),
            SourceResponseId = NormalizeOptional(sourceResponseId) ?? string.Empty,
            Arguments = ResponsesJsonValues.ParseBoundaryPayload(argumentsJson),
            ObservedAt = observedAt,
        };
        apply.TodoItems.AddRange(todos.Select(static x => x.Clone()));

        await _dispatchPort.DispatchAsync(
            actor.Id,
            CreateEnvelope(
                actor.Id,
                Any.Pack(apply),
                $"{sourceResponseId}:todo:{Guid.NewGuid():N}"),
            ct);

        var snapshots = todos
            .Select(item => new ResponsesTodoItemSnapshot(
                item.Id,
                item.Content,
                item.Status,
                item.SourceResponseId,
                item.CreatedAt.ToDateTimeOffset(),
                item.UpdatedAt.ToDateTimeOffset()))
            .ToArray();
        return new ResponsesTodoWriteResult(actor.Id, sourceResponseId, snapshots);
    }

    // Refactor (iter159/cluster-623-first):
    //   Old pattern: fake Task substitute synthesized child_actor_id, returned accepted, recorded parent trace only
    //   New principle: removed active substitute path; TodoWrite remains the only real substitute
    public async Task<ResponsesWebTraceResult> RecordWebTraceAsync(
        string scopeId,
        string ownerSubject,
        string sourceResponseId,
        ResponsesWebTraceInput trace,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trace);

        var actor = await EnsureActorAsync(scopeId, ownerSubject, ct);
        var traceId = string.IsNullOrWhiteSpace(trace.TraceId)
            ? ResponseAgentToolStateIds.NewWebTraceId()
            : trace.TraceId.Trim();
        await _dispatchPort.DispatchAsync(
            actor.Id,
            CreateEnvelope(
                actor.Id,
                Any.Pack(new RecordResponsesWebTraceRequested
                {
                    SourceResponseId = NormalizeOptional(sourceResponseId) ?? string.Empty,
                    TraceId = traceId,
                    ToolName = trace.ToolName.Trim(),
                    CacheKey = trace.CacheKey.Trim(),
                    Url = NormalizeOptional(trace.Url) ?? string.Empty,
                    Query = NormalizeOptional(trace.Query) ?? string.Empty,
                    CacheHit = trace.CacheHit,
                    // Refactor (iter161-cluster-001 #1251-first):
                    //   Old pattern: first slice stopped writing legacy Value.
                    //   New principle: keep typed result primary while writing Value as readmodel fallback.
                    Result = ResponsesWebResultMigration.ToLegacyValue(trace.Result),
                    TypedResult = trace.Result.Clone(),
                    ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                }),
                $"{sourceResponseId}:web:{traceId}"),
            ct);

        return new ResponsesWebTraceResult(actor.Id, traceId, trace.CacheKey, trace.CacheHit, trace.Result.Clone());
    }

    private async Task<IActor> EnsureActorAsync(
        string scopeId,
        string ownerSubject,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("scopeId is required.", nameof(scopeId));
        if (string.IsNullOrWhiteSpace(ownerSubject))
            throw new ArgumentException("ownerSubject is required.", nameof(ownerSubject));

        var actorId = await ResolveActorIdAsync(scopeId, ownerSubject);
        var actor = await _runtime.CreateAsync<ResponsesAgentToolStateGAgent>(actorId, ct: ct);
        // The register dispatch is idempotent at the actor (HandleRegisterAsync
        // returns early when scope/owner already match). We do not cache a
        // "registered" set in this adapter — that would violate the
        // middle-tier state constraint in CLAUDE.md (no service-level
        // entity-id → fact-state dictionary). The cost is one extra ignored
        // envelope per command, which is dwarfed by the projection write the
        // command itself triggers.
        // Refactor (iter18/cluster-006):
        //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
        //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
        await _dispatchPort.DispatchAsync(
            actor.Id,
            CreateEnvelope(
                actor.Id,
                Any.Pack(new RegisterResponsesAgentToolStateRequested
                {
                    Record = new ResponsesAgentToolStateRecord
                    {
                        ScopeId = scopeId.Trim(),
                        OwnerSubject = ownerSubject.Trim(),
                        CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                        UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                    },
                }),
                $"{actor.Id}:registered"),
            ct);
        return actor;
    }

    private async Task<string> ResolveActorIdAsync(string scopeId, string ownerSubject)
    {
        var actorId = ResponseAgentToolStateIds.BuildActorId(scopeId, ownerSubject);
        var legacyActorId = ResponseAgentToolStateIds.BuildLegacyActorId(scopeId, ownerSubject);
        return !string.Equals(actorId, legacyActorId, StringComparison.Ordinal)
               && await _runtime.ExistsAsync(legacyActorId)
            ? legacyActorId
            : actorId;
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

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
