using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatRouting;
using Aevatar.Hosting;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.ChatRouting;

/// <summary>
/// REST admin surface for <see cref="ChatRoutePolicyGAgent"/>.
///
/// Without this endpoint there's no production-reachable way to write a chat
/// route policy: <see cref="ChatRoutePolicyGAgent"/> isn't published as a
/// platform GAgent type (it deliberately stays config-only, not a Studio
/// service), so the generic <c>/api/scopes/{scopeId}/invoke/{endpointId}</c>
/// surface can't address it. This endpoint dispatches
/// <see cref="UpsertChatRoutePolicyRequested"/> /
/// <see cref="RemoveChatRouteRuleRequested"/> directly via
/// <see cref="IActorDispatchPort"/>.
///
/// Authorization model: the same scope-access guard the other
/// scope-bound endpoints use — caller's scope claim must match the URL
/// <c>scopeId</c>. The body's owner_scope is server-stamped from the URL
/// scopeId so callers cannot write a policy targeting someone else's caller
/// scope by accident or by intent.
/// </summary>
internal static class ChatRoutePolicyAdminEndpoints
{
    private const string ProjectionKindActorIdPrefix = "chat-route-policy:";

    private static readonly JsonParser BodyParser = new(
        JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private static readonly JsonFormatter ResponseFormatter = new(
        JsonFormatter.Settings.Default.WithFormatDefaultValues(false));

    public static IEndpointRouteBuilder MapChatRoutePolicyAdminEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/scopes/{scopeId}/chat-route-policy")
            .WithTags("ChatRoutePolicy");

        group.MapPut("", HandleUpsertAsync);
        group.MapDelete("/rules/{ruleId}", HandleRemoveRuleAsync);
        group.MapGet("", HandleGetAsync);

        return app;
    }

    /// <summary>
    /// PUT /api/scopes/{scopeId}/chat-route-policy
    ///
    /// Body: protobuf-JSON of <see cref="UpsertChatRoutePolicyRequested"/>
    /// minus <c>owner_scope</c> (server stamps it from the URL scopeId so
    /// callers can't write a policy targeting a different caller scope).
    ///
    /// <c>default_target</c> is required; missing it returns 400 with the
    /// same error message <see cref="ChatRoutePolicyGAgent.HandleUpsertAsync"/>
    /// would have thrown — caught synchronously instead of fire-and-forget.
    /// </summary>
    private static async Task<IResult> HandleUpsertAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorDispatchPort actorDispatchPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        UpsertChatRoutePolicyRequested command;
        try
        {
            using var reader = new StreamReader(http.Request.Body);
            var bodyJson = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(bodyJson))
                return JsonError(StatusCodes.Status400BadRequest, "empty_body",
                    "Request body is required: protobuf-JSON of UpsertChatRoutePolicyRequested.");

            command = BodyParser.Parse<UpsertChatRoutePolicyRequested>(bodyJson);
        }
        catch (InvalidProtocolBufferException ex)
        {
            return JsonError(StatusCodes.Status400BadRequest, "invalid_body",
                $"Could not parse request body as UpsertChatRoutePolicyRequested: {ex.Message}");
        }

        if (command.DefaultTarget is null ||
            command.DefaultTarget.ActionCase == ChatRouteAction.ActionOneofCase.None)
        {
            return JsonError(StatusCodes.Status400BadRequest, "default_target_required",
                "default_target is required: a chat route policy must declare a default ChatRouteAction " +
                "(e.g. ForwardToModel) so the resolver always has a fallback when no rule matches.");
        }

        // Server-stamp owner_scope from URL scope so a caller can't write a
        // policy keyed to a different caller scope. Mirrors the resolver's
        // NyxID-native caller scope shape (see OwnerScope.ForNyxIdNative).
        command.OwnerScope = new ChatRouteCallerScope
        {
            NyxUserId = scopeId,
            Platform = OwnerScope.NyxIdPlatform,
            RegistrationScopeId = string.Empty,
            SenderId = string.Empty,
        };

        var (actorId, commandId) = await DispatchAsync(command, scopeId, actorRuntime, actorDispatchPort, ct);
        return Results.Accepted(value: new
        {
            actor_id = actorId,
            command_id = commandId,
            note = "Upsert dispatched. Re-query GET to observe materialized state.",
        });
    }

    /// <summary>
    /// DELETE /api/scopes/{scopeId}/chat-route-policy/rules/{ruleId}
    /// </summary>
    private static async Task<IResult> HandleRemoveRuleAsync(
        HttpContext http,
        string scopeId,
        string ruleId,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorDispatchPort actorDispatchPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        if (string.IsNullOrWhiteSpace(ruleId))
            return JsonError(StatusCodes.Status400BadRequest, "rule_id_required", "rule_id path segment is required.");

        var command = new RemoveChatRouteRuleRequested { RuleId = ruleId.Trim() };
        var (actorId, commandId) = await DispatchAsync(command, scopeId, actorRuntime, actorDispatchPort, ct);
        return Results.Accepted(value: new
        {
            actor_id = actorId,
            command_id = commandId,
            note = "Rule removal dispatched. Re-query GET to observe materialized state.",
        });
    }

    /// <summary>
    /// GET /api/scopes/{scopeId}/chat-route-policy
    ///
    /// Returns the materialized current state from the readmodel as
    /// protobuf-JSON, or 404 when no policy has been upserted yet for this
    /// caller scope.
    /// </summary>
    private static async Task<IResult> HandleGetAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IChatRoutePolicyQueryPort queryPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var snapshot = await queryPort.LookupForCallerAsync(
            OwnerScope.ForNyxIdNative(scopeId),
            ct);
        if (snapshot is null)
            return JsonError(StatusCodes.Status404NotFound, "policy_not_found",
                $"No chat route policy materialized for scope '{scopeId}'. PUT one to create.");

        // Re-pack the snapshot into a transport-friendly proto so the client
        // sees the same shape it would PUT (default_target + rules) without
        // the readmodel envelope fields (state_version, last_event_id).
        var view = new UpsertChatRoutePolicyRequested
        {
            OwnerScope = new ChatRouteCallerScope
            {
                NyxUserId = scopeId,
                Platform = OwnerScope.NyxIdPlatform,
            },
            DefaultTarget = snapshot.DefaultTarget.Clone(),
        };
        foreach (var rule in snapshot.Rules)
            view.Rules.Add(rule.Clone());

        return Results.Content(ResponseFormatter.Format(view), "application/json");
    }

    private static async Task<(string ActorId, string CommandId)> DispatchAsync(
        IMessage command,
        string scopeId,
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        CancellationToken ct)
    {
        var actorId = $"{ProjectionKindActorIdPrefix}{scopeId}";
        var actor = await actorRuntime.CreateAsync<ChatRoutePolicyGAgent>(actorId, ct);
        var commandId = Guid.NewGuid().ToString("N");
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect("chat-route-policy-admin", actor.Id),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = commandId,
            },
            Runtime = new EnvelopeRuntime
            {
                Deduplication = new DeliveryDeduplication
                {
                    OperationId = commandId,
                },
            },
        };
        await actorDispatchPort.DispatchAsync(actor.Id, envelope, ct);
        return (actor.Id, commandId);
    }

    private static IResult JsonError(int status, string error, string detail) =>
        Results.Json(new { error, detail }, statusCode: status);
}
