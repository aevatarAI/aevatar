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

        _logger.LogWarning(
            "Projection failure recorded. scope={Scope} stage={Stage} eventId={EventId} eventType={EventType} sourceVersion={SourceVersion} failureCount={FailureCount} reason={Reason}",
            alert.ScopeKey,
            alert.Stage,
            alert.EventId,
            alert.EventType,
            alert.SourceVersion,
            alert.FailureCount,
            alert.Reason);
        return Task.CompletedTask;
    }
}
