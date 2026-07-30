using Aevatar.AI.Abstractions;
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
        var record = new AuditRecord
        {
            AuditId = auditId,
            OccurredAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            ScopeId = ResolveScopeId(executionContext),
            AuditActorId = identity.AuditActorId,
            IdentityKeyId = identity.IdentityKeyId,
            ActorKind = actor.Kind,
            CredentialSource = MapCredentialSource(credentialSource),
            OperationKind = AuditOperationKind.Tool,
            OperationName = toolName,
            SensitivityLevel = AuditSensitivityLevel.Internal,
            Outcome = outcome,
            CapturePlane = AuditCapturePlane.ToolExecution,
            Target = new AuditTarget
            {
                Kind = Normalize(receipt.SubjectKind) ?? "tool",
                Id = Normalize(receipt.SubjectId) ?? toolCallId,
            },
            Correlation = new AuditCorrelation
            {
                RequestId = executionContext.Request.RequestId ?? string.Empty,
                CallId = toolCallId,
                SessionId = executionContext.Caller.ResponseId ?? string.Empty,
                WorkflowRunId = executionContext.WorkflowRuntime.ParentRunId
                                ?? executionContext.WorkflowRuntime.RootRunId
                                ?? string.Empty,
                ApprovalId = receipt.ApprovalRequestId ?? string.Empty,
            },
            ErrorCode = receipt.ErrorCode ?? string.Empty,
            ErrorSummary = receipt.ErrorMessage ?? string.Empty,
        };

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
        return record;
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
