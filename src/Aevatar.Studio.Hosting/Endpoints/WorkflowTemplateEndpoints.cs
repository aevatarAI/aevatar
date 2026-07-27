using System.Diagnostics;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Aevatar.Studio.Hosting.Endpoints;

internal static class WorkflowTemplateEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/studio/workflow-templates", HandleListAsync)
            .WithTags("StudioWorkflowTemplates")
            .Produces<WorkflowTemplateCatalogPage>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status400BadRequest);
        app.MapGet(
                "/api/studio/workflow-templates/{templateId}/revisions/{revision}",
                HandleGetAsync)
            .WithTags("StudioWorkflowTemplates")
            .Produces<WorkflowTemplateDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
    }

    internal static async Task<IResult> HandleListAsync(
        HttpContext http,
        [FromServices] IWorkflowTemplateCatalogQueryPort catalog,
        string? query,
        string? category,
        string? cursor,
        int? pageSize,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var logger = loggerFactory.CreateLogger(typeof(WorkflowTemplateEndpoints));
        try
        {
            var page = await catalog.ListAsync(
                new WorkflowTemplateCatalogQuery(query, category, cursor, pageSize ?? 20),
                ct);
            http.Response.Headers.ETag = page.ETag;
            http.Response.Headers.CacheControl = "public, max-age=60";
            var expectedETag = EntityTagHeaderValue.Parse(page.ETag);
            var isNotModified = http.Request.GetTypedHeaders().IfNoneMatch?
                .Any(candidate => candidate.Compare(expectedETag, useStrongComparison: false)) == true;
            var resultCode = isNotModified
                ? StatusCodes.Status304NotModified
                : StatusCodes.Status200OK;
            logger.LogInformation(
                "Workflow template catalog list returned {ResultCode} in {ElapsedMilliseconds} ms.",
                resultCode,
                stopwatch.ElapsedMilliseconds);
            return isNotModified ? Results.StatusCode(resultCode) : Results.Ok(page);
        }
        catch (ArgumentException)
        {
            logger.LogInformation(
                "Workflow template catalog list returned {ResultCode} in {ElapsedMilliseconds} ms.",
                StatusCodes.Status400BadRequest,
                stopwatch.ElapsedMilliseconds);
            return Error(
                StatusCodes.Status400BadRequest,
                "INVALID_WORKFLOW_TEMPLATE_QUERY",
                "The workflow template query is invalid.");
        }
    }

    internal static async Task<IResult> HandleGetAsync(
        HttpContext http,
        string templateId,
        string revision,
        [FromServices] IWorkflowTemplateCatalogQueryPort catalog,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var logger = loggerFactory.CreateLogger(typeof(WorkflowTemplateEndpoints));
        try
        {
            var lookup = await catalog.GetAsync(templateId, revision, ct);
            var result = MapLookup(http, lookup, out var statusCode);
            logger.LogInformation(
                "Workflow template {TemplateId} revision {Revision} returned {ResultCode} in {ElapsedMilliseconds} ms.",
                templateId,
                revision,
                statusCode,
                stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (ArgumentException)
        {
            logger.LogInformation(
                "Workflow template {TemplateId} revision {Revision} returned {ResultCode} in {ElapsedMilliseconds} ms.",
                templateId,
                revision,
                StatusCodes.Status400BadRequest,
                stopwatch.ElapsedMilliseconds);
            return Error(
                StatusCodes.Status400BadRequest,
                "INVALID_WORKFLOW_TEMPLATE_IDENTITY",
                "The workflow template identity is invalid.");
        }
    }

    private static IResult MapLookup(
        HttpContext http,
        WorkflowTemplateLookupResult lookup,
        out int statusCode)
    {
        switch (lookup.Status)
        {
            case WorkflowTemplateLookupStatus.Found when lookup.Detail != null:
                statusCode = StatusCodes.Status200OK;
                http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                return Results.Ok(lookup.Detail);
            case WorkflowTemplateLookupStatus.Incompatible when lookup.Detail != null:
                statusCode = StatusCodes.Status409Conflict;
                return Results.Json(
                    new WorkflowTemplateErrorResponse(
                        "WORKFLOW_TEMPLATE_INCOMPATIBLE",
                        "The requested workflow template revision is incompatible with this host.",
                        lookup.Detail),
                    statusCode: statusCode);
            case WorkflowTemplateLookupStatus.Disabled:
                statusCode = StatusCodes.Status404NotFound;
                return Error(
                    statusCode,
                    "WORKFLOW_TEMPLATE_DISABLED",
                    "The requested workflow template revision is disabled.");
            case WorkflowTemplateLookupStatus.NotFound:
                statusCode = StatusCodes.Status404NotFound;
                return Error(
                    statusCode,
                    "WORKFLOW_TEMPLATE_NOT_FOUND",
                    "The requested workflow template revision was not found.");
            default:
                throw new InvalidOperationException("Workflow template lookup returned an invalid result.");
        }
    }

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new WorkflowTemplateErrorResponse(code, message), statusCode: statusCode);
}

internal sealed record WorkflowTemplateErrorResponse(
    string Code,
    string Message,
    WorkflowTemplateDetail? Detail = null);
