using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public static class NyxIdRelayCardActionParser
{
    public static CardActionSubmission? Parse(string? rawText, NyxIdRelayCallbackPayload payload)
    {
        var submission = new CardActionSubmission();
        if (!string.IsNullOrWhiteSpace(payload.PlatformMessageId))
            submission.SourceMessageId = payload.PlatformMessageId.Trim();

        if (string.IsNullOrWhiteSpace(rawText))
            return submission;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(rawText);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (TryReadString(root, "action_id", out var actionId) ||
            TryReadString(root, "a", out actionId))
        {
            submission.ActionId = actionId;
        }

        if (TryReadString(root, "submitted_value", out var submittedValue) ||
            TryReadString(root, "s", out submittedValue))
        {
            submission.SubmittedValue = submittedValue;
        }

        if (string.IsNullOrEmpty(submission.SourceMessageId) &&
            TryReadString(root, "source_message_id", out var sourceMessageId))
        {
            submission.SourceMessageId = sourceMessageId;
        }

        if (root.TryGetProperty("value", out var valueElement))
            CopyScalarMap(valueElement, submission.Arguments);
        if (root.TryGetProperty("v", out var compactValueElement))
            CopyScalarMap(compactValueElement, submission.Arguments);
        if (root.TryGetProperty("form_value", out var formValueElement))
            CopyScalarMap(formValueElement, submission.FormFields);
        if (root.TryGetProperty("arguments", out var argumentsElement))
            CopyScalarMap(argumentsElement, submission.Arguments);
        if (root.TryGetProperty("form_fields", out var formFieldsElement))
            CopyScalarMap(formFieldsElement, submission.FormFields);

        submission.ActionKind = ResolveActionKind(root, submission.Arguments);
        submission.Arguments.Remove("action_kind");

        // Lark relays button clicks with the composed value object nested under `value`
        // (`{"tag":"button","value":{"action_id":...,"value":...},...}`), so the typed
        // identity fields arrive flattened into Arguments rather than at the root. Mirror
        // them into the typed fields without removing the boundary arguments: workflow
        // resume and other typed callback consumers still rely on the original payload.
        if (string.IsNullOrEmpty(submission.ActionId) &&
            submission.Arguments.TryGetValue("action_id", out var nestedActionId) &&
            !string.IsNullOrWhiteSpace(nestedActionId))
        {
            submission.ActionId = nestedActionId.Trim();
        }

        if (string.IsNullOrEmpty(submission.SubmittedValue) &&
            submission.Arguments.TryGetValue("value", out var nestedValue) &&
            !string.IsNullOrWhiteSpace(nestedValue))
        {
            submission.SubmittedValue = nestedValue;
        }

        MapKnownPayloads(submission);

        if (string.IsNullOrEmpty(submission.ActionId) &&
            submission.Arguments.TryGetValue("agent_builder_action", out var builderAction) &&
            !string.IsNullOrWhiteSpace(builderAction))
        {
            submission.ActionId = builderAction;
        }

        return submission;
    }

    private static void MapKnownPayloads(CardActionSubmission submission)
    {
        // Refactor (iter93/cluster-093):
        // Old: workflow resume + LLM selection control semantics lived in the open `arguments` map.
        // New: repository-owned semantics use typed payloads; `arguments` is only for adapter/third-party
        // extension data plus legacy callback JSON inbound compatibility.
        if (TryBuildWorkflowResumePayload(submission, out var workflowResume))
        {
            submission.WorkflowResume = workflowResume;
            RemoveKeys(
                submission.Arguments,
                "actor_id",
                "run_id",
                "step_id",
                "approved",
                "execution_id",
                "tool_call_id",
                "approval_request_id");
        }

        if (TryBuildLlmSelectionPayload(submission, out var llmSelection))
        {
            submission.LlmSelection = llmSelection;
            RemoveKeys(
                submission.Arguments,
                "llm_action",
                "service_id",
                "preset_id",
                "model",
                "page",
                "display_mode");
        }

        if (TryBuildNyxIdApprovalPayload(submission, out var nyxIdApproval))
        {
            submission.NyxIdApproval = nyxIdApproval;
            RemoveKeys(
                submission.Arguments,
                "nyxid_approval_request_id",
                "nyxid_approval_approved");
        }

        if (TryBuildAgentRunApprovalPayload(submission, out var agentRunApproval))
        {
            submission.AgentRunApproval = agentRunApproval;
            RemoveKeys(
                submission.Arguments,
                "agent_run_id",
                "agent_run_approval_request_id",
                "agent_run_tool_call_id",
                "agent_run_tool_name",
                "agent_run_arguments_sha256",
                "agent_run_approved");
        }
    }

    private static ActionElementKind ResolveActionKind(
        JsonElement root,
        Google.Protobuf.Collections.MapField<string, string> arguments)
    {
        if (arguments.TryGetValue("action_kind", out var actionKind) &&
            TryMapActionKind(actionKind, out var mappedFromValue))
        {
            return mappedFromValue;
        }

        if (TryReadString(root, "action_kind", out var rootActionKind) &&
            TryMapActionKind(rootActionKind, out var mappedFromRoot))
        {
            return mappedFromRoot;
        }

        if (TryReadString(root, "tag", out var tag) &&
            TryMapLarkActionTag(tag, out var mappedFromTag))
        {
            return mappedFromTag;
        }

        return ActionElementKind.Unspecified;
    }

    private static bool TryMapActionKind(string? value, out ActionElementKind kind)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        kind = normalized switch
        {
            "button" => ActionElementKind.Button,
            "select" or "select_static" => ActionElementKind.Select,
            "text_input" or "input" => ActionElementKind.TextInput,
            "form_submit" or "submit" => ActionElementKind.FormSubmit,
            "link" => ActionElementKind.Link,
            _ => ActionElementKind.Unspecified,
        };
        return kind != ActionElementKind.Unspecified;
    }

    private static bool TryMapLarkActionTag(string? value, out ActionElementKind kind)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        kind = normalized switch
        {
            "button" => ActionElementKind.Button,
            "select_static" => ActionElementKind.Select,
            "input" => ActionElementKind.TextInput,
            _ => ActionElementKind.Unspecified,
        };
        return kind != ActionElementKind.Unspecified;
    }

    private static bool TryBuildWorkflowResumePayload(
        CardActionSubmission submission,
        out WorkflowResumeActionPayload payload)
    {
        payload = new WorkflowResumeActionPayload();
        if (!TryGetRequiredValue(submission.Arguments, "actor_id", out var actorId) ||
            !TryGetRequiredValue(submission.Arguments, "run_id", out var runId) ||
            !TryGetRequiredValue(submission.Arguments, "step_id", out var stepId))
        {
            return false;
        }

        payload.ActorId = actorId;
        payload.RunId = runId;
        payload.StepId = stepId;
        if (submission.Arguments.TryGetValue("approved", out var rawApproved) &&
            bool.TryParse(rawApproved, out var approved))
        {
            payload.Approved = approved;
        }

        if (submission.FormFields.TryGetValue("user_input", out var userInput))
            payload.UserInput = userInput ?? string.Empty;
        if (submission.FormFields.TryGetValue("edited_content", out var editedContent))
            payload.EditedContent = editedContent ?? string.Empty;
        if (submission.FormFields.TryGetValue("feedback", out var feedback))
            payload.Feedback = feedback ?? string.Empty;

        if (TryBuildWorkflowToolApprovalResumePayload(submission, out var toolApproval))
            payload.ToolApproval = toolApproval;

        return true;
    }

    private static bool TryBuildWorkflowToolApprovalResumePayload(
        CardActionSubmission submission,
        out WorkflowToolApprovalResumeActionPayload payload)
    {
        payload = new WorkflowToolApprovalResumeActionPayload();
        if (!TryGetRequiredValue(submission.Arguments, "execution_id", out var executionId) ||
            !TryGetRequiredValue(submission.Arguments, "tool_call_id", out var toolCallId) ||
            !TryGetRequiredValue(submission.Arguments, "approval_request_id", out var approvalRequestId))
        {
            return false;
        }

        payload.ExecutionId = executionId;
        payload.ToolCallId = toolCallId;
        payload.ApprovalRequestId = approvalRequestId;
        return true;
    }

    private static bool TryBuildLlmSelectionPayload(
        CardActionSubmission submission,
        out LlmSelectionActionPayload payload)
    {
        payload = new LlmSelectionActionPayload();

        if (!submission.Arguments.TryGetValue("llm_action", out var rawAction) ||
            string.IsNullOrWhiteSpace(rawAction))
        {
            rawAction = submission.ActionId switch
            {
                "ls" or "llm_select_service" => "select_service",
                "lp" or "llm_apply_preset" => "apply_preset",
                _ => string.Empty,
            };
        }

        if (string.IsNullOrWhiteSpace(rawAction))
            return false;

        payload.Action = rawAction.Trim();
        if (submission.Arguments.TryGetValue("service_id", out var serviceId) &&
            !string.IsNullOrWhiteSpace(serviceId))
        {
            payload.ServiceId = serviceId.Trim();
        }
        else if (payload.Action == "select_service" && !string.IsNullOrWhiteSpace(submission.SubmittedValue))
        {
            payload.ServiceId = submission.SubmittedValue.Trim();
        }

        if (submission.Arguments.TryGetValue("preset_id", out var presetId) &&
            !string.IsNullOrWhiteSpace(presetId))
        {
            payload.PresetId = presetId.Trim();
        }
        else if (payload.Action == "apply_preset" && !string.IsNullOrWhiteSpace(submission.SubmittedValue))
        {
            payload.PresetId = submission.SubmittedValue.Trim();
        }

        if (submission.Arguments.TryGetValue("model", out var model) &&
            !string.IsNullOrWhiteSpace(model))
        {
            payload.Model = model.Trim();
        }

        if (submission.Arguments.TryGetValue("page", out var rawPage) &&
            int.TryParse(rawPage, out var page) &&
            page > 0)
        {
            payload.Page = page;
        }
        else if (payload.Action == "list_page" &&
                 !string.IsNullOrWhiteSpace(submission.SubmittedValue) &&
                 int.TryParse(submission.SubmittedValue, out var submittedPage) &&
                 submittedPage > 0)
        {
            payload.Page = submittedPage;
        }

        if (submission.Arguments.TryGetValue("display_mode", out var displayMode) &&
            !string.IsNullOrWhiteSpace(displayMode))
        {
            payload.DisplayMode = displayMode.Trim();
        }

        return true;
    }

    private static bool TryBuildNyxIdApprovalPayload(
        CardActionSubmission submission,
        out NyxIdApprovalActionPayload payload)
    {
        payload = new NyxIdApprovalActionPayload();
        if (!TryGetRequiredValue(submission.Arguments, "nyxid_approval_request_id", out var requestId))
            return false;

        if (!submission.Arguments.TryGetValue("nyxid_approval_approved", out var rawApproved) ||
            !bool.TryParse(rawApproved, out var approved))
        {
            return false;
        }

        payload.RequestId = requestId;
        payload.Approved = approved;
        return true;
    }

    private static bool TryBuildAgentRunApprovalPayload(
        CardActionSubmission submission,
        out AgentRunApprovalActionPayload payload)
    {
        payload = new AgentRunApprovalActionPayload();
        if (!TryGetRequiredValue(submission.Arguments, "agent_run_id", out var runId) ||
            !TryGetRequiredValue(submission.Arguments, "agent_run_approval_request_id", out var approvalRequestId) ||
            !TryGetRequiredValue(submission.Arguments, "agent_run_tool_call_id", out var toolCallId) ||
            !TryGetRequiredValue(submission.Arguments, "agent_run_tool_name", out var toolName) ||
            !TryGetRequiredValue(submission.Arguments, "agent_run_arguments_sha256", out var argumentsSha256) ||
            !submission.Arguments.TryGetValue("agent_run_approved", out var rawApproved) ||
            !bool.TryParse(rawApproved, out var approved))
        {
            return false;
        }

        payload.RunId = runId;
        payload.ApprovalRequestId = approvalRequestId;
        payload.ToolCallId = toolCallId;
        payload.ToolName = toolName;
        payload.ArgumentsSha256 = argumentsSha256;
        payload.Approved = approved;
        return true;
    }

    private static bool TryGetRequiredValue(
        Google.Protobuf.Collections.MapField<string, string> values,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!values.TryGetValue(key, out var raw))
            return false;

        value = (raw ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void RemoveKeys(
        Google.Protobuf.Collections.MapField<string, string> values,
        params string[] keys)
    {
        foreach (var key in keys)
            values.Remove(key);
    }

    private static void CopyScalarMap(JsonElement element, Google.Protobuf.Collections.MapField<string, string> target)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    target[property.Name] = property.Value.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    target[property.Name] = property.Value.ToString();
                    break;
            }
        }
    }

    private static bool TryReadString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var parsed = property.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(parsed))
            return false;

        value = parsed;
        return true;
    }

}
