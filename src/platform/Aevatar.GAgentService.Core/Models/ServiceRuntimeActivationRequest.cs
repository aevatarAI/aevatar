using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Core;

public sealed record ServiceRuntimeActivationRequest(
    ServiceIdentity Identity,
    PreparedServiceRevisionArtifact Artifact,
    string RevisionId,
    string DeploymentActorId);
