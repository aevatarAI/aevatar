using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptReadModelProjector
    : IProjectionProjector<ScriptExecutionProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<ScriptReadModelDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ScriptReadModelProjector(
        IProjectionWriteDispatcher<ScriptReadModelDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(
        ScriptExecutionProjectionContext context,
        CancellationToken ct = default)
    {
        _ = context;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(
        ScriptExecutionProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryGetObservedPayload(
                envelope,
                out var observedPayload,
                out var sourceEventId,
                out _) ||
            observedPayload?.Is(ScriptDomainFactCommitted.Descriptor) != true)
        {
            return;
        }

        var fact = observedPayload.Unpack<ScriptDomainFactCommitted>();
        var actorId = string.IsNullOrWhiteSpace(fact.ActorId) ? context.RootActorId : fact.ActorId;
        var document = new ScriptReadModelDocument
        {
            Id = actorId,
            ScriptId = fact.ScriptId ?? string.Empty,
            DefinitionActorId = fact.DefinitionActorId ?? string.Empty,
            Revision = fact.Revision ?? string.Empty,
            ReadModelTypeUrl = fact.ReadModelTypeUrl ?? string.Empty,
            ReadModelPayload = fact.ReadModelPayload?.Clone() ?? Any.Pack(new Empty()),
            StateVersion = fact.StateVersion,
            LastEventId = sourceEventId,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }

    public ValueTask CompleteAsync(
        ScriptExecutionProjectionContext context,
        IReadOnlyList<string> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        _ = ct;
        return ValueTask.CompletedTask;
    }
}
