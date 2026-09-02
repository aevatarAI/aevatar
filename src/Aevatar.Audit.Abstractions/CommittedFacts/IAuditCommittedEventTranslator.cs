using Aevatar.Audit;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Audit.Abstractions.CommittedFacts;

public interface IAuditCommittedEventTranslator
{
    string EventTypeUrl { get; }

    IReadOnlyList<AuditRecord> Translate(CommittedAuditTranslationContext context, Any eventPayload);
}
