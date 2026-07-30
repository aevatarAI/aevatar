using System.Diagnostics;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.Abstractions.Middleware;
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
        ToolCallContext context,
        FinalizedToolCallReceipt finalizedReceipt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(finalizedReceipt);

        var executionContext = context.ExecutionContext ?? AgentToolRequestContext.Current ?? AgentToolExecutionContext.Empty;
        var actor = ResolveActor(executionContext);
        var identity = _identityHasher.Hash(actor.CanonicalKey);
        var receipt = finalizedReceipt.Receipt;
        var record = BuildRecord(
            context,
            executionContext,
            receipt,
            actor.Kind,
            identity.AuditActorId,
            identity.IdentityKeyId,
            _timeProvider.GetUtcNow());
        AddAnnotations(record, executionContext, receipt, finalizedReceipt.IsSynthetic);
        AddFailure(record, receipt);
        return record;
    }

    private static AuditRecord BuildRecord(
        ToolCallContext context,
        AgentToolExecutionContext executionContext,
        AgentToolReceipt receipt,
        AuditActorKind actorKind,
        string auditActorId,
        string identityKeyId,
        DateTimeOffset recordedAt)
    {
        var operationName = ResolveOperationName(receipt, context);
        var targetKind = ResolveTargetKind(receipt);
        var targetId = ResolveTargetId(receipt, context);
        var scopeId = ResolveScopeId(executionContext);
        var correlation = BuildCorrelation(context, executionContext, receipt);

        var record = new AuditRecord
        {
            AuditId = CreateAuditId(executionContext, context, receipt),
            OccurredAt = Timestamp.FromDateTimeOffset(recordedAt),
            RecordedAt = Timestamp.FromDateTimeOffset(recordedAt),
            EventKind = operationName,
            Subject = $"{targetKind}/{targetId}",
            SchemaVersion = AuditContractSemantics.CurrentSchemaVersion,
            Source = "urn:aevatar:audit:tool-execution",
            ScopeId = scopeId,
            AuditActorId = auditActorId,
            IdentityKeyId = identityKeyId,
            ActorKind = actorKind,
            CredentialSource = MapCredentialSource(context.CredentialSource),
            OperationKind = AuditOperationKind.Tool,
            OperationName = operationName,
            SensitivityLevel = AuditSensitivityLevel.Internal,
            Outcome = MapOutcome(receipt),
            LifecyclePhase = MapLifecyclePhase(receipt.Status),
            TerminalOutcome = MapTerminalOutcome(receipt),
            CapturePlane = AuditCapturePlane.ToolExecution,
            Target = new AuditTarget
            {
                Kind = targetKind,
                Id = targetId,
                DisplayName = string.Empty,
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
        };
        record.Redaction.OmittedFields.Add(["model.prompt", "tool.arguments", "tool.result"]);
        return record;
    }

    private static void AddAnnotations(
        AuditRecord record,
        AgentToolExecutionContext executionContext,
        AgentToolReceipt receipt,
        bool isSynthetic)
    {
        record.Annotations.Add("tool_name", record.OperationName);
        record.Annotations.Add("tool_receipt_status", receipt.Status.ToString());
        record.Annotations.Add("approval_mode", receipt.ApprovalMode.ToString());
        record.Annotations.Add("is_destructive", receipt.IsDestructive ? "true" : "false");
        record.Annotations.Add("receipt_synthetic", isSynthetic ? "true" : "false");
        AddIfPresent(record.Annotations, "side_effect_kind", receipt.SideEffectKind);
        AddIfPresent(record.Annotations, "subject_kind", receipt.SubjectKind);
        AddIfPresent(record.Annotations, "subject_version", receipt.SubjectVersion);
        AddIfPresent(record.Annotations, "subject_hash", receipt.SubjectHash);
        AddIfPresent(record.Annotations, "channel_platform", executionContext.Channel.Platform);
        AddIfPresent(record.Annotations, "schedule_id", executionContext.Schedule.ScheduleId);
    }

    private static void AddFailure(AuditRecord record, AgentToolReceipt receipt)
    {
        if (record.Outcome == AuditOutcome.Error || record.Outcome == AuditOutcome.Denied)
        {
            record.ErrorCode = ResolveFailureCode(receipt.ErrorCode, receipt.Status);
            record.ErrorSummary = record.ErrorCode;
            var timedOut = record.TerminalOutcome == AuditTerminalOutcome.TimedOut;
            record.Failure = new AuditFailure
            {
                Code = record.ErrorCode,
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
                SanitizedMessage = CodexExecutionAuditFailureSemantics.IsOwned(record.ErrorCode)
                    ? nameof(CodexExecutionException)
                    : record.ErrorCode,
            };
        }
    }

    private static string ResolveFailureCode(string? value, AgentToolReceiptStatus status)
    {
        var normalized = value?.Trim();
        if (IsOwnedNyxIdProxyFailureCode(normalized) ||
            IsOwnedWebFetchFailureCode(normalized) ||
            CodexExecutionAuditFailureSemantics.IsOwned(normalized))
            return normalized!;

        return normalized switch
        {
            "approval_denied" => "approval_denied",
            "approval_timeout" => "approval_timeout",
            "credential_denied" => "credential_denied",
            "middleware_terminated" => "middleware_terminated",
            "tool_call_terminated" => "tool_call_terminated",
            "tool_execution_exception" => "tool_execution_exception",
            "CODE_EXECUTE_FAILED" => "CODE_EXECUTE_FAILED",
            _ => DefaultErrorCode(status),
        };
    }

    private static bool IsOwnedNyxIdProxyFailureCode(string? value)
    {
        if (value is "NYXID_PROXY_UNAUTHORIZED" or "NYXID_PROXY_FORBIDDEN")
            return true;

        return value != null &&
               value.Length == NyxIdProxyHttpFailurePrefix.Length + 3 &&
               value.StartsWith(NyxIdProxyHttpFailurePrefix, StringComparison.Ordinal) &&
               value[NyxIdProxyHttpFailurePrefix.Length] is >= '1' and <= '5' &&
               value[NyxIdProxyHttpFailurePrefix.Length + 1] is >= '0' and <= '9' &&
               value[NyxIdProxyHttpFailurePrefix.Length + 2] is >= '0' and <= '9';
    }

    private static bool IsOwnedWebFetchFailureCode(string? value)
    {
        if (value is "WEB_FETCH_DNS_FAILURE" or
            "WEB_FETCH_TLS_FAILURE" or
            "WEB_FETCH_TIMEOUT" or
            "WEB_FETCH_TRANSPORT_FAILURE" or
            "WEB_FETCH_URL_REJECTED")
        {
            return true;
        }

        return value != null &&
               value.Length == WebFetchHttpFailurePrefix.Length + 3 &&
               value.StartsWith(WebFetchHttpFailurePrefix, StringComparison.Ordinal) &&
               value[WebFetchHttpFailurePrefix.Length] is >= '1' and <= '5' &&
               value[WebFetchHttpFailurePrefix.Length + 1] is >= '0' and <= '9' &&
               value[WebFetchHttpFailurePrefix.Length + 2] is >= '0' and <= '9';
    }

    private static AuditCorrelation BuildCorrelation(
        ToolCallContext context,
        AgentToolExecutionContext executionContext,
        AgentToolReceipt receipt)
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
            CallId = string.IsNullOrWhiteSpace(receipt.CallId)
                ? context.ToolCallId ?? string.Empty
                : receipt.CallId,
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

    private static ToolAuditActor ResolveActor(AgentToolExecutionContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.Schedule.ScheduleId))
        {
            return new ToolAuditActor(
                AuditActorKind.Schedule,
                BuildCanonicalActorKey("schedule", context.Schedule.ScheduleId));
        }

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
        {
            return new ToolAuditActor(
                AuditActorKind.NyxidUser,
                BuildCanonicalActorKey("nyxid", context.Caller.OwnerSubject));
        }

        return new ToolAuditActor(
            AuditActorKind.System,
            "system");
    }

    private static string ResolveScopeId(AgentToolExecutionContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.Caller.ScopeId))
            return context.Caller.ScopeId.Trim();

        if (!string.IsNullOrWhiteSpace(context.Channel.RegistrationScopeId))
            return context.Channel.RegistrationScopeId.Trim();

        return "system";
    }

    private static AuditCredentialSource MapCredentialSource(AgentToolCredentialSource source) =>
        source switch
        {
            AgentToolCredentialSource.NyxIdAssertion => AuditCredentialSource.NyxidAssertion,
            AgentToolCredentialSource.BearerToken => AuditCredentialSource.BearerToken,
            AgentToolCredentialSource.ChannelRegistration => AuditCredentialSource.ChannelRegistration,
            AgentToolCredentialSource.ScheduledRun => AuditCredentialSource.ScheduledRun,
            AgentToolCredentialSource.System => AuditCredentialSource.System,
            AgentToolCredentialSource.ServiceAccount => AuditCredentialSource.ServiceAccount,
            _ => AuditCredentialSource.System,
        };

    private static AuditOutcome MapOutcome(AgentToolReceipt receipt) =>
        ResolveFailureCode(receipt.ErrorCode, receipt.Status) == "codex_execution_cancelled"
            ? AuditOutcome.Cancelled
            : receipt.Status switch
        {
            AgentToolReceiptStatus.Success => AuditOutcome.Success,
            AgentToolReceiptStatus.ApprovalRequired => AuditOutcome.Accepted,
            AgentToolReceiptStatus.Denied => AuditOutcome.Denied,
            AgentToolReceiptStatus.Error => AuditOutcome.Error,
            _ => AuditOutcome.Unspecified,
        };

    private static AuditLifecyclePhase MapLifecyclePhase(AgentToolReceiptStatus status) =>
        status switch
        {
            AgentToolReceiptStatus.ApprovalRequired => AuditLifecyclePhase.WaitingApproval,
            AgentToolReceiptStatus.Unspecified => AuditLifecyclePhase.Running,
            _ => AuditLifecyclePhase.Terminal,
        };

    private static AuditTerminalOutcome MapTerminalOutcome(AgentToolReceipt receipt) =>
        ResolveFailureCode(receipt.ErrorCode, receipt.Status) switch
        {
            "approval_timeout" => AuditTerminalOutcome.TimedOut,
            "codex_execution_timed_out" => AuditTerminalOutcome.TimedOut,
            "WEB_FETCH_TIMEOUT" => AuditTerminalOutcome.TimedOut,
            "codex_execution_cancelled" => AuditTerminalOutcome.Cancelled,
            _ => receipt.Status switch
            {
                AgentToolReceiptStatus.Success => AuditTerminalOutcome.Succeeded,
                AgentToolReceiptStatus.ApprovalRequired => AuditTerminalOutcome.Unspecified,
                AgentToolReceiptStatus.Unspecified => AuditTerminalOutcome.Unspecified,
                _ => AuditTerminalOutcome.Failed,
            },
        };

    private static string ResolveOperationName(AgentToolReceipt receipt, ToolCallContext context) =>
        Normalize(receipt.ToolName) ??
        Normalize(context.ToolName) ??
        Normalize(context.Tool.Name) ??
        "unknown_tool";

    private static string ResolveTargetKind(AgentToolReceipt receipt) =>
        Normalize(receipt.SubjectKind) ?? "tool";

    private static string ResolveTargetId(AgentToolReceipt receipt, ToolCallContext context) =>
        Normalize(receipt.SubjectId) ??
        Normalize(receipt.CallId) ??
        Normalize(context.ToolCallId) ??
        "unknown";

    private static string CreateAuditId(
        AgentToolExecutionContext executionContext,
        ToolCallContext context,
        AgentToolReceipt receipt)
    {
        var requestId = Normalize(executionContext.Request.RequestId);
        var callId = Normalize(receipt.CallId) ?? Normalize(context.ToolCallId);
        if (requestId != null && callId != null)
            return $"tool:{requestId}:{callId}";

        if (callId != null)
            return $"tool:{callId}";

        return $"tool:{Guid.NewGuid():N}";
    }

    private static string DefaultErrorCode(AgentToolReceiptStatus status) =>
        status switch
        {
            AgentToolReceiptStatus.Denied => "tool_denied",
            AgentToolReceiptStatus.Error => "tool_error",
            _ => string.Empty,
        };

    private static void AddIfPresent(IDictionary<string, string> annotations, string key, string? value)
    {
        var normalized = Normalize(value);
        if (normalized != null)
            annotations.Add(key, normalized);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildCanonicalActorKey(string prefix, params string?[] segments)
    {
        var normalizedSegments = segments
            .Select(NormalizeCanonicalSegment)
            .ToArray();
        return string.Join(':', new[] { prefix }.Concat(normalizedSegments));
    }

    private static string NormalizeCanonicalSegment(string? value)
    {
        var normalized = Normalize(value);
        if (normalized is null)
            throw new ArgumentException("Canonical actor key segment is required.", nameof(value));

        if (normalized.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("Canonical actor key segments cannot contain ':'.", nameof(value));

        return normalized;
    }

    private sealed record ToolAuditActor(AuditActorKind Kind, string CanonicalKey);
}
