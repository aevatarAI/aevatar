using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdRelayWorkflowCards
{
    public static async Task<IResult?> TryHandleAsync(
        HttpContext http,
        RelayMessage message,
        ILogger logger,
        CancellationToken ct)
    {
        if (!TryBuildCommand(message, out var command))
            return null;

        var dispatchService = http.RequestServices.GetService<
            ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        if (dispatchService is null)
        {
            logger.LogError(
                "Workflow resume service unavailable for relay card action: message={MessageId}",
                message.MessageId);
            return Results.Json(
                new
                {
                    error = "workflow_resume_service_unavailable",
                    message = "Workflow resume service unavailable.",
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var dispatch = await dispatchService.DispatchAsync(command!, ct);
        if (!dispatch.Succeeded || dispatch.Receipt is null)
        {
            var error = dispatch.Error;
            if (error is null)
            {
                return Results.Json(
                    new
                    {
                        error = "workflow_resume_dispatch_failed",
                        message = "Workflow control dispatch failed.",
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return error.Code switch
            {
                WorkflowRunControlStartErrorCode.InvalidActorId => Results.Json(
                    new { error = "invalid_actor_id", message = "actorId is required." },
                    statusCode: StatusCodes.Status400BadRequest),
                WorkflowRunControlStartErrorCode.InvalidRunId => Results.Json(
                    new { error = "invalid_run_id", message = "runId is required." },
                    statusCode: StatusCodes.Status400BadRequest),
                WorkflowRunControlStartErrorCode.InvalidStepId => Results.Json(
                    new { error = "invalid_step_id", message = "stepId is required." },
                    statusCode: StatusCodes.Status400BadRequest),
                WorkflowRunControlStartErrorCode.ActorNotFound => Results.Json(
                    new { error = "actor_not_found", message = $"Actor '{error.ActorId}' not found." },
                    statusCode: StatusCodes.Status404NotFound),
                WorkflowRunControlStartErrorCode.ActorNotWorkflowRun => Results.Json(
                    new
                    {
                        error = "actor_not_workflow_run",
                        message = $"Actor '{error.ActorId}' is not a workflow run actor.",
                    },
                    statusCode: StatusCodes.Status409Conflict),
                WorkflowRunControlStartErrorCode.RunBindingMissing => Results.Json(
                    new
                    {
                        error = "run_binding_missing",
                        message = $"Actor '{error.ActorId}' does not have a bound run id.",
                    },
                    statusCode: StatusCodes.Status409Conflict),
                WorkflowRunControlStartErrorCode.RunBindingMismatch => Results.Json(
                    new
                    {
                        error = "run_binding_mismatch",
                        message =
                            $"Actor '{error.ActorId}' is bound to run '{error.BoundRunId}', not '{error.RequestedRunId}'.",
                    },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(
                    new
                    {
                        error = "workflow_resume_dispatch_failed",
                        message = "Workflow control dispatch failed.",
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
            };
        }

        return Results.Accepted(value: new
        {
            status = "workflow_resume_accepted",
            message_id = message.MessageId,
            command_id = dispatch.Receipt.CommandId,
        });
    }

    public static bool TryBuildCommand(
        RelayMessage message,
        out WorkflowResumeCommand? command)
    {
        command = null;

        if (!string.Equals(
                NyxIdRelayPayloads.GetContentType(message.Content),
                "card_action",
                StringComparison.Ordinal))
        {
            return false;
        }

        var payload = NyxIdRelayPayloads.NormalizeOptional(message.Content?.Text);
        if (payload is null)
            return false;

        Dictionary<string, string> values;
        try
        {
            using var document = JsonDocument.Parse(payload);
            values = ExtractCardActionValues(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }

        if (!TryGetRequiredValue(values, "actor_id", out var actorId) ||
            !TryGetRequiredValue(values, "run_id", out var runId) ||
            !TryGetRequiredValue(values, "step_id", out var stepId))
        {
            return false;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.platform"] = NyxIdRelayPayloads.NormalizeOptional(message.Platform) ?? string.Empty,
            ["channel.conversation_id"] = NyxIdRelayPayloads.NormalizeOptional(message.Conversation?.PlatformId)
                                          ?? NyxIdRelayPayloads.NormalizeOptional(message.Conversation?.Id)
                                          ?? string.Empty,
        };
        if (NyxIdRelayPayloads.NormalizeOptional(message.MessageId) is { } messageId)
            metadata["channel.message_id"] = messageId;

        var approved = ResolveApproved(values);
        command = new WorkflowResumeCommand(
            actorId,
            runId,
            stepId,
            NyxIdRelayPayloads.NormalizeOptional(message.MessageId),
            approved,
            ResolveUserInput(values, approved),
            metadata,
            ResolveEditedContent(values),
            ResolveFeedback(values, approved));
        return true;
    }

    private static Dictionary<string, string> ExtractCardActionValues(JsonElement root)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        CopyValues(root, values);

        if (root.TryGetProperty("value", out var actionValue))
            CopyValues(actionValue, values);
        if (root.TryGetProperty("form_value", out var formValue))
            CopyValues(formValue, values);

        return values;
    }

    private static void CopyValues(JsonElement element, IDictionary<string, string> values)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    values[property.Name] = property.Value.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    values[property.Name] = property.Value.ToString();
                    break;
            }
        }
    }

    private static bool TryGetRequiredValue(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!values.TryGetValue(key, out var raw))
            return false;

        value = (raw ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool ResolveApproved(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("approved", out var rawApproved) &&
            bool.TryParse(rawApproved, out var approved))
        {
            return approved;
        }

        if (values.TryGetValue("action", out var rawAction))
            return ResolveDecisionLikeValue(rawAction);

        if (values.TryGetValue("decision", out var rawDecision))
            return ResolveDecisionLikeValue(rawDecision);

        return true;
    }

    private static bool ResolveDecisionLikeValue(string? raw)
    {
        var normalized = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "reject" or "rejected" or "deny" or "denied" or "cancel" => false,
            _ => true,
        };
    }

    private static string? ResolveUserInput(
        IReadOnlyDictionary<string, string> values,
        bool approved)
    {
        var preferredKeys = approved
            ? new[] { "edited_content", "user_input", "input", "comment" }
            : new[] { "user_input", "comment", "input", "edited_content" };

        foreach (var key in preferredKeys)
        {
            if (values.TryGetValue(key, out var raw))
            {
                var normalized = NyxIdRelayPayloads.NormalizeOptional(raw);
                if (normalized is not null)
                    return normalized;
            }
        }

        return null;
    }

    private static string? ResolveEditedContent(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("edited_content", out var raw))
            return null;

        return NyxIdRelayPayloads.NormalizeOptional(raw);
    }

    private static string? ResolveFeedback(
        IReadOnlyDictionary<string, string> values,
        bool approved)
    {
        if (approved)
        {
            if (values.TryGetValue("user_input", out var approvedRaw))
                return NyxIdRelayPayloads.NormalizeOptional(approvedRaw);

            return null;
        }

        foreach (var key in new[] { "user_input", "comment", "input", "edited_content" })
        {
            if (values.TryGetValue(key, out var raw))
            {
                var normalized = NyxIdRelayPayloads.NormalizeOptional(raw);
                if (normalized is not null)
                    return normalized;
            }
        }

        return null;
    }
}
