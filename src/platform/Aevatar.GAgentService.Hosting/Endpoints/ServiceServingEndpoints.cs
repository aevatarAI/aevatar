using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static partial class ServiceEndpoints
{
    public static RouteGroupBuilder MapGAgentServiceServingEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{serviceId}:deploy", HandleDeployRevisionAsync);
        group.MapPost("/{serviceId}/deployments/{deploymentId}:deactivate", HandleDeactivateDeploymentAsync);
        group.MapGet("/{serviceId}/deployments", HandleGetDeploymentsAsync);
        group.MapPost("/{serviceId}:serving-targets", HandleReplaceServingTargetsAsync);
        group.MapGet("/{serviceId}/serving", HandleGetServingSetAsync);
        group.MapPost("/{serviceId}/rollouts", HandleStartRolloutAsync);
        group.MapPost("/{serviceId}/rollouts/{rolloutId}:advance", HandleAdvanceRolloutAsync);
        group.MapPost("/{serviceId}/rollouts/{rolloutId}:pause", HandlePauseRolloutAsync);
        group.MapPost("/{serviceId}/rollouts/{rolloutId}:resume", HandleResumeRolloutAsync);
        group.MapPost("/{serviceId}/rollouts/{rolloutId}:rollback", HandleRollbackRolloutAsync);
        group.MapGet("/{serviceId}/rollouts", HandleGetRolloutAsync);
        group.MapGet("/{serviceId}/traffic", HandleGetTrafficViewAsync);
        return group;
    }

    private static async Task<IResult> HandleDeployRevisionAsync(
        string serviceId,
        ActivateServiceRevisionHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            RevisionId = request.RevisionId ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/deployments", receipt);
    }

    private static async Task<IResult> HandleDeactivateDeploymentAsync(
        string serviceId,
        string deploymentId,
        ServiceIdentityHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.DeactivateServiceDeploymentAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            DeploymentId = deploymentId ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/deployments/{deploymentId}", receipt);
    }

    private static Task<ServiceDeploymentCatalogSnapshot?> HandleGetDeploymentsAsync(
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceLifecycleQueryPort queryPort,
        CancellationToken ct) =>
        queryPort.GetServiceDeploymentsAsync(
            ToIdentity(query.TenantId, query.AppId, query.Namespace, serviceId),
            ct);

    private static async Task<IResult> HandleReplaceServingTargetsAsync(
        string serviceId,
        ReplaceServiceServingTargetsHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.ReplaceServiceServingTargetsAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            Targets = { request.Targets.Select(ToServingTargetSpec) },
            RolloutId = request.RolloutId ?? string.Empty,
            Reason = request.Reason ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/serving", receipt);
    }

    private static Task<ServiceServingSetSnapshot?> HandleGetServingSetAsync(
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceServingQueryPort queryPort,
        CancellationToken ct) =>
        queryPort.GetServiceServingSetAsync(
            ToIdentity(query.TenantId, query.AppId, query.Namespace, serviceId),
            ct);

    private static async Task<IResult> HandleStartRolloutAsync(
        string serviceId,
        StartServiceRolloutHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.StartServiceRolloutAsync(new StartServiceRolloutCommand
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            Plan = new ServiceRolloutPlanSpec
            {
                RolloutId = request.RolloutId ?? string.Empty,
                DisplayName = request.DisplayName ?? string.Empty,
                Stages = { request.Stages.Select(ToRolloutStageSpec) },
            },
            BaselineTargets = { (request.BaselineTargets ?? []).Select(ToServingTargetSpec) },
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/rollouts", receipt);
    }

    private static async Task<IResult> HandleAdvanceRolloutAsync(
        string serviceId,
        string rolloutId,
        ServiceIdentityHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.AdvanceServiceRolloutAsync(new AdvanceServiceRolloutCommand
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            RolloutId = rolloutId ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/rollouts/{rolloutId}", receipt);
    }

    private static async Task<IResult> HandlePauseRolloutAsync(
        string serviceId,
        string rolloutId,
        RolloutActionHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.PauseServiceRolloutAsync(new PauseServiceRolloutCommand
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            RolloutId = rolloutId ?? string.Empty,
            Reason = request.Reason ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/rollouts/{rolloutId}", receipt);
    }

    private static async Task<IResult> HandleResumeRolloutAsync(
        string serviceId,
        string rolloutId,
        ServiceIdentityHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.ResumeServiceRolloutAsync(new ResumeServiceRolloutCommand
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            RolloutId = rolloutId ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/rollouts/{rolloutId}", receipt);
    }

    private static async Task<IResult> HandleRollbackRolloutAsync(
        string serviceId,
        string rolloutId,
        RolloutActionHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.RollbackServiceRolloutAsync(new RollbackServiceRolloutCommand
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            RolloutId = rolloutId ?? string.Empty,
            Reason = request.Reason ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/rollouts/{rolloutId}", receipt);
    }

    private static Task<ServiceRolloutSnapshot?> HandleGetRolloutAsync(
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceServingQueryPort queryPort,
        CancellationToken ct) =>
        queryPort.GetServiceRolloutAsync(
            ToIdentity(query.TenantId, query.AppId, query.Namespace, serviceId),
            ct);

    private static Task<ServiceTrafficViewSnapshot?> HandleGetTrafficViewAsync(
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceServingQueryPort queryPort,
        CancellationToken ct) =>
        queryPort.GetServiceTrafficViewAsync(
            ToIdentity(query.TenantId, query.AppId, query.Namespace, serviceId),
            ct);

    private static ServiceServingTargetSpec ToServingTargetSpec(ServiceServingTargetHttpRequest request) =>
        new()
        {
            RevisionId = request.RevisionId ?? string.Empty,
            AllocationWeight = request.AllocationWeight,
            ServingState = ParseServingState(request.ServingState),
            EnabledEndpointIds = { request.EnabledEndpointIds ?? [] },
        };

    private static ServiceRolloutStageSpec ToRolloutStageSpec(ServiceRolloutStageHttpRequest request) =>
        new()
        {
            StageId = request.StageId ?? string.Empty,
            Targets = { request.Targets.Select(ToServingTargetSpec) },
        };

    private static ServiceServingState ParseServingState(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "paused" => ServiceServingState.Paused,
            "draining" => ServiceServingState.Draining,
            "disabled" => ServiceServingState.Disabled,
            _ => ServiceServingState.Active,
        };
    }

    public sealed record ServiceServingTargetHttpRequest(
        string RevisionId,
        int AllocationWeight,
        string? ServingState = null,
        IReadOnlyList<string>? EnabledEndpointIds = null);

    public sealed record ReplaceServiceServingTargetsHttpRequest(
        string TenantId,
        string AppId,
        string Namespace,
        IReadOnlyList<ServiceServingTargetHttpRequest> Targets,
        string? RolloutId = null,
        string? Reason = null);

    public sealed record ServiceRolloutStageHttpRequest(
        string StageId,
        IReadOnlyList<ServiceServingTargetHttpRequest> Targets);

    public sealed record StartServiceRolloutHttpRequest(
        string TenantId,
        string AppId,
        string Namespace,
        string RolloutId,
        string? DisplayName,
        IReadOnlyList<ServiceRolloutStageHttpRequest> Stages,
        IReadOnlyList<ServiceServingTargetHttpRequest>? BaselineTargets = null);

    public sealed record RolloutActionHttpRequest(
        string TenantId,
        string AppId,
        string Namespace,
        string? Reason = null);
}
