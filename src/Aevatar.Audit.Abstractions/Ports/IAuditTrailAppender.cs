using Aevatar.Audit;

namespace Aevatar.Audit.Abstractions.Ports;

public interface IAuditTrailAppender
{
    Task<AuditTrailAppendResult> AppendAsync(
        AuditRecord record,
        CancellationToken cancellationToken = default);
}
