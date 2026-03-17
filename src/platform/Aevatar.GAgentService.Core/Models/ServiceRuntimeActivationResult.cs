namespace Aevatar.GAgentService.Core;

public sealed record ServiceRuntimeActivationResult(
    string DeploymentId,
    string PrimaryActorId,
    string Status);
