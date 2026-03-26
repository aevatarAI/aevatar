using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Aevatar.AppPlatform.Hosting.Endpoints;

internal static class AppPlatformEndpointModels
{
    internal sealed record FunctionInvokeBinaryPayloadHttpRequest(
        string? TypeUrl,
        string? PayloadBase64);

    internal sealed record FunctionInvokeTypedPayloadHttpRequest(
        string? TypeUrl,
        JsonElement? PayloadJson);

    internal sealed record AppListQuery(
        [FromQuery(Name = "ownerScopeId")] string? OwnerScopeId);

    internal sealed record RouteDeleteQuery(
        [FromQuery(Name = "routePath")] string? RoutePath);

    internal sealed record ResolveRouteQuery(
        [FromQuery(Name = "routePath")] string? RoutePath);

    internal sealed record OperationStreamQuery(
        [FromQuery(Name = "afterSequence")] ulong? AfterSequence);

    internal sealed record CreateAppHttpRequest(
        string? AppId,
        string? OwnerScopeId,
        string? DisplayName,
        string? Description,
        string? Visibility,
        string? DefaultReleaseId = null);

    internal sealed record UpsertAppHttpRequest(
        string? OwnerScopeId,
        string? DisplayName,
        string? Description,
        string? Visibility,
        string? DefaultReleaseId = null);

    internal sealed record SetDefaultReleaseHttpRequest(
        string? ReleaseId);

    internal sealed record AppServiceRefHttpRequest(
        string? TenantId,
        string? AppId,
        string? Namespace,
        string? ServiceId,
        string? RevisionId,
        string? ImplementationKind,
        string? Role);

    internal sealed record AppFunctionRefHttpRequest(
        string? FunctionId,
        string? ServiceId,
        string? EndpointId);

    internal sealed record AppConnectorRefHttpRequest(
        string? ResourceId,
        string? ConnectorName);

    internal sealed record AppSecretRefHttpRequest(
        string? ResourceId,
        string? SecretName);

    internal sealed record UpsertReleaseHttpRequest(
        string? DisplayName,
        string? Status,
        IReadOnlyList<AppServiceRefHttpRequest>? Services = null,
        IReadOnlyList<AppFunctionRefHttpRequest>? Functions = null,
        IReadOnlyList<AppConnectorRefHttpRequest>? Connectors = null,
        IReadOnlyList<AppSecretRefHttpRequest>? Secrets = null);

    internal sealed record ReplaceResourcesHttpRequest(
        IReadOnlyList<AppConnectorRefHttpRequest>? Connectors = null,
        IReadOnlyList<AppSecretRefHttpRequest>? Secrets = null);

    internal sealed record UpsertRouteHttpRequest(
        string? RoutePath,
        string? ReleaseId,
        string? FunctionId);

    internal sealed record FunctionInvokeHttpRequest(
        string? CommandId,
        string? CorrelationId,
        FunctionInvokeBinaryPayloadHttpRequest? BinaryPayload = null,
        FunctionInvokeTypedPayloadHttpRequest? TypedPayload = null,
        string? CallerServiceKey = null,
        string? CallerTenantId = null,
        string? CallerAppId = null,
        string? CallerScopeId = null,
        string? CallerSessionId = null);

    internal sealed record FunctionStreamHttpRequest(
        string? Prompt,
        string? SessionId = null,
        Dictionary<string, string>? Headers = null,
        string? EventFormat = null);

    internal sealed record FunctionRunResumeHttpRequest(
        string? ActorId,
        string? RunId,
        string? StepId,
        bool Approved,
        string? UserInput = null,
        string? CommandId = null,
        Dictionary<string, string>? Metadata = null);

    internal sealed record FunctionRunStopHttpRequest(
        string? ActorId,
        string? RunId,
        string? Reason = null,
        string? CommandId = null);
}
