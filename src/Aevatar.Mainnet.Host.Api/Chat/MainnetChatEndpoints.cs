using System.Text.Json;
using Aevatar.Capabilities;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Aevatar.Mainnet.Host.Api.Chat;

internal enum MainnetChatRequestKind
{
    ExternalWorkflowCompatibility,
    Assistant,
    Unsupported,
}

internal sealed record MainnetChatRequestClassification(
    MainnetChatRequestKind Kind,
    JsonElement? Body = null);

public static class MainnetChatEndpoints
{
    private static readonly HashSet<string> AssistantTypes = new(StringComparer.Ordinal)
    {
        "text",
        "action.continue",
        "approval.resolve",
        "input.resolve",
        "task.stop",
        "task.steer",
        "step.retry",
        "step.skip",
    };

    public static IEndpointRouteBuilder MapMainnetChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat", HandlePostAsync)
            .WithTags("Chat")
            .WithName("StartMainnetChat");
        return app;
    }

    internal static async Task<MainnetChatRequestClassification> ClassifyRequestAsync(
        HttpRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ExternalWorkflowChatCompatibilityAdapter.AcceptsForm(request))
            return new MainnetChatRequestClassification(MainnetChatRequestKind.ExternalWorkflowCompatibility);
        if (!IsJson(request.ContentType))
            return new MainnetChatRequestClassification(MainnetChatRequestKind.Unsupported);

        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new MainnetChatRequestClassification(MainnetChatRequestKind.Unsupported);

            var body = document.RootElement.Clone();
            if (ExternalWorkflowChatCompatibilityAdapter.AcceptsJson(body))
                return new MainnetChatRequestClassification(MainnetChatRequestKind.ExternalWorkflowCompatibility, body);
            if (!body.TryGetProperty("type", out var type))
                return new MainnetChatRequestClassification(MainnetChatRequestKind.Unsupported);

            if (type.ValueKind != JsonValueKind.String ||
                !AssistantTypes.Contains(type.GetString()?.Trim() ?? string.Empty))
            {
                return new MainnetChatRequestClassification(MainnetChatRequestKind.Unsupported);
            }

            return new MainnetChatRequestClassification(MainnetChatRequestKind.Assistant, body);
        }
        catch (JsonException)
        {
            return new MainnetChatRequestClassification(MainnetChatRequestKind.Unsupported);
        }
        finally
        {
            if (request.Body.CanSeek)
                request.Body.Position = 0;
        }
    }

    private static async Task HandlePostAsync(HttpContext http, CancellationToken ct)
    {
        var classification = await ClassifyRequestAsync(http.Request, ct);
        switch (classification.Kind)
        {
            case MainnetChatRequestKind.ExternalWorkflowCompatibility:
                await ExternalWorkflowChatCompatibilityAdapter.HandleAsync(http, classification.Body, ct);
                return;
            case MainnetChatRequestKind.Assistant:
                if (await TryHandleWorkflowSignalContinuationAsync(http, classification.Body!.Value, ct)
                        .ConfigureAwait(false))
                {
                    return;
                }

                await NyxIdChatEndpoints.HandlePublicChatAsync(http, classification.Body!.Value, ct);
                return;
            default:
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new
                {
                    code = "INVALID_CHAT_INPUT",
                    message = "The chat request body or Assistant type is invalid.",
                }, cancellationToken: ct);
                return;
        }
    }

    internal static async Task<bool> TryHandleWorkflowSignalContinuationAsync(
        HttpContext http,
        JsonElement body,
        CancellationToken ct)
    {
        if (!body.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString()?.Trim(), "text", StringComparison.Ordinal) ||
            !body.TryGetProperty("conversationId", out var conversationIdElement) ||
            conversationIdElement.ValueKind != JsonValueKind.String ||
            !body.TryGetProperty("prompt", out var promptElement) ||
            promptElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var conversationId = Normalize(conversationIdElement.GetString());
        var prompt = Normalize(promptElement.GetString());
        if (conversationId is null || prompt is null ||
            !AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
        {
            return false;
        }

        var recoveryReadPort = http.RequestServices.GetService<IWorkflowChatHistoryCreateRecoveryReadPort>();
        var currentStateQueryPort = http.RequestServices.GetService<IWorkflowExecutionCurrentStateQueryPort>();
        var signalDispatchService = http.RequestServices
            .GetService<ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        if (recoveryReadPort is null || currentStateQueryPort is null || signalDispatchService is null)
            return false;

        try
        {
            var recovery = await recoveryReadPort
                .GetByConversationAsync(scopeId, conversationId, ct)
                .ConfigureAwait(false);
            if (recovery is null || string.IsNullOrWhiteSpace(recovery.WorkflowActorId))
                return false;

            var actorId = recovery.WorkflowActorId.Trim();
            var snapshot = await currentStateQueryPort
                .GetWorkflowActorCurrentStateAsync(actorId, ct)
                .ConfigureAwait(false);
            if (!IsWaitingForSignal(snapshot, scopeId, out var runId, out var stepId, out var signalName))
                return false;

            var clientRequestId = TryGetString(body, "clientRequestId") ?? Guid.NewGuid().ToString("N");
            var dispatch = await signalDispatchService.DispatchAsync(
                    new WorkflowSignalCommand(
                        actorId,
                        runId,
                        signalName,
                        clientRequestId,
                        prompt,
                        stepId,
                        clientRequestId),
                    ct)
                .ConfigureAwait(false);
            if (!dispatch.Succeeded || dispatch.Receipt is null)
                return false;

            http.Response.StatusCode = StatusCodes.Status202Accepted;
            await http.Response.WriteAsJsonAsync(new
            {
                accepted = true,
                actorId = dispatch.Receipt.ActorId,
                runId = dispatch.Receipt.RunId,
                signalName,
                stepId,
                acceptedCommandId = dispatch.Receipt.CommandId,
                correlationId = dispatch.Receipt.CorrelationId,
                routed = "workflow_signal_continuation",
            }, cancellationToken: ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            http.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger("Aevatar.Mainnet.Chat.WorkflowSignalContinuation")
                .LogWarning(
                    ex,
                    "Workflow signal continuation pre-routing failed: scopeId={ScopeId} conversationId={ConversationId}",
                    scopeId,
                    conversationId);
            return false;
        }
    }

    private static bool IsWaitingForSignal(
        WorkflowActorSnapshot? snapshot,
        string scopeId,
        out string runId,
        out string stepId,
        out string signalName)
    {
        runId = string.Empty;
        stepId = string.Empty;
        signalName = string.Empty;
        if (snapshot is null ||
            snapshot.CompletionStatus != WorkflowRunCompletionStatus.WaitingForSignal ||
            !string.Equals(snapshot.ScopeId?.Trim(), scopeId, StringComparison.Ordinal) ||
            snapshot.ActivityWaiting is null ||
            !string.Equals(snapshot.ActivityWaiting.Availability, "available", StringComparison.Ordinal) ||
            !string.Equals(snapshot.ActivityWaiting.WaitingKind, "signal", StringComparison.Ordinal))
        {
            return false;
        }

        runId = string.IsNullOrWhiteSpace(snapshot.RunId)
            ? snapshot.ActorId
            : snapshot.RunId.Trim();
        stepId = snapshot.ActivityWaiting.StepId?.Trim() ?? string.Empty;
        signalName = snapshot.ActivityWaiting.Prompt?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(runId) &&
               !string.IsNullOrWhiteSpace(stepId) &&
               !string.IsNullOrWhiteSpace(signalName);
    }

    private static string? TryGetString(JsonElement body, string propertyName) =>
        body.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? Normalize(property.GetString())
            : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsJson(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) &&
        MediaTypeHeaderValue.TryParse(contentType, out var parsed) &&
        parsed.MediaType.Value is { } mediaType &&
        (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
         mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
}
