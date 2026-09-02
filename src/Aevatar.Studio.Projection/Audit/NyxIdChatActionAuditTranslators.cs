using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Core.Identity;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Audit;

public sealed class NyxIdChatActionRequestedAuditTranslator(
    IAuditActorIdentityHasher? identityHasher = null) : IAuditCommittedEventTranslator
{
    public string EventTypeUrl => Any.Pack(new NyxIdChatActionRequestedEvent()).TypeUrl;

    public IReadOnlyList<AuditRecord> Translate(
        CommittedAuditTranslationContext context,
        Any eventPayload)
    {
        if (identityHasher is null ||
            eventPayload is null ||
            !eventPayload.Is(NyxIdChatActionRequestedEvent.Descriptor))
        {
            return [];
        }

        var evt = eventPayload.Unpack<NyxIdChatActionRequestedEvent>();
        var record = NyxIdChatActionAuditRecordBuilder.Build(
            context,
            identityHasher,
            evt.State,
            evt.Request,
            "requested",
            AuditLifecyclePhase.Accepted,
            AuditTerminalOutcome.Unspecified);
        return record is null ? [] : [record];
    }
}

public sealed class NyxIdChatActionContinuationResolvedAuditTranslator(
    IAuditActorIdentityHasher? identityHasher = null) : IAuditCommittedEventTranslator
{
    public string EventTypeUrl =>
        Any.Pack(new NyxIdChatContinuationAdmissionCommittedEvent()).TypeUrl;

    public IReadOnlyList<AuditRecord> Translate(
        CommittedAuditTranslationContext context,
        Any eventPayload)
    {
        if (identityHasher is null ||
            eventPayload is null ||
            !eventPayload.Is(NyxIdChatContinuationAdmissionCommittedEvent.Descriptor))
        {
            return [];
        }

        var evt = eventPayload.Unpack<NyxIdChatContinuationAdmissionCommittedEvent>();
        if (evt.Admission is not
            {
                Kind: NyxIdChatContinuationKind.Action,
                Status: NyxIdChatContinuationAdmissionStatus.Accepted,
            } || evt.State is null)
        {
            return [];
        }

        var records = new List<AuditRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var report in evt.Admission.ActionReports)
        {
            if (!seen.Add(report.ActionRequestId) ||
                report.Disposition is NyxIdChatActionDisposition.Unspecified or
                    NyxIdChatActionDisposition.Completed)
            {
                continue;
            }

            var request = evt.State.RecentActions.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.ActionRequestId,
                    report.ActionRequestId,
                    StringComparison.Ordinal) &&
                candidate.Reports.Any(committed =>
                    committed.ToByteString().Equals(report.ToByteString())));
            if (request is null ||
                !NyxIdChatActionAuditRecordBuilder.TryMapTerminal(
                    report.Disposition,
                    out var terminalOutcome,
                    out var errorCode,
                    out var failure))
            {
                continue;
            }

            var record = NyxIdChatActionAuditRecordBuilder.Build(
                context,
                identityHasher,
                evt.State,
                request,
                "resolved",
                AuditLifecyclePhase.Terminal,
                terminalOutcome,
                errorCode,
                failure);
            if (record is not null)
                records.Add(record);
        }

        return records;
    }
}

public sealed class NyxIdChatActionPostconditionResolvedAuditTranslator(
    IAuditActorIdentityHasher? identityHasher = null) : IAuditCommittedEventTranslator
{
    public string EventTypeUrl => Any.Pack(new NyxIdChatOperationReconciledEvent()).TypeUrl;

    public IReadOnlyList<AuditRecord> Translate(
        CommittedAuditTranslationContext context,
        Any eventPayload)
    {
        if (identityHasher is null ||
            eventPayload is null ||
            !eventPayload.Is(NyxIdChatOperationReconciledEvent.Descriptor))
        {
            return [];
        }

        var evt = eventPayload.Unpack<NyxIdChatOperationReconciledEvent>();
        var result = evt.Result?.ActionPostcondition;
        if (evt.State is null ||
            result is not
            {
                Verified: true,
                Disposition: NyxIdChatActionDisposition.Completed,
            })
        {
            return [];
        }

        var request = evt.State.RecentActions.FirstOrDefault(candidate =>
            string.Equals(
                candidate.ActionRequestId,
                result.ActionRequestId,
                StringComparison.Ordinal) &&
            candidate.PostconditionResult is not null &&
            candidate.PostconditionResult.ToByteString().Equals(result.ToByteString()));
        if (request is null)
            return [];

        var record = NyxIdChatActionAuditRecordBuilder.Build(
            context,
            identityHasher,
            evt.State,
            request,
            "resolved",
            AuditLifecyclePhase.Terminal,
            AuditTerminalOutcome.Succeeded);
        return record is null ? [] : [record];
    }
}

file static class NyxIdChatActionAuditRecordBuilder
{
    private const string RequestedOperation = "chat.action.requested";
    private const string ResolvedOperation = "chat.action.resolved";

    public static AuditRecord? Build(
        CommittedAuditTranslationContext context,
        IAuditActorIdentityHasher identityHasher,
        NyxIdChatConversationGAgentState? state,
        NyxIdChatActionRequestState? request,
        string phase,
        AuditLifecyclePhase lifecyclePhase,
        AuditTerminalOutcome terminalOutcome,
        string errorCode = "",
        AuditFailure? failure = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(identityHasher);

        var ownerSubject = Normalize(state?.OwnerSubject);
        var scopeId = Normalize(state?.ScopeId);
        var eventId = Normalize(context.StateEvent.EventId);
        var actionRequestId = Normalize(request?.ActionRequestId);
        var actionName = request is null ? null : ActionName(request.Action);
        if (ownerSubject is null ||
            scopeId is null ||
            eventId is null ||
            actionRequestId is null ||
            actionName is null)
        {
            return null;
        }

        var identity = identityHasher.Hash(AuditCanonicalActorKeys.ForNyxIdUser(ownerSubject));
        var operationName = string.Equals(phase, "requested", StringComparison.Ordinal)
            ? RequestedOperation
            : ResolvedOperation;
        var causationId = FirstNonBlank(context.CausationId, eventId);
        var correlationId = Normalize(context.CorrelationId) ?? string.Empty;
        var record = new AuditRecord
        {
            AuditId = $"chat-action:{eventId}:{phase}:{actionRequestId}",
            OccurredAt = Timestamp.FromDateTimeOffset(context.ObservedAt),
            RecordedAt = Timestamp.FromDateTimeOffset(context.RecordedAt ?? context.ObservedAt),
            ScopeId = scopeId,
            AuditActorId = identity.AuditActorId,
            IdentityKeyId = identity.IdentityKeyId,
            ActorKind = AuditActorKind.NyxidUser,
            CredentialSource = AuditCredentialSource.NyxidAssertion,
            OperationKind = AuditOperationKind.Authorization,
            OperationName = operationName,
            SensitivityLevel = AuditSensitivityLevel.Confidential,
            Outcome = LegacyOutcome(lifecyclePhase, terminalOutcome),
            LifecyclePhase = lifecyclePhase,
            TerminalOutcome = terminalOutcome,
            Target = new AuditTarget { Kind = "chat_action", Id = actionName },
            Correlation = new AuditCorrelation
            {
                RequestId = Normalize(context.RequestId) ?? string.Empty,
                CommandId = FirstNonBlank(context.CommandId, context.Envelope.Id),
                CorrelationId = correlationId,
                CausationId = causationId,
                TraceId = Normalize(context.TraceId)?.ToLowerInvariant() ?? string.Empty,
                SpanId = Normalize(context.SpanId)?.ToLowerInvariant() ?? string.Empty,
                Traceparent = BuildTraceparent(
                    context.TraceId,
                    context.SpanId,
                    context.TraceFlags),
            },
            CapturePlane = AuditCapturePlane.ProjectionArtifact,
            CommittedFactRef = new AuditCommittedFactReference
            {
                CommittedEventId = eventId,
                ActorId = identity.AuditActorId,
                EventTypeUrl = context.EventTypeUrl,
                StateVersion = context.StateEvent.Version,
            },
            EventKind = operationName,
            Subject = $"chat_action/{actionName}",
            SchemaVersion = AuditContractSemantics.CurrentSchemaVersion,
            Source = "urn:aevatar:audit:projection-artifact",
            ErrorCode = errorCode,
            Provenance = new AuditExecutionProvenance
            {
                ScopeId = scopeId,
                CausationId = causationId,
                CorrelationId = correlationId,
                ActorId = identity.AuditActorId,
                ActorStateVersion = context.StateEvent.Version,
                ActorEventId = eventId,
                Chat = new AuditChatProvenance
                {
                    Surface = AuditChatSurface.NyxidAssistant,
                    ConversationId = Normalize(request!.ConversationActorId) ?? string.Empty,
                    TurnId = Normalize(request.OriginTurnId) ?? string.Empty,
                    TaskId = Normalize(request.TaskId) ?? string.Empty,
                    StepId = Normalize(request.StepId) ?? string.Empty,
                    ActionRequestId = actionRequestId,
                },
            },
            Redaction = new AuditRedaction
            {
                Policy = "aevatar.audit.safe-fields.v1",
                ValuesSanitized = true,
            },
        };
        record.Redaction.OmittedFields.Add(
        [
            "action.params",
            "action.postcondition",
            "action.resource",
            "owner_subject",
            "source_event.payload",
        ]);
        record.Annotations.Add("action_kind", actionName);
        record.Annotations.Add("advisory_risk", RiskName(request.AdvisoryRisk));
        record.Annotations.Add("remember_eligible", request.RememberEligible ? "true" : "false");
        if (failure is not null)
        {
            record.Failure = failure.Clone();
            record.ErrorSummary = failure.SanitizedMessage;
        }

        return record;
    }

    public static bool TryMapTerminal(
        NyxIdChatActionDisposition disposition,
        out AuditTerminalOutcome terminalOutcome,
        out string errorCode,
        out AuditFailure? failure)
    {
        terminalOutcome = disposition switch
        {
            NyxIdChatActionDisposition.Declined => AuditTerminalOutcome.Cancelled,
            NyxIdChatActionDisposition.Failed => AuditTerminalOutcome.Failed,
            NyxIdChatActionDisposition.Cancelled => AuditTerminalOutcome.Cancelled,
            NyxIdChatActionDisposition.Expired => AuditTerminalOutcome.TimedOut,
            _ => AuditTerminalOutcome.Unspecified,
        };
        errorCode = disposition switch
        {
            NyxIdChatActionDisposition.Declined => "action_declined",
            NyxIdChatActionDisposition.Failed => "action_failed",
            NyxIdChatActionDisposition.Expired => "action_expired",
            _ => string.Empty,
        };
        failure = disposition switch
        {
            NyxIdChatActionDisposition.Failed => Failure(
                errorCode,
                AuditFailureCategory.Execution,
                "The browser action failed."),
            NyxIdChatActionDisposition.Expired => Failure(
                errorCode,
                AuditFailureCategory.Timeout,
                "The browser action expired."),
            _ => null,
        };
        return terminalOutcome != AuditTerminalOutcome.Unspecified;
    }

    private static AuditFailure Failure(
        string code,
        AuditFailureCategory category,
        string message) =>
        new()
        {
            Code = code,
            Category = category,
            Retryability = AuditRetryability.Unknown,
            FailedPhase = AuditLifecyclePhase.Running,
            SanitizedMessage = message,
        };

    private static string? ActionName(NyxIdAssistantActionKind action) =>
        action switch
        {
            NyxIdAssistantActionKind.ServiceConnect => "service_connect",
            NyxIdAssistantActionKind.ServiceReauthorize => "service_reauthorize",
            NyxIdAssistantActionKind.ProviderSetAppCredentials => "provider_set_app_credentials",
            NyxIdAssistantActionKind.KeyCreate => "key_create",
            NyxIdAssistantActionKind.KeyRotate => "key_rotate",
            NyxIdAssistantActionKind.NodeRegisterToken => "node_register_token",
            NyxIdAssistantActionKind.NodeRotateToken => "node_rotate_token",
            NyxIdAssistantActionKind.NodeInjectCredential => "node_inject_credential",
            NyxIdAssistantActionKind.ServiceAccountCreate => "service_account_create",
            NyxIdAssistantActionKind.ServiceAccountRotateSecret => "service_account_rotate_secret",
            NyxIdAssistantActionKind.DeveloperAppCreate => "developer_app_create",
            NyxIdAssistantActionKind.DeveloperAppRotateSecret => "developer_app_rotate_secret",
            NyxIdAssistantActionKind.AccountMfaSetup => "account_mfa_setup",
            NyxIdAssistantActionKind.DeviceOnboard => "device_onboard",
            _ => null,
        };

    private static string RiskName(NyxIdAssistantActionRisk risk) =>
        risk switch
        {
            NyxIdAssistantActionRisk.Low => "low",
            NyxIdAssistantActionRisk.Grant => "grant",
            NyxIdAssistantActionRisk.Destructive => "destructive",
            _ => "unspecified",
        };

    private static AuditOutcome LegacyOutcome(
        AuditLifecyclePhase lifecycle,
        AuditTerminalOutcome terminal) =>
        lifecycle != AuditLifecyclePhase.Terminal
            ? AuditOutcome.Accepted
            : terminal switch
            {
                AuditTerminalOutcome.Succeeded => AuditOutcome.Success,
                AuditTerminalOutcome.Cancelled => AuditOutcome.Cancelled,
                _ => AuditOutcome.Error,
            };

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ??
        string.Empty;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildTraceparent(
        string? traceId,
        string? spanId,
        string? traceFlags)
    {
        var normalizedTraceId = Normalize(traceId)?.ToLowerInvariant();
        var normalizedSpanId = Normalize(spanId)?.ToLowerInvariant();
        if (normalizedTraceId is not { Length: 32 } || normalizedSpanId is not { Length: 16 })
            return string.Empty;

        var normalizedFlags = Normalize(traceFlags)?.ToLowerInvariant();
        if (normalizedFlags is not { Length: 2 })
            normalizedFlags = "00";
        return $"00-{normalizedTraceId}-{normalizedSpanId}-{normalizedFlags}";
    }
}
