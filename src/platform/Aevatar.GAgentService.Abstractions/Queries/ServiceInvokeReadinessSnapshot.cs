namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record ServiceInvocationCatalogSnapshot(
    string ServiceKey,
    IReadOnlyList<ServiceInvokeReadinessSnapshot> Entries,
    DateTimeOffset ObservedAt,
    long AggregateStateVersion,
    string LastEventId,
    long SourceCatalogVersion,
    long SourceServingVersion,
    long SourceRevisionVersion);

public sealed record ServiceInvokeReadinessSnapshot(
    string ServiceKey,
    string EndpointId,
    ServiceInvokeReadinessStatus ReadinessStatus,
    ServiceInvokeUnavailableReason UnavailableReason,
    string SelectedRevisionId,
    string SelectedDeploymentId,
    string SelectedActorId,
    DateTimeOffset ObservedAt,
    long AggregateStateVersion,
    string LastEventId,
    long SourceCatalogVersion,
    long SourceServingVersion,
    long SourceRevisionVersion);
