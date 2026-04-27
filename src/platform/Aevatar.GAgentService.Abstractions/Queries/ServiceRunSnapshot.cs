namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record ServiceRunSnapshot(
    string ScopeId,
    string ServiceId,
    string ServiceKey,
    string RunId,
    string CommandId,
    string CorrelationId,
    string EndpointId,
    ServiceImplementationKind ImplementationKind,
    string TargetActorId,
    string RevisionId,
    string DeploymentId,
    ServiceRunStatus Status,
    string ActorId,
    string TenantId,
    string AppId,
    string Namespace,
    long StateVersion,
    string LastEventId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ServiceRunQuery(
    string ScopeId,
    string ServiceId,
    int Take = 50);
