using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.DynamicRuntime.Hosting.CapabilityApi;

public static class DynamicRuntimeEndpoints
{
    public static IEndpointRouteBuilder MapDynamicRuntimeCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/script-runtime").WithTags("DynamicRuntime");

        group.MapPost("/images:build", BuildImage);
        group.MapPost("/images:publish", PublishImage);
        group.MapPost("/images/{imageName}/tags/{tag}:publish", PublishImageTag);
        group.MapGet("/images/{imageName}", GetImage);
        group.MapGet("/images/{imageName}/tags/{tag}", GetImageTag);
        group.MapGet("/images/{imageName}/digests/{digest}", GetImageDigest);

        group.MapPost("/compose:apply", ApplyCompose);
        group.MapPost("/compose/{stackId}:up", ComposeUp);
        group.MapPost("/compose/{stackId}:down", ComposeDown);
        group.MapPost("/compose/{stackId}/services/{serviceName}:scale", ScaleComposeService);
        group.MapPost("/compose/{stackId}/services/{serviceName}:rollout", RolloutComposeService);
        group.MapGet("/compose/{stackId}", GetStack);
        group.MapGet("/compose/{stackId}/services", GetComposeServices);
        group.MapGet("/compose/{stackId}/events", GetComposeEvents);

        group.MapPost("/services:register", RegisterService);
        group.MapPost("/services:update", UpdateService);
        group.MapPost("/services/{serviceId}:activate", ActivateService);
        group.MapPost("/services/{serviceId}:deactivate", DeactivateService);
        group.MapGet("/services/{serviceId}", GetServiceDefinition);

        group.MapPost("/containers:create", CreateContainer);
        group.MapPost("/containers/{containerId}:start", StartContainer);
        group.MapPost("/containers/{containerId}:stop", StopContainer);
        group.MapPost("/containers/{containerId}/exec", ExecuteContainer);
        group.MapDelete("/containers/{containerId}", DeleteContainer);
        group.MapGet("/containers/{containerId}", GetContainer);
        group.MapGet("/containers/{containerId}/runs", GetContainerRuns);

        group.MapPost("/runs/{runId}:cancel", CancelRun);
        group.MapGet("/runs/{runId}", GetRun);

        group.MapPost("/build-jobs:plan", SubmitBuildPlan);
        group.MapPost("/build-jobs/{buildJobId}:validate", ValidateBuild);
        group.MapPost("/build-jobs/{buildJobId}:approve", ApproveBuild);
        group.MapPost("/build-jobs/{buildJobId}:execute", ExecuteBuild);
        group.MapPost("/build-jobs/{buildJobId}:rollback", RollbackBuild);
        group.MapPost("/build-jobs:publish", PublishBuildResult);
        group.MapGet("/build-jobs/{buildJobId}", GetBuildJob);
        group.MapGet("/build-jobs", GetBuildJobs);

        return app;
    }

    private static Task<IResult> BuildImage(HttpContext http, BuildImageInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.BuildImageAsync(new BuildImageRequest(input.ImageName, input.SourceBundleDigest, input.Tag), context, ct));

    private static Task<IResult> PublishImage(HttpContext http, PublishImageInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.PublishImageAsync(new PublishImageRequest(input.ImageName, input.Tag, input.Digest), context, ct), requireIfMatch: true);

    private static Task<IResult> PublishImageTag(HttpContext http, string imageName, string tag, PublishImageTagInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.PublishImageAsync(new PublishImageRequest(imageName, tag, input.Digest), context, ct), requireIfMatch: true);

    private static Task<IResult> ApplyCompose(HttpContext http, ApplyComposeInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
    {
        var services = input.Services.Select(service => new ComposeServiceSpec(service.ServiceName, service.ImageRef, service.ReplicasDesired, service.ServiceMode)).ToList();
        var request = new ComposeApplyYamlRequest(input.StackId, input.ComposeSpecDigest, input.ComposeYaml, input.DesiredGeneration, services);
        return ExecuteCommand(http, context => commandService.ApplyComposeAsync(request, context, ct), requireIfMatch: true);
    }

    private static Task<IResult> ComposeUp(HttpContext http, string stackId, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.ComposeUpAsync(stackId, context, ct), requireIfMatch: true);

    private static Task<IResult> ComposeDown(HttpContext http, string stackId, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.ComposeDownAsync(stackId, context, ct), requireIfMatch: true);

    private static Task<IResult> ScaleComposeService(HttpContext http, string stackId, string serviceName, ScaleComposeServiceInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
    {
        var request = new ComposeServiceScaleRequest(stackId, serviceName, input.ReplicasDesired);
        return ExecuteCommand(http, context => commandService.ScaleComposeServiceAsync(request, context, ct), requireIfMatch: true);
    }

    private static Task<IResult> RolloutComposeService(HttpContext http, string stackId, string serviceName, RolloutComposeServiceInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
    {
        var request = new ComposeServiceRolloutRequest(stackId, serviceName, input.ImageRef);
        return ExecuteCommand(http, context => commandService.RolloutComposeServiceAsync(request, context, ct), requireIfMatch: true);
    }

    private static Task<IResult> RegisterService(HttpContext http, RegisterServiceInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
    {
        var request = new RegisterServiceDefinitionRequest(
            input.ServiceId,
            input.Version,
            input.ScriptCode,
            input.EntrypointType,
            input.ServiceMode,
            input.PublicEndpoints,
            input.EventSubscriptions,
            input.CapabilitiesHash);
        return ExecuteCommand(http, context => commandService.RegisterServiceAsync(request, context, ct));
    }

    private static Task<IResult> UpdateService(HttpContext http, UpdateServiceInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
    {
        var request = new UpdateServiceDefinitionRequest(
            input.ServiceId,
            input.Version,
            input.ScriptCode,
            input.EntrypointType,
            input.ServiceMode,
            input.PublicEndpoints,
            input.EventSubscriptions,
            input.CapabilitiesHash);
        return ExecuteCommand(http, context => commandService.UpdateServiceAsync(request, context, ct), requireIfMatch: true);
    }

    private static Task<IResult> ActivateService(HttpContext http, string serviceId, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.ActivateServiceAsync(serviceId, context, ct), requireIfMatch: true);

    private static Task<IResult> DeactivateService(HttpContext http, string serviceId, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.DeactivateServiceAsync(serviceId, context, ct), requireIfMatch: true);

    private static Task<IResult> CreateContainer(HttpContext http, CreateContainerInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
    {
        var request = new CreateContainerRequest(
            input.ContainerId,
            input.StackId,
            input.ServiceName,
            input.ServiceId,
            input.ImageDigest,
            input.RoleActorId);
        return ExecuteCommand(http, context => commandService.CreateContainerAsync(request, context, ct));
    }

    private static Task<IResult> StartContainer(HttpContext http, string containerId, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.StartContainerAsync(containerId, context, ct));

    private static Task<IResult> StopContainer(HttpContext http, string containerId, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.StopContainerAsync(containerId, context, ct));

    private static Task<IResult> ExecuteContainer(HttpContext http, string containerId, ExecuteContainerInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
    {
        var scriptInput = new ScriptRoleRequest(
            input.Input ?? string.Empty,
            input.InputJson,
            input.InputMetadata,
            input.CorrelationId,
            input.CausationId,
            input.MessageType);
        var request = new ExecuteContainerRequest(containerId, input.ServiceId, scriptInput, input.RunId);
        return ExecuteCommand(http, context => commandService.ExecuteContainerAsync(request, context, ct));
    }

    private static Task<IResult> DeleteContainer(HttpContext http, string containerId, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.DestroyContainerAsync(containerId, context, ct));

    private static Task<IResult> CancelRun(HttpContext http, string runId, CancelRunInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.CancelRunAsync(runId, input.Reason, context, ct));

    private static Task<IResult> SubmitBuildPlan(HttpContext http, BuildPlanInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.SubmitBuildPlanAsync(new SubmitBuildPlanRequest(input.BuildJobId, input.StackId, input.ServiceName, input.SourceBundleDigest, input.RequestedByAgentId), context, ct));

    private static Task<IResult> ValidateBuild(HttpContext http, string buildJobId, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.ValidateBuildAsync(buildJobId, context, ct));

    private static Task<IResult> ApproveBuild(HttpContext http, string buildJobId, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.ApproveBuildAsync(buildJobId, context, ct), requireIfMatch: true);

    private static Task<IResult> ExecuteBuild(HttpContext http, string buildJobId, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.ExecuteBuildAsync(buildJobId, context, ct), requireIfMatch: true);

    private static Task<IResult> RollbackBuild(HttpContext http, string buildJobId, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.RollbackBuildAsync(buildJobId, context, ct), requireIfMatch: true);

    private static Task<IResult> PublishBuildResult(HttpContext http, BuildPublishInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct) =>
        ExecuteCommand(http, context => commandService.PublishBuildResultAsync(new PublishBuildResultRequest(input.BuildJobId, input.ResultImageDigest), context, ct), requireIfMatch: true);

    private static async Task<IResult> GetImage(string imageName, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => (await queryService.GetImageAsync(imageName, ct)) is { } value ? Results.Ok(value) : Results.NotFound();

    private static async Task<IResult> GetImageTag(string imageName, string tag, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => (await queryService.GetImageTagAsync(imageName, tag, ct)) is { } value ? Results.Ok(value) : Results.NotFound();

    private static async Task<IResult> GetImageDigest(string imageName, string digest, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => (await queryService.GetImageDigestAsync(imageName, digest, ct)) is { } value ? Results.Ok(value) : Results.NotFound();

    private static async Task<IResult> GetStack(string stackId, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => (await queryService.GetStackAsync(stackId, ct)) is { } value ? Results.Ok(value) : Results.NotFound();

    private static async Task<IResult> GetComposeServices(string stackId, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => Results.Ok(await queryService.GetComposeServicesAsync(stackId, ct));

    private static async Task<IResult> GetComposeEvents(string stackId, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => Results.Ok(await queryService.GetComposeEventsAsync(stackId, ct));

    private static async Task<IResult> GetServiceDefinition(string serviceId, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => (await queryService.GetServiceDefinitionAsync(serviceId, ct)) is { } value ? Results.Ok(value) : Results.NotFound();

    private static async Task<IResult> GetContainer(string containerId, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => (await queryService.GetContainerAsync(containerId, ct)) is { } value ? Results.Ok(value) : Results.NotFound();

    private static async Task<IResult> GetContainerRuns(string containerId, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => Results.Ok(await queryService.GetContainerRunsAsync(containerId, ct));

    private static async Task<IResult> GetRun(string runId, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => (await queryService.GetRunAsync(runId, ct)) is { } value ? Results.Ok(value) : Results.NotFound();

    private static async Task<IResult> GetBuildJob(string buildJobId, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => (await queryService.GetBuildJobAsync(buildJobId, ct)) is { } value ? Results.Ok(value) : Results.NotFound();

    private static async Task<IResult> GetBuildJobs(IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => Results.Ok(await queryService.GetBuildJobsAsync(ct));

    private static async Task<IResult> ExecuteCommand(HttpContext http, Func<DynamicCommandContext, Task<DynamicCommandResult>> command, bool requireIfMatch = false)
    {
        if (!TryCreateContext(http, requireIfMatch, out var context, out var failure))
            return failure!;

        var result = await command(context);
        if (!string.IsNullOrWhiteSpace(result.ETag))
            http.Response.Headers.ETag = result.ETag;
        return Results.Ok(result);
    }

    private static bool TryCreateContext(HttpContext http, bool requireIfMatch, out DynamicCommandContext context, out IResult? failure)
    {
        var idempotency = http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotency))
        {
            context = new DynamicCommandContext(string.Empty);
            failure = Results.BadRequest(new { code = "IDEMPOTENCY_KEY_REQUIRED", message = "Idempotency-Key header is required." });
            return false;
        }

        var ifMatch = http.Request.Headers["If-Match"].ToString();
        if (requireIfMatch && string.IsNullOrWhiteSpace(ifMatch))
        {
            context = new DynamicCommandContext(idempotency);
            failure = Results.BadRequest(new { code = "VERSION_CONFLICT", message = "If-Match header is required." });
            return false;
        }

        context = new DynamicCommandContext(idempotency, string.IsNullOrWhiteSpace(ifMatch) ? null : ifMatch);
        failure = null;
        return true;
    }
}
