using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class LoggingProjectionFailureAlertSink : IProjectionFailureAlertSink
{
    private readonly ILogger<LoggingProjectionFailureAlertSink> _logger;

    public LoggingProjectionFailureAlertSink(ILogger<LoggingProjectionFailureAlertSink>? logger = null)
    {
        _logger = logger ?? NullLogger<LoggingProjectionFailureAlertSink>.Instance;
    }

    public Task PublishAsync(ProjectionFailureAlert alert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ct.ThrowIfCancellationRequested();

        if (alert.Kind == ProjectionFailureAlertKind.DiagnosticRetentionDropped)
        {
            _logger.LogWarning(
                "Projection failure diagnostics dropped after durable repair handoff. scope={Scope} droppedCount={DroppedCount} droppedFailureIds={DroppedFailureIds} diagnosticDroppedTotal={DiagnosticDroppedTotal} unresolvedFailureCount={UnresolvedFailureCount}",
                alert.ScopeKey,
                alert.DroppedCount,
                alert.DroppedFailureIds,
                alert.DiagnosticDroppedTotal,
                alert.UnresolvedFailureCount);
        }
        else
        {
            _logger.LogWarning(
                "Projection failure recorded. scope={Scope} stage={Stage} eventId={EventId} eventType={EventType} sourceVersion={SourceVersion} unresolvedFailureCount={UnresolvedFailureCount} reason={Reason}",
                alert.ScopeKey,
                alert.Stage,
                alert.EventId,
                alert.EventType,
                alert.SourceVersion,
                alert.UnresolvedFailureCount,
                alert.Reason);
        }
        return Task.CompletedTask;
    }
}
