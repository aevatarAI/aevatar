namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record ServiceTrafficViewSnapshot(
    string ServiceKey,
    long Generation,
    string ActiveRolloutId,
    IReadOnlyList<ServiceTrafficEndpointSnapshot> Endpoints,
    DateTimeOffset UpdatedAt);

public sealed record ServiceTrafficEndpointSnapshot(
    string EndpointId,
    IReadOnlyList<ServiceTrafficTargetSnapshot> Targets);

public sealed record ServiceTrafficTargetSnapshot(
    string DeploymentId,
    string RevisionId,
    string PrimaryActorId,
    int AllocationWeight,
    string ServingState);
