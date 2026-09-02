using Aevatar.Audit.Abstractions.Models;

namespace Aevatar.Audit.Abstractions.Identity;

public interface IAuditActorIdentityHasher
{
    AuditActorIdentity Hash(string canonicalActorKey);

    IReadOnlyList<AuditActorIdentity> HashAll(string canonicalActorKey) =>
        [Hash(canonicalActorKey)];

    bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId);
}
