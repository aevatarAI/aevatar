using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Hosting.Identity;
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
        group.MapGet("/{serviceId}/rollouts/commands/{commandId}", HandleGetRolloutCommandObservationAsync);
        group.MapGet("/{serviceId}/traffic", HandleGetTrafficViewAsync);
        return group;
    }

    private static async Task<IResult> HandleDeployRevisionAsync(
        HttpContext http,
        string serviceId,
        ActivateServiceRevisionHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        var receipt = await commandPort.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity,
            RevisionId = request.RevisionId ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/deployments", receipt);
    }

    private static async Task<IResult> HandleDeactivateDeploymentAsync(
        HttpContext http,
        string serviceId,
        string deploymentId,
        ServiceIdentityHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        var receipt = await commandPort.DeactivateServiceDeploymentAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity,
            DeploymentId = deploymentId ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/deployments/{deploymentId}", receipt);
    }

    private static async Task<IResult> HandleGetDeploymentsAsync(
        HttpContext http,
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceLifecycleQueryPort queryPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                query.TenantId,
                query.AppId,
                query.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        return JsonOrNull(await queryPort.GetServiceDeploymentsAsync(identity, ct));
    }

    private static async Task<IResult> HandleReplaceServingTargetsAsync(
        HttpContext http,
        string serviceId,
        ReplaceServiceServingTargetsHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        var receipt = await commandPort.ReplaceServiceServingTargetsAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity,
            Targets = { request.Targets.Select(ToServingTargetSpec) },
            RolloutId = request.RolloutId ?? string.Empty,
            Reason = request.Reason ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/serving", receipt);
    }

    private static async Task<IResult> HandleGetServingSetAsync(
        HttpContext http,
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceServingQueryPort queryPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                query.TenantId,
                query.AppId,
                query.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        return JsonOrNull(await queryPort.GetServiceServingSetAsync(identity, ct));
    }

    private static async Task<IResult> HandleStartRolloutAsync(
        HttpContext http,
        string serviceId,
        StartServiceRolloutHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        var receipt = await commandPort.StartServiceRolloutAsync(new StartServiceRolloutCommand
        {
            Identity = identity,
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
        HttpContext http,
        string serviceId,
        string rolloutId,
        ServiceIdentityHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        var receipt = await commandPort.AdvanceServiceRolloutAsync(new AdvanceServiceRolloutCommand
        {
            Identity = identity,
            RolloutId = rolloutId ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/rollouts/{rolloutId}", receipt);
    }

    private static async Task<IResult> HandlePauseRolloutAsync(
        HttpContext http,
        string serviceId,
        string rolloutId,
        RolloutActionHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        var receipt = await commandPort.PauseServiceRolloutAsync(new PauseServiceRolloutCommand
        {
            Identity = identity,
            RolloutId = rolloutId ?? string.Empty,
            Reason = request.Reason ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/rollouts/commands/{receipt.CommandId}", receipt);
    }

    private static async Task<IResult> HandleResumeRolloutAsync(
        HttpContext http,
        string serviceId,
        string rolloutId,
        ServiceIdentityHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        var receipt = await commandPort.ResumeServiceRolloutAsync(new ResumeServiceRolloutCommand
        {
            Identity = identity,
            RolloutId = rolloutId ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/rollouts/commands/{receipt.CommandId}", receipt);
    }

    private static async Task<IResult> HandleRollbackRolloutAsync(
        HttpContext http,
        string serviceId,
        string rolloutId,
        RolloutActionHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        var receipt = await commandPort.RollbackServiceRolloutAsync(new RollbackServiceRolloutCommand
        {
            Identity = identity,
            RolloutId = rolloutId ?? string.Empty,
            Reason = request.Reason ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/rollouts/commands/{receipt.CommandId}", receipt);
    }

    private static async Task<IResult> HandleGetRolloutAsync(
        HttpContext http,
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceServingQueryPort queryPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                query.TenantId,
                query.AppId,
                query.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        return JsonOrNull(await queryPort.GetServiceRolloutAsync(identity, ct));
    }

    private static async Task<IResult> HandleGetRolloutCommandObservationAsync(
        HttpContext http,
        string serviceId,
        string commandId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceServingQueryPort queryPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                query.TenantId,
                query.AppId,
                query.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        return JsonOrNull(await queryPort.GetServiceRolloutCommandObservationAsync(identity, commandId, ct));
    }

    private static async Task<IResult> HandleGetTrafficViewAsync(
        HttpContext http,
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceServingQueryPort queryPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                query.TenantId,
                query.AppId,
                query.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        return JsonOrNull(await queryPort.GetServiceTrafficViewAsync(identity, ct));
    }

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
