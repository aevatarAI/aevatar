using System.Text.Json.Serialization;

namespace Aevatar.GAgentService.Abstractions.Queries;

[method: JsonConstructor]
public sealed record ServiceDeploymentCatalogSnapshot(
    string ServiceKey,
    IReadOnlyList<ServiceDeploymentSnapshot> Deployments,
    IReadOnlyList<ServiceDeploymentActivationFailureSnapshot> ActivationFailures,
    DateTimeOffset UpdatedAt)
{
    public ServiceDeploymentCatalogSnapshot(
        string serviceKey,
        IReadOnlyList<ServiceDeploymentSnapshot> deployments,
        DateTimeOffset updatedAt)
        : this(serviceKey, deployments, [], updatedAt)
    {
    }
}

public sealed record ServiceDeploymentSnapshot(
    string DeploymentId,
    string RevisionId,
    string PrimaryActorId,
    string Status,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset UpdatedAt,
    string ArtifactHash = "");

public sealed record ServiceDeploymentActivationFailureSnapshot(
    string RevisionId,
    ServiceDeploymentActivationFailureCode FailureCode,
    string FailureReason,
    DateTimeOffset OccurredAt,
    [property: JsonIgnore] string ActivationAttemptId = "");
