using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Access;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.AppPlatform.Application.Services;
using Aevatar.Authentication.Abstractions;
using Aevatar.AppPlatform.Hosting.OpenApi;
using Aevatar.AppPlatform.Hosting.Serialization;
using Aevatar.Presentation.AGUI;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace Aevatar.AppPlatform.Hosting.Endpoints;

public static class AppPlatformEndpoints
{
    public static IEndpointRouteBuilder MapAppPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var aiGroup = app.MapGroup("/api/ai");
        aiGroup.MapGet("/openapi", HandleGetAiOpenApiAsync);

        var operationsGroup = app.MapGroup("/api/operations");
        operationsGroup.MapGet("/{operationId}", HandleGetOperationAsync);
        operationsGroup.MapGet("/{operationId}/result", HandleGetOperationResultAsync);
        operationsGroup.MapGet("/{operationId}/events", HandleListOperationEventsAsync);
        operationsGroup.MapGet("/{operationId}:stream", HandleStreamOperationAsync);

        var group = app.MapGroup("/api/apps");
        group.MapPost(string.Empty, HandleCreateAppAsync);
        group.MapGet(string.Empty, HandleListAppsAsync);
        group.MapGet("/resolve", HandleResolveRouteAsync);
        group.MapGet("/{appId}", HandleGetAppAsync);
        group.MapPut("/{appId}", HandleUpsertAppAsync);
        group.MapPost("/{appId}:default-release", HandleSetDefaultReleaseAsync);
        group.MapGet("/{appId}/functions", HandleListFunctionsAsync);
        group.MapGet("/{appId}/functions/{functionId}", HandleGetFunctionAsync);
        group.MapPost("/{appId}/functions/{functionId}:invoke", HandleInvokeFunctionAsync);
        group.MapPost("/{appId}/functions/{functionId}:stream", HandleStreamFunctionAsync);
        group.MapPost("/{appId}/functions/{functionId}/runs:resume", HandleResumeFunctionRunAsync);
        group.MapPost("/{appId}/functions/{functionId}/runs:stop", HandleStopFunctionRunAsync);
        group.MapGet("/{appId}/releases", HandleListReleasesAsync);
        group.MapGet("/{appId}/releases/{releaseId}", HandleGetReleaseAsync);
        group.MapPut("/{appId}/releases/{releaseId}", HandleUpsertReleaseAsync);
        group.MapPost("/{appId}/releases/{releaseId}:publish", HandlePublishReleaseAsync);
        group.MapPost("/{appId}/releases/{releaseId}:archive", HandleArchiveReleaseAsync);
        group.MapGet("/{appId}/releases/{releaseId}/functions", HandleListReleaseFunctionsAsync);
        group.MapGet("/{appId}/releases/{releaseId}/functions/{functionId}", HandleGetReleaseFunctionAsync);
        group.MapPut("/{appId}/releases/{releaseId}/functions/{functionId}", HandleUpsertReleaseFunctionAsync);
        group.MapDelete("/{appId}/releases/{releaseId}/functions/{functionId}", HandleDeleteReleaseFunctionAsync);
        group.MapPost("/{appId}/releases/{releaseId}/functions/{functionId}:invoke", HandleInvokeReleaseFunctionAsync);
        group.MapPost("/{appId}/releases/{releaseId}/functions/{functionId}:stream", HandleStreamReleaseFunctionAsync);
        group.MapPost("/{appId}/releases/{releaseId}/functions/{functionId}/runs:resume", HandleResumeReleaseFunctionRunAsync);
        group.MapPost("/{appId}/releases/{releaseId}/functions/{functionId}/runs:stop", HandleStopReleaseFunctionRunAsync);
        group.MapGet("/{appId}/releases/{releaseId}/resources", HandleGetReleaseResourcesAsync);
        group.MapPut("/{appId}/releases/{releaseId}/resources", HandleReplaceReleaseResourcesAsync);
        group.MapGet("/{appId}/routes", HandleListRoutesAsync);
        group.MapPut("/{appId}/routes", HandleUpsertRouteAsync);
        group.MapDelete("/{appId}/routes", HandleDeleteRouteAsync);
        return app;
    }

    private static async Task<IResult> HandleCreateAppAsync(
        HttpContext http,
        AppPlatformEndpointModels.CreateAppHttpRequest request,
        [FromServices] IAppControlCommandPort commandPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        AppDefinitionSnapshot definition;
        try
        {
            definition = ToCreateAppDefinition(request);
        }
        catch (InvalidOperationException ex)
        {
            return ToBadRequestResult("APP_MUTATION_INVALID", ex.Message);
        }

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Write, definition);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        try
        {
            var created = await commandPort.CreateAppAsync(definition, ct);
            return Results.Created($"/api/apps/{created.AppId}", created);
        }
        catch (InvalidOperationException ex)
        {
            return ToBadRequestResult("APP_MUTATION_INVALID", ex.Message);
        }
    }

    private static Task<IReadOnlyList<AppDefinitionSnapshot>> HandleListAppsAsync(
        HttpContext http,
        [AsParameters] AppPlatformEndpointModels.AppListQuery query,
        [FromServices] IAppDefinitionQueryPort queryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleListAppsAsyncCore(http, query, queryPort, authorizer, ct);

    private static async Task<IResult> HandleResolveRouteAsync(
        HttpContext http,
        [AsParameters] AppPlatformEndpointModels.ResolveRouteQuery query,
        [FromServices] IAppRouteQueryPort queryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.RoutePath))
            return ToBadRequestResult("APP_ROUTE_INVALID", "routePath is required.");

        var resolution = await queryPort.ResolveAsync(query.RoutePath, ct);
        if (resolution == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Read, resolution.App);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        return decision.Allowed
            ? Results.Ok(resolution)
            : ToDeniedResult(http.User, decision, accessRequest.Action);
    }

    private static async Task<IResult> HandleGetAppAsync(
        HttpContext http,
        string appId,
        [FromServices] IAppDefinitionQueryPort queryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var snapshot = await queryPort.GetAsync(appId, ct);
        if (snapshot == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Read, snapshot);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        return decision.Allowed
            ? Results.Ok(snapshot)
            : ToDeniedResult(http.User, decision, accessRequest.Action);
    }

    private static async Task<IResult> HandleUpsertAppAsync(
        HttpContext http,
        string appId,
        AppPlatformEndpointModels.UpsertAppHttpRequest request,
        [FromServices] IAppControlCommandPort commandPort,
        [FromServices] IAppDefinitionQueryPort queryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var existing = await queryPort.GetAsync(appId, ct);
        AppDefinitionSnapshot definition;
        try
        {
            definition = ToUpsertAppDefinition(appId, request, existing);
        }
        catch (InvalidOperationException ex)
        {
            return ToBadRequestResult("APP_MUTATION_INVALID", ex.Message);
        }

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Write, existing ?? definition);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        try
        {
            var snapshot = await commandPort.UpsertAppAsync(definition, ct);
            return Results.Ok(snapshot);
        }
        catch (InvalidOperationException ex)
        {
            return ToBadRequestResult("APP_MUTATION_INVALID", ex.Message);
        }
    }

    private static async Task<IResult> HandleSetDefaultReleaseAsync(
        HttpContext http,
        string appId,
        AppPlatformEndpointModels.SetDefaultReleaseHttpRequest request,
        [FromServices] IAppControlCommandPort commandPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Publish, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        try
        {
            var snapshot = await commandPort.SetDefaultReleaseAsync(appId, request.ReleaseId ?? string.Empty, ct);
            return Results.Ok(snapshot);
        }
        catch (InvalidOperationException ex)
        {
            return ToBadRequestResult("APP_MUTATION_INVALID", ex.Message);
        }
    }

    private static Task<IResult> HandleListReleasesAsync(
        HttpContext http,
        string appId,
        [FromServices] IAppReleaseQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleListReleasesAsyncCore(http, appId, queryPort, appQueryPort, authorizer, ct);

    private static async Task<IResult> HandleGetReleaseAsync(
        HttpContext http,
        string appId,
        string releaseId,
        [FromServices] IAppReleaseQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Read, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        var snapshot = await queryPort.GetAsync(appId, releaseId, ct);
        return snapshot == null ? Results.NotFound() : Results.Ok(snapshot);
    }

    private static async Task<IResult> HandleUpsertReleaseAsync(
        HttpContext http,
        string appId,
        string releaseId,
        AppPlatformEndpointModels.UpsertReleaseHttpRequest request,
        [FromServices] IAppControlCommandPort commandPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Write, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        try
        {
            var snapshot = await commandPort.UpsertReleaseAsync(ToReleaseSnapshot(appId, releaseId, request), ct);
            return Results.Ok(snapshot);
        }
        catch (InvalidOperationException ex)
        {
            return ToBadRequestResult("APP_MUTATION_INVALID", ex.Message);
        }
    }

    private static async Task<IResult> HandlePublishReleaseAsync(
        HttpContext http,
        string appId,
        string releaseId,
        [FromServices] IAppControlCommandPort commandPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        await HandleReleaseStatusMutationAsync(http, appId, releaseId, AppAccessActions.Publish, commandPort.PublishReleaseAsync, appQueryPort, authorizer, ct);

    private static async Task<IResult> HandleArchiveReleaseAsync(
        HttpContext http,
        string appId,
        string releaseId,
        [FromServices] IAppControlCommandPort commandPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        await HandleReleaseStatusMutationAsync(http, appId, releaseId, AppAccessActions.Publish, commandPort.ArchiveReleaseAsync, appQueryPort, authorizer, ct);

    private static Task<IResult> HandleListFunctionsAsync(
        HttpContext http,
        string appId,
        [FromServices] IAppFunctionQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleListFunctionsAsyncCore(http, appId, releaseId: null, queryPort, appQueryPort, authorizer, ct);

    private static Task<IResult> HandleListReleaseFunctionsAsync(
        HttpContext http,
        string appId,
        string releaseId,
        [FromServices] IAppFunctionQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleListFunctionsAsyncCore(http, appId, releaseId, queryPort, appQueryPort, authorizer, ct);

    private static Task<IResult> HandleGetFunctionAsync(
        HttpContext http,
        string appId,
        string functionId,
        [FromServices] IAppFunctionQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleGetFunctionAsyncCore(http, appId, releaseId: null, functionId, queryPort, appQueryPort, authorizer, ct);

    private static Task<IResult> HandleGetReleaseFunctionAsync(
        HttpContext http,
        string appId,
        string releaseId,
        string functionId,
        [FromServices] IAppFunctionQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleGetFunctionAsyncCore(http, appId, releaseId, functionId, queryPort, appQueryPort, authorizer, ct);

    private static async Task<IResult> HandleUpsertReleaseFunctionAsync(
        HttpContext http,
        string appId,
        string releaseId,
        string functionId,
        AppPlatformEndpointModels.AppFunctionRefHttpRequest request,
        [FromServices] IAppControlCommandPort commandPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Write, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        try
        {
            var snapshot = await commandPort.UpsertFunctionAsync(
                appId,
                releaseId,
                new AppEntryRef
                {
                    EntryId = functionId,
                    ServiceId = request.ServiceId ?? string.Empty,
                    EndpointId = request.EndpointId ?? string.Empty,
                },
                ct);
            return Results.Ok(snapshot);
        }
        catch (InvalidOperationException ex)
        {
            return ToBadRequestResult("APP_MUTATION_INVALID", ex.Message);
        }
    }

    private static async Task<IResult> HandleDeleteReleaseFunctionAsync(
        HttpContext http,
        string appId,
        string releaseId,
        string functionId,
        [FromServices] IAppControlCommandPort commandPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Write, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        try
        {
            var deleted = await commandPort.DeleteFunctionAsync(appId, releaseId, functionId, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return ToBadRequestResult("APP_MUTATION_INVALID", ex.Message);
        }
    }

    private static Task<IResult> HandleInvokeFunctionAsync(
        HttpContext http,
        string appId,
        string functionId,
        AppPlatformEndpointModels.FunctionInvokeHttpRequest request,
        [FromServices] IAppFunctionQueryPort functionQueryPort,
        [FromServices] IAppFunctionInvocationPort invocationPort,
        [FromServices] IAppFunctionInvokeRequestSerializer requestSerializer,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleInvokeFunctionAsyncCore(http, appId, releaseId: null, functionId, request, functionQueryPort, invocationPort, requestSerializer, appQueryPort, authorizer, ct);

    private static Task<IResult> HandleInvokeReleaseFunctionAsync(
        HttpContext http,
        string appId,
        string releaseId,
        string functionId,
        AppPlatformEndpointModels.FunctionInvokeHttpRequest request,
        [FromServices] IAppFunctionQueryPort functionQueryPort,
        [FromServices] IAppFunctionInvocationPort invocationPort,
        [FromServices] IAppFunctionInvokeRequestSerializer requestSerializer,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleInvokeFunctionAsyncCore(http, appId, releaseId, functionId, request, functionQueryPort, invocationPort, requestSerializer, appQueryPort, authorizer, ct);

    private static Task HandleStreamFunctionAsync(
        HttpContext http,
        string appId,
        string functionId,
        AppPlatformEndpointModels.FunctionStreamHttpRequest request,
        [FromServices] IAppFunctionExecutionTargetQueryPort targetQueryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleStreamFunctionAsyncCore(http, appId, releaseId: null, functionId, request, targetQueryPort, appQueryPort, authorizer, ct);

    private static Task HandleStreamReleaseFunctionAsync(
        HttpContext http,
        string appId,
        string releaseId,
        string functionId,
        AppPlatformEndpointModels.FunctionStreamHttpRequest request,
        [FromServices] IAppFunctionExecutionTargetQueryPort targetQueryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleStreamFunctionAsyncCore(http, appId, releaseId, functionId, request, targetQueryPort, appQueryPort, authorizer, ct);

    private static Task<IResult> HandleResumeFunctionRunAsync(
        HttpContext http,
        string appId,
        string functionId,
        AppPlatformEndpointModels.FunctionRunResumeHttpRequest request,
        [FromServices] IAppFunctionExecutionTargetQueryPort targetQueryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        [FromServices] IWorkflowActorBindingReader bindingReader,
        [FromServices] ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> resumeService,
        CancellationToken ct) =>
        HandleResumeFunctionRunAsyncCore(http, appId, releaseId: null, functionId, request, targetQueryPort, appQueryPort, authorizer, bindingReader, resumeService, ct);

    private static Task<IResult> HandleResumeReleaseFunctionRunAsync(
        HttpContext http,
        string appId,
        string releaseId,
        string functionId,
        AppPlatformEndpointModels.FunctionRunResumeHttpRequest request,
        [FromServices] IAppFunctionExecutionTargetQueryPort targetQueryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        [FromServices] IWorkflowActorBindingReader bindingReader,
        [FromServices] ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> resumeService,
        CancellationToken ct) =>
        HandleResumeFunctionRunAsyncCore(http, appId, releaseId, functionId, request, targetQueryPort, appQueryPort, authorizer, bindingReader, resumeService, ct);

    private static Task<IResult> HandleStopFunctionRunAsync(
        HttpContext http,
        string appId,
        string functionId,
        AppPlatformEndpointModels.FunctionRunStopHttpRequest request,
        [FromServices] IAppFunctionExecutionTargetQueryPort targetQueryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        [FromServices] IWorkflowActorBindingReader bindingReader,
        [FromServices] ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        CancellationToken ct) =>
        HandleStopFunctionRunAsyncCore(http, appId, releaseId: null, functionId, request, targetQueryPort, appQueryPort, authorizer, bindingReader, stopService, ct);

    private static Task<IResult> HandleStopReleaseFunctionRunAsync(
        HttpContext http,
        string appId,
        string releaseId,
        string functionId,
        AppPlatformEndpointModels.FunctionRunStopHttpRequest request,
        [FromServices] IAppFunctionExecutionTargetQueryPort targetQueryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        [FromServices] IWorkflowActorBindingReader bindingReader,
        [FromServices] ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        CancellationToken ct) =>
        HandleStopFunctionRunAsyncCore(http, appId, releaseId, functionId, request, targetQueryPort, appQueryPort, authorizer, bindingReader, stopService, ct);

    private static async Task<IResult> HandleGetReleaseResourcesAsync(
        HttpContext http,
        string appId,
        string releaseId,
        [FromServices] IAppResourceQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Read, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        var snapshot = await queryPort.GetReleaseResourcesAsync(appId, releaseId, ct);
        return snapshot == null ? Results.NotFound() : Results.Ok(snapshot);
    }

    private static async Task<IResult> HandleReplaceReleaseResourcesAsync(
        HttpContext http,
        string appId,
        string releaseId,
        AppPlatformEndpointModels.ReplaceResourcesHttpRequest request,
        [FromServices] IAppControlCommandPort commandPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.ManageResources, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        try
        {
            var snapshot = await commandPort.ReplaceReleaseResourcesAsync(ToResourcesSnapshot(appId, releaseId, request), ct);
            return Results.Ok(snapshot);
        }
        catch (InvalidOperationException ex)
        {
            return ToBadRequestResult("APP_MUTATION_INVALID", ex.Message);
        }
    }

    private static Task<IResult> HandleListRoutesAsync(
        HttpContext http,
        string appId,
        [FromServices] IAppRouteQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct) =>
        HandleListRoutesAsyncCore(http, appId, queryPort, appQueryPort, authorizer, ct);

    private static async Task<IResult> HandleUpsertRouteAsync(
        HttpContext http,
        string appId,
        AppPlatformEndpointModels.UpsertRouteHttpRequest request,
        [FromServices] IAppControlCommandPort commandPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Write, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        try
        {
            var snapshot = await commandPort.UpsertRouteAsync(
                new AppRouteSnapshot
                {
                    AppId = appId,
                    RoutePath = request.RoutePath ?? string.Empty,
                    ReleaseId = request.ReleaseId ?? string.Empty,
                    EntryId = request.FunctionId ?? string.Empty,
                },
                ct);
            return Results.Ok(snapshot);
        }
        catch (InvalidOperationException ex)
        {
            return ToBadRequestResult("APP_MUTATION_INVALID", ex.Message);
        }
    }

    private static async Task<IResult> HandleDeleteRouteAsync(
        HttpContext http,
        string appId,
        [AsParameters] AppPlatformEndpointModels.RouteDeleteQuery query,
        [FromServices] IAppControlCommandPort commandPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Write, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        try
        {
            var deleted = await commandPort.DeleteRouteAsync(appId, query.RoutePath ?? string.Empty, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return ToBadRequestResult("APP_MUTATION_INVALID", ex.Message);
        }
    }

    private static async Task<IResult> HandleGetOperationAsync(
        HttpContext http,
        string operationId,
        [FromServices] IOperationQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var snapshot = await queryPort.GetAsync(operationId, ct);
        if (snapshot == null)
            return Results.NotFound();

        var app = await appQueryPort.GetAsync(snapshot.AppId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Observe, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        return decision.Allowed
            ? Results.Ok(snapshot)
            : ToDeniedResult(http.User, decision, accessRequest.Action);
    }

    private static async Task<IResult> HandleListOperationEventsAsync(
        HttpContext http,
        string operationId,
        [FromServices] IOperationQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var snapshot = await queryPort.GetAsync(operationId, ct);
        if (snapshot == null)
            return Results.NotFound();

        var app = await appQueryPort.GetAsync(snapshot.AppId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Observe, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        var events = await queryPort.ListEventsAsync(operationId, ct);
        return Results.Ok(events);
    }

    private static async Task<IResult> HandleGetOperationResultAsync(
        HttpContext http,
        string operationId,
        [FromServices] IOperationQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var snapshot = await queryPort.GetAsync(operationId, ct);
        if (snapshot == null)
            return Results.NotFound();

        var app = await appQueryPort.GetAsync(snapshot.AppId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Observe, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        var result = await queryPort.GetResultAsync(operationId, ct);
        if (result == null)
        {
            return Results.Json(new
            {
                code = "OPERATION_RESULT_NOT_READY",
                message = $"Operation '{operationId}' does not have a terminal result yet.",
            }, statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(result);
    }

    private static async Task HandleStreamOperationAsync(
        HttpContext http,
        string operationId,
        [AsParameters] AppPlatformEndpointModels.OperationStreamQuery query,
        [FromServices] IOperationQueryPort queryPort,
        [FromServices] IAppDefinitionQueryPort appQueryPort,
        [FromServices] IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var snapshot = await queryPort.GetAsync(operationId, ct);
        if (snapshot == null)
        {
            await Results.NotFound().ExecuteAsync(http);
            return;
        }

        var app = await appQueryPort.GetAsync(snapshot.AppId, ct);
        if (app == null)
        {
            await Results.NotFound().ExecuteAsync(http);
            return;
        }

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Observe, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
        {
            await ToDeniedResult(http.User, decision, accessRequest.Action).ExecuteAsync(http);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(ct);

        var writer = new OperationSseWriter(http.Response);
        await writer.WriteAsync("operation.snapshot", snapshot, ct);

        if (IsTerminalOperationStatus(snapshot.Status))
        {
            var existingResult = await queryPort.GetResultAsync(operationId, ct);
            if (existingResult != null)
                await writer.WriteAsync("operation.result", existingResult, ct);
            return;
        }

        await foreach (var operationEvent in queryPort.WatchAsync(operationId, query.AfterSequence ?? 0, ct))
        {
            await writer.WriteAsync("operation.event", operationEvent, ct);
            if (!IsTerminalOperationStatus(operationEvent.Status))
                continue;

            var result = await queryPort.GetResultAsync(operationId, ct);
            if (result != null)
                await writer.WriteAsync("operation.result", result, ct);
            break;
        }
    }

    private static IResult HandleGetAiOpenApiAsync(
        HttpContext http,
        [FromServices] IAppOpenApiDocumentPort openApiDocumentPort)
    {
        var serverUrl = $"{http.Request.Scheme}://{http.Request.Host.Value}".TrimEnd('/');
        var document = openApiDocumentPort.BuildDocument(serverUrl);
        return Results.Json(document, contentType: "application/json");
    }

    private static async Task<IReadOnlyList<AppDefinitionSnapshot>> HandleListAppsAsyncCore(
        HttpContext http,
        AppPlatformEndpointModels.AppListQuery query,
        IAppDefinitionQueryPort queryPort,
        IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var subjectScopeId = ResolveSubjectScopeId(http.User);
        var apps = await queryPort.ListAsync(query.OwnerScopeId, ct);

        var results = new List<AppDefinitionSnapshot>(apps.Count);
        foreach (var app in apps)
        {
            var decision = await authorizer.AuthorizeAsync(
                new AppAccessRequest(
                    subjectScopeId,
                    AppAccessActions.Read,
                    AppDefinitionQueryApplicationService.BuildAccessResource(app)),
                ct);
            if (decision.Allowed)
                results.Add(app);
        }

        return results;
    }

    private static async Task<IResult> HandleListRoutesAsyncCore(
        HttpContext http,
        string appId,
        IAppRouteQueryPort queryPort,
        IAppDefinitionQueryPort appQueryPort,
        IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Read, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        var routes = await queryPort.ListAsync(appId, ct);
        return Results.Ok(routes);
    }

    private static async Task<IResult> HandleListReleasesAsyncCore(
        HttpContext http,
        string appId,
        IAppReleaseQueryPort queryPort,
        IAppDefinitionQueryPort appQueryPort,
        IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Read, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        var releases = await queryPort.ListAsync(appId, ct);
        return Results.Ok(releases);
    }

    private static async Task<IResult> HandleListFunctionsAsyncCore(
        HttpContext http,
        string appId,
        string? releaseId,
        IAppFunctionQueryPort queryPort,
        IAppDefinitionQueryPort appQueryPort,
        IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Read, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        var functions = await queryPort.ListAsync(appId, releaseId, ct);
        return Results.Ok(functions);
    }

    private static async Task<IResult> HandleGetFunctionAsyncCore(
        HttpContext http,
        string appId,
        string? releaseId,
        string functionId,
        IAppFunctionQueryPort queryPort,
        IAppDefinitionQueryPort appQueryPort,
        IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Read, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        var descriptor = await queryPort.GetAsync(appId, functionId, releaseId, ct);
        return descriptor == null ? Results.NotFound() : Results.Ok(descriptor);
    }

    private static async Task<IResult> HandleInvokeFunctionAsyncCore(
        HttpContext http,
        string appId,
        string? releaseId,
        string functionId,
        AppPlatformEndpointModels.FunctionInvokeHttpRequest request,
        IAppFunctionQueryPort functionQueryPort,
        IAppFunctionInvocationPort invocationPort,
        IAppFunctionInvokeRequestSerializer requestSerializer,
        IAppDefinitionQueryPort appQueryPort,
        IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Invoke, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        var function = await functionQueryPort.GetAsync(appId, functionId, releaseId, ct);
        if (function == null)
            return Results.NotFound();

        AppFunctionInvokeAcceptedReceipt receipt;
        try
        {
            receipt = await invocationPort.InvokeAsync(appId, functionId, requestSerializer.Deserialize(request), releaseId, ct);
        }
        catch (InvalidOperationException ex)
        {
            return ToBadRequestResult("FUNCTION_INVOKE_INVALID", ex.Message);
        }

        return Results.Accepted(receipt.StatusUrl, receipt);
    }

    private static async Task HandleStreamFunctionAsyncCore(
        HttpContext http,
        string appId,
        string? releaseId,
        string functionId,
        AppPlatformEndpointModels.FunctionStreamHttpRequest request,
        IAppFunctionExecutionTargetQueryPort targetQueryPort,
        IAppDefinitionQueryPort appQueryPort,
        IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
        {
            await Results.NotFound().ExecuteAsync(http);
            return;
        }

        var accessRequest = BuildAccessRequest(http.User, AppAccessActions.Invoke, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
        {
            await ToDeniedResult(http.User, decision, accessRequest.Action).ExecuteAsync(http);
            return;
        }

        var target = await targetQueryPort.ResolveAsync(appId, functionId, releaseId, ct);
        if (target == null)
        {
            await Results.NotFound().ExecuteAsync(http);
            return;
        }

        if (target.ServiceRef.ImplementationKind != AppImplementationKind.Workflow)
        {
            await ToBadRequestResult(
                "FUNCTION_STREAM_UNSUPPORTED",
                "Only workflow-backed functions support event streaming.")
                .ExecuteAsync(http);
            return;
        }

        if (!TryParseFunctionStreamEventFormat(request.EventFormat, out var eventFormat))
        {
            await ToBadRequestResult(
                "FUNCTION_STREAM_INVALID",
                "eventFormat must be either 'workflow' or 'agui'.")
                .ExecuteAsync(http);
            return;
        }

        if (string.IsNullOrWhiteSpace(target.PrimaryActorId))
        {
            await Results.Json(
                new
                {
                    code = "FUNCTION_TARGET_NOT_READY",
                    message = "Function target is not activated.",
                },
                statusCode: StatusCodes.Status409Conflict)
                .ExecuteAsync(http);
            return;
        }

        var chatRunService = http.RequestServices.GetService<ICommandInteractionService<
            WorkflowChatRunRequest,
            WorkflowChatRunAcceptedReceipt,
            WorkflowChatRunStartError,
            WorkflowRunEventEnvelope,
            WorkflowProjectionCompletionStatus>>();
        if (chatRunService == null)
        {
            await Results.Json(
                new
                {
                    code = "FUNCTION_STREAM_UNAVAILABLE",
                    message = "Workflow streaming runtime is not available.",
                },
                statusCode: StatusCodes.Status501NotImplemented)
                .ExecuteAsync(http);
            return;
        }

        if (eventFormat == FunctionStreamEventFormat.Workflow)
        {
            await WorkflowCapabilityEndpoints.HandleChat(
                http,
                new ChatInput
                {
                    Prompt = string.IsNullOrWhiteSpace(request.Prompt) ? string.Empty : request.Prompt.Trim(),
                    AgentId = target.PrimaryActorId,
                    SessionId = string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId.Trim(),
                    ScopeId = NormalizeRequired(target.App.OwnerScopeId, nameof(target.App.OwnerScopeId)),
                    Metadata = BuildFunctionScopedHeaders(request.Headers),
                },
                chatRunService,
                ct);
            return;
        }

        await HandleAguiFunctionStreamAsync(
            http,
            target,
            request.Prompt,
            request.SessionId,
            BuildFunctionScopedHeaders(request.Headers),
            chatRunService,
            ct);
    }

    private static async Task HandleAguiFunctionStreamAsync(
        HttpContext http,
        AppFunctionExecutionTarget target,
        string? prompt,
        string? sessionId,
        IReadOnlyDictionary<string, string>? headers,
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct)
    {
        prompt = string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt.Trim();
        var normalizedSessionId = NormalizeOptional(sessionId);
        var normalizedScopeId = NormalizeRequired(target.App.OwnerScopeId, nameof(target.App.OwnerScopeId));
        var started = false;

        async Task StartAsync(CancellationToken token)
        {
            if (started)
                return;

            started = true;
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.StartAsync(token);
        }

        await using var writer = new AGUISseWriter(http.Response, AppFunctionAguiEventMapper.TypeRegistry);

        try
        {
            var result = await chatRunService.ExecuteAsync(
                new WorkflowChatRunRequest(
                    prompt,
                    WorkflowName: null,
                    ActorId: target.PrimaryActorId,
                    SessionId: normalizedSessionId,
                    WorkflowYamls: null,
                    Metadata: headers,
                    ScopeId: normalizedScopeId),
                async (frame, token) =>
                {
                    if (!AppFunctionAguiEventMapper.TryMap(frame, out var aguiEvent) || aguiEvent == null)
                        return;

                    await StartAsync(token);
                    await writer.WriteAsync(aguiEvent, token);
                },
                async (receipt, token) =>
                {
                    if (!string.IsNullOrWhiteSpace(receipt.CorrelationId))
                        http.Response.Headers["X-Correlation-Id"] = receipt.CorrelationId;

                    await StartAsync(token);
                    await writer.WriteAsync(AppFunctionAguiEventMapper.BuildRunContextEvent(receipt), token);
                },
                ct);

            if (!result.Succeeded && !started)
            {
                var (statusCode, code, message) = MapFunctionStreamStartError(result.Error);
                await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!started)
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    StatusCodes.Status500InternalServerError,
                    "EXECUTION_FAILED",
                    "Workflow execution failed.",
                    CancellationToken.None);
                return;
            }

            await writer.WriteAsync(AppFunctionAguiEventMapper.BuildRunErrorEvent(ex), CancellationToken.None);
        }
    }

    private static async Task<IResult> HandleResumeFunctionRunAsyncCore(
        HttpContext http,
        string appId,
        string? releaseId,
        string functionId,
        AppPlatformEndpointModels.FunctionRunResumeHttpRequest request,
        IAppFunctionExecutionTargetQueryPort targetQueryPort,
        IAppDefinitionQueryPort appQueryPort,
        IAppAccessAuthorizer authorizer,
        IWorkflowActorBindingReader bindingReader,
        ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> resumeService,
        CancellationToken ct)
    {
        var targetResult = await ValidateWorkflowFunctionRunTargetAsync(
            http.User,
            appId,
            releaseId,
            functionId,
            NormalizeOptional(request.ActorId),
            targetQueryPort,
            appQueryPort,
            authorizer,
            bindingReader,
            ct);
        if (targetResult.Result != null)
            return targetResult.Result;

        var actorId = NormalizeOptional(request.ActorId);
        var runId = NormalizeOptional(request.RunId);
        var stepId = NormalizeOptional(request.StepId);
        var commandId = NormalizeOptional(request.CommandId);
        if (actorId == null || runId == null || stepId == null)
        {
            return Results.BadRequest(new
            {
                error = "actorId, runId and stepId are required.",
            });
        }

        var dispatch = await resumeService.DispatchAsync(
            new WorkflowResumeCommand(
                actorId,
                runId,
                stepId,
                commandId,
                request.Approved,
                NormalizeOptional(request.UserInput),
                NormalizeMetadata(request.Metadata)),
            ct);
        if (!dispatch.Succeeded || dispatch.Receipt == null)
            return MapRunControlDispatchFailure(dispatch.Error);

        return Results.Ok(new
        {
            accepted = true,
            actorId = dispatch.Receipt.ActorId,
            runId = dispatch.Receipt.RunId,
            stepId,
            commandId = dispatch.Receipt.CommandId,
            correlationId = dispatch.Receipt.CorrelationId,
        });
    }

    private static async Task<IResult> HandleStopFunctionRunAsyncCore(
        HttpContext http,
        string appId,
        string? releaseId,
        string functionId,
        AppPlatformEndpointModels.FunctionRunStopHttpRequest request,
        IAppFunctionExecutionTargetQueryPort targetQueryPort,
        IAppDefinitionQueryPort appQueryPort,
        IAppAccessAuthorizer authorizer,
        IWorkflowActorBindingReader bindingReader,
        ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        CancellationToken ct)
    {
        var targetResult = await ValidateWorkflowFunctionRunTargetAsync(
            http.User,
            appId,
            releaseId,
            functionId,
            NormalizeOptional(request.ActorId),
            targetQueryPort,
            appQueryPort,
            authorizer,
            bindingReader,
            ct);
        if (targetResult.Result != null)
            return targetResult.Result;

        var actorId = NormalizeOptional(request.ActorId);
        var runId = NormalizeOptional(request.RunId);
        var commandId = NormalizeOptional(request.CommandId);
        var reason = NormalizeOptional(request.Reason);
        if (actorId == null || runId == null)
        {
            return Results.BadRequest(new
            {
                error = "actorId and runId are required.",
            });
        }

        var dispatch = await stopService.DispatchAsync(
            new WorkflowStopCommand(
                actorId,
                runId,
                commandId,
                reason),
            ct);
        if (!dispatch.Succeeded || dispatch.Receipt == null)
            return MapRunControlDispatchFailure(dispatch.Error);

        return Results.Ok(new
        {
            accepted = true,
            actorId = dispatch.Receipt.ActorId,
            runId = dispatch.Receipt.RunId,
            reason,
            commandId = dispatch.Receipt.CommandId,
            correlationId = dispatch.Receipt.CorrelationId,
        });
    }

    private static async Task<(AppFunctionExecutionTarget? Target, IResult? Result)> ValidateWorkflowFunctionRunTargetAsync(
        ClaimsPrincipal principal,
        string appId,
        string? releaseId,
        string functionId,
        string? actorId,
        IAppFunctionExecutionTargetQueryPort targetQueryPort,
        IAppDefinitionQueryPort appQueryPort,
        IAppAccessAuthorizer authorizer,
        IWorkflowActorBindingReader bindingReader,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return (null, Results.NotFound());

        var accessRequest = BuildAccessRequest(principal, AppAccessActions.Invoke, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return (null, ToDeniedResult(principal, decision, accessRequest.Action));

        var target = await targetQueryPort.ResolveAsync(appId, functionId, releaseId, ct);
        if (target == null)
            return (null, Results.NotFound());

        if (target.ServiceRef.ImplementationKind != AppImplementationKind.Workflow)
        {
            return (null, ToBadRequestResult(
                "FUNCTION_RUN_CONTROL_UNSUPPORTED",
                "Only workflow-backed functions support run control."));
        }

        if (!string.IsNullOrWhiteSpace(releaseId) &&
            !string.IsNullOrWhiteSpace(actorId) &&
            !string.IsNullOrWhiteSpace(target.PrimaryActorId))
        {
            var binding = await bindingReader.GetAsync(actorId, ct);
            if (binding?.ActorKind == WorkflowActorKind.Run &&
                !string.IsNullOrWhiteSpace(binding.EffectiveDefinitionActorId) &&
                !string.Equals(binding.EffectiveDefinitionActorId, target.PrimaryActorId, StringComparison.Ordinal))
            {
                return (null, Results.Json(
                    new
                    {
                        code = "FUNCTION_RUN_TARGET_MISMATCH",
                        message = $"Run actor '{actorId}' is not bound to function '{functionId}' on release '{releaseId}'.",
                    },
                    statusCode: StatusCodes.Status409Conflict));
            }
        }

        return (target, null);
    }

    private static async Task<IResult> HandleReleaseStatusMutationAsync(
        HttpContext http,
        string appId,
        string releaseId,
        string action,
        Func<string, string, CancellationToken, Task<AppReleaseSnapshot>> mutation,
        IAppDefinitionQueryPort appQueryPort,
        IAppAccessAuthorizer authorizer,
        CancellationToken ct)
    {
        var app = await appQueryPort.GetAsync(appId, ct);
        if (app == null)
            return Results.NotFound();

        var accessRequest = BuildAccessRequest(http.User, action, app);
        var decision = await authorizer.AuthorizeAsync(accessRequest, ct);
        if (!decision.Allowed)
            return ToDeniedResult(http.User, decision, accessRequest.Action);

        try
        {
            var snapshot = await mutation(appId, releaseId, ct);
            return Results.Ok(snapshot);
        }
        catch (InvalidOperationException ex)
        {
            return ToBadRequestResult("APP_MUTATION_INVALID", ex.Message);
        }
    }

    private static Dictionary<string, string> BuildFunctionScopedHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        var scopedHeaders = headers == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        scopedHeaders.Remove("scope_id");
        scopedHeaders.Remove(WorkflowRunCommandMetadataKeys.ScopeId);
        return scopedHeaders;
    }

    private static IResult MapRunControlDispatchFailure(WorkflowRunControlStartError error)
    {
        var (statusCode, message) = error.Code switch
        {
            WorkflowRunControlStartErrorCode.InvalidActorId => (
                StatusCodes.Status400BadRequest,
                "actorId is required."),
            WorkflowRunControlStartErrorCode.InvalidRunId => (
                StatusCodes.Status400BadRequest,
                "runId is required."),
            WorkflowRunControlStartErrorCode.InvalidStepId => (
                StatusCodes.Status400BadRequest,
                "stepId is required."),
            WorkflowRunControlStartErrorCode.InvalidSignalName => (
                StatusCodes.Status400BadRequest,
                "signalName is required."),
            WorkflowRunControlStartErrorCode.ActorNotFound => (
                StatusCodes.Status404NotFound,
                $"Actor '{error.ActorId}' not found."),
            WorkflowRunControlStartErrorCode.ActorNotWorkflowRun => (
                StatusCodes.Status400BadRequest,
                $"Actor '{error.ActorId}' is not a workflow run actor."),
            WorkflowRunControlStartErrorCode.RunBindingMissing => (
                StatusCodes.Status409Conflict,
                $"Actor '{error.ActorId}' does not have a bound run id."),
            WorkflowRunControlStartErrorCode.RunBindingMismatch => (
                StatusCodes.Status409Conflict,
                $"Actor '{error.ActorId}' is bound to run '{error.BoundRunId}', not '{error.RequestedRunId}'."),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Workflow control dispatch failed."),
        };

        return Results.Json(new { error = message }, statusCode: statusCode);
    }

    private static (int StatusCode, string Code, string Message) MapFunctionStreamStartError(WorkflowChatRunStartError error) =>
        error switch
        {
            WorkflowChatRunStartError.AgentNotFound => (StatusCodes.Status404NotFound, "AGENT_NOT_FOUND", "Agent not found."),
            WorkflowChatRunStartError.WorkflowNotFound => (StatusCodes.Status404NotFound, "WORKFLOW_NOT_FOUND", "Workflow not found."),
            WorkflowChatRunStartError.AgentTypeNotSupported => (StatusCodes.Status400BadRequest, "AGENT_TYPE_NOT_SUPPORTED", "Actor is not workflow-capable."),
            WorkflowChatRunStartError.ProjectionDisabled => (StatusCodes.Status503ServiceUnavailable, "PROJECTION_DISABLED", "Projection pipeline is disabled."),
            WorkflowChatRunStartError.WorkflowBindingMismatch => (StatusCodes.Status409Conflict, "WORKFLOW_BINDING_MISMATCH", "Actor is bound to a different workflow."),
            WorkflowChatRunStartError.AgentWorkflowNotConfigured => (StatusCodes.Status409Conflict, "AGENT_WORKFLOW_NOT_CONFIGURED", "Actor has no bound workflow."),
            WorkflowChatRunStartError.InvalidWorkflowYaml => (StatusCodes.Status400BadRequest, "INVALID_WORKFLOW_YAML", "Workflow YAML is invalid."),
            WorkflowChatRunStartError.WorkflowNameMismatch => (StatusCodes.Status400BadRequest, "WORKFLOW_NAME_MISMATCH", "Workflow name does not match workflow YAML."),
            WorkflowChatRunStartError.PromptRequired => (StatusCodes.Status400BadRequest, "PROMPT_REQUIRED", "Prompt is required."),
            WorkflowChatRunStartError.ConflictingScopeId => (StatusCodes.Status400BadRequest, "CONFLICTING_SCOPE_ID", "Conflicting scope_id values were provided."),
            _ => (StatusCodes.Status400BadRequest, "RUN_START_FAILED", "Failed to resolve actor."),
        };

    private static bool TryParseFunctionStreamEventFormat(string? rawValue, out FunctionStreamEventFormat eventFormat)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            eventFormat = FunctionStreamEventFormat.Workflow;
            return true;
        }

        if (string.Equals(rawValue, "workflow", StringComparison.OrdinalIgnoreCase))
        {
            eventFormat = FunctionStreamEventFormat.Workflow;
            return true;
        }

        if (string.Equals(rawValue, "agui", StringComparison.OrdinalIgnoreCase))
        {
            eventFormat = FunctionStreamEventFormat.Agui;
            return true;
        }

        eventFormat = FunctionStreamEventFormat.Workflow;
        return false;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized;
    }

    private static IReadOnlyDictionary<string, string>? NormalizeMetadata(IDictionary<string, string>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return null;

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            var normalizedKey = NormalizeOptional(key);
            var normalizedValue = NormalizeOptional(value);
            if (normalizedKey == null || normalizedValue == null)
                continue;

            normalized[normalizedKey] = normalizedValue;
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static async Task WriteJsonErrorResponseAsync(
        HttpContext http,
        int statusCode,
        string code,
        string message,
        CancellationToken ct)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsJsonAsync(new { code, message }, cancellationToken: ct);
    }

    private static AppDefinitionSnapshot ToCreateAppDefinition(AppPlatformEndpointModels.CreateAppHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AppDefinitionSnapshot
        {
            AppId = NormalizeRequired(request.AppId, nameof(request.AppId)),
            OwnerScopeId = NormalizeRequired(request.OwnerScopeId, nameof(request.OwnerScopeId)),
            DisplayName = request.DisplayName?.Trim() ?? string.Empty,
            Description = request.Description?.Trim() ?? string.Empty,
            Visibility = ParseVisibility(request.Visibility),
            DefaultReleaseId = request.DefaultReleaseId?.Trim() ?? string.Empty,
        };
    }

    private static AppDefinitionSnapshot ToUpsertAppDefinition(
        string appId,
        AppPlatformEndpointModels.UpsertAppHttpRequest request,
        AppDefinitionSnapshot? existing)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ownerScopeId = request.OwnerScopeId?.Trim()
                           ?? existing?.OwnerScopeId
                           ?? string.Empty;
        var visibility = request.Visibility == null
            ? existing?.Visibility ?? AppVisibility.Private
            : ParseVisibility(request.Visibility);
        var defaultReleaseId = request.DefaultReleaseId?.Trim()
                               ?? existing?.DefaultReleaseId
                               ?? string.Empty;

        var snapshot = new AppDefinitionSnapshot
        {
            AppId = NormalizeRequired(appId, nameof(appId)),
            OwnerScopeId = NormalizeRequired(ownerScopeId, nameof(request.OwnerScopeId)),
            DisplayName = request.DisplayName?.Trim() ?? existing?.DisplayName ?? string.Empty,
            Description = request.Description?.Trim() ?? existing?.Description ?? string.Empty,
            Visibility = visibility,
            DefaultReleaseId = defaultReleaseId,
        };
        if (existing != null)
            snapshot.RoutePaths.Add(existing.RoutePaths);

        return snapshot;
    }

    private static AppReleaseSnapshot ToReleaseSnapshot(
        string appId,
        string releaseId,
        AppPlatformEndpointModels.UpsertReleaseHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = new AppReleaseSnapshot
        {
            AppId = NormalizeRequired(appId, nameof(appId)),
            ReleaseId = NormalizeRequired(releaseId, nameof(releaseId)),
            DisplayName = request.DisplayName?.Trim() ?? string.Empty,
            Status = ParseReleaseStatus(request.Status),
        };

        if (request.Services != null)
        {
            snapshot.ServiceRefs.Add(request.Services.Select(ToServiceRef));
        }

        if (request.Functions != null)
        {
            snapshot.EntryRefs.Add(request.Functions.Select(ToEntryRef));
        }

        if (request.Connectors != null)
        {
            snapshot.ConnectorRefs.Add(request.Connectors.Select(ToConnectorRef));
        }

        if (request.Secrets != null)
        {
            snapshot.SecretRefs.Add(request.Secrets.Select(ToSecretRef));
        }

        return snapshot;
    }

    private static AppReleaseResourcesSnapshot ToResourcesSnapshot(
        string appId,
        string releaseId,
        AppPlatformEndpointModels.ReplaceResourcesHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = new AppReleaseResourcesSnapshot
        {
            AppId = NormalizeRequired(appId, nameof(appId)),
            ReleaseId = NormalizeRequired(releaseId, nameof(releaseId)),
        };
        if (request.Connectors != null)
            snapshot.ConnectorRefs.Add(request.Connectors.Select(ToConnectorRef));
        if (request.Secrets != null)
            snapshot.SecretRefs.Add(request.Secrets.Select(ToSecretRef));
        return snapshot;
    }

    private static AppServiceRef ToServiceRef(AppPlatformEndpointModels.AppServiceRefHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AppServiceRef
        {
            TenantId = request.TenantId?.Trim() ?? string.Empty,
            AppId = request.AppId?.Trim() ?? string.Empty,
            Namespace = request.Namespace?.Trim() ?? string.Empty,
            ServiceId = NormalizeRequired(request.ServiceId, nameof(request.ServiceId)),
            RevisionId = request.RevisionId?.Trim() ?? string.Empty,
            ImplementationKind = ParseImplementationKind(request.ImplementationKind),
            Role = ParseServiceRole(request.Role),
        };
    }

    private static AppEntryRef ToEntryRef(AppPlatformEndpointModels.AppFunctionRefHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AppEntryRef
        {
            EntryId = NormalizeRequired(request.FunctionId, nameof(request.FunctionId)),
            ServiceId = NormalizeRequired(request.ServiceId, nameof(request.ServiceId)),
            EndpointId = NormalizeRequired(request.EndpointId, nameof(request.EndpointId)),
        };
    }

    private static AppConnectorRef ToConnectorRef(AppPlatformEndpointModels.AppConnectorRefHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AppConnectorRef
        {
            ResourceId = NormalizeRequired(request.ResourceId, nameof(request.ResourceId)),
            ConnectorName = NormalizeRequired(request.ConnectorName, nameof(request.ConnectorName)),
        };
    }

    private static AppSecretRef ToSecretRef(AppPlatformEndpointModels.AppSecretRefHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AppSecretRef
        {
            ResourceId = NormalizeRequired(request.ResourceId, nameof(request.ResourceId)),
            SecretName = NormalizeRequired(request.SecretName, nameof(request.SecretName)),
        };
    }

    private static AppAccessRequest BuildAccessRequest(
        ClaimsPrincipal principal,
        string action,
        AppDefinitionSnapshot app) =>
        new(
            ResolveSubjectScopeId(principal),
            action,
            AppDefinitionQueryApplicationService.BuildAccessResource(app));

    private static IResult ToBadRequestResult(string code, string message)
    {
        return Results.Json(new
        {
            code,
            message,
        }, statusCode: StatusCodes.Status400BadRequest);
    }

    private static IResult ToDeniedResult(
        ClaimsPrincipal principal,
        AppAccessDecision decision,
        string action)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return Results.Json(new
            {
                code = "AUTH_CHALLENGE_REQUIRED",
                message = decision.Reason,
                requiredActions = new[] { action },
            }, statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Json(new
        {
            code = "ACCESS_DENIED",
            message = decision.Reason,
            requiredActions = new[] { action },
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    private static string ResolveSubjectScopeId(ClaimsPrincipal principal)
    {
        var scopeId = principal.FindFirst(AevatarStandardClaimTypes.ScopeId)?.Value;
        if (!string.IsNullOrWhiteSpace(scopeId))
            return scopeId.Trim();

        return principal.FindFirst("uid")?.Value?.Trim()
               ?? principal.FindFirst("sub")?.Value?.Trim()
               ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value?.Trim()
               ?? string.Empty;
    }

    private enum FunctionStreamEventFormat
    {
        Workflow = 0,
        Agui = 1,
    }

    private static AppVisibility ParseVisibility(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "public" => AppVisibility.Public,
            "private" or "" or null => AppVisibility.Private,
            _ => throw new InvalidOperationException($"Unsupported app visibility '{rawValue}'."),
        };
    }

    private static AppReleaseStatus ParseReleaseStatus(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "draft" or "" or null => AppReleaseStatus.Draft,
            "published" => AppReleaseStatus.Published,
            "archived" => AppReleaseStatus.Archived,
            _ => throw new InvalidOperationException($"Unsupported app release status '{rawValue}'."),
        };
    }

    private static AppImplementationKind ParseImplementationKind(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "static" => AppImplementationKind.Static,
            "scripting" => AppImplementationKind.Scripting,
            "workflow" => AppImplementationKind.Workflow,
            "" or null => AppImplementationKind.Unspecified,
            _ => throw new InvalidOperationException($"Unsupported app implementation kind '{rawValue}'."),
        };
    }

    private static AppServiceRole ParseServiceRole(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "entry" => AppServiceRole.Entry,
            "companion" => AppServiceRole.Companion,
            "internal" or "" or null => AppServiceRole.Internal,
            _ => throw new InvalidOperationException($"Unsupported app service role '{rawValue}'."),
        };
    }

    private static bool IsTerminalOperationStatus(AppOperationStatus status) =>
        status is AppOperationStatus.Completed or AppOperationStatus.Failed or AppOperationStatus.Cancelled;

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{paramName} is required.");

        return normalized;
    }
}
