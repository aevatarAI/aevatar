using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf.WellKnownTypes;
using ChannelAddressModel = Aevatar.GAgents.Channel.Abstractions.ChannelDeliveryAddress;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Caller-scoped catalog read model row. Distinct from <see cref="UserAgentCatalogEntry"/>
/// so runner-owned execution facts are never reintroduced into catalog actor state.
/// </summary>
public sealed class UserAgentCatalogReadModelEntry
{
    public string AgentId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string NyxProviderSlug { get; init; } = string.Empty;
    public string AgentType { get; init; } = string.Empty;
    public string TemplateName { get; init; } = string.Empty;
    public string ScopeId { get; init; } = string.Empty;
    public string ApiKeyId { get; init; } = string.Empty;
    public string ScheduleCron { get; init; } = string.Empty;
    public string ScheduleTimezone { get; init; } = string.Empty;
    public ScheduledAgentScheduleMode ScheduleMode { get; init; } = ScheduledAgentScheduleMode.Cron;
    public Timestamp? RunAt { get; init; }
    public Timestamp? RetiredAt { get; init; }
    public string RetirementReason { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public Timestamp? LastRunAt { get; init; }
    public Timestamp? NextRunAt { get; init; }
    public int ErrorCount { get; init; }
    public string LastError { get; init; } = string.Empty;
    public Timestamp? CreatedAt { get; init; }
    public Timestamp? UpdatedAt { get; init; }
    public bool Tombstoned { get; init; }
    public ChannelAddressModel ChannelAddress { get; init; } = ChannelAddressModel.Empty;
    public ScheduledAgentOutputFormat OutputFormat { get; init; } = ScheduledAgentOutputFormat.Auto;
    public OwnerScope? OwnerScope { get; init; }
    public ScheduledAgentSharingGrant? SharingGrant { get; init; }
    public string TargetPlatform { get; init; } = string.Empty;
    public long CatalogAuthorityStateVersion { get; init; }
    public string CatalogLastEventId { get; init; } = string.Empty;
    public long? RunnerAuthorityStateVersion { get; init; }
    public string RunnerLastEventId { get; init; } = string.Empty;
}
