using Aevatar.Capabilities;
using Aevatar.Foundation.VoicePresence;
using Aevatar.GAgents.NyxidChat.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Voice;

internal static class DefaultVoiceAgentEndpoints
{
    public static IEndpointRouteBuilder MapDefaultVoiceAgentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/scopes/{scopeId}/voice-agents")
            .WithTags("VoicePresence");
        group.MapPost("", HandleProvisionAsync);
        group.MapDelete("/{actorId}", HandleDeleteAsync);
        return app;
    }

    private static async Task<IResult> HandleProvisionAsync(
        HttpContext http,
        string scopeId,
        [FromServices] INyxIdVoiceAgentCommandService commandService,
        [FromServices] IEnumerable<VoicePresenceModuleRegistration> moduleRegistrations,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var normalizedScopeId = NormalizeOptional(scopeId);
        if (normalizedScopeId is null)
            return JsonError(StatusCodes.Status400BadRequest, "invalid_input", "scopeId is required.");

        var registrations = moduleRegistrations.ToArray();
        if (registrations.Length == 0)
        {
            return JsonError(
                StatusCodes.Status503ServiceUnavailable,
                "voice_not_configured",
                "Voice provider is not configured.");
        }

        if (!registrations.Any(registration => registration.Names.Any(name => string.Equals(
                name,
                NyxIdVoiceServiceDefaults.OpenAIRealtimeModuleName,
                StringComparison.OrdinalIgnoreCase))))
        {
            return JsonError(
                StatusCodes.Status503ServiceUnavailable,
                "openai_voice_not_configured",
                "OpenAI realtime voice provider is not configured.");
        }

        var result = await commandService.ProvisionAsync(
            new NyxIdVoiceAgentProvisionCommand(
                normalizedScopeId,
                NyxIdVoiceServiceDefaults.OpenAIRealtimeModuleName),
            ct);
        return result.Status switch
        {
            NyxIdVoiceAgentProvisionStatus.Accepted => Results.Accepted(value: new
            {
                scopeId = normalizedScopeId,
                actorId = result.ActorId,
                agentKind = NyxIdVoiceServiceDefaults.GAgentKind,
                moduleName = result.ModuleName,
                commandId = result.CommandId,
                correlationId = result.CorrelationId,
                stage = result.Stage,
                statusUrl = BuildCapabilityStatusUrl(
                    normalizedScopeId,
                    result.ActorId,
                    result.ModuleName),
            }),
            NyxIdVoiceAgentProvisionStatus.AdmissionUnavailable => JsonError(
                StatusCodes.Status503ServiceUnavailable,
                "voice_agent_admission_unavailable",
                "The voice agent could not become visible in the requested scope."),
            _ => JsonError(
                StatusCodes.Status503ServiceUnavailable,
                "voice_agent_provision_failed",
                "The voice agent could not be provisioned."),
        };
    }

    private static async Task<IResult> HandleDeleteAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        [FromServices] INyxIdVoiceAgentCommandService commandService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var normalizedScopeId = NormalizeOptional(scopeId);
        var normalizedActorId = NormalizeOptional(actorId);
        if (normalizedScopeId is null || normalizedActorId is null)
            return JsonError(StatusCodes.Status400BadRequest, "invalid_input", "scopeId and actorId are required.");

        var result = await commandService.DeleteAsync(
            new NyxIdVoiceAgentDeleteCommand(normalizedScopeId, normalizedActorId),
            ct);
        return result.Status switch
        {
            NyxIdVoiceAgentDeleteStatus.Deleted => Results.Ok(new
            {
                scopeId = normalizedScopeId,
                actorId = normalizedActorId,
                agentKind = NyxIdVoiceServiceDefaults.GAgentKind,
                stage = "deleted",
            }),
            NyxIdVoiceAgentDeleteStatus.NotFound => JsonError(
                StatusCodes.Status404NotFound,
                "voice_agent_not_found",
                "The voice agent is not registered in the requested scope."),
            NyxIdVoiceAgentDeleteStatus.Denied => JsonError(
                StatusCodes.Status403Forbidden,
                "admission_denied",
                "Scope admission denied voice agent deletion."),
            NyxIdVoiceAgentDeleteStatus.AdmissionUnavailable => JsonError(
                StatusCodes.Status503ServiceUnavailable,
                "voice_agent_admission_unavailable",
                "Voice agent ownership could not be verified or removed."),
            _ => JsonError(
                StatusCodes.Status500InternalServerError,
                "voice_agent_delete_failed",
                "The voice agent could not be deleted."),
        };
    }

    private static string BuildCapabilityStatusUrl(string scopeId, string actorId, string moduleName) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId)}/gagent-actors/{Uri.EscapeDataString(actorId)}" +
        $"/voice-presence?agentKind={Uri.EscapeDataString(NyxIdVoiceServiceDefaults.GAgentKind)}" +
        $"&moduleName={Uri.EscapeDataString(moduleName)}";

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static IResult JsonError(int status, string error, string detail) =>
        Results.Json(new { error, detail }, statusCode: status);
}
