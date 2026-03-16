using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Projection.ReadModels;

public sealed class ServiceRevisionCatalogReadModel
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ServiceRevisionCatalogReadModel>
{
    public string Id { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public long StateVersion { get; set; }

    public string LastEventId { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ServiceRevisionEntryReadModel> Revisions { get; set; } = [];

    public ServiceRevisionCatalogReadModel DeepClone()
    {
        return new ServiceRevisionCatalogReadModel
        {
            Id = Id,
            ActorId = ActorId,
            StateVersion = StateVersion,
            LastEventId = LastEventId,
            UpdatedAt = UpdatedAt,
            Revisions = Revisions
                .Select(x => x.DeepClone())
                .ToList(),
        };
    }
}

public sealed class ServiceRevisionEntryReadModel
{
    public string RevisionId { get; set; } = string.Empty;

    public string ImplementationKind { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string ArtifactHash { get; set; } = string.Empty;

    public string FailureReason { get; set; } = string.Empty;

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? PreparedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset? RetiredAt { get; set; }

    public List<ServiceCatalogEndpointReadModel> Endpoints { get; set; } = [];

    public ServiceRevisionEntryReadModel DeepClone()
    {
        return new ServiceRevisionEntryReadModel
        {
            RevisionId = RevisionId,
            ImplementationKind = ImplementationKind,
            Status = Status,
            ArtifactHash = ArtifactHash,
            FailureReason = FailureReason,
            CreatedAt = CreatedAt,
            PreparedAt = PreparedAt,
            PublishedAt = PublishedAt,
            RetiredAt = RetiredAt,
            Endpoints = Endpoints
                .Select(x => x.DeepClone())
                .ToList(),
        };
    }
}
