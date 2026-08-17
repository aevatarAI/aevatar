namespace Aevatar.GAgentService.Abstractions;

public enum ScopeBindingReadinessStatus
{
    Unknown = 0,
    ServiceCatalogMissing = 1,
    ServingSetMissing = 2,
    EligibleServingTargetMissing = 3,
    ServiceCatalogTargetMissing = 4,
    Ready = 5,
    TrafficViewTargetMissing = 6,
    PreparedArtifactMissing = 7,
    InvocationCatalogNotReady = 8,
}

public sealed record ScopeBindingReadinessRequest(
    string ScopeId,
    string ServiceId,
    string? AppId = null,
    string? ExpectedRevisionId = null,
    string? ExpectedDeploymentId = null,
    IReadOnlyList<string>? ExpectedEndpointIds = null,
    string? ExpectedActivationAttemptId = null);

public sealed record ScopeBindingReadinessSnapshot(
    string ScopeId,
    string ServiceId,
    ScopeBindingReadinessStatus Status,
    bool ServiceCatalogVisible,
    bool ServingSetVisible,
    bool EligibleServingTargetVisible,
    bool InvokeReady,
    string? RevisionId = null,
    string? DeploymentId = null,
    DateTimeOffset? ObservedAtUtc = null,
    ServiceDeploymentActivationFailureCode? TerminalActivationFailureCode = null);
