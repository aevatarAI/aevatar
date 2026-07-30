using System.Diagnostics;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Core.Auditing;

public sealed class ToolAuditRecordFactory
{
    private const string NyxIdProxyHttpFailurePrefix = "NYXID_PROXY_HTTP_";
    private const string WebFetchHttpFailurePrefix = "WEB_FETCH_HTTP_";

    private readonly IAuditActorIdentityHasher _identityHasher;
    private readonly TimeProvider _timeProvider;

    public ToolAuditRecordFactory(
        IAuditActorIdentityHasher identityHasher,
        TimeProvider? timeProvider = null)
    {
        _identityHasher = identityHasher ?? throw new ArgumentNullException(nameof(identityHasher));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AuditRecord Create(
        string auditId,
        string executionPhase,
        IAgentTool tool,
        string toolName,
        string toolCallId,
        string argumentsSha256,
        AgentToolCallSafety callSafety,
        AgentToolExecutionContext executionContext,
        AgentToolCredentialSource credentialSource,
        AgentToolReceipt receipt,
        AuditOutcome outcome,
        bool isMutation)
    {
        var actor = ResolveActor(executionContext);
        var identity = _identityHasher.Hash(actor.CanonicalKey);
        var recordedAt = _timeProvider.GetUtcNow();
        var scopeId = ResolveScopeId(executionContext);
        var correlation = BuildCorrelation(executionContext, receipt, toolCallId);
        var lifecyclePhase = MapLifecyclePhase(executionPhase, receipt.Status);
        var terminalOutcome = MapTerminalOutcome(lifecyclePhase, receipt, outcome);
        var errorCode = ResolveFailureCode(receipt.ErrorCode, receipt.Status);
        var targetKind = Normalize(receipt.SubjectKind) ?? "tool";
        var targetId = Normalize(receipt.SubjectId) ?? toolCallId;
        var record = new AuditRecord
        {
            AuditId = auditId,
            OccurredAt = Timestamp.FromDateTimeOffset(recordedAt),
            RecordedAt = Timestamp.FromDateTimeOffset(recordedAt),
            EventKind = toolName,
            Subject = $"{targetKind}/{targetId}",
            SchemaVersion = AuditContractSemantics.CurrentSchemaVersion,
            Source = "urn:aevatar:audit:tool-execution",
            ScopeId = scopeId,
            AuditActorId = identity.AuditActorId,
            IdentityKeyId = identity.IdentityKeyId,
            ActorKind = actor.Kind,
            CredentialSource = MapCredentialSource(credentialSource),
            OperationKind = AuditOperationKind.Tool,
            OperationName = toolName,
            SensitivityLevel = AuditSensitivityLevel.Internal,
            Outcome = MapOutcome(outcome, receipt),
            LifecyclePhase = lifecyclePhase,
            TerminalOutcome = terminalOutcome,
            CapturePlane = AuditCapturePlane.ToolExecution,
            Target = new AuditTarget
            {
                Kind = targetKind,
                Id = targetId,
            },
            Correlation = correlation,
            Provenance = new AuditExecutionProvenance
            {
                ScopeId = scopeId,
                RunId = correlation.WorkflowRunId,
                CorrelationId = correlation.CorrelationId,
            },
            Redaction = new AuditRedaction
            {
                Policy = "aevatar.audit.tool-safe-fields.v1",
                ValuesSanitized = true,
            },
            ErrorCode = errorCode,
            ErrorSummary = errorCode,
        };
        record.Redaction.OmittedFields.Add(["model.prompt", "tool.arguments", "tool.result"]);

        record.Annotations.Add("tool_name", toolName);
        record.Annotations.Add("arguments_sha256", argumentsSha256);
        record.Annotations.Add("execution_phase", executionPhase);
        record.Annotations.Add("tool_receipt_status", receipt.Status.ToString());
        record.Annotations.Add("approval_mode", receipt.ApprovalMode.ToString());
        record.Annotations.Add("is_mutation", isMutation ? "true" : "false");
        record.Annotations.Add("is_destructive", callSafety.IsDestructive ? "true" : "false");
        AddIfPresent(record.Annotations, "side_effect_kind", receipt.SideEffectKind ?? tool.SideEffectKind);
        AddIfPresent(record.Annotations, "subject_kind", receipt.SubjectKind);
        AddIfPresent(record.Annotations, "subject_version", receipt.SubjectVersion);
        AddIfPresent(record.Annotations, "subject_hash", receipt.SubjectHash);
        AddIfPresent(record.Annotations, "channel_platform", executionContext.Channel.Platform);
        AddIfPresent(record.Annotations, "schedule_id", executionContext.Schedule.ScheduleId);

        if (terminalOutcome is AuditTerminalOutcome.Failed or AuditTerminalOutcome.TimedOut)
        {
            var timedOut = terminalOutcome == AuditTerminalOutcome.TimedOut;
            record.Failure = new AuditFailure
            {
                Code = errorCode,
                Category = timedOut
                    ? AuditFailureCategory.Timeout
                    : receipt.Status == AgentToolReceiptStatus.Denied
                        ? AuditFailureCategory.Authorization
                        : AuditFailureCategory.Execution,
                Retryability = receipt.Status == AgentToolReceiptStatus.Denied
                    ? AuditRetryability.NotRetryable
                    : AuditRetryability.Unknown,
                FailedPhase = string.IsNullOrWhiteSpace(receipt.ApprovalRequestId)
                    ? AuditLifecyclePhase.Running
                    : AuditLifecyclePhase.WaitingApproval,
                SanitizedMessage = errorCode,
            };
        }

        return record;
    }

    private static AuditCorrelation BuildCorrelation(
        AgentToolExecutionContext executionContext,
        AgentToolReceipt receipt,
        string toolCallId)
    {
        var activity = Activity.Current;
        var hasW3CContext = activity?.IdFormat == ActivityIdFormat.W3C;
        return new AuditCorrelation
        {
            TraceId = activity?.TraceId.ToString() ?? string.Empty,
            SpanId = activity?.SpanId.ToString() ?? string.Empty,
            Traceparent = hasW3CContext ? activity?.Id ?? string.Empty : string.Empty,
            Tracestate = hasW3CContext ? activity?.TraceStateString ?? string.Empty : string.Empty,
            RequestId = executionContext.Request.RequestId ?? string.Empty,
            CommandId = receipt.WorkflowRunDelivery?.WorkflowCommandId ?? string.Empty,
            CallId = toolCallId,
            SessionId = executionContext.Caller.ResponseId ?? string.Empty,
            WorkflowRunId = executionContext.WorkflowRuntime.ParentRunId
                            ?? executionContext.WorkflowRuntime.RootRunId
                            ?? string.Empty,
            ApprovalId = receipt.ApprovalRequestId ?? string.Empty,
            CorrelationId = receipt.WorkflowRunDelivery?.WorkflowCorrelationId
                            ?? executionContext.Request.RequestId
                            ?? string.Empty,
        };
    }

    private static AuditOutcome MapOutcome(AuditOutcome outcome, AgentToolReceipt receipt) =>
        ResolveFailureCode(receipt.ErrorCode, receipt.Status) == "codex_execution_cancelled"
            ? AuditOutcome.Cancelled
            : receipt.Status == AgentToolReceiptStatus.Unspecified
                ? AuditOutcome.Accepted
                : outcome == AuditOutcome.Unspecified
                    ? AuditOutcome.Accepted
                    : outcome;

    private static AuditLifecyclePhase MapLifecyclePhase(
        string executionPhase,
        AgentToolReceiptStatus status)
    {
        if (status == AgentToolReceiptStatus.ApprovalRequired ||
            string.Equals(executionPhase, "waiting_approval", StringComparison.Ordinal))
        {
            return AuditLifecyclePhase.WaitingApproval;
        }

        return string.Equals(executionPhase, "terminal", StringComparison.Ordinal) &&
               status != AgentToolReceiptStatus.Unspecified
            ? AuditLifecyclePhase.Terminal
            : AuditLifecyclePhase.Running;
    }

    private static AuditTerminalOutcome MapTerminalOutcome(
        AuditLifecyclePhase lifecyclePhase,
        AgentToolReceipt receipt,
        AuditOutcome outcome)
    {
        if (lifecyclePhase != AuditLifecyclePhase.Terminal)
            return AuditTerminalOutcome.Unspecified;

        return ResolveFailureCode(receipt.ErrorCode, receipt.Status) switch
        {
            "approval_timeout" or "codex_execution_timed_out" or "WEB_FETCH_TIMEOUT" =>
                AuditTerminalOutcome.TimedOut,
            "codex_execution_cancelled" => AuditTerminalOutcome.Cancelled,
            _ => outcome switch
            {
                AuditOutcome.Success => AuditTerminalOutcome.Succeeded,
                AuditOutcome.Cancelled => AuditTerminalOutcome.Cancelled,
                _ => AuditTerminalOutcome.Failed,
            },
        };
    }

    private static string ResolveFailureCode(string? value, AgentToolReceiptStatus status)
    {
        var normalized = Normalize(value);
        if (normalized is not null)
            return normalized;

        return status switch
        {
            AgentToolReceiptStatus.Denied => "tool_denied",
            AgentToolReceiptStatus.Error => "tool_error",
            _ => string.Empty,
        };
    }

    private static ToolAuditActor ResolveActor(AgentToolExecutionContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.Schedule.ScheduleId))
            return new ToolAuditActor(AuditActorKind.Schedule, BuildCanonicalActorKey("schedule", context.Schedule.ScheduleId));

        if (!string.IsNullOrWhiteSpace(context.Channel.Platform) &&
            !string.IsNullOrWhiteSpace(context.Channel.RegistrationScopeId) &&
            !string.IsNullOrWhiteSpace(context.Channel.SenderId))
        {
            return new ToolAuditActor(
                AuditActorKind.ChannelSender,
                BuildCanonicalActorKey(
                    "channel",
                    context.Channel.Platform,
                    context.Channel.RegistrationScopeId,
                    context.Channel.SenderId));
        }

        if (!string.IsNullOrWhiteSpace(context.Caller.OwnerSubject))
            return new ToolAuditActor(AuditActorKind.NyxidUser, BuildCanonicalActorKey("nyxid", context.Caller.OwnerSubject));

        return new ToolAuditActor(AuditActorKind.System, "system");
    }

    private static string ResolveScopeId(AgentToolExecutionContext context) =>
        Normalize(context.Caller.ScopeId) ?? Normalize(context.Channel.RegistrationScopeId) ?? "system";

    private static AuditCredentialSource MapCredentialSource(AgentToolCredentialSource source) =>
        source switch
        {
            AgentToolCredentialSource.NyxIdAssertion => AuditCredentialSource.NyxidAssertion,
            AgentToolCredentialSource.BearerToken => AuditCredentialSource.BearerToken,
            AgentToolCredentialSource.ChannelRegistration => AuditCredentialSource.ChannelRegistration,
            AgentToolCredentialSource.ScheduledRun => AuditCredentialSource.ScheduledRun,
            AgentToolCredentialSource.ServiceAccount => AuditCredentialSource.ServiceAccount,
            _ => AuditCredentialSource.System,
        };

    private static void AddIfPresent(IDictionary<string, string> annotations, string key, string? value)
    {
        var normalized = Normalize(value);
        if (normalized is not null)
            annotations.Add(key, normalized);
    }

    private static string BuildCanonicalActorKey(string prefix, params string?[] segments) =>
        string.Join(':', new[] { prefix }.Concat(segments.Select(NormalizeCanonicalSegment)));

    private static string NormalizeCanonicalSegment(string? value)
    {
        var normalized = Normalize(value)
            ?? throw new ArgumentException("Canonical actor key segment is required.", nameof(value));
        if (normalized.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("Canonical actor key segments cannot contain ':'.", nameof(value));
        return normalized;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ToolAuditActor(AuditActorKind Kind, string CanonicalKey);
}
