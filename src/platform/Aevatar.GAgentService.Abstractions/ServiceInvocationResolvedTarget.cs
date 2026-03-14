namespace Aevatar.GAgentService.Abstractions;

public sealed record ServiceInvocationResolvedService(
    string ServiceKey,
    string ActiveRevisionId,
    string DeploymentId,
    string PrimaryActorId,
    string DeploymentStatus,
    IReadOnlyList<string> PolicyIds);

public sealed record ServiceInvocationResolvedTarget(
    ServiceInvocationResolvedService Service,
    PreparedServiceRevisionArtifact Artifact,
    ServiceEndpointDescriptor Endpoint);
