using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Materializes <see cref="ExternalIdentityBindingState"/> into one
/// <see cref="ExternalIdentityBindingDocument"/> per actor. Mirrors the
/// <c>WorkflowExecutionCurrentStateProjector</c> pattern: unpack the
/// committed-state envelope, copy the typed state fields, upsert via
/// the write dispatcher. Read side (`IExternalIdentityBindingQueryPort`)
/// reads the same documents — see ADR-0018 §Projection Readiness.
/// </summary>
public sealed class ExternalIdentityBindingProjector
    : ICurrentStateProjectionMaterializer<ExternalIdentityBindingMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ExternalIdentityBindingDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;
    private readonly ILogger<ExternalIdentityBindingProjector> _logger;

    public ExternalIdentityBindingProjector(
        IProjectionWriteDispatcher<ExternalIdentityBindingDocument> writeDispatcher,
        IProjectionClock clock,
        ILogger<ExternalIdentityBindingProjector>? logger = null)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? NullLogger<ExternalIdentityBindingProjector>.Instance;
    }

    public async ValueTask ProjectAsync(
        ExternalIdentityBindingMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<ExternalIdentityBindingState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var document = new ExternalIdentityBindingDocument
        {
            Id = context.RootActorId,
            ExternalSubject = state.ExternalSubject?.Clone(),
            BindingId = state.BindingId ?? string.Empty,
            BoundAtUtcValue = state.BoundAt,
            RevokedAtUtcValue = state.RevokedAt,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
        };

        if (string.IsNullOrEmpty(document.BindingId))
        {
            _logger.LogWarning(
                "Deleting external identity binding document {DocumentId} because projected BindingId is empty. event={EventId}, version={Version}",
                document.Id,
                document.LastEventId,
                document.StateVersion);
            await _writeDispatcher.DeleteAsync(document.Id, ct);
            return;
        }

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
