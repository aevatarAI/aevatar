using Aevatar.Audit.Abstractions.Models;

namespace Aevatar.Audit.Abstractions.Ports;

public interface IAuditTrailQueryPort
{
    Task<AuditTrailPage> QueryAsync(
        AuditTrailQuery query,
        CancellationToken cancellationToken = default);
}
