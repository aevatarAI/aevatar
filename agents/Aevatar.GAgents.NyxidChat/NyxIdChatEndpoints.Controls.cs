using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Capabilities;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.GAgents.NyxidChat;

public static partial class NyxIdChatEndpoints
{
    private const int MaxControlIdentityLength = 256;
    private const int MaxSteeringInstructionLength = 32_768;

    private static void MapControlEndpoints(RouteGroupBuilder group)
    {
        group.MapPost(
            "/{scopeId}/nyxid-chat/conversations/{actorId}:stop",
            HandleStopControlAsync);
        group.MapPost(
            "/{scopeId}/nyxid-chat/conversations/{actorId}:steer",
            HandleSteeringControlAsync);
        group.MapPost(
            "/{scopeId}/nyxid-chat/conversations/{actorId}/turns/{turnId}/steps/{stepId}:retry",
            HandleRetryControlAsync);
        group.MapPost(
            "/{scopeId}/nyxid-chat/conversations/{actorId}/turns/{turnId}/steps/{stepId}:skip",
            HandleSkipControlAsync);
    }

    private static async Task<IResult> HandleStopControlAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        NyxIdChatStopRequest request,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] INyxIdChatControlCommandPort commandPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (!TryValidateControlIdentity(scopeId, out var normalizedScopeId) ||
            !TryValidateControlIdentity(actorId, out var normalizedActorId) ||
            !TryValidateControlIdentity(request.TurnId, out var turnId) ||
            !TryValidateControlIdentity(request.StopRequestId, out var requestId) ||
            !TryValidateControlIdentity(request.ClientRequestId, out var clientRequestId) ||
            request.ExpectedStateVersion < 0)
        {
            return InvalidControlRequest();
        }

        var admissionError = await AuthorizeConversationAsync(
            admissionPort,
            normalizedScopeId,
            normalizedActorId,
            ScopeResourceOperation.Control,
            ct).ConfigureAwait(false);
        if (admissionError is not null)
            return admissionError;

        var (commandId, correlationId) = CreateControlTraceIdentity();
        var receipt = await commandPort.DispatchStopAsync(new NyxIdChatStopCommand
        {
            ScopeId = normalizedScopeId,
            ConversationActorId = normalizedActorId,
            TurnId = turnId,
            StopRequestId = requestId,
            ClientRequestId = clientRequestId,
            CommandId = commandId,
            CorrelationId = correlationId,
            ExpectedStateVersion = request.ExpectedStateVersion,
        }, ct).ConfigureAwait(false);
        return AcceptedControl(normalizedScopeId, normalizedActorId, receipt);
    }

    private static async Task<IResult> HandleSteeringControlAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        NyxIdChatSteeringRequest request,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] INyxIdChatControlCommandPort commandPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (!TryValidateControlIdentity(scopeId, out var normalizedScopeId) ||
            !TryValidateControlIdentity(actorId, out var normalizedActorId) ||
            !TryValidateControlIdentity(request.TurnId, out var turnId) ||
            !TryValidateControlIdentity(request.SteeringId, out var requestId) ||
            !TryValidateControlIdentity(request.ClientRequestId, out var clientRequestId) ||
            string.IsNullOrWhiteSpace(request.Instruction) ||
            request.Instruction.Length > MaxSteeringInstructionLength ||
            request.ExpectedStateVersion < 0 ||
            !HasValidControlInputParts(request.InputParts))
        {
            return InvalidControlRequest();
        }

        var accessToken = ExtractNyxIdAccessToken(http);
        if (string.IsNullOrWhiteSpace(accessToken))
            return Results.Unauthorized();
        var admissionError = await AuthorizeConversationAsync(
            admissionPort,
            normalizedScopeId,
            normalizedActorId,
            ScopeResourceOperation.Control,
            ct).ConfigureAwait(false);
        if (admissionError is not null)
            return admissionError;

        var (commandId, correlationId) = CreateControlTraceIdentity();
        var control = await BuildLlmControlAsync(http, accessToken, ct).ConfigureAwait(false);
        var command = new NyxIdChatSteeringCommand
        {
            ScopeId = normalizedScopeId,
            ConversationActorId = normalizedActorId,
            TurnId = turnId,
            SteeringId = requestId,
            ClientRequestId = clientRequestId,
            CommandId = commandId,
            CorrelationId = correlationId,
            Instruction = request.Instruction.Trim(),
            ExpectedStateVersion = request.ExpectedStateVersion,
            LlmControl = control.ToPayload(),
            ToolContext = BuildControlToolContext(
                normalizedScopeId,
                turnId,
                requestId,
                accessToken,
                control),
        };
        command.InputParts.AddRange(request.InputParts?.Select(static part => part.ToProto()) ?? []);
        var receipt = await commandPort.DispatchSteeringAsync(command, ct).ConfigureAwait(false);
        return AcceptedControl(normalizedScopeId, normalizedActorId, receipt);
    }

    private static async Task<IResult> HandleRetryControlAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        string turnId,
        string stepId,
        NyxIdChatRetryStepRequest request,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] INyxIdChatControlCommandPort commandPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (!TryValidateStepControlRequest(
                scopeId,
                actorId,
                turnId,
                stepId,
                request.TaskId,
                request.RetryRequestId,
                request.ClientRequestId,
                request.ExpectedOperationGeneration,
                request.ExpectedStateVersion,
                out var identity))
        {
            return InvalidControlRequest();
        }

        var accessToken = ExtractNyxIdAccessToken(http);
        if (string.IsNullOrWhiteSpace(accessToken))
            return Results.Unauthorized();
        var admissionError = await AuthorizeConversationAsync(
            admissionPort,
            identity.ScopeId,
            identity.ActorId,
            ScopeResourceOperation.Control,
            ct).ConfigureAwait(false);
        if (admissionError is not null)
            return admissionError;

        var (commandId, correlationId) = CreateControlTraceIdentity();
        var control = await BuildLlmControlAsync(http, accessToken, ct).ConfigureAwait(false);
        var receipt = await commandPort.DispatchRetryAsync(new NyxIdChatRetryStepCommand
        {
            ScopeId = identity.ScopeId,
            ConversationActorId = identity.ActorId,
            TurnId = identity.TurnId,
            TaskId = identity.TaskId,
            StepId = identity.StepId,
            RetryRequestId = identity.RequestId,
            ClientRequestId = identity.ClientRequestId,
            CommandId = commandId,
            CorrelationId = correlationId,
            ExpectedOperationGeneration = request.ExpectedOperationGeneration,
            ExpectedStateVersion = request.ExpectedStateVersion,
            LlmControl = control.ToPayload(),
            ToolContext = BuildControlToolContext(
                identity.ScopeId,
                identity.TurnId,
                identity.RequestId,
                accessToken,
                control),
        }, ct).ConfigureAwait(false);
        return AcceptedControl(identity.ScopeId, identity.ActorId, receipt);
    }

    private static async Task<IResult> HandleSkipControlAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        string turnId,
        string stepId,
        NyxIdChatSkipStepRequest request,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] INyxIdChatControlCommandPort commandPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;
        if (!TryValidateStepControlRequest(
                scopeId,
                actorId,
                turnId,
                stepId,
                request.TaskId,
                request.SkipRequestId,
                request.ClientRequestId,
                request.ExpectedOperationGeneration,
                request.ExpectedStateVersion,
                out var identity))
        {
            return InvalidControlRequest();
        }

        var admissionError = await AuthorizeConversationAsync(
            admissionPort,
            identity.ScopeId,
            identity.ActorId,
            ScopeResourceOperation.Control,
            ct).ConfigureAwait(false);
        if (admissionError is not null)
            return admissionError;

        var (commandId, correlationId) = CreateControlTraceIdentity();
        var receipt = await commandPort.DispatchSkipAsync(new NyxIdChatSkipStepCommand
        {
            ScopeId = identity.ScopeId,
            ConversationActorId = identity.ActorId,
            TurnId = identity.TurnId,
            TaskId = identity.TaskId,
            StepId = identity.StepId,
            SkipRequestId = identity.RequestId,
            ClientRequestId = identity.ClientRequestId,
            CommandId = commandId,
            CorrelationId = correlationId,
            ExpectedOperationGeneration = request.ExpectedOperationGeneration,
            ExpectedStateVersion = request.ExpectedStateVersion,
        }, ct).ConfigureAwait(false);
        return AcceptedControl(identity.ScopeId, identity.ActorId, receipt);
    }

    private static AgentToolExecutionContextPayload BuildControlToolContext(
        string scopeId,
        string turnId,
        string requestId,
        string accessToken,
        LLMControlContext control)
    {
        var context = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(requestId, null),
            Credentials = new AgentToolCredentials(accessToken, null, null),
            Caller = new AgentToolCallerContext(scopeId, scopeId, turnId),
            Channel = new AgentToolChannelContext("nyxid-chat", null, scopeId, null, null),
        };
        return control.ToToolContext(context).ToPayload();
    }

    private static IResult AcceptedControl(
        string scopeId,
        string actorId,
        NyxIdChatControlAcceptedReceipt receipt)
    {
        var stateUrl = BuildControlStateUrl(scopeId, actorId);
        return Results.Accepted(stateUrl, new
        {
            status = "accepted",
            requestId = receipt.RequestId,
            commandId = receipt.CommandId,
            correlationId = receipt.CorrelationId,
            stateUrl,
        });
    }

    private static string BuildControlStateUrl(string scopeId, string actorId) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId)}/nyxid-chat/conversations/" +
        $"{Uri.EscapeDataString(actorId)}/state";

    private static (string CommandId, string CorrelationId) CreateControlTraceIdentity()
    {
        var commandId = Guid.NewGuid().ToString("N");
        return (commandId, commandId);
    }

    private static bool TryValidateStepControlRequest(
        string? scopeId,
        string? actorId,
        string? turnId,
        string? stepId,
        string? taskId,
        string? requestId,
        string? clientRequestId,
        long expectedOperationGeneration,
        long expectedStateVersion,
        out StepControlIdentity identity)
    {
        identity = default;
        if (!TryValidateControlIdentity(scopeId, out var normalizedScopeId) ||
            !TryValidateControlIdentity(actorId, out var normalizedActorId) ||
            !TryValidateControlIdentity(turnId, out var normalizedTurnId) ||
            !TryValidateControlIdentity(stepId, out var normalizedStepId) ||
            !TryValidateControlIdentity(taskId, out var normalizedTaskId) ||
            !TryValidateControlIdentity(requestId, out var normalizedRequestId) ||
            !TryValidateControlIdentity(clientRequestId, out var normalizedClientRequestId) ||
            expectedOperationGeneration <= 0 ||
            expectedStateVersion < 0)
        {
            return false;
        }

        identity = new StepControlIdentity(
            normalizedScopeId,
            normalizedActorId,
            normalizedTurnId,
            normalizedTaskId,
            normalizedStepId,
            normalizedRequestId,
            normalizedClientRequestId);
        return true;
    }

    private static bool TryValidateControlIdentity(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > MaxControlIdentityLength)
            return false;

        return normalized.All(static character =>
            !char.IsControl(character) &&
            !char.IsWhiteSpace(character) &&
            character is not '/' and not '\\' and not '?' and not '#');
    }

    private static bool HasValidControlInputParts(IReadOnlyList<ContentPartDto>? parts) =>
        parts is null || parts.All(static part =>
            part is not null &&
            part.ToProto().Kind != Aevatar.AI.Abstractions.ChatContentPartKind.Unspecified);

    private static IResult InvalidControlRequest() =>
        Results.Json(
            new
            {
                code = "NYXID_CHAT_CONTROL_REQUEST_INVALID",
                message = "The control request contains an invalid or missing identity or value.",
            },
            statusCode: StatusCodes.Status400BadRequest);

    private readonly record struct StepControlIdentity(
        string ScopeId,
        string ActorId,
        string TurnId,
        string TaskId,
        string StepId,
        string RequestId,
        string ClientRequestId);

    public sealed record NyxIdChatStopRequest(
        string? TurnId,
        string? StopRequestId,
        string? ClientRequestId,
        long ExpectedStateVersion = 0);

    public sealed record NyxIdChatSteeringRequest(
        string? TurnId,
        string? SteeringId,
        string? ClientRequestId,
        string? Instruction,
        IReadOnlyList<ContentPartDto>? InputParts = null,
        long ExpectedStateVersion = 0);

    public sealed record NyxIdChatRetryStepRequest(
        string? TaskId,
        string? RetryRequestId,
        string? ClientRequestId,
        long ExpectedOperationGeneration,
        long ExpectedStateVersion = 0);

    public sealed record NyxIdChatSkipStepRequest(
        string? TaskId,
        string? SkipRequestId,
        string? ClientRequestId,
        long ExpectedOperationGeneration,
        long ExpectedStateVersion = 0);
}
