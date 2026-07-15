using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Core.Auditing;

public sealed class ToolAuditRecordFactory
{
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

        var record = new AuditRecord
        {
            AuditId = CreateAuditId(executionContext, context, receipt),
            OccurredAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            ScopeId = ResolveScopeId(executionContext),
            AuditActorId = identity.AuditActorId,
            IdentityKeyId = identity.IdentityKeyId,
            ActorKind = actor.Kind,
            CredentialSource = MapCredentialSource(context.CredentialSource),
            OperationKind = AuditOperationKind.Tool,
            OperationName = ResolveOperationName(receipt, context),
            SensitivityLevel = AuditSensitivityLevel.Internal,
            Outcome = MapOutcome(receipt.Status),
            CapturePlane = AuditCapturePlane.ToolExecution,
            Target = new AuditTarget
            {
                Kind = ResolveTargetKind(receipt),
                Id = ResolveTargetId(receipt, context),
                DisplayName = string.Empty,
            },
            Correlation = new AuditCorrelation
            {
                TraceId = string.Empty,
                RequestId = executionContext.Request.RequestId ?? string.Empty,
                CallId = string.IsNullOrWhiteSpace(receipt.CallId)
                    ? context.ToolCallId ?? string.Empty
                    : receipt.CallId,
                SessionId = executionContext.Caller.ResponseId ?? string.Empty,
                WorkflowRunId = executionContext.WorkflowRuntime.ParentRunId
                                ?? executionContext.WorkflowRuntime.RootRunId
                                ?? string.Empty,
                ApprovalId = receipt.ApprovalRequestId ?? string.Empty,
            },
        };

        record.Annotations.Add("tool_name", ResolveOperationName(receipt, context));
        record.Annotations.Add("tool_receipt_status", receipt.Status.ToString());
        record.Annotations.Add("approval_mode", receipt.ApprovalMode.ToString());
        record.Annotations.Add("is_destructive", receipt.IsDestructive ? "true" : "false");
        record.Annotations.Add("receipt_synthetic", finalizedReceipt.IsSynthetic ? "true" : "false");
        AddIfPresent(record.Annotations, "side_effect_kind", receipt.SideEffectKind);
        AddIfPresent(record.Annotations, "subject_kind", receipt.SubjectKind);
        AddIfPresent(record.Annotations, "subject_version", receipt.SubjectVersion);
        AddIfPresent(record.Annotations, "subject_hash", receipt.SubjectHash);
        AddIfPresent(record.Annotations, "channel_platform", executionContext.Channel.Platform);
        AddIfPresent(record.Annotations, "schedule_id", executionContext.Schedule.ScheduleId);

        if (record.Outcome == AuditOutcome.Error || record.Outcome == AuditOutcome.Denied)
        {
            record.ErrorCode = string.IsNullOrWhiteSpace(receipt.ErrorCode)
                ? DefaultErrorCode(receipt.Status)
                : receipt.ErrorCode.Trim();
            record.ErrorSummary = string.IsNullOrWhiteSpace(receipt.ErrorMessage)
                ? record.ErrorCode
                : receipt.ErrorMessage.Trim();
        }

        return record;
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
            AgentToolCredentialSource.IdentityAssertion => AuditCredentialSource.NyxidAssertion,
            AgentToolCredentialSource.BearerToken => AuditCredentialSource.BearerToken,
            AgentToolCredentialSource.ChannelRegistration => AuditCredentialSource.ChannelRegistration,
            AgentToolCredentialSource.ScheduledRun => AuditCredentialSource.ScheduledRun,
            AgentToolCredentialSource.System => AuditCredentialSource.System,
            AgentToolCredentialSource.ServiceAccount => AuditCredentialSource.ServiceAccount,
            _ => AuditCredentialSource.System,
        };

    private static AuditOutcome MapOutcome(AgentToolReceiptStatus status) =>
        status switch
        {
            AgentToolReceiptStatus.Success => AuditOutcome.Success,
            AgentToolReceiptStatus.ApprovalRequired => AuditOutcome.Accepted,
            AgentToolReceiptStatus.Denied => AuditOutcome.Denied,
            AgentToolReceiptStatus.Error => AuditOutcome.Error,
            _ => AuditOutcome.Error,
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
