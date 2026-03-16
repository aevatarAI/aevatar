using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;

namespace Aevatar.GAgentService.Core;

public sealed record ServiceRuntimeActivationRequest(
    ServiceIdentity Identity,
    PreparedServiceRevisionArtifact Artifact,
    string RevisionId,
    string DeploymentActorId,
    ActivationCapabilityView? CapabilityView = null);
