using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatPublicToolReceiptResult
{
    private const int MaxRawReceiptBytes = 256 * 1024;
    private const int MaxIdentifierBytes = 512;
    private const int MaxWorkflowNameBytes = 512;
    private const int MaxProjectedResultBytes = 4 * 1024;
    private const int MaxPartialOutputBytes = 3 * 1024;
    private const string PublicReceiptUnavailableCode = "PUBLIC_RECEIPT_UNAVAILABLE";

    public static string Project(AgentToolReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Status != AgentToolReceiptStatus.Success ||
            string.IsNullOrWhiteSpace(receipt.ResultJson) ||
            Encoding.UTF8.GetByteCount(receipt.ResultJson) > MaxRawReceiptBytes)
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(
                receipt.ResultJson,
                new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return string.Empty;

            var projected = receipt.ToolName switch
            {
                "aevatar_start_workflow" => ProjectWorkflowStart(receipt, document.RootElement),
                "aevatar_read_workflow_run_artifact" =>
                    ProjectWorkflowRunArtifact(receipt, document.RootElement),
                _ => null,
            };
            if (projected is null)
                return string.Empty;

            var json = JsonSerializer.Serialize(projected);
            return Encoding.UTF8.GetByteCount(json) <= MaxProjectedResultBytes
                ? json
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public static string ResolvePresentationResult(AgentToolReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!string.IsNullOrWhiteSpace(receipt.ResultJson))
            return receipt.ResultJson;
        if (receipt.Status == AgentToolReceiptStatus.Success && RequiresTypedProjection(receipt.ToolName))
        {
            return JsonSerializer.Serialize(new
            {
                status = "unproven",
                error = new { code = PublicReceiptUnavailableCode },
            });
        }
        if (receipt.Status == AgentToolReceiptStatus.Success)
            return "completed";
        if (!string.IsNullOrWhiteSpace(receipt.ErrorCode))
        {
            return JsonSerializer.Serialize(new
            {
                status = "failed",
                error = new { code = receipt.ErrorCode },
            });
        }
        return "not completed";
    }

    public static string NormalizeErrorCode(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 128 &&
               normalized.All(character =>
                   char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? normalized
            : string.Empty;
    }

    private static Dictionary<string, object?>? ProjectWorkflowStart(
        AgentToolReceipt receipt,
        JsonElement root)
    {
        var runId = ReadBoundedString(root, "run_id", MaxIdentifierBytes);
        var actorId = ReadBoundedString(root, "actor_id", MaxIdentifierBytes);
        var commandId = ReadBoundedString(root, "command_id", MaxIdentifierBytes);
        var status = NormalizeWorkflowStartStatus(ReadBoundedString(root, "status", 32));
        var mutationStage = receipt.MutationStage switch
        {
            AgentToolReceiptMutationStage.Accepted => "accepted",
            AgentToolReceiptMutationStage.ReadModelObserved => "read_model_observed",
            _ => null,
        };
        if (runId is null ||
            actorId is null ||
            commandId is null ||
            mutationStage is null ||
            !IsWorkflowStartStatus(status) ||
            !HasValidWorkflowStartIdentity(receipt, runId, actorId) ||
            !string.Equals(runId, receipt.SubjectId, StringComparison.Ordinal) ||
            !string.Equals(commandId, receipt.CallId, StringComparison.Ordinal))
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["run_id"] = runId,
            ["actor_id"] = actorId,
            ["command_id"] = commandId,
            ["status"] = status,
            ["mutation_stage"] = mutationStage,
        };
        if (IsTerminalWorkflowStartStatus(status))
        {
            var stateVersion = ReadPositiveInt64(root, "state_version");
            if (stateVersion.HasValue)
                result["state_version"] = stateVersion.Value;
            if (ReadOptionalBoundedString(root, "partial_output", MaxPartialOutputBytes) is { } partialOutput)
                result["partial_output"] = partialOutput;
        }

        return result;
    }

    private static Dictionary<string, object?>? ProjectWorkflowRunArtifact(
        AgentToolReceipt receipt,
        JsonElement root)
    {
        var workflowRunId = ReadBoundedString(root, "workflow_run_id", MaxIdentifierBytes);
        var artifactActorId = ReadBoundedString(root, "artifact_actor_id", MaxIdentifierBytes);
        var rootActorId = ReadBoundedString(root, "root_actor_id", MaxIdentifierBytes);
        var artifact = ReadBoundedString(root, "artifact", 32)?.ToLowerInvariant();
        var status = NormalizeArtifactStatus(ReadBoundedString(root, "status", 64));
        if (workflowRunId is null ||
            artifact != "report" ||
            !string.Equals(workflowRunId, receipt.SubjectId, StringComparison.Ordinal))
        {
            return null;
        }

        if (status == "pending")
        {
            if (!TryReadBoolean(root, "pending", out var pendingReceiptFlag) ||
                !pendingReceiptFlag ||
                root.TryGetProperty("success", out _))
                return null;
            var pendingResult = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["workflow_run_id"] = workflowRunId,
                ["artifact"] = artifact,
                ["status"] = status,
                ["pending"] = true,
            };
            if (artifactActorId is not null)
                pendingResult["artifact_actor_id"] = artifactActorId;
            return pendingResult;
        }

        var workflowName = ReadBoundedString(root, "workflow_name", MaxWorkflowNameBytes);
        var commandId = ReadBoundedString(root, "command_id", MaxIdentifierBytes);
        var stateVersion = ReadPositiveInt64(root, "state_version");
        if (artifactActorId is null ||
            rootActorId is null ||
            !string.Equals(artifactActorId, rootActorId, StringComparison.Ordinal) ||
            workflowName is null ||
            commandId is null ||
            stateVersion is null)
        {
            return null;
        }

        if (IsMaterializedNonTerminalStatus(status))
        {
            if ((root.TryGetProperty("success", out _) &&
                 (!TryReadBoolean(root, "success", out var nonTerminalSuccess) || nonTerminalSuccess)) ||
                (root.TryGetProperty("pending", out _) &&
                 (!TryReadBoolean(root, "pending", out var declaredPending) || !declaredPending)))
            {
                return null;
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["workflow_run_id"] = workflowRunId,
                ["artifact_actor_id"] = artifactActorId,
                ["artifact"] = artifact,
                ["workflow_name"] = workflowName,
                ["status"] = "pending",
                ["pending"] = true,
                ["state_version"] = stateVersion.Value,
                ["command_id"] = commandId,
            };
        }

        if (!TryReadBoolean(root, "success", out var success))
            return null;
        if (root.TryGetProperty("pending", out _) &&
            (!TryReadBoolean(root, "pending", out var terminalPending) || terminalPending))
        {
            return null;
        }
        if (status is not ("completed" or "failed" or "stopped" or "timed_out") ||
            (status == "completed") != success)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["workflow_run_id"] = workflowRunId,
            ["artifact_actor_id"] = artifactActorId,
            ["artifact"] = artifact,
            ["workflow_name"] = workflowName,
            ["status"] = status,
            ["success"] = success,
            ["state_version"] = stateVersion.Value,
            ["command_id"] = commandId,
        };
        if (status == "completed" && success)
        {
            var finalOutputBytes = ReadNonNegativeInt64(root, "final_output_bytes");
            var finalOutputSha256 = ReadSha256(root, "final_output_sha256");
            if (finalOutputBytes is null || finalOutputSha256 is null)
                return null;
            result["final_output_bytes"] = finalOutputBytes.Value;
            result["final_output_sha256"] = finalOutputSha256;
        }

        return result;
    }

    private static bool RequiresTypedProjection(string toolName) =>
        toolName is "aevatar_start_workflow" or "aevatar_read_workflow_run_artifact";

    private static bool IsMaterializedNonTerminalStatus(string? status) =>
        status is "running" or "awaiting_tool_approval" or "waiting_for_signal";

    private static bool IsWorkflowStartStatus(string? status) =>
        status is "accepted" or "streaming" or "completed" or "failed" or "stopped" or "timed_out" or
            "not_found" or "disabled";

    private static bool IsTerminalWorkflowStartStatus(string? status) =>
        status is "completed" or "failed" or "stopped" or "timed_out" or "not_found" or "disabled";

    private static string? NormalizeWorkflowStartStatus(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "timedout" or "timed_out" => "timed_out",
            "notfound" or "not_found" => "not_found",
            var normalized => normalized,
        };

    private static string? NormalizeArtifactStatus(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "awaitingtoolapproval" or "awaiting_tool_approval" => "awaiting_tool_approval",
            "waitingforsignal" or "waiting_for_signal" => "waiting_for_signal",
            "timedout" or "timed_out" => "timed_out",
            var normalized => normalized,
        };

    private static bool HasValidWorkflowStartIdentity(
        AgentToolReceipt receipt,
        string runId,
        string actorId)
    {
        if (string.Equals(runId, actorId, StringComparison.Ordinal))
            return true;

        var handoff = receipt.ManagedWorkflowHandoff;
        return handoff is not null &&
               string.Equals(runId, handoff.InvocationId, StringComparison.Ordinal) &&
               string.Equals(runId, handoff.ChildRunId, StringComparison.Ordinal) &&
               string.Equals(actorId, handoff.ParentActorId, StringComparison.Ordinal);
    }

    private static string? ReadBoundedString(JsonElement root, string name, int maxBytes)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString()?.Trim();
        return !string.IsNullOrEmpty(text) && Encoding.UTF8.GetByteCount(text) <= maxBytes
            ? text
            : null;
    }

    private static string? ReadOptionalBoundedString(JsonElement root, string name, int maxBytes)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > maxBytes
            ? null
            : text;
    }

    private static bool TryReadBoolean(JsonElement root, string name, out bool result)
    {
        if (root.TryGetProperty(name, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            result = value.GetBoolean();
            return true;
        }

        result = false;
        return false;
    }

    private static long? ReadPositiveInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number) &&
        number > 0
            ? number
            : null;

    private static long? ReadNonNegativeInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number) &&
        number >= 0
            ? number
            : null;

    private static string? ReadSha256(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var digest = value.GetString();
        return digest is { Length: 64 } && digest.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f')
            ? digest
            : null;
    }
}
