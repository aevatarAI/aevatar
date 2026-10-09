using Aevatar.Capabilities;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Hosting.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Studio.Hosting.Endpoints;

public static class WorkflowYamlApiEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowYamlApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/workflows/parse-yaml", Parse)
            .WithName("parse_workflow_yaml")
            .WithTags("Workflows")
            .WithSummary("Parse Workflow YAML into a document, graph, and findings.")
            .WithDescription("Uses the current authenticated scope. Parsing success does not establish runtime capability admission or publication readiness.")
            .WithMetadata(new AevatarToolMetadata(true))
            .Produces<ParseYamlHttpResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();
        return app;
    }

    private static IResult Parse(HttpContext http, ParseYamlRequest request, [FromServices] WorkflowEditorService editor)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out _))
            return Results.Unauthorized();
        return Results.Ok(ParseYamlHttpResponse.FromApplicationResponse(editor.ParseYaml(request)));
    }
}
