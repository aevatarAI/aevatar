using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Core;

public sealed record ServiceRuntimeDeactivationRequest(
    ServiceIdentity Identity,
    string DeploymentId,
    string RevisionId,
    string PrimaryActorId);
