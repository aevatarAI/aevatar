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

        group.MapPost("/images:publish", PublishImage);
        group.MapPost("/compose:apply", ApplyCompose);
        group.MapPost("/containers:create", CreateContainer);
        group.MapPost("/runs:start", StartRun);
        group.MapPost("/runs:complete", CompleteRun);
        group.MapPost("/runs:fail", FailRun);
        group.MapPost("/build-jobs:plan", SubmitBuildPlan);
        group.MapPost("/build-jobs/{buildJobId}:validate", ValidateBuild);
        group.MapPost("/build-jobs/{buildJobId}:approve", ApproveBuild);
        group.MapPost("/build-jobs/{buildJobId}:execute", ExecuteBuild);
        group.MapPost("/build-jobs/{buildJobId}:rollback", RollbackBuild);
        group.MapPost("/build-jobs:publish", PublishBuildResult);

        group.MapGet("/images/{imageName}", GetImage);
        group.MapGet("/compose/{stackId}", GetStack);
        group.MapGet("/containers/{containerId}", GetContainer);
        group.MapGet("/runs/{runId}", GetRun);
        group.MapGet("/build-jobs/{buildJobId}", GetBuildJob);

        return app;
    }

    private static async Task<IResult> PublishImage(HttpContext http, PublishImageInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
        => WithETag(http, await commandService.PublishImageAsync(new PublishImageRequest(input.ImageName, input.Tag, input.Digest), CreateContext(http), ct));

    private static async Task<IResult> ApplyCompose(HttpContext http, ApplyComposeInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
    {
        var services = input.Services.Select(x => new ComposeServiceSpec(x.ServiceName, x.ReplicasDesired, x.ServiceMode)).ToList();
        var result = await commandService.ApplyComposeAsync(new ApplyComposeRequest(input.StackId, input.ComposeSpecDigest, input.DesiredGeneration, services), CreateContext(http), ct);
        return WithETag(http, result);
    }

    private static async Task<IResult> CreateContainer(HttpContext http, CreateContainerInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
        => WithETag(http, await commandService.CreateContainerAsync(new CreateContainerRequest(input.ContainerId, input.StackId, input.ServiceName, input.ImageDigest, input.RoleActorId), CreateContext(http), ct));

    private static async Task<IResult> StartRun(HttpContext http, StartRunInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
        => WithETag(http, await commandService.StartRunAsync(new StartRunRequest(input.RunId, input.ContainerId), CreateContext(http), ct));

    private static async Task<IResult> CompleteRun(HttpContext http, CompleteRunInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
        => WithETag(http, await commandService.CompleteRunAsync(new CompleteRunRequest(input.RunId, input.Result), CreateContext(http), ct));

    private static async Task<IResult> FailRun(HttpContext http, FailRunInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
        => WithETag(http, await commandService.FailRunAsync(new FailRunRequest(input.RunId, input.Error), CreateContext(http), ct));

    private static async Task<IResult> SubmitBuildPlan(HttpContext http, BuildPlanInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
        => WithETag(http, await commandService.SubmitBuildPlanAsync(new SubmitBuildPlanRequest(input.BuildJobId, input.StackId, input.ServiceName, input.SourceBundleDigest), CreateContext(http), ct));

    private static async Task<IResult> ValidateBuild(HttpContext http, string buildJobId, IDynamicRuntimeCommandService commandService, CancellationToken ct)
        => WithETag(http, await commandService.ValidateBuildAsync(buildJobId, CreateContext(http), ct));

    private static async Task<IResult> ApproveBuild(HttpContext http, string buildJobId, IDynamicRuntimeCommandService commandService, CancellationToken ct)
        => WithETag(http, await commandService.ApproveBuildAsync(buildJobId, CreateContext(http), ct));

    private static async Task<IResult> ExecuteBuild(HttpContext http, string buildJobId, IDynamicRuntimeCommandService commandService, CancellationToken ct)
        => WithETag(http, await commandService.ExecuteBuildAsync(buildJobId, CreateContext(http), ct));

    private static async Task<IResult> RollbackBuild(HttpContext http, string buildJobId, IDynamicRuntimeCommandService commandService, CancellationToken ct)
        => WithETag(http, await commandService.RollbackBuildAsync(buildJobId, CreateContext(http), ct));

    private static async Task<IResult> PublishBuildResult(HttpContext http, BuildPublishInput input, IDynamicRuntimeCommandService commandService, CancellationToken ct)
        => WithETag(http, await commandService.PublishBuildResultAsync(new PublishBuildResultRequest(input.BuildJobId, input.ResultImageDigest), CreateContext(http), ct));

    private static async Task<IResult> GetImage(string imageName, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => (await queryService.GetImageAsync(imageName, ct)) is { } value ? Results.Ok(value) : Results.NotFound();

    private static async Task<IResult> GetStack(string stackId, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => (await queryService.GetStackAsync(stackId, ct)) is { } value ? Results.Ok(value) : Results.NotFound();

    private static async Task<IResult> GetContainer(string containerId, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => (await queryService.GetContainerAsync(containerId, ct)) is { } value ? Results.Ok(value) : Results.NotFound();

    private static async Task<IResult> GetRun(string runId, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => (await queryService.GetRunAsync(runId, ct)) is { } value ? Results.Ok(value) : Results.NotFound();

    private static async Task<IResult> GetBuildJob(string buildJobId, IDynamicRuntimeQueryService queryService, CancellationToken ct)
        => (await queryService.GetBuildJobAsync(buildJobId, ct)) is { } value ? Results.Ok(value) : Results.NotFound();

    private static DynamicCommandContext CreateContext(HttpContext http)
    {
        var idempotency = http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotency))
            idempotency = Guid.NewGuid().ToString("N");

        var ifMatch = http.Request.Headers["If-Match"].ToString();
        if (string.IsNullOrWhiteSpace(ifMatch))
            ifMatch = null;

        return new DynamicCommandContext(idempotency, ifMatch);
    }

    private static IResult WithETag(HttpContext http, DynamicCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ETag))
            http.Response.Headers.ETag = result.ETag;

        return Results.Ok(result);
    }
}
