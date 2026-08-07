namespace Aevatar.Studio.Application.Provisioning;

public sealed record StudioMemberInvocationReadinessQueryResult(
    string ScopeId,
    string MemberId,
    string PublishedServiceId,
    string EndpointId,
    string RevisionId,
    bool CanInvoke,
    string Status,
    string ReasonCode,
    string Message,
    string? DeploymentId = null,
    DateTimeOffset? ObservedAtUtc = null);

public interface IStudioMemberInvocationReadinessQueryPort
{
    Task<StudioMemberInvocationReadinessQueryResult?> GetAsync(
        string scopeId,
        string memberId,
        string endpointId,
        CancellationToken ct = default);
}
