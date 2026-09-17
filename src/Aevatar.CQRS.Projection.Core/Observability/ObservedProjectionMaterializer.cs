using System.Diagnostics;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Runtime.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Observability;

internal sealed class ObservedProjectionMaterializer<TContext, TInner>
    : IProjectionMaterializer<TContext>
    where TContext : class, IProjectionMaterializationContext
    where TInner : class, IProjectionMaterializer<TContext>
{
    private readonly TInner _inner;
    private readonly ILogger<ObservedProjectionMaterializer<TContext, TInner>> _logger;

    public ObservedProjectionMaterializer(
        TInner inner,
        ILogger<ObservedProjectionMaterializer<TContext, TInner>>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? NullLogger<ObservedProjectionMaterializer<TContext, TInner>>.Instance;
    }

    public async ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        var lastEventId = envelope.Id;
        long? stateVersion = null;
        if (CommittedStateEventEnvelope.TryUnpack(envelope, out var published) && published?.StateEvent != null)
        {
            if (!string.IsNullOrWhiteSpace(published.StateEvent.EventId))
                lastEventId = published.StateEvent.EventId;
            stateVersion = published.StateEvent.Version;
        }

        using var activity = AevatarActivitySource.StartProjectionMaterialize(typeof(TContext).Name, lastEventId);
        EnrichWorkflowTags(activity, context);

        var startedAt = Stopwatch.GetTimestamp();
        var result = ProjectionProcessingMetrics.ResultFailed;
        Exception? terminalException = null;
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
            result = ProjectionProcessingMetrics.ResultCompleted;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            terminalException = ex;
            result = ProjectionProcessingMetrics.ResultCancelled;
            AevatarActivitySource.SafeSetStatus(activity, ActivityStatusCode.Error, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            terminalException = ex;
            AevatarActivitySource.SafeSetStatus(activity, ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            ProjectionProcessingMetrics.RecordMaterializerTerminal(
                context.ProjectionKind,
                typeof(TInner).Name,
                result,
                elapsed);
            LogTerminal(context.ProjectionKind, stateVersion, result, elapsed, terminalException);
        }
    }

    private void LogTerminal(
        string projectionKind,
        long? stateVersion,
        string result,
        TimeSpan elapsed,
        Exception? terminalException)
    {
        try
        {
            if (string.Equals(result, ProjectionProcessingMetrics.ResultFailed, StringComparison.Ordinal))
            {
                _logger.LogError(
                    "Projection materializer failed. projectionKind={ProjectionKind} materializerKind={MaterializerKind} stateVersion={StateVersion} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}",
                    projectionKind,
                    typeof(TInner).Name,
                    stateVersion,
                    elapsed.TotalMilliseconds,
                    result,
                    terminalException?.GetType().Name ?? "Unknown");
                return;
            }

            if (string.Equals(result, ProjectionProcessingMetrics.ResultCancelled, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Projection materializer cancelled. projectionKind={ProjectionKind} materializerKind={MaterializerKind} stateVersion={StateVersion} elapsedMs={ElapsedMs} result={Result}",
                    projectionKind,
                    typeof(TInner).Name,
                    stateVersion,
                    elapsed.TotalMilliseconds,
                    result);
                return;
            }

            _logger.LogInformation(
                "Projection materializer completed. projectionKind={ProjectionKind} materializerKind={MaterializerKind} stateVersion={StateVersion} elapsedMs={ElapsedMs} result={Result}",
                projectionKind,
                typeof(TInner).Name,
                stateVersion,
                elapsed.TotalMilliseconds,
                result);
        }
        catch (Exception ex)
        {
            TraceLoggingFailure(ex);
        }
    }

    private static void TraceLoggingFailure(Exception exception)
    {
        try
        {
            Trace.TraceWarning(
                "Projection materializer terminal log emission failed. errorType={0}",
                exception.GetType().Name);
        }
        catch (Exception)
        {
            return;
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

    public ObservedCurrentStateProjectionMaterializer(
        TInner inner,
        ILogger<ObservedProjectionMaterializer<TContext, TInner>>? logger = null)
    {
        _inner = new ObservedProjectionMaterializer<TContext, TInner>(inner, logger);
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

    public ObservedProjectionArtifactMaterializer(
        TInner inner,
        ILogger<ObservedProjectionMaterializer<TContext, TInner>>? logger = null)
    {
        _inner = new ObservedProjectionMaterializer<TContext, TInner>(inner, logger);
    }

    public ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default) =>
        _inner.ProjectAsync(context, envelope, ct);
}
