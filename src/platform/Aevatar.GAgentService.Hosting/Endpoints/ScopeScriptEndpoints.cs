using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class ScopeScriptEndpoints
{
    public static IEndpointRouteBuilder MapScopeScriptCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scopes").WithTags("ScopeScripts");
        group.MapPut("/{scopeId}/scripts/{scriptId}", HandleUpsertScriptAsync)
            .Produces<ScopeScriptUpsertResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapGet("/{scopeId}/scripts", HandleListScriptsAsync)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapGet("/{scopeId}/scripts/{scriptId}", HandleGetScriptDetailAsync)
            .Produces<ScopeScriptDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapGet("/{scopeId}/scripts/{scriptId}/catalog", HandleGetScriptCatalogAsync)
            .Produces<ScopeScriptCatalogHttpResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{scopeId}/scripts/{scriptId}/evolutions/proposals", HandleProposeScriptEvolutionAsync)
            .Produces<ScriptPromotionDecision>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        return app;
    }

    internal static async Task<IResult> HandleUpsertScriptAsync(
        string scopeId,
        string scriptId,
        UpsertScopeScriptHttpRequest request,
        [FromServices] IScopeScriptCommandPort scriptCommandPort,
        CancellationToken ct)
    {
        try
        {
            var result = await scriptCommandPort.UpsertAsync(
                new ScopeScriptUpsertRequest(
                    scopeId,
                    scriptId,
                    request.SourceText,
                    request.RevisionId,
                    request.ExpectedBaseRevision),
                ct);
            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_SCRIPT_REQUEST",
                message = ex.Message,
            });
        }
    }

    internal static async Task<IResult> HandleListScriptsAsync(
        string scopeId,
        bool includeSource,
        [FromServices] IScopeScriptQueryPort scriptQueryPort,
        [FromServices] IScriptDefinitionSnapshotPort definitionSnapshotPort,
        CancellationToken ct)
    {
        try
        {
            var scripts = await scriptQueryPort.ListAsync(scopeId, ct);
            if (!includeSource)
                return Results.Ok(scripts);

            var details = new List<ScopeScriptDetail>(scripts.Count);
            foreach (var script in scripts)
                details.Add(await BuildScriptDetailAsync(scopeId, script, definitionSnapshotPort, ct));

            return Results.Ok(details);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_SCRIPT_REQUEST",
                message = ex.Message,
            });
        }
    }

    internal static async Task<IResult> HandleGetScriptDetailAsync(
        string scopeId,
        string scriptId,
        [FromServices] IScopeScriptQueryPort scriptQueryPort,
        [FromServices] IScriptDefinitionSnapshotPort definitionSnapshotPort,
        CancellationToken ct)
    {
        try
        {
            var script = await scriptQueryPort.GetByScriptIdAsync(scopeId, scriptId, ct);
            if (script == null)
            {
                return Results.NotFound(new
                {
                    code = "SCOPE_SCRIPT_NOT_FOUND",
                    message = $"Script '{scriptId}' was not found for scope '{scopeId}'.",
                });
            }

            return Results.Json(await BuildScriptDetailAsync(scopeId, script, definitionSnapshotPort, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_SCRIPT_REQUEST",
                message = ex.Message,
            });
        }
    }

    internal static async Task<IResult> HandleProposeScriptEvolutionAsync(
        string scopeId,
        string scriptId,
        ProposeScopeScriptEvolutionHttpRequest request,
        [FromServices] IScriptEvolutionApplicationService scriptEvolutionService,
        CancellationToken ct)
    {
        try
        {
            var decision = await scriptEvolutionService.ProposeAsync(
                new ProposeScriptEvolutionRequest(
                    ScriptId: scriptId,
                    BaseRevision: request.BaseRevision ?? string.Empty,
                    CandidateRevision: request.CandidateRevision ?? string.Empty,
                    CandidateSource: request.CandidateSource ?? string.Empty,
                    CandidateSourceHash: request.CandidateSourceHash ?? string.Empty,
                    Reason: request.Reason ?? string.Empty,
                    ProposalId: request.ProposalId ?? string.Empty,
                    ScopeId: scopeId),
                ct);
            return Results.Ok(decision);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_SCRIPT_REQUEST",
                message = ex.Message,
            });
        }
    }

    internal static async Task<IResult> HandleGetScriptCatalogAsync(
        string scopeId,
        string scriptId,
        [FromServices] IScriptCatalogQueryPort catalogQueryPort,
        [FromServices] IScriptingActorAddressResolver scriptingActorAddressResolver,
        CancellationToken ct)
    {
        try
        {
            var normalizedScopeId = string.IsNullOrWhiteSpace(scopeId)
                ? throw new InvalidOperationException($"{nameof(scopeId)} is required.")
                : scopeId.Trim();
            var normalizedScriptId = string.IsNullOrWhiteSpace(scriptId)
                ? throw new InvalidOperationException($"{nameof(scriptId)} is required.")
                : scriptId.Trim();
            var catalogActorId = scriptingActorAddressResolver.GetCatalogActorId(normalizedScopeId);
            var snapshot = await catalogQueryPort.GetCatalogEntryAsync(catalogActorId, normalizedScriptId, ct);
            if (snapshot == null)
                return Results.NotFound();

            return Results.Ok(new ScopeScriptCatalogHttpResponse(
                snapshot.ScriptId,
                snapshot.ActiveRevision,
                snapshot.ActiveDefinitionActorId,
                snapshot.ActiveSourceHash,
                snapshot.PreviousRevision,
                snapshot.RevisionHistory.ToArray(),
                snapshot.LastProposalId,
                snapshot.CatalogActorId,
                snapshot.ScopeId,
                snapshot.UpdatedAtUnixTimeMs <= 0
                    ? DateTimeOffset.UnixEpoch
                    : DateTimeOffset.FromUnixTimeMilliseconds(snapshot.UpdatedAtUnixTimeMs)));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_SCRIPT_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<ScopeScriptDetail> BuildScriptDetailAsync(
        string scopeId,
        ScopeScriptSummary script,
        IScriptDefinitionSnapshotPort definitionSnapshotPort,
        CancellationToken ct)
    {
        var snapshot = string.IsNullOrWhiteSpace(script.DefinitionActorId) || string.IsNullOrWhiteSpace(script.ActiveRevision)
            ? null
            : await definitionSnapshotPort.TryGetAsync(script.DefinitionActorId, script.ActiveRevision, ct);

        return new ScopeScriptDetail(
            true,
            scopeId,
            script,
            snapshot == null
                ? null
                : new ScopeScriptSource(
                    snapshot.SourceText,
                    string.IsNullOrWhiteSpace(snapshot.DefinitionActorId)
                        ? script.DefinitionActorId
                        : snapshot.DefinitionActorId,
                    snapshot.Revision,
                    snapshot.SourceHash));
    }

    public sealed record UpsertScopeScriptHttpRequest(
        string SourceText,
        string? RevisionId = null,
        string? ExpectedBaseRevision = null);

    public sealed record ProposeScopeScriptEvolutionHttpRequest(
        string? BaseRevision,
        string? CandidateRevision,
        string? CandidateSource,
        string? CandidateSourceHash,
        string? Reason,
        string? ProposalId);

    public sealed record ScopeScriptCatalogHttpResponse(
        string ScriptId,
        string ActiveRevision,
        string ActiveDefinitionActorId,
        string ActiveSourceHash,
        string PreviousRevision,
        IReadOnlyList<string> RevisionHistory,
        string LastProposalId,
        string CatalogActorId,
        string ScopeId,
        DateTimeOffset UpdatedAt);
}
