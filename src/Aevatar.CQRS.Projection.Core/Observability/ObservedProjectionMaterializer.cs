using System.Diagnostics;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Runtime.Observability;

namespace Aevatar.CQRS.Projection.Core.Observability;

internal sealed class ObservedProjectionMaterializer<TContext, TInner>
    : IProjectionMaterializer<TContext>
    where TContext : class, IProjectionMaterializationContext
    where TInner : class, IProjectionMaterializer<TContext>
{
    private readonly TInner _inner;

    public ObservedProjectionMaterializer(TInner inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        var lastEventId = envelope.Id;
        long? stateVersion = null;
        if (CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out _, out var observedEventId, out var observedStateVersion))
        {
            if (!string.IsNullOrWhiteSpace(observedEventId))
                lastEventId = observedEventId;
            stateVersion = observedStateVersion;
        }

        using var activity = AevatarActivitySource.StartProjectionMaterialize(typeof(TContext).Name, lastEventId);
        EnrichWorkflowTags(activity, context);

        try
        {
            await _inner.ProjectAsync(context, envelope, ct);
            if (stateVersion.HasValue)
            {
                AevatarActivitySource.SafeSetTag(
                    activity,
                    AevatarActivitySource.ProjectionStateVersionTag,
                    stateVersion.Value);
            }

            AevatarActivitySource.SafeSetStatus(activity, ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            AevatarActivitySource.SafeSetStatus(activity, ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private static void EnrichWorkflowTags(Activity? activity, TContext context)
    {
        if (!string.Equals(context.GetType().Name, "WorkflowExecutionProjectionContext", StringComparison.Ordinal))
            return;

        AevatarActivitySource.SafeSetTag(
            activity,
            AevatarActivitySource.WorkflowRunIdTag,
            context.RootActorId);
    }
}

internal sealed class ObservedCurrentStateProjectionMaterializer<TContext, TInner>
    : ICurrentStateProjectionMaterializer<TContext>
    where TContext : class, IProjectionMaterializationContext
    where TInner : class, ICurrentStateProjectionMaterializer<TContext>
{
    private readonly ObservedProjectionMaterializer<TContext, TInner> _inner;

    public ObservedCurrentStateProjectionMaterializer(TInner inner)
    {
        _inner = new ObservedProjectionMaterializer<TContext, TInner>(inner);
    }

    public ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default) =>
        _inner.ProjectAsync(context, envelope, ct);
}

internal sealed class ObservedProjectionArtifactMaterializer<TContext, TInner>
    : IProjectionArtifactMaterializer<TContext>
    where TContext : class, IProjectionMaterializationContext
    where TInner : class, IProjectionArtifactMaterializer<TContext>
{
    private readonly ObservedProjectionMaterializer<TContext, TInner> _inner;

    public ObservedProjectionArtifactMaterializer(TInner inner)
    {
        _inner = new ObservedProjectionMaterializer<TContext, TInner>(inner);
    }

    public ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default) =>
        _inner.ProjectAsync(context, envelope, ct);
}
