using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Core.Auditing;
using Aevatar.Audit.Abstractions.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Core.Middleware;

public sealed class ToolExecutionAuditMiddleware : IToolCallMiddleware
{
    private readonly IAuditTrailAppender _auditTrailAppender;
    private readonly ToolAuditRecordFactory _recordFactory;
    private readonly ILogger<ToolExecutionAuditMiddleware> _logger;

    public ToolExecutionAuditMiddleware(
        IAuditTrailAppender auditTrailAppender,
        ToolAuditRecordFactory recordFactory,
        ILogger<ToolExecutionAuditMiddleware>? logger = null)
    {
        _auditTrailAppender = auditTrailAppender ?? throw new ArgumentNullException(nameof(auditTrailAppender));
        _recordFactory = recordFactory ?? throw new ArgumentNullException(nameof(recordFactory));
        _logger = logger ?? NullLogger<ToolExecutionAuditMiddleware>.Instance;
    }

    public async Task InvokeAsync(ToolCallContext context, Func<Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Exception? executionException = null;
        try
        {
            await next().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            executionException = ex;
            throw;
        }
        finally
        {
            await AppendAuditRecordAsync(context, executionException).ConfigureAwait(false);
        }
    }

    private async Task AppendAuditRecordAsync(ToolCallContext context, Exception? executionException)
    {
        try
        {
            var receipt = ToolCallReceiptFinalizer.Finalize(context, executionException);
            var record = _recordFactory.Create(context, receipt);
            await _auditTrailAppender.AppendAsync(record, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Tool execution audit append failed. tool={ToolName} callId={ToolCallId}",
                context.ToolName,
                context.ToolCallId);
        }
    }
}
