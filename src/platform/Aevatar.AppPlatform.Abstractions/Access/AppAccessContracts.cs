namespace Aevatar.AppPlatform.Abstractions.Access;

public sealed record AppAccessResource(
    string OwnerScopeId,
    string? TenantId = null,
    string? AppId = null,
    string? ServiceKey = null,
    string? EndpointId = null,
    bool IsPublic = false);

public sealed record AppAccessRequest(
    string? SubjectScopeId,
    string Action,
    AppAccessResource Resource,
    string? CallerServiceKey = null);

public sealed record AppAccessDecision(
    bool Allowed,
    string Reason);
