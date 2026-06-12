using Aevatar.AI.Abstractions.Voice;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Hosting;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Mainnet.Host.Api.Voice;

internal static class VoicePresenceCapabilityAdminEndpoints
{
    private const string VoiceNotConfiguredReason = "voice_not_configured";
    private static readonly JsonParser BodyParser = new(
        JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    public static IEndpointRouteBuilder MapVoicePresenceCapabilityAdminEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPut(
            "/api/scopes/{scopeId}/gagent-actors/{actorId}/voice-presence/modules/{moduleName}",
            HandleEnableAsync)
            .WithTags("VoicePresence");

        return app;
    }

    private static async Task<IResult> HandleEnableAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        string moduleName,
        [FromQuery] string? agentKind,
        [FromServices] IGAgentActorRegistryQueryPort registryQueryPort,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var normalizedActorId = NormalizeRequired(actorId);
        if (normalizedActorId is null)
            return JsonError(StatusCodes.Status400BadRequest, "actor_id_required", "actorId path segment is required.");

        var normalizedModuleName = NormalizeRequired(moduleName);
        if (normalizedModuleName is null)
            return JsonError(StatusCodes.Status400BadRequest, "module_name_required", "moduleName path segment is required.");

        var normalizedAgentKind = NormalizeRequired(agentKind);
        if (normalizedAgentKind is null)
            return JsonError(StatusCodes.Status400BadRequest, "agent_kind_required", "agentKind query parameter is required.");

        var request = await ReadBodyAsync(http, ct);
        if (request.Error is not null)
            return request.Error;

        var enableRequest = request.Value ?? new VoicePresenceEnableRequested();
        var bodyModuleName = NormalizeRequired(enableRequest.ModuleName);
        if (bodyModuleName is not null &&
            !string.Equals(bodyModuleName, normalizedModuleName, StringComparison.Ordinal))
        {
            return JsonError(
                StatusCodes.Status400BadRequest,
                "module_name_mismatch",
                "Body module_name must match the moduleName path segment.");
        }

        enableRequest.ModuleName = normalizedModuleName;
        var target = await ResolveTargetAsync(
            registryQueryPort,
            scopeId,
            normalizedAgentKind,
            normalizedActorId,
            ct);
        if (target.Error is not null)
            return target.Error;

        var admission = await admissionPort.AuthorizeTargetAsync(
            new ScopeResourceTarget(
                scopeId.Trim(),
                ScopeResourceKind.GAgentActor,
                normalizedAgentKind,
                normalizedActorId,
                ScopeResourceOperation.Stream),
            ct);
        if (!admission.IsAllowed)
            return ToAdmissionError(admission.Status);

        var commandPort = http.RequestServices.GetService<IVoicePresenceCapabilityCommandPort>();
        if (commandPort is null)
        {
            return JsonError(
                StatusCodes.Status503ServiceUnavailable,
                VoiceNotConfiguredReason,
                "Voice presence is not configured for this host.");
        }

        var normalizedRequest = VoicePresenceEnableRequests.Normalize(enableRequest);
        var receipt = await commandPort.EnableAsync(normalizedActorId, normalizedRequest, ct);
        return Results.Accepted(value: new
        {
            actor_id = receipt.ActorId,
            module_name = receipt.ModuleName,
            command_id = receipt.CommandId,
            correlation_id = receipt.CorrelationId,
            note = "Voice presence enable accepted for dispatch; readmodel materialization is asynchronous.",
        });
    }

    private static async Task<BodyReadResult> ReadBodyAsync(HttpContext http, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(http.Request.Body);
            var bodyJson = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(bodyJson))
                return new BodyReadResult(new VoicePresenceEnableRequested(), null);

            return new BodyReadResult(BodyParser.Parse<VoicePresenceEnableRequested>(bodyJson), null);
        }
        catch (InvalidProtocolBufferException ex)
        {
            return new BodyReadResult(
                null,
                JsonError(
                    StatusCodes.Status400BadRequest,
                    "invalid_body",
                    $"Could not parse request body as VoicePresenceEnableRequested: {ex.Message}"));
        }
        catch (InvalidJsonException ex)
        {
            return new BodyReadResult(
                null,
                JsonError(
                    StatusCodes.Status400BadRequest,
                    "invalid_body",
                    $"Could not parse request body as VoicePresenceEnableRequested: {ex.Message}"));
        }
    }

    private static async Task<TargetResolution> ResolveTargetAsync(
        IGAgentActorRegistryQueryPort registryQueryPort,
        string scopeId,
        string agentKind,
        string actorId,
        CancellationToken ct)
    {
        GAgentActorRegistrySnapshot snapshot;
        try
        {
            snapshot = await registryQueryPort.ListActorsAsync(scopeId.Trim(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TargetResolution.Failed(JsonError(
                StatusCodes.Status503ServiceUnavailable,
                "admission_unavailable",
                "Scope actor registry is unavailable."));
        }

        var group = snapshot.Groups.FirstOrDefault(candidate =>
            string.Equals(candidate.AgentKind, agentKind, StringComparison.Ordinal));
        if (group is null || group.ActorIds.Count == 0)
        {
            return TargetResolution.Failed(JsonError(
                StatusCodes.Status404NotFound,
                "actor_not_found",
                "No target actor for the supplied agentKind is registered in this scope."));
        }

        if (!group.ActorIds.Any(candidate => string.Equals(candidate, actorId, StringComparison.Ordinal)))
        {
            return TargetResolution.Failed(JsonError(
                StatusCodes.Status404NotFound,
                "actor_not_found",
                "No target actor for the supplied actorId and agentKind is registered in this scope."));
        }

        return TargetResolution.Success();
    }

    private static IResult ToAdmissionError(ScopeResourceAdmissionStatus status) =>
        status switch
        {
            ScopeResourceAdmissionStatus.NotFound => JsonError(
                StatusCodes.Status404NotFound,
                "actor_not_found",
                "The target actor was not found for this scope."),
            ScopeResourceAdmissionStatus.Unavailable => JsonError(
                StatusCodes.Status503ServiceUnavailable,
                "admission_unavailable",
                "Scope resource admission is unavailable."),
            _ => JsonError(
                StatusCodes.Status403Forbidden,
                "actor_scope_denied",
                "The caller is not allowed to enable voice presence on this target actor."),
        };

    private static string? NormalizeRequired(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static IResult JsonError(int status, string error, string detail) =>
        Results.Json(new { error, detail }, statusCode: status);

    private sealed record BodyReadResult(VoicePresenceEnableRequested? Value, IResult? Error);

    private sealed record TargetResolution(IResult? Error)
    {
        public static TargetResolution Success() => new((IResult?)null);

        public static TargetResolution Failed(IResult error) => new(error);
    }
}
