using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Endpoints;

public static class PaperEndpoints
{
    public static IEndpointRouteBuilder MapPaperEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/paper", HandleExportPdf)
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError)
            .WithTags("Paper");

        return app;
    }

    private static async Task<IResult> HandleExportPdf(
        ChronoGraphReadService readService,
        PaperGeneratorService paperGenerator,
        ILogger<PaperGeneratorService> logger,
        CancellationToken ct)
    {
        logger.LogInformation("Paper PDF export requested");

        try
        {
            var snapshot = await readService.GetBlueSnapshotAsync(ct);

            if (snapshot.Nodes.Count == 0)
                return Results.NotFound(new { message = "No purified nodes found in graph" });

            var pdfBytes = await paperGenerator.GeneratePdfAsync(snapshot, ct);
            return Results.File(pdfBytes, "application/pdf", "paper.pdf");
        }
        catch (OperationCanceledException)
        {
            throw; // Let framework handle client disconnect
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paper PDF export failed");
            return Results.Problem("Failed to generate PDF", statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
