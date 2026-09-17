using Aevatar.Workflow.Projection.Configuration;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowTerminalStateReconciler
{
    internal const int PageSize = 200;
    internal const string PublisherActorId = "workflow.run.terminal-recovery";
    internal static readonly TimeSpan MinimumStaleAge = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaximumStaleAge = TimeSpan.FromDays(7);

    private readonly IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> _documentReader;
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly WorkflowExecutionProjectionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowTerminalStateReconciler> _logger;

    public WorkflowTerminalStateReconciler(
        IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> documentReader,
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        WorkflowExecutionProjectionOptions options,
        TimeProvider timeProvider,
        ILogger<WorkflowTerminalStateReconciler>? logger = null)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<WorkflowTerminalStateReconciler>.Instance;
    }

    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || !_options.EnableTerminalStateReconciliation)
            return 0;

        var cutoff = _timeProvider.GetUtcNow() - ResolveStaleAge(_options);
        var cursor = (string?)null;
        var candidateCount = 0;
        var dispatchedCount = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _documentReader.QueryAsync(
                    BuildQuery(cursor, cutoff),
                    ct)
                .ConfigureAwait(false);

            foreach (var candidate in result.Items)
            {
                ct.ThrowIfCancellationRequested();
                candidateCount++;
                if (!IsCandidate(candidate, cutoff))
                    continue;

                try
                {
                    var actorId = candidate.RootActorId;
                    if (!await _actorRuntime.ExistsAsync(actorId).ConfigureAwait(false))
                    {
                        _logger.LogWarning(
                            "Workflow terminal reconciliation skipped a missing run actor. actorId={ActorId} runId={RunId}",
                            actorId,
                            candidate.RunId);
                        continue;
                    }

                    ct.ThrowIfCancellationRequested();
                    var envelope = CreateEnvelope(candidate, _timeProvider.GetUtcNow());
                    var admission = await _dispatchPort.DispatchAsync(actorId, envelope, ct).ConfigureAwait(false);
                    if (admission.Accepted)
                        dispatchedCount++;
                    else
                        _logger.LogWarning(
                            "Workflow terminal reconciliation was not accepted. actorId={ActorId} runId={RunId}",
                            actorId,
                            candidate.RunId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Workflow terminal reconciliation dispatch failed. actorId={ActorId} runId={RunId} observedStateVersion={ObservedStateVersion}",
                        candidate.RootActorId,
                        candidate.RunId,
                        candidate.StateVersion);
                }
            }

            var nextCursor = result.NextCursor;
            if (string.IsNullOrWhiteSpace(nextCursor))
                break;

            if (string.Equals(cursor, nextCursor, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Workflow terminal reconciliation query returned a non-advancing cursor. cursor={Cursor}",
                    cursor);
                break;
            }

            cursor = nextCursor;
        }

        if (candidateCount > 0)
        {
            _logger.LogInformation(
                "Workflow terminal reconciliation sweep completed. candidateCount={CandidateCount} dispatchedCount={DispatchedCount} cutoff={Cutoff}",
                candidateCount,
                dispatchedCount,
                cutoff);
        }

        return dispatchedCount;
    }

    private static ProjectionDocumentQuery BuildQuery(string? cursor, DateTimeOffset cutoff) =>
        new()
        {
            Cursor = cursor,
            Take = PageSize,
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(WorkflowExecutionCurrentStateDocument.Status),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString("running"),
                },
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(WorkflowExecutionCurrentStateDocument.UpdatedAtUtcValue),
                    Operator = ProjectionDocumentFilterOperator.Lte,
                    Value = ProjectionDocumentValue.FromDateTime(cutoff.UtcDateTime),
                },
            ],
            Sorts =
            [
                new ProjectionDocumentSort
                {
                    FieldPath = nameof(WorkflowExecutionCurrentStateDocument.UpdatedAtUtcValue),
                    Direction = ProjectionDocumentSortDirection.Asc,
                },
                new ProjectionDocumentSort
                {
                    FieldPath = nameof(WorkflowExecutionCurrentStateDocument.RootActorId),
                    Direction = ProjectionDocumentSortDirection.Asc,
                },
            ],
        };

    private static bool IsCandidate(
        WorkflowExecutionCurrentStateDocument candidate,
        DateTimeOffset cutoff) =>
        string.Equals(candidate.Status, "running", StringComparison.Ordinal) &&
        candidate.UpdatedAt <= cutoff &&
        !string.IsNullOrWhiteSpace(candidate.RootActorId) &&
        !string.IsNullOrWhiteSpace(candidate.RunId);

    private static EventEnvelope CreateEnvelope(
        WorkflowExecutionCurrentStateDocument candidate,
        DateTimeOffset now)
    {
        var commandId = Guid.NewGuid().ToString("N");
        return new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTimeOffset(now),
            Payload = Any.Pack(new ReconcileWorkflowTerminalStateCommand
            {
                RunId = candidate.RunId,
                ObservedStateVersion = candidate.StateVersion,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, candidate.RootActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = commandId,
            },
        };
    }

    private static TimeSpan ResolveStaleAge(WorkflowExecutionProjectionOptions options)
    {
        var configured = TimeSpan.FromSeconds(Math.Max(0, options.TerminalStateReconciliationStaleAfterSeconds));
        return configured < MinimumStaleAge
            ? MinimumStaleAge
            : configured > MaximumStaleAge
                ? MaximumStaleAge
                : configured;
    }
}
