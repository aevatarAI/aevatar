using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Presentation.AGUI;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.Responses;

public sealed class ResponsesForwardingApplicationService(
    ITeamEntryMemberResolver teamEntryMemberResolver,
    IMemberPublishedServiceResolver memberPublishedServiceResolver,
    IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
    ResponsesForwardedCompletionRecorder completionRecorder,
    ILogger<ResponsesForwardingApplicationService> logger) : IResponsesForwardingApplicationService
{
    private const string DefaultGAgentChatEndpointId = "chat";
    private const string RegistrationScopeMetadataKey = "scope_id";

    public async Task<ResponsesForwardingResult> ForwardAsync(
        ResponsesForwardCommandResult plan,
        string bearerToken,
        Func<AGUIEvent, CancellationToken, ValueTask>? onEventAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var target = await BuildInvocationRequestAsync(plan, bearerToken, ct);
        if (target.Error is not null)
        {
            await TryCommitFailureAsync(plan, target.Error.Code, target.Error.Message, CancellationToken.None);
            return new ResponsesForwardingResult(target.Error, null);
        }

        var collector = completionRecorder.CreateCollector(plan);
        try
        {
            var result = await staticGAgentStreamInvocationPort.InvokeAsync(
                target.Request!,
                async (evt, token) =>
                {
                    await collector.ObserveAsync(evt, token);
                    if (onEventAsync != null)
                        await onEventAsync(evt, token);
                },
                onAcceptedAsync: null,
                ct);

            if (!result.Succeeded)
            {
                var code = result.StartError.ToString().ToLowerInvariant();
                const string message = "GAgent invocation could not be started.";
                await CommitFailureAsync(plan, code, message, CancellationToken.None);
                return ResponsesForwardingResult.FromError(502, code, message);
            }

            if (result.CompletionStatus == GAgentDraftRunCompletionStatus.Failed &&
                !collector.HasFailureEvent)
            {
                var failure = await collector.CommitFailureAndReadAsync(
                    "gagent_invocation_failed",
                    "GAgent invocation failed.",
                    CancellationToken.None);
                return failure.Error is not null
                    ? new ResponsesForwardingResult(failure.Error, null)
                    : ResponsesForwardingResult.FromSnapshot(failure.Snapshot!);
            }

            var completion = await collector.CommitAndReadAsync(ct);
            return completion.Error is not null
                ? new ResponsesForwardingResult(completion.Error, null)
                : ResponsesForwardingResult.FromSnapshot(completion.Snapshot!);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryCommitFailureAsync(plan, "request_timeout", "Request timed out.", CancellationToken.None);
            return ResponsesForwardingResult.FromError(408, "request_timeout", "Request timed out.");
        }
        catch (InvalidOperationException ex) when (IsServiceNotFoundException(ex))
        {
            logger.LogWarning(
                ex,
                "AGUI-backed invocation resolved to unknown service for response {ResponseId}",
                plan.Normalized.ResponseId);
            var failure = await CommitFailureAsync(
                plan,
                "gagent_target_not_found",
                ex.Message,
                CancellationToken.None);
            return ResponsesForwardingResult.FromError(404, "gagent_target_not_found", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AGUI-backed invocation failed for response {ResponseId}", plan.Normalized.ResponseId);
            var failure = await CommitFailureAsync(
                plan,
                "gagent_invocation_failed",
                "GAgent invocation failed.",
                CancellationToken.None);
            return ResponsesForwardingResult.FromError(
                500,
                "gagent_invocation_failed",
                "GAgent invocation failed.");
        }
    }

    private async Task<InvocationRequestResult> BuildInvocationRequestAsync(
        ResponsesForwardCommandResult plan,
        string bearerToken,
        CancellationToken ct)
    {
        if (plan.Action.ForwardToTeam is not null)
            return await BuildForwardToTeamRequestAsync(plan, bearerToken, plan.Action.ForwardToTeam, ct);

        if (plan.Action.ForwardToGagent is not null)
            return await BuildForwardToGAgentRequestAsync(plan, bearerToken, plan.Action.ForwardToGagent, ct);

        return InvocationRequestResult.FromError(500, "chat_route_invalid", "Forward decision has no forwarding target.");
    }

    private async Task<InvocationRequestResult> BuildForwardToTeamRequestAsync(
        ResponsesForwardCommandResult plan,
        string bearerToken,
        ForwardToTeam forwardToTeam,
        CancellationToken ct)
    {
        var teamId = forwardToTeam.TeamId?.Trim() ?? string.Empty;
        var endpointId = forwardToTeam.EndpointId?.Trim() ?? string.Empty;
        if (teamId.Length == 0)
            return InvocationRequestResult.FromError(500, "chat_route_invalid", "ForwardToTeam decision missing team_id.");
        if (endpointId.Length == 0)
            return InvocationRequestResult.FromError(500, "chat_route_invalid", "ForwardToTeam decision missing endpoint_id.");

        TeamEntryMemberResolution resolution;
        try
        {
            resolution = await teamEntryMemberResolver.ResolveAsync(plan.CallerScope.ScopeId, teamId, ct);
        }
        catch (TeamEntryMemberResolutionException ex)
        {
            return InvocationRequestResult.FromError(ResolveTeamEntryHttpStatusCode(ex.Code), ex.Code, ex.Message);
        }

        return InvocationRequestResult.FromRequest(BuildInvocationRequest(
            plan,
            bearerToken,
            resolution.ScopeId,
            resolution.PublishedServiceId,
            endpointId));
    }

    private async Task<InvocationRequestResult> BuildForwardToGAgentRequestAsync(
        ResponsesForwardCommandResult plan,
        string bearerToken,
        ForwardToGAgent forwardToGAgent,
        CancellationToken ct)
    {
        var memberId = forwardToGAgent.ActorId?.Trim() ?? string.Empty;
        if (memberId.Length == 0)
            return InvocationRequestResult.FromError(500, "chat_route_invalid", "ForwardToGAgent decision missing actor_id.");

        MemberPublishedServiceResolution resolution;
        try
        {
            resolution = await memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(plan.CallerScope.ScopeId, memberId),
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return InvocationRequestResult.FromError(400, "chat_route_invalid", ex.Message);
        }

        return InvocationRequestResult.FromRequest(BuildInvocationRequest(
            plan,
            bearerToken,
            resolution.ScopeId,
            resolution.PublishedServiceId,
            DefaultGAgentChatEndpointId));
    }

    private static StaticGAgentStreamInvocationRequest BuildInvocationRequest(
        ResponsesForwardCommandResult plan,
        string bearerToken,
        string scopeId,
        string publishedServiceId,
        string endpointId)
    {
        var identity = new ServiceIdentity
        {
            TenantId = scopeId,
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = publishedServiceId,
        };
        var input = new StaticGAgentStreamInvocationInput(
            Prompt: plan.Normalized.Prompt ?? string.Empty,
            SessionId: plan.Normalized.ResponseId,
            Headers: BuildStaticGAgentInvocationHeaders(plan, bearerToken));
        return new StaticGAgentStreamInvocationRequest(identity, endpointId, input);
    }

    private static Dictionary<string, string> BuildStaticGAgentInvocationHeaders(
        ResponsesForwardCommandResult plan,
        string bearerToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.RequestId] = plan.Normalized.ResponseId,
            [RegistrationScopeMetadataKey] = plan.CallerScope.ScopeId,
        };

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            headers[LLMRequestMetadataKeys.NyxIdAccessToken] = bearerToken;
            headers[ConnectorRequest.HttpAuthorizationMetadataKey] = $"Bearer {bearerToken}";
        }

        return headers;
    }

    private async Task<ResponsesForwardingResult?> CommitFailureAsync(
        ResponsesForwardCommandResult plan,
        string code,
        string message,
        CancellationToken ct)
    {
        var committed = await completionRecorder.CommitAndReadAsync(
            plan,
            ResponsesForwardedCompletionRecorder.BuildFailureCompletion(code, message, DateTimeOffset.UtcNow),
            ct);
        return committed.Error is not null
            ? new ResponsesForwardingResult(committed.Error, null)
            : ResponsesForwardingResult.FromSnapshot(committed.Snapshot!);
    }

    private async Task TryCommitFailureAsync(
        ResponsesForwardCommandResult plan,
        string code,
        string message,
        CancellationToken ct)
    {
        try
        {
            await completionRecorder.CommitAndReadAsync(
                plan,
                ResponsesForwardedCompletionRecorder.BuildFailureCompletion(code, message, DateTimeOffset.UtcNow),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to commit forwarding failure fact for response {ResponseId}", plan.Normalized.ResponseId);
        }
    }

    private static int ResolveTeamEntryHttpStatusCode(string code) =>
        code switch
        {
            TeamEntryMemberErrorCodes.TeamNotFound => 404,
            TeamEntryMemberErrorCodes.EntryMemberNotFound => 404,
            TeamEntryMemberErrorCodes.TeamArchived => 409,
            TeamEntryMemberErrorCodes.EntryMemberNotConfigured => 409,
            TeamEntryMemberErrorCodes.EntryMemberMismatch => 409,
            TeamEntryMemberErrorCodes.EntryMemberNotReady => 503,
            _ => 400,
        };

    private static bool IsServiceNotFoundException(InvalidOperationException ex) =>
        ex.Message.StartsWith("Service '", StringComparison.Ordinal) &&
        ex.Message.Contains("was not found", StringComparison.Ordinal);

    private sealed record InvocationRequestResult(
        StaticGAgentStreamInvocationRequest? Request,
        ResponsesCommandError? Error)
    {
        public static InvocationRequestResult FromRequest(StaticGAgentStreamInvocationRequest request) =>
            new(request, null);

        public static InvocationRequestResult FromError(int statusCode, string code, string message) =>
            new(null, new ResponsesCommandError(statusCode, code, message));
    }
}
