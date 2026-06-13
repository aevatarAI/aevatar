using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Capabilities;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Voice;

internal static class VoicePresenceCapabilityAdminEndpoints
{
    private static readonly JsonParser BodyParser = new(
        JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    public static IEndpointRouteBuilder MapVoicePresenceCapabilityAdminEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(
                "/api/scopes/{scopeId}/gagent-actors/{actorId}/voice-presence/enable",
                HandleEnableAsync)
            .WithTags("VoicePresence");

        return app;
    }

    private static async Task<IResult> HandleEnableAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        [FromQuery(Name = "agentKind")] string? agentKind,
        [FromServices] IVoicePresenceCapabilityCommandPort commandPort,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] IEnumerable<VoicePresenceModuleRegistration> moduleRegistrations,
        [FromServices] IAgentKindRegistry agentKindRegistry,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var normalizedScopeId = NormalizeOptional(scopeId);
        if (normalizedScopeId is null)
            return JsonError(StatusCodes.Status400BadRequest, "invalid_input", "scopeId is required.");

        var normalizedActorId = NormalizeOptional(actorId);
        if (normalizedActorId is null)
            return JsonError(StatusCodes.Status400BadRequest, "invalid_input", "actorId is required.");

        var normalizedAgentKind = NormalizeOptional(agentKind);
        if (normalizedAgentKind is null)
            return JsonError(StatusCodes.Status400BadRequest, "invalid_input", "agentKind query parameter is required.");

        if (!agentKindRegistry.TryResolve(normalizedAgentKind, out _))
            return JsonError(StatusCodes.Status400BadRequest, "invalid_input", $"Unknown agentKind '{normalizedAgentKind}'.");

        VoicePresenceEnableRequested command;
        try
        {
            using var reader = new StreamReader(http.Request.Body);
            var bodyJson = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(bodyJson))
                return JsonError(StatusCodes.Status400BadRequest, "empty_body", "Request body is required.");

            command = BodyParser.Parse<VoicePresenceEnableRequested>(bodyJson);
        }
        catch (InvalidProtocolBufferException ex)
        {
            return JsonError(StatusCodes.Status400BadRequest, "invalid_body",
                $"Could not parse request body as VoicePresenceEnableRequested: {ex.Message}");
        }
        catch (InvalidJsonException ex)
        {
            return JsonError(StatusCodes.Status400BadRequest, "invalid_body",
                $"Could not parse request body as VoicePresenceEnableRequested: {ex.Message}");
        }

        var normalizedModuleName = NormalizeOptional(command.ModuleName);
        if (normalizedModuleName is null)
            return JsonError(StatusCodes.Status400BadRequest, "module_name_required", "module_name is required.");

        var registrations = moduleRegistrations.ToArray();
        if (registrations.Length == 0)
            return JsonError(StatusCodes.Status503ServiceUnavailable, "voice_not_configured", "Voice provider is not configured.");

        if (!TryResolveModule(registrations, normalizedModuleName))
            return JsonError(StatusCodes.Status404NotFound, "unknown_module", $"Voice module '{normalizedModuleName}' is not registered.");

        command.ModuleName = normalizedModuleName;
        command.VoiceSessionDefaults ??= new VoiceSessionDefaults();

        var remoteSupport = NormalizeRemoteAudioSupport(command.RemoteAudioSupport);
        if (remoteSupport is VoiceRemoteAudioSupport.LocalOnly or VoiceRemoteAudioSupport.Unavailable)
        {
            return JsonError(
                StatusCodes.Status400BadRequest,
                "invalid_input",
                $"Remote audio support '{remoteSupport}' cannot be enabled through the mainnet voice presence admin endpoint.");
        }
        command.RemoteAudioSupport = remoteSupport;

        ScopeResourceAdmissionResult admission;
        try
        {
            admission = await admissionPort.AuthorizeTargetAsync(
                new ScopeResourceTarget(
                    normalizedScopeId,
                    ScopeResourceKind.GAgentActor,
                    normalizedAgentKind,
                    normalizedActorId,
                    ScopeResourceOperation.Use),
                ct);
        }
        catch (Exception ex)
        {
            return JsonError(StatusCodes.Status503ServiceUnavailable, "admission_unavailable", ex.Message);
        }

        var admissionError = MapAdmissionError(admission.Status);
        if (admissionError is not null)
            return admissionError;

        VoicePresenceCapabilityAcceptedReceipt receipt;
        try
        {
            receipt = await commandPort.EnableAsync(normalizedActorId, command, ct);
        }
        catch (VoicePresenceCapabilityCommandException ex)
            when (ex.Error == VoicePresenceCapabilityCommandError.InvalidInput)
        {
            return JsonError(StatusCodes.Status400BadRequest, "invalid_input", ex.Message);
        }
        catch (VoicePresenceCapabilityCommandException ex)
            when (ex.Error == VoicePresenceCapabilityCommandError.ActorNotFound)
        {
            return JsonError(StatusCodes.Status404NotFound, "actor_not_found", ex.Message);
        }
        catch (VoicePresenceCapabilityCommandException ex)
            when (ex.Error == VoicePresenceCapabilityCommandError.DispatchFailed)
        {
            return JsonError(StatusCodes.Status503ServiceUnavailable, "command_dispatch_failed", ex.Message);
        }

        return Results.Accepted(value: new
        {
            scope_id = normalizedScopeId,
            actor_id = receipt.ActorId,
            agent_kind = normalizedAgentKind,
            module_name = receipt.ModuleName,
            command_id = receipt.CommandId,
            correlation_id = receipt.CorrelationId,
            stage = receipt.Stage,
            note = "Voice presence enable command accepted for dispatch. Re-attach after the capability read model observes the committed state.",
        });
    }

    private static IResult? MapAdmissionError(ScopeResourceAdmissionStatus status) =>
        status switch
        {
            ScopeResourceAdmissionStatus.Allowed => null,
            ScopeResourceAdmissionStatus.NotFound => JsonError(
                StatusCodes.Status404NotFound,
                "actor_not_found",
                "Actor is not registered in the requested scope."),
            ScopeResourceAdmissionStatus.Denied or ScopeResourceAdmissionStatus.ScopeMismatch => JsonError(
                StatusCodes.Status403Forbidden,
                "admission_denied",
                "Scope admission denied voice presence enable access."),
            ScopeResourceAdmissionStatus.Unavailable => JsonError(
                StatusCodes.Status503ServiceUnavailable,
                "admission_unavailable",
                "Scope admission is unavailable."),
            _ => JsonError(
                StatusCodes.Status503ServiceUnavailable,
                "admission_unavailable",
                "Scope admission failed."),
        };

    private static bool TryResolveModule(
        IEnumerable<VoicePresenceModuleRegistration> registrations,
        string moduleName)
    {
        foreach (var candidate in registrations)
        {
            if (candidate.Names.Any(name => string.Equals(name, moduleName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static VoiceRemoteAudioSupport NormalizeRemoteAudioSupport(VoiceRemoteAudioSupport remoteAudioSupport) =>
        remoteAudioSupport == VoiceRemoteAudioSupport.Unspecified
            ? VoiceRemoteAudioSupport.Supported
            : remoteAudioSupport;

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static IResult JsonError(int status, string error, string detail) =>
        Results.Json(new { error, detail }, statusCode: status);
}
