using Aevatar.Scripting.Application.Queries;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Scripting.Hosting.CapabilityApi;

public static class ScriptQueryEndpoints
{
    public static IEndpointRouteBuilder MapScriptQueryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scripts/runtimes").WithTags("ScriptRuntimeQueries");

        group.MapGet(string.Empty, HandleListSnapshots)
            .Produces<IReadOnlyList<ScriptReadModelSnapshotHttpResponse>>(StatusCodes.Status200OK);

        group.MapGet("/{actorId}/readmodel", HandleGetSnapshot)
            .Produces<ScriptReadModelSnapshotHttpResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{actorId}/queries", HandleExecuteDeclaredQuery)
            .Produces<ScriptDeclaredQueryHttpResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status400BadRequest);

        return app;
    }

    internal static async Task<IResult> HandleListSnapshots(
        int take,
        IScriptReadModelQueryApplicationService service,
        CancellationToken ct = default)
    {
        var snapshots = await service.ListSnapshotsAsync(take <= 0 ? 200 : take, ct);
        return Results.Ok(snapshots.Select(static snapshot => new ScriptReadModelSnapshotHttpResponse(
            snapshot.ActorId,
            snapshot.ScriptId,
            snapshot.DefinitionActorId,
            snapshot.Revision,
            snapshot.ReadModelTypeUrl,
            ScriptJsonPayloads.ToJson(snapshot.ReadModelPayload),
            snapshot.StateVersion,
            snapshot.LastEventId,
            snapshot.UpdatedAt)));
    }

    internal static async Task<IResult> HandleGetSnapshot(
        string actorId,
        IScriptReadModelQueryApplicationService service,
        CancellationToken ct = default)
    {
        var snapshot = await service.GetSnapshotAsync(actorId, ct);
        if (snapshot == null)
            return Results.NotFound();

        return Results.Ok(new ScriptReadModelSnapshotHttpResponse(
            snapshot.ActorId,
            snapshot.ScriptId,
            snapshot.DefinitionActorId,
            snapshot.Revision,
            snapshot.ReadModelTypeUrl,
            ScriptJsonPayloads.ToJson(snapshot.ReadModelPayload),
            snapshot.StateVersion,
            snapshot.LastEventId,
            snapshot.UpdatedAt));
    }

    internal static async Task<IResult> HandleExecuteDeclaredQuery(
        string actorId,
        ScriptDeclaredQueryHttpRequest request,
        IScriptReadModelQueryApplicationService service,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return Results.BadRequest(new { code = "ACTOR_ID_REQUIRED", message = "actorId is required." });

        try
        {
            var payload = ScriptJsonPayloads.PackStruct(request.JsonPayload);
            var result = await service.ExecuteDeclaredQueryAsync(actorId, payload, ct);
            return Results.Ok(new ScriptDeclaredQueryHttpResponse(
                ActorId: actorId,
                QueryResultJson: ScriptJsonPayloads.ToJson(result)));
        }
        catch (InvalidProtocolBufferException ex)
        {
            return Results.BadRequest(new { code = "INVALID_QUERY_PAYLOAD", message = ex.Message });
        }
        catch (InvalidJsonException ex)
        {
            return Results.BadRequest(new { code = "INVALID_QUERY_PAYLOAD", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.NotFound(new { code = "READ_MODEL_NOT_FOUND", message = ex.Message });
        }
    }
}

public sealed record ScriptDeclaredQueryHttpRequest(string? JsonPayload);

public sealed record ScriptDeclaredQueryHttpResponse(
    string ActorId,
    string QueryResultJson);

public sealed record ScriptReadModelSnapshotHttpResponse(
    string ActorId,
    string ScriptId,
    string DefinitionActorId,
    string Revision,
    string ReadModelTypeUrl,
    string ReadModelPayloadJson,
    long StateVersion,
    string LastEventId,
    DateTimeOffset UpdatedAt);
