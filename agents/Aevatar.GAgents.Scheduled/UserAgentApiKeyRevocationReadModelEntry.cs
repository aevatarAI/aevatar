using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

public sealed class UserAgentApiKeyRevocationReadModelEntry
{
    public string AgentId { get; init; } = string.Empty;
    public string ApiKeyId { get; init; } = string.Empty;
    public OwnerScope? OwnerScope { get; init; }
    public Timestamp? RequestedAt { get; init; }
    public int AttemptCount { get; init; }
    public Timestamp? LastAttemptAt { get; init; }
    public int LastHttpStatus { get; init; }
    public string LastError { get; init; } = string.Empty;
    public UserAgentApiKeyRevocationFailureKind FailureKind { get; init; }
    public long CatalogAuthorityStateVersion { get; init; }
    public string CatalogLastEventId { get; init; } = string.Empty;
}
