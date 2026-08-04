using Aevatar.Audit;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.Audit.Abstractions.Tests;

public sealed class AuditRecordProtoTests
{
    [Fact]
    public void AuditRecord_RoundTrips_TypedFieldsAndAnnotations()
    {
        var record = CreateRecord();

        var parsed = AuditRecord.Parser.ParseFrom(record.ToByteArray());

        parsed.ShouldBe(record);
        parsed.ActorKind.ShouldBe(AuditActorKind.NyxidUser);
        parsed.CredentialSource.ShouldBe(AuditCredentialSource.NyxidAssertion);
        parsed.OperationKind.ShouldBe(AuditOperationKind.Tool);
        parsed.SensitivityLevel.ShouldBe(AuditSensitivityLevel.Confidential);
        parsed.Outcome.ShouldBe(AuditOutcome.Success);
        parsed.CapturePlane.ShouldBe(AuditCapturePlane.ToolExecution);
        parsed.LifecyclePhase.ShouldBe(AuditLifecyclePhase.Terminal);
        parsed.TerminalOutcome.ShouldBe(AuditTerminalOutcome.Succeeded);
        parsed.SchemaVersion.ShouldBe("1.0");
        parsed.Target.Kind.ShouldBe("workflow");
        parsed.Correlation.RequestId.ShouldBe("req-1");
        parsed.Correlation.Traceparent.ShouldBe("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
        parsed.CommittedFactRef.CommittedEventId.ShouldBe("event-1");
        parsed.CommittedFactRef.StateVersion.ShouldBe(42);
        parsed.ToolExecution.ArgumentsSha256.ShouldBe(new string('a', 64));
        parsed.ToolExecution.ExecutionPhase.ShouldBe(AuditToolExecutionPhase.Terminal);
        parsed.ToolExecution.IsMutation.ShouldBeTrue();
        parsed.Annotations["risk"].ShouldBe("low");
    }

    [Fact]
    public void AuditRecord_RoundTripsTypedChatProvenanceWithoutRawIdentity()
    {
        var record = CreateRecord();
        record.Provenance.Chat = new AuditChatProvenance
        {
            Surface = AuditChatSurface.NyxidAssistant,
            ConversationId = "conversation-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            ActionRequestId = "action-alpha",
        };

        var parsed = AuditRecord.Parser.ParseFrom(record.ToByteArray());

        parsed.Provenance.Chat.ShouldBe(record.Provenance.Chat);
        AuditChatProvenance.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .ShouldBe(["surface", "conversation_id", "turn_id", "task_id", "step_id", "action_request_id"]);
        AuditRecord.Descriptor.Fields.InFieldNumberOrder().Select(static field => field.Name)
            .ShouldNotContain("owner_subject");
    }

    [Fact]
    public void AuditRecord_Descriptor_DoesNotExposeRawSecretOrIdentityFields()
    {
        var forbidden = new[]
        {
            "token",
            "authorization",
            "cookie",
            "oauth_code",
            "api_key",
            "sender_binding_id",
            "raw_subject",
            "owner_subject",
            "full_prompt",
            "prompt",
            "tool_args",
            "tool_result",
            "arguments_json",
            "result_json",
            "params"
        };

        var fieldNames = AuditRecord.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Concat(AuditTarget.Descriptor.Fields.InFieldNumberOrder().Select(static field => field.Name))
            .Concat(AuditCorrelation.Descriptor.Fields.InFieldNumberOrder().Select(static field => field.Name))
            .Concat(AuditCommittedFactReference.Descriptor.Fields.InFieldNumberOrder().Select(static field => field.Name))
            .ToList();

        foreach (var forbiddenName in forbidden)
        {
            fieldNames.ShouldNotContain(forbiddenName);
        }
    }

    [Fact]
    public void AuditRecord_Descriptor_ExposesCapturePlaneAndCommittedFactReferenceAsTypedFields()
    {
        var auditRecordFields = AuditRecord.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .ToList();
        var committedFactFields = AuditCommittedFactReference.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .ToList();

        auditRecordFields.ShouldContain("capture_plane");
        auditRecordFields.ShouldContain("committed_fact_ref");
        auditRecordFields.ShouldContain("lifecycle_phase");
        auditRecordFields.ShouldContain("terminal_outcome");
        auditRecordFields.ShouldContain("failure");
        auditRecordFields.ShouldContain("provenance");
        auditRecordFields.ShouldContain("redaction");
        var toolExecutionField = AuditRecord.Descriptor.FindFieldByName("tool_execution");
        toolExecutionField.ShouldNotBeNull();
        toolExecutionField!.MessageType.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .ShouldBe(["arguments_sha256", "execution_phase", "is_mutation"]);
        committedFactFields.ShouldBe(
        [
            "committed_event_id",
            "actor_id",
            "actor_type",
            "event_type_url",
            "state_version"
        ]);
    }

    private static AuditRecord CreateRecord()
    {
        var record = new AuditRecord
        {
            AuditId = "audit-1",
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:04:05Z")),
            RecordedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:04:06Z")),
            EventKind = "tools.invoke",
            Subject = "workflow/wf-1",
            SchemaVersion = "1.0",
            Source = "urn:aevatar:audit:tool-execution",
            ScopeId = "scope-1",
            AuditActorId = "audit_actor:hmac-sha256:abc",
            IdentityKeyId = "key-1",
            ActorKind = AuditActorKind.NyxidUser,
            CredentialSource = AuditCredentialSource.NyxidAssertion,
            OperationKind = AuditOperationKind.Tool,
            OperationName = "tools.invoke",
            SensitivityLevel = AuditSensitivityLevel.Confidential,
            Outcome = AuditOutcome.Success,
            LifecyclePhase = AuditLifecyclePhase.Terminal,
            TerminalOutcome = AuditTerminalOutcome.Succeeded,
            CapturePlane = AuditCapturePlane.ToolExecution,
            Target = new AuditTarget { Kind = "workflow", Id = "wf-1", DisplayName = "Workflow One" },
            Correlation = new AuditCorrelation
            {
                TraceId = "0123456789abcdef0123456789abcdef",
                SpanId = "0123456789abcdef",
                Traceparent = "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01",
                RequestId = "req-1",
                CorrelationId = "corr-1",
            },
            CommittedFactRef = new AuditCommittedFactReference
            {
                CommittedEventId = "event-1",
                ActorId = "actor-1",
                ActorType = "WorkflowRunGAgent",
                EventTypeUrl = "type.googleapis.com/aevatar.workflow.WorkflowRunCompletedEvent",
                StateVersion = 42
            },
            RequestSummary = "started workflow",
            ResultSummary = "accepted",
            Provenance = new AuditExecutionProvenance
            {
                ScopeId = "scope-1",
                WorkflowId = "wf-1",
                ActorId = "actor-1",
                ActorStateVersion = 42,
                ActorEventId = "event-1",
            },
            Redaction = new AuditRedaction
            {
                Policy = "aevatar.audit.safe-fields.v1",
                ValuesSanitized = true,
            },
            ToolExecution = new AuditToolExecution
            {
                ArgumentsSha256 = new string('a', 64),
                ExecutionPhase = AuditToolExecutionPhase.Terminal,
                IsMutation = true,
            },
        };
        record.Redaction.OmittedFields.Add("tool.arguments");
        record.Annotations.Add("risk", "low");
        return record;
    }
}
