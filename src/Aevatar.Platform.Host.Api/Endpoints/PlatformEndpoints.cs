using Aevatar.Platform.Application.Abstractions.Queries;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Platform.Host.Api.Endpoints;

public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Platform");

        group.MapGet("/agents", ListAgents)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/routes/{subsystem}/commands/{*command}", ResolveCommandRoute)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/routes/{subsystem}/queries/{*query}", ResolveQueryRoute)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static IResult ListAgents(IPlatformAgentQueryApplicationService queryService) =>
        Results.Ok(queryService.ListAgents());

    private static IResult ResolveCommandRoute(
        string subsystem,
        string command,
        IPlatformAgentQueryApplicationService queryService)
    {
        var uri = queryService.ResolveCommandRoute(subsystem, command);
        return uri == null ? Results.NotFound() : Results.Ok(new { target = uri.ToString() });
    }

    private static IResult ResolveQueryRoute(
        string subsystem,
        string query,
        IPlatformAgentQueryApplicationService queryService)
    {
        var uri = queryService.ResolveQueryRoute(subsystem, query);
        return uri == null ? Results.NotFound() : Results.Ok(new { target = uri.ToString() });
    }
}
