namespace Aevatar.GAgentService.Abstractions;

public sealed record ServiceInvocationResolvedService(
    string ServiceKey,
    string RevisionId,
    string DeploymentId,
    string PrimaryActorId,
    string ServingState,
    IReadOnlyList<string> PolicyIds);

public sealed record ServiceInvocationResolvedTarget(
    ServiceInvocationResolvedService Service,
    PreparedServiceRevisionArtifact Artifact,
    ServiceEndpointDescriptor Endpoint);
