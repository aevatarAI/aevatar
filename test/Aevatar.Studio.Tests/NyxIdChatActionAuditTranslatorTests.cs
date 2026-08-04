using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Core.Sanitization;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Projection.Audit;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdChatActionAuditTranslatorTests
{
    [Fact]
    public void Requested_ShouldCreateOneOpaqueIdempotentChatRecord()
    {
        var hasher = new OpaqueHasher();
        var translator = new NyxIdChatActionRequestedAuditTranslator(hasher);
        var request = Request();
        var evt = new NyxIdChatActionRequestedEvent
        {
            Request = request,
            State = State(ownerSubject: "user-audit-alpha"),
        };
        evt.State.PendingActions.Add(request.Clone());
        var context = Context(evt, "event-requested-alpha");

        var first = translator.Translate(context, Any.Pack(evt)).Should().ContainSingle().Subject;
        var second = translator.Translate(context, Any.Pack(evt)).Should().ContainSingle().Subject;

        first.ToByteArray().Should().Equal(second.ToByteArray());
        first.AuditId.Should().Be("chat-action:event-requested-alpha:requested:action-alpha");
        first.AuditActorId.Should().Be("audit_actor:hmac-sha256:opaque");
        first.IdentityKeyId.Should().Be("test-key");
        first.ActorKind.Should().Be(AuditActorKind.NyxidUser);
        first.OperationName.Should().Be("chat.action.requested");
        first.LifecyclePhase.Should().Be(AuditLifecyclePhase.Accepted);
        first.TerminalOutcome.Should().Be(AuditTerminalOutcome.Unspecified);
        first.Target.Should().BeEquivalentTo(new AuditTarget
        {
            Kind = "chat_action",
            Id = "service_connect",
        });
        first.Provenance.Chat.Should().BeEquivalentTo(new AuditChatProvenance
        {
            Surface = AuditChatSurface.NyxidAssistant,
            ConversationId = "conversation-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            ActionRequestId = "action-alpha",
        });
        first.Redaction.OmittedFields.Should().Contain(
            ["action.params", "owner_subject", "source_event.payload"]);
        first.ToString().Should().NotContain("user-audit-alpha")
            .And.NotContain("service-secret");
        hasher.CanonicalKeys.Should().OnlyContain(key => key == "nyxid:user-audit-alpha");
        new AuditRecordSanitizer().Sanitize(first).Should().BeEquivalentTo(first);
    }

    [Fact]
    public void Requested_ShouldSkipOwnerlessAndUnknownActionFacts()
    {
        var translator = new NyxIdChatActionRequestedAuditTranslator(new OpaqueHasher());
        var request = Request();
        var ownerless = new NyxIdChatActionRequestedEvent
        {
            Request = request,
            State = State(ownerSubject: string.Empty),
        };
        var unknown = ownerless.Clone();
        unknown.State.OwnerSubject = "user-audit-alpha";
        unknown.Request.Action = (NyxIdAssistantActionKind)999;

        translator.Translate(Context(ownerless, "event-ownerless"), Any.Pack(ownerless))
            .Should().BeEmpty();
        translator.Translate(Context(unknown, "event-unknown"), Any.Pack(unknown))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(NyxIdChatActionDisposition.Declined, AuditTerminalOutcome.Cancelled, "action_declined", false)]
    [InlineData(NyxIdChatActionDisposition.Failed, AuditTerminalOutcome.Failed, "action_failed", true)]
    [InlineData(NyxIdChatActionDisposition.Cancelled, AuditTerminalOutcome.Cancelled, "", false)]
    [InlineData(NyxIdChatActionDisposition.Expired, AuditTerminalOutcome.TimedOut, "action_expired", true)]
    public void ContinuationResolution_ShouldMapOnlyMatchedTerminalReports(
        NyxIdChatActionDisposition disposition,
        AuditTerminalOutcome expectedOutcome,
        string expectedCode,
        bool expectsFailure)
    {
        var report = Report(disposition);
        var request = Request();
        request.Reports.Add(report.Clone());
        var state = State(ownerSubject: "user-audit-alpha");
        state.RecentActions.Add(request);
        var evt = new NyxIdChatContinuationAdmissionCommittedEvent
        {
            Admission = new NyxIdChatContinuationAdmissionState
            {
                Kind = NyxIdChatContinuationKind.Action,
                Status = NyxIdChatContinuationAdmissionStatus.Accepted,
                OriginTurnId = "turn-alpha",
            },
            State = state,
        };
        evt.Admission.ActionReports.Add(report.Clone());

        var record = new NyxIdChatActionContinuationResolvedAuditTranslator(new OpaqueHasher())
            .Translate(Context(evt, "event-resolution-alpha"), Any.Pack(evt))
            .Should().ContainSingle().Subject;

        record.AuditId.Should().Be("chat-action:event-resolution-alpha:resolved:action-alpha");
        record.OperationName.Should().Be("chat.action.resolved");
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        record.TerminalOutcome.Should().Be(expectedOutcome);
        record.ErrorCode.Should().Be(expectedCode);
        (record.Failure is not null).Should().Be(expectsFailure);
        record.Provenance.Chat.ActionRequestId.Should().Be("action-alpha");
        record.ToString().Should().NotContain("user-audit-alpha")
            .And.NotContain("resource-secret")
            .And.NotContain("service-secret");
        new AuditRecordSanitizer().Sanitize(record).Should().BeEquivalentTo(record);
    }

    [Fact]
    public void ContinuationResolution_ShouldIgnoreCompletedRejectedAndUnmatchedReports()
    {
        var completed = Report(NyxIdChatActionDisposition.Completed);
        var request = Request();
        request.Reports.Add(completed.Clone());
        var evt = Continuation(request, completed);
        var translator = new NyxIdChatActionContinuationResolvedAuditTranslator(new OpaqueHasher());

        translator.Translate(Context(evt, "event-completed"), Any.Pack(evt)).Should().BeEmpty();
        evt.Admission.Status = NyxIdChatContinuationAdmissionStatus.Rejected;
        evt.Admission.ActionReports[0] = Report(NyxIdChatActionDisposition.Failed);
        translator.Translate(Context(evt, "event-rejected"), Any.Pack(evt)).Should().BeEmpty();
        evt.Admission.Status = NyxIdChatContinuationAdmissionStatus.Accepted;
        evt.State.RecentActions.Clear();
        translator.Translate(Context(evt, "event-unmatched"), Any.Pack(evt)).Should().BeEmpty();
    }

    [Fact]
    public void VerifiedPostcondition_ShouldBeTheOnlySuccessfulResolution()
    {
        var result = new NyxIdChatActionPostconditionResult
        {
            ActionRequestId = "action-alpha",
            Disposition = NyxIdChatActionDisposition.Completed,
            Verified = true,
            Resource = new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef { UserServiceId = "resource-secret" },
            },
        };
        var request = Request();
        request.PostconditionResult = result.Clone();
        var state = State(ownerSubject: "user-audit-alpha");
        state.RecentActions.Add(request);
        var evt = new NyxIdChatOperationReconciledEvent
        {
            Result = new NyxIdChatOperationResultSignal
            {
                Key = OperationKey(),
                ActionPostcondition = result,
            },
            State = state,
        };
        var translator = new NyxIdChatActionPostconditionResolvedAuditTranslator(new OpaqueHasher());

        var record = translator.Translate(Context(evt, "event-verified-alpha"), Any.Pack(evt))
            .Should().ContainSingle().Subject;

        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Succeeded);
        record.Outcome.Should().Be(AuditOutcome.Success);
        record.Failure.Should().BeNull();
        record.ToString().Should().NotContain("resource-secret")
            .And.NotContain("user-audit-alpha");
        new AuditRecordSanitizer().Sanitize(record).Should().BeEquivalentTo(record);

        evt.Result.ActionPostcondition.Verified = false;
        evt.State.RecentActions[0].PostconditionResult = evt.Result.ActionPostcondition.Clone();
        translator.Translate(Context(evt, "event-unverified-alpha"), Any.Pack(evt))
            .Should().BeEmpty();
    }

    private static NyxIdChatContinuationAdmissionCommittedEvent Continuation(
        NyxIdChatActionRequestState request,
        NyxIdChatActionReport report)
    {
        var state = State(ownerSubject: "user-audit-alpha");
        state.RecentActions.Add(request);
        var evt = new NyxIdChatContinuationAdmissionCommittedEvent
        {
            Admission = new NyxIdChatContinuationAdmissionState
            {
                Kind = NyxIdChatContinuationKind.Action,
                Status = NyxIdChatContinuationAdmissionStatus.Accepted,
                OriginTurnId = "turn-alpha",
            },
            State = state,
        };
        evt.Admission.ActionReports.Add(report);
        return evt;
    }

    private static NyxIdChatActionRequestState Request() => new()
    {
        ConversationActorId = "conversation-alpha",
        OriginTurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = "step-alpha",
        ActionRequestId = "action-alpha",
        Action = NyxIdAssistantActionKind.ServiceConnect,
        AdvisoryRisk = NyxIdAssistantActionRisk.Grant,
        RememberEligible = true,
        Params = new NyxIdAssistantActionParams
        {
            CatalogServiceConnect = new NyxIdCatalogServiceConnectParams
            {
                ServiceSlug = "service-secret",
            },
        },
    };

    private static NyxIdChatActionReport Report(NyxIdChatActionDisposition disposition) => new()
    {
        ActionRequestId = "action-alpha",
        OriginTurnId = "turn-alpha",
        Disposition = disposition,
        Resource = new NyxIdChatSafeResourceRef
        {
            UserService = new NyxIdChatUserServiceRef { UserServiceId = "resource-secret" },
        },
    };

    private static NyxIdChatConversationGAgentState State(string ownerSubject) => new()
    {
        ConversationActorId = "conversation-alpha",
        ScopeId = "scope-alpha",
        OwnerSubject = ownerSubject,
    };

    private static NyxIdChatOperationKey OperationKey() => new()
    {
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = "step-postcondition",
        OperationId = "operation-alpha",
        OperationGeneration = 1,
    };

    private static CommittedAuditTranslationContext Context(IMessage evt, string eventId) => new(
        new EventEnvelope { Id = "command-alpha" },
        new CommittedStateEventPublished(),
        new StateEvent
        {
            AgentId = "conversation-alpha",
            EventId = eventId,
            Version = 9,
        },
        "conversation-alpha",
        Any.Pack(evt).TypeUrl,
        DateTimeOffset.Parse("2026-08-01T02:00:00Z"),
        "command-alpha",
        "request-alpha",
        "correlation-alpha");

    private sealed class OpaqueHasher : IAuditActorIdentityHasher
    {
        public List<string> CanonicalKeys { get; } = [];

        public AuditActorIdentity Hash(string canonicalActorKey)
        {
            CanonicalKeys.Add(canonicalActorKey);
            return new AuditActorIdentity("audit_actor:hmac-sha256:opaque", "test-key");
        }

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => false;
    }
}
