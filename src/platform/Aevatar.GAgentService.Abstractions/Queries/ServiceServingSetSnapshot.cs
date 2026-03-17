namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record ServiceServingSetSnapshot(
    string ServiceKey,
    long Generation,
    string ActiveRolloutId,
    IReadOnlyList<ServiceServingTargetSnapshot> Targets,
    DateTimeOffset UpdatedAt);

public sealed record ServiceServingTargetSnapshot(
    string DeploymentId,
    string RevisionId,
    string PrimaryActorId,
    int AllocationWeight,
    string ServingState,
    IReadOnlyList<string> EnabledEndpointIds);
