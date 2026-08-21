using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
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
        out IMessage artifactFact) =>
        TryBuild(envelope, actorId, stateRunId, requireTypedRunId: false, out artifactFact);

    public static bool TryBuild(
        EventEnvelope envelope,
        string actorId,
        string? stateRunId,
        bool requireTypedRunId,
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
        if (TryBuildWorkflowRoleReplyFromRoleChatSession(
                envelope,
                normalizedRunId,
                requireTypedRunId,
                out var roleChatReplyFact))
        {
            artifactFact = roleChatReplyFact;
            return true;
        }

        if (TryBuildWorkflowRoleReplyRecordedEvent(
                envelope,
                normalizedRunId,
                requireTypedRunId,
                out var roleReplyFact))
        {
            artifactFact = roleReplyFact;
            return true;
        }

        if (TryBuildWorkflowRuntimeOperationRecordedEvent(
                envelope,
                normalizedRunId,
                requireTypedRunId,
                out var operationFact))
        {
            artifactFact = operationFact;
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

        if (envelope.Payload.Is(WorkflowToolApprovalResumeRejectedEvent.Descriptor))
        {
            artifactFact = envelope.Payload.Unpack<WorkflowToolApprovalResumeRejectedEvent>();
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

    private static bool TryBuildWorkflowRuntimeOperationRecordedEvent(
        EventEnvelope envelope,
        string fallbackRunId,
        bool requireTypedRunId,
        out WorkflowRuntimeOperationRecordedEvent evt)
    {
        evt = null!;
        if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) != true)
            return false;

        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        if (published?.StateEvent?.EventData?.Is(RoleChatSessionProgressedEvent.Descriptor) != true)
            return false;

        var progress = published.StateEvent.EventData.Unpack<RoleChatSessionProgressedEvent>();
        if (!TryResolveCommittedSessionRunId(
                published,
                progress.SessionId,
                explicitRunId: null,
                fallbackRunId,
                requireTypedRunId,
                out var runId))
        {
            return false;
        }
        var publisherActorId = envelope.Route?.PublisherActorId ?? string.Empty;
        evt = new WorkflowRuntimeOperationRecordedEvent
        {
            RunId = runId,
            SessionId = progress.SessionId ?? string.Empty,
            RoleActorId = publisherActorId,
            ProgressSequence = progress.Sequence,
            Source = BuildSourceIdentity(publisherActorId, published.StateEvent),
        };
        if (published.StateEvent.Timestamp != null)
            evt.EventTime = published.StateEvent.Timestamp.Clone();

        switch (progress.PayloadCase)
        {
            case RoleChatSessionProgressedEvent.PayloadOneofCase.ModelStarted:
                evt.OperationId = progress.ModelStarted.OperationId ?? string.Empty;
                evt.Round = progress.ModelStarted.Round;
                evt.Model = progress.ModelStarted.Model ?? string.Empty;
                evt.Provider = progress.ModelStarted.Provider ?? string.Empty;
                evt.InputSummary = SanitizeArtifactText(progress.ModelStarted.InputSummary);
                evt.AvailableToolNames.Add(progress.ModelStarted.AvailableToolNames);
                evt.ToolCatalogPolicyVersion = progress.ModelStarted.ToolCatalogPolicyVersion ?? string.Empty;
                if (progress.ModelStarted.ToolCatalogProof != null)
                {
                    try
                    {
                        evt.ToolCatalogProof = ToWorkflowToolCatalogProof(
                            AgentTurnToolCatalogProofPayloadMapper.FromPayload(
                                progress.ModelStarted.ToolCatalogProof));
                    }
                    catch (AgentTurnToolCatalogException)
                    {
                        return false;
                    }
                }
                evt.Kind = WorkflowRuntimeOperationKind.Model;
                evt.Phase = WorkflowRuntimeOperationPhase.Started;
                break;
            case RoleChatSessionProgressedEvent.PayloadOneofCase.ModelCompleted:
                evt.OperationId = progress.ModelCompleted.OperationId ?? string.Empty;
                evt.Round = progress.ModelCompleted.Round;
                evt.Model = progress.ModelCompleted.Model ?? string.Empty;
                evt.Kind = WorkflowRuntimeOperationKind.Model;
                evt.Phase = WorkflowRuntimeOperationPhase.Completed;
                evt.Output = SanitizeArtifactText(progress.ModelCompleted.Content);
                evt.ReasoningContent = SanitizeArtifactText(progress.ModelCompleted.ReasoningContent);
                evt.FinishReason = SanitizeArtifactText(progress.ModelCompleted.FinishReason);
                evt.Success = progress.ModelCompleted.Success;
                evt.Error = SanitizeArtifactText(progress.ModelCompleted.Error);
                if (progress.ModelCompleted.Usage != null)
                    evt.Usage = ToWorkflowUsage(progress.ModelCompleted.Usage, progress.ModelCompleted.Model);
                break;
            case RoleChatSessionProgressedEvent.PayloadOneofCase.ToolStarted:
                evt.ToolCallId = progress.ToolStarted.CallId ?? string.Empty;
                evt.OperationId = ResolveToolOperationId(
                    progress.ToolStarted.CallId,
                    progress.ToolStarted.OperationId);
                evt.ToolName = progress.ToolStarted.ToolName ?? string.Empty;
                evt.Kind = WorkflowRuntimeOperationKind.Tool;
                evt.Phase = WorkflowRuntimeOperationPhase.Started;
                break;
            case RoleChatSessionProgressedEvent.PayloadOneofCase.ToolCompleted:
                evt.ToolCallId = progress.ToolCompleted.Result?.CallId ?? string.Empty;
                evt.OperationId = ResolveToolOperationId(
                    evt.ToolCallId,
                    progress.ToolCompleted.OperationId);
                evt.ToolName = progress.ToolCompleted.ToolName ?? string.Empty;
                evt.Kind = WorkflowRuntimeOperationKind.Tool;
                evt.Phase = WorkflowRuntimeOperationPhase.Completed;
                evt.ArgumentsJson = SanitizeToolDetail(progress.ToolCompleted.SafeArgumentsJson);
                if (progress.ToolCompleted.Result != null)
                {
                    evt.ResultJson = SanitizeToolDetail(progress.ToolCompleted.Result.ResultJson);
                    evt.Success = progress.ToolCompleted.Result.Success;
                    evt.Error = SanitizeToolDetail(progress.ToolCompleted.Result.Error);
                }
                break;
            default:
                return false;
        }

        return !string.IsNullOrWhiteSpace(evt.SessionId) &&
               !string.IsNullOrWhiteSpace(evt.OperationId) &&
               evt.Kind != WorkflowRuntimeOperationKind.Unspecified &&
               evt.Phase != WorkflowRuntimeOperationPhase.Unspecified;
    }

    private static string ResolveToolOperationId(string? callId, string? operationId) =>
        !string.IsNullOrWhiteSpace(callId)
            ? callId.Trim()
            : operationId?.Trim() ?? string.Empty;

    private static WorkflowUsageMetrics ToWorkflowUsage(TokenUsagePayload usage, string? model) =>
        new()
        {
            PromptTokens = Math.Max(0, usage.PromptTokens),
            CompletionTokens = Math.Max(0, usage.CompletionTokens),
            TotalTokens = Math.Max(0, usage.TotalTokens),
            Model = model ?? string.Empty,
        };

    private static WorkflowAgentTurnToolCatalogProof ToWorkflowToolCatalogProof(
        AgentTurnToolCatalogProof proof)
    {
        var result = new WorkflowAgentTurnToolCatalogProof
        {
            Budget = new WorkflowAgentTurnToolCatalogBudgetProof
            {
                MaximumToolCount = proof.Budget.MaximumToolCount,
                MaximumSchemaBytes = proof.Budget.MaximumSchemaBytes,
                MaximumConnectedReadToolCount = proof.Budget.MaximumConnectedReadToolCount,
                MaximumConnectedWriteToolCount = proof.Budget.MaximumConnectedWriteToolCount,
            },
            ToolCount = proof.ToolCount,
            SchemaBytes = proof.SchemaBytes,
            ConnectedReadToolCount = proof.ConnectedReadToolCount,
            ConnectedWriteToolCount = proof.ConnectedWriteToolCount,
            CatalogDigest = proof.CatalogDigest,
        };
        result.ToolDescriptors.AddRange(proof.ToolDescriptors.Select(static descriptor =>
            new WorkflowAgentTurnToolDescriptorProof
            {
                Name = descriptor.Name,
                Description = descriptor.Description,
                CanonicalSchemaJson = ByteString.CopyFrom(descriptor.CanonicalSchemaBytes.Span),
                SchemaSha256 = descriptor.SchemaSha256,
                Origin = ToWorkflowToolOrigin(descriptor.Origin),
                SelectorDigest = descriptor.SelectorDigest,
            }));
        return result;
    }

    private static WorkflowAgentTurnToolOrigin ToWorkflowToolOrigin(AgentTurnToolOrigin origin) => origin switch
    {
        AgentTurnToolOrigin.AgentRuntime => WorkflowAgentTurnToolOrigin.AgentRuntime,
        AgentTurnToolOrigin.RouteToolSet => WorkflowAgentTurnToolOrigin.RouteToolSet,
        AgentTurnToolOrigin.AgentProfile => WorkflowAgentTurnToolOrigin.AgentProfile,
        AgentTurnToolOrigin.ConnectedService => WorkflowAgentTurnToolOrigin.ConnectedService,
        AgentTurnToolOrigin.ResponsesState => WorkflowAgentTurnToolOrigin.ResponsesState,
        AgentTurnToolOrigin.CallerForwarded => WorkflowAgentTurnToolOrigin.CallerForwarded,
        AgentTurnToolOrigin.Workflow => WorkflowAgentTurnToolOrigin.Workflow,
        AgentTurnToolOrigin.Voice => WorkflowAgentTurnToolOrigin.Voice,
        _ => WorkflowAgentTurnToolOrigin.Unspecified,
    };

    private static bool TryBuildWorkflowRoleReplyRecordedEvent(
        EventEnvelope envelope,
        string fallbackRunId,
        bool requireTypedRunId,
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
        if (!TryResolveCommittedSessionRunId(
                published,
                completed.SessionId,
                completed.RunId,
                fallbackRunId,
                requireTypedRunId,
                out var runId))
        {
            return false;
        }
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
            Source = BuildSourceIdentity(publisherActorId, published.StateEvent),
        };

        return true;
    }

    // O1 (06-19-workflow-run-observatory): build the role-reply artifact fact from the committed
    // RoleChatSessionCompletedEvent so tool detail (arguments + result/success/error) is carried into
    // the workflow run timeline. tool_calls (arguments) join tool_receipts (results) by call_id.
    private static bool TryBuildWorkflowRoleReplyFromRoleChatSession(
        EventEnvelope envelope,
        string fallbackRunId,
        bool requireTypedRunId,
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
        if (!TryResolveCommittedSessionRunId(
                published,
                completed.SessionId,
                completed.WorkflowLlmCompletionDeliveryContext?.RunId,
                fallbackRunId,
                requireTypedRunId,
                out var runId))
        {
            return false;
        }
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
            Source = BuildSourceIdentity(publisherActorId, published.StateEvent),
        };
        evt.ToolCalls.AddRange(BuildEnrichedToolCalls(completed));
        return true;
    }

    private static bool TryResolveCommittedSessionRunId(
        CommittedStateEventPublished published,
        string? sessionId,
        string? explicitRunId,
        string fallbackRunId,
        bool requireTypedRunId,
        out string runId)
    {
        var typedRunId = explicitRunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(typedRunId) &&
            published.StateRoot?.Is(RoleGAgentState.Descriptor) == true)
        {
            var roleState = published.StateRoot.Unpack<RoleGAgentState>();
            if (!string.IsNullOrWhiteSpace(sessionId) &&
                roleState.Sessions.TryGetValue(sessionId, out var session))
            {
                typedRunId = session.WorkflowLlmCompletionDeliveryContext?.RunId?.Trim() ?? string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(typedRunId))
        {
            runId = requireTypedRunId ? string.Empty : fallbackRunId;
            return !requireTypedRunId;
        }

        runId = WorkflowRunIdNormalizer.Normalize(typedRunId);
        return !string.IsNullOrWhiteSpace(runId);
    }

    private static WorkflowArtifactSourceIdentity BuildSourceIdentity(
        string publisherActorId,
        StateEvent stateEvent) =>
        new()
        {
            PublisherActorId = publisherActorId,
            CommittedEventId = stateEvent.EventId ?? string.Empty,
            CommittedStateVersion = stateEvent.Version,
        };

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
