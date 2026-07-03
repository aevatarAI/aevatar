using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Models;

namespace Aevatar.Audit.Abstractions.Ports;

public interface IAuditTrailAppender
{
    Task<AuditTrailAppendReceipt> AppendAsync(
        AuditRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditTrailAppendReceipt>> AppendManyAsync(
        IReadOnlyList<AuditRecord> records,
        CancellationToken cancellationToken = default);
}
