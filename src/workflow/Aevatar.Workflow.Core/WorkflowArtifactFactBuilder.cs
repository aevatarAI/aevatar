using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Security;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal static class WorkflowArtifactFactBuilder
{
    // O1 (06-19-workflow-run-observatory): tool args/results may carry secrets; redact at the
    // materialization boundary before truncating to ToolDetailMaxLength.
    private const int ToolDetailMaxLength = 2000;

    public static bool TryBuild(
        EventEnvelope envelope,
        string actorId,
        string? stateRunId,
        out IMessage artifactFact)
    {
        artifactFact = null!;
        if (envelope.Payload == null)
            return false;

        var normalizedRunId = string.IsNullOrWhiteSpace(stateRunId)
            ? WorkflowRunIdNormalizer.Normalize(actorId)
            : WorkflowRunIdNormalizer.Normalize(stateRunId);

        // O1: the committed RoleChatSessionCompletedEvent is the richer source — it carries tool_calls
        // (arguments) plus tool_receipts (result/success/error). Prefer it so tool detail is surfaced.
        if (TryBuildWorkflowRoleReplyFromRoleChatSession(envelope, normalizedRunId, out var roleChatReplyFact))
        {
            artifactFact = roleChatReplyFact;
            return true;
        }

        if (TryBuildWorkflowRoleReplyRecordedEvent(envelope, normalizedRunId, out var roleReplyFact))
        {
            artifactFact = roleReplyFact;
            return true;
        }

        if (envelope.Payload.Is(WorkflowCompletedEvent.Descriptor))
            return false;

        if (envelope.Payload.Is(StepRequestEvent.Descriptor))
        {
            artifactFact = envelope.Payload.Unpack<StepRequestEvent>();
            return true;
        }

        if (envelope.Payload.Is(StepCompletedEvent.Descriptor))
        {
            artifactFact = envelope.Payload.Unpack<StepCompletedEvent>();
            return true;
        }

        if (envelope.Payload.Is(WorkflowSuspendedEvent.Descriptor))
        {
            artifactFact = envelope.Payload.Unpack<WorkflowSuspendedEvent>();
            return true;
        }

        if (envelope.Payload.Is(WaitingForSignalEvent.Descriptor))
        {
            artifactFact = envelope.Payload.Unpack<WaitingForSignalEvent>();
            return true;
        }

        if (envelope.Payload.Is(WorkflowSignalBufferedEvent.Descriptor))
        {
            artifactFact = envelope.Payload.Unpack<WorkflowSignalBufferedEvent>();
            return true;
        }

        if (envelope.Payload.Is(WorkflowExternalApprovalContinuationRegisteredEvent.Descriptor))
        {
            artifactFact = envelope.Payload.Unpack<WorkflowExternalApprovalContinuationRegisteredEvent>();
            return true;
        }

        if (envelope.Payload.Is(WorkflowExternalApprovalContinuationClearedEvent.Descriptor))
        {
            artifactFact = envelope.Payload.Unpack<WorkflowExternalApprovalContinuationClearedEvent>();
            return true;
        }

        return false;
    }

    private static bool TryBuildWorkflowRoleReplyRecordedEvent(
        EventEnvelope envelope,
        string runId,
        out WorkflowRoleReplyRecordedEvent evt)
    {
        evt = null!;

        if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) != true)
            return false;

        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        if (published?.StateEvent?.EventData == null ||
            !published.StateEvent.EventData.Is(WorkflowLlmInvocationCompletedEvent.Descriptor))
        {
            return false;
        }

        var completed = published.StateEvent.EventData.Unpack<WorkflowLlmInvocationCompletedEvent>();
        var publisherActorId = envelope.Route?.PublisherActorId ?? string.Empty;
        // Refactor (iter15/cluster-028):
        //   Old pattern: parsed childActorId prefix to derive RoleId via string split.
        //   New principle: role id comes from typed event payload / readmodel; actor id is opaque address only.
        evt = new WorkflowRoleReplyRecordedEvent
        {
            RunId = runId,
            RoleActorId = publisherActorId,
            RoleId = publisherActorId,
            SessionId = completed.SessionId ?? string.Empty,
            Content = SanitizeArtifactText(completed.Content),
            ReasoningContent = SanitizeArtifactText(completed.ReasoningContent),
            ContentEmitted = completed.Success,
        };

        return true;
    }

    // O1 (06-19-workflow-run-observatory): build the role-reply artifact fact from the committed
    // RoleChatSessionCompletedEvent so tool detail (arguments + result/success/error) is carried into
    // the workflow run timeline. tool_calls (arguments) join tool_receipts (results) by call_id.
    private static bool TryBuildWorkflowRoleReplyFromRoleChatSession(
        EventEnvelope envelope,
        string runId,
        out WorkflowRoleReplyRecordedEvent evt)
    {
        evt = null!;

        if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) != true)
            return false;

        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        if (published?.StateEvent?.EventData == null ||
            !published.StateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
        {
            return false;
        }

        var completed = published.StateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>();
        var publisherActorId = envelope.Route?.PublisherActorId ?? string.Empty;
        var roleId = string.IsNullOrWhiteSpace(completed.RoleId) ? publisherActorId : completed.RoleId;

        evt = new WorkflowRoleReplyRecordedEvent
        {
            RunId = runId,
            RoleActorId = publisherActorId,
            RoleId = roleId,
            SessionId = completed.SessionId ?? string.Empty,
            Content = SanitizeArtifactText(completed.Content),
            ReasoningContent = SanitizeArtifactText(completed.ReasoningContent),
            ContentEmitted = completed.ContentEmitted,
        };
        evt.ToolCalls.AddRange(BuildEnrichedToolCalls(completed));
        return true;
    }

    private static IReadOnlyList<WorkflowRoleReplyToolCall> BuildEnrichedToolCalls(
        RoleChatSessionCompletedEvent completed)
    {
        if (completed.ToolCalls.Count == 0)
            return [];

        var receiptsByCallId = new Dictionary<string, AgentToolReceipt>(StringComparer.Ordinal);
        foreach (var receipt in completed.ToolReceipts)
        {
            var callId = receipt.CallId ?? string.Empty;
            if (callId.Length == 0)
                continue;

            receiptsByCallId[callId] = receipt;
        }

        var toolCalls = new List<WorkflowRoleReplyToolCall>(completed.ToolCalls.Count);
        foreach (var toolCall in completed.ToolCalls)
        {
            var callId = toolCall.CallId ?? string.Empty;
            var enriched = new WorkflowRoleReplyToolCall
            {
                ToolName = toolCall.ToolName ?? string.Empty,
                CallId = callId,
                ArgumentsJson = SanitizeToolDetail(toolCall.ArgumentsJson),
            };

            if (callId.Length > 0 && receiptsByCallId.TryGetValue(callId, out var receipt))
            {
                enriched.ResultJson = SanitizeToolDetail(receipt.ResultJson);
                enriched.Success = receipt.Status == AgentToolReceiptStatus.Success;
                enriched.Error = ResolveReceiptError(receipt);
            }

            toolCalls.Add(enriched);
        }

        return toolCalls;
    }

    private static string ResolveReceiptError(AgentToolReceipt receipt)
    {
        if (!string.IsNullOrWhiteSpace(receipt.ErrorMessage))
            return SanitizeToolDetail(receipt.ErrorMessage);
        if (!string.IsNullOrWhiteSpace(receipt.ErrorCode))
            return WorkflowAuditTextSanitizer.Sanitize(receipt.ErrorCode);

        return string.Empty;
    }

    private static string SanitizeToolDetail(string? value)
    {
        return SanitizeArtifactText(value);
    }

    private static string SanitizeArtifactText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var scrubbed = SecretScrubber.Scrub(value);
        var sanitized = WorkflowAuditTextSanitizer.SanitizeForDisplay(scrubbed, ToolDetailMaxLength);

        return scrubbed.Contains(SecretScrubber.Marker, StringComparison.Ordinal)
            ? sanitized.Replace(WorkflowAuditTextSanitizer.RedactedValue, SecretScrubber.Marker, StringComparison.Ordinal)
            : sanitized;
    }
}
