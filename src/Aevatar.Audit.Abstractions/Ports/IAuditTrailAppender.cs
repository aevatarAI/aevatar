using Aevatar.Audit;
<<<<<<< HEAD
=======
using Aevatar.Audit.Abstractions.Models;
>>>>>>> origin/crnd/milestone32-platform-audit-trail

namespace Aevatar.Audit.Abstractions.Ports;

public interface IAuditTrailAppender
{
<<<<<<< HEAD
    Task<AuditTrailAppendResult> AppendAsync(AuditRecord record, CancellationToken ct = default);
=======
    Task<AuditTrailAppendReceipt> AppendAsync(
        AuditRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditTrailAppendReceipt>> AppendManyAsync(
        IReadOnlyList<AuditRecord> records,
        CancellationToken cancellationToken = default);
>>>>>>> origin/crnd/milestone32-platform-audit-trail
}
