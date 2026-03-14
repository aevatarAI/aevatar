using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Scripting.Hosting.CapabilityApi;

public static class ScriptCapabilityEndpoints
{
    public static IEndpointRouteBuilder MapScriptCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scripts").WithTags("Scripts");

        group.MapPost("/evolutions/proposals", HandleProposeEvolution)
            .Produces<ScriptPromotionDecision>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError);

        app.MapScriptQueryEndpoints();
        return app;
    }

    internal static async Task<IResult> HandleProposeEvolution(
        ProposeScriptEvolutionHttpRequest request,
        IScriptEvolutionApplicationService service,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ScriptId))
            return ValidationError("SCRIPT_ID_REQUIRED", "ScriptId is required.");

        if (string.IsNullOrWhiteSpace(request.CandidateRevision))
            return ValidationError("CANDIDATE_REVISION_REQUIRED", "CandidateRevision is required.");

        if (string.IsNullOrWhiteSpace(request.CandidateSource))
            return ValidationError("CANDIDATE_SOURCE_REQUIRED", "CandidateSource is required.");

        try
        {
            var decision = await service.ProposeAsync(
                new ProposeScriptEvolutionRequest(
                    ScriptId: request.ScriptId,
                    BaseRevision: request.BaseRevision ?? string.Empty,
                    CandidateRevision: request.CandidateRevision,
                    CandidateSource: request.CandidateSource,
                    CandidateSourceHash: request.CandidateSourceHash ?? string.Empty,
                    Reason: request.Reason ?? string.Empty,
                    ProposalId: request.ProposalId ?? string.Empty),
                ct);

            return Results.Ok(decision);
        }
        catch (InvalidOperationException ex)
        {
            return ValidationError("INVALID_REQUEST", ex.Message);
        }
    }

    private static IResult ValidationError(string code, string message) =>
        Results.BadRequest(new
        {
            code,
            message,
        });
}

public sealed record ProposeScriptEvolutionHttpRequest(
    string? ScriptId,
    string? BaseRevision,
    string? CandidateRevision,
    string? CandidateSource,
    string? CandidateSourceHash,
    string? Reason,
    string? ProposalId);
