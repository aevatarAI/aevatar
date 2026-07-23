using Google.Protobuf;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public sealed record SystemAgentProfileDefinition(
    string DefinitionKey,
    string ProfileSlug,
    AgentProfileContent Content,
    bool Required = true)
{
    private AgentProfileContent _content = Content.Clone();

    public AgentProfileContent Content
    {
        get => _content.Clone();
        init => _content = value.Clone();
    }

    public SystemAgentProfileDefinition DeepClone() =>
        this with { Content = Content.Clone() };
}

public interface ISystemAgentProfileDefinitionSource
{
    IReadOnlyList<SystemAgentProfileDefinition> GetDefinitions();
}

public interface ISystemAgentProfileOrnnAccessTokenProvider
{
    Task<string?> GetAccessTokenAsync(
        string definitionKey,
        CancellationToken ct = default);
}

public interface ISystemAgentProfileProvisioningService
{
    Task ReconcileAsync(CancellationToken ct = default);
}

public interface ISystemAgentProfileReadinessService
{
    Task<SystemAgentProfileReadinessSnapshot> GetAsync(CancellationToken ct = default);
}

public enum SystemAgentProfileReadinessStatus
{
    Unspecified = 0,
    Ready = 1,
    Pending = 2,
    Unavailable = 3,
    Unhealthy = 4,
}

public enum SystemAgentProfileReadinessReason
{
    None = 0,
    NamespaceMissing = 1,
    NamespaceProvisioning = 2,
    NamespaceProvisioningFailed = 3,
    NamespaceConflict = 4,
    ManagementSnapshotMissing = 5,
    ProfileIdentityConflict = 6,
    DraftDrift = 7,
    PublicationPending = 8,
    OrnnAccessTokenUnavailable = 9,
    ExecutionSnapshotMissing = 10,
    ExecutionSnapshotLagging = 11,
}

public sealed record SystemAgentProfileReadinessEntry(
    string DefinitionKey,
    bool Required,
    AgentProfileReference Reference,
    SystemAgentProfileReadinessStatus Status,
    SystemAgentProfileReadinessReason Reason,
    string ProfileId,
    long DraftRevision,
    ByteString DesiredContentSha256,
    ByteString DraftSha256,
    long PublishedRevision,
    ByteString PublishedSourceDraftSha256,
    ByteString PublishedSnapshotSha256,
    long ExecutionPublishedRevision,
    ByteString ExecutionSnapshotSha256)
{
    private AgentProfileReference _reference = Reference.Clone();

    public AgentProfileReference Reference
    {
        get => _reference.Clone();
        init => _reference = value.Clone();
    }

    public SystemAgentProfileReadinessEntry DeepClone() =>
        this with { Reference = Reference.Clone() };
}

public sealed record SystemAgentProfileReadinessSnapshot(
    IReadOnlyList<SystemAgentProfileReadinessEntry> Profiles)
{
    private IReadOnlyList<SystemAgentProfileReadinessEntry> _profiles =
        Profiles.Select(static profile => profile.DeepClone()).ToArray();

    public IReadOnlyList<SystemAgentProfileReadinessEntry> Profiles
    {
        get => _profiles.Select(static profile => profile.DeepClone()).ToArray();
        init => _profiles = value.Select(static profile => profile.DeepClone()).ToArray();
    }

    public bool IsReady =>
        _profiles
            .Where(static profile => profile.Required)
            .All(static profile => profile.Status == SystemAgentProfileReadinessStatus.Ready);
}
