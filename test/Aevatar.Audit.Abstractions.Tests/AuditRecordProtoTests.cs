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
        parsed.Target.Kind.ShouldBe("workflow");
        parsed.Correlation.RequestId.ShouldBe("req-1");
        parsed.CommittedFactRef.CommittedEventId.ShouldBe("event-1");
        parsed.CommittedFactRef.StateVersion.ShouldBe(42);
        parsed.Annotations["risk"].ShouldBe("low");
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
            "full_prompt",
            "tool_args",
            "tool_result"
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
            ScopeId = "scope-1",
            AuditActorId = "audit_actor:hmac-sha256:abc",
            IdentityKeyId = "key-1",
            ActorKind = AuditActorKind.NyxidUser,
            CredentialSource = AuditCredentialSource.NyxidAssertion,
            OperationKind = AuditOperationKind.Tool,
            OperationName = "tools.invoke",
            SensitivityLevel = AuditSensitivityLevel.Confidential,
            Outcome = AuditOutcome.Success,
            CapturePlane = AuditCapturePlane.ToolExecution,
            Target = new AuditTarget { Kind = "workflow", Id = "wf-1", DisplayName = "Workflow One" },
            Correlation = new AuditCorrelation { TraceId = "trace-1", RequestId = "req-1" },
            CommittedFactRef = new AuditCommittedFactReference
            {
                CommittedEventId = "event-1",
                ActorId = "actor-1",
                ActorType = "WorkflowRunGAgent",
                EventTypeUrl = "type.googleapis.com/aevatar.workflow.WorkflowRunCompletedEvent",
                StateVersion = 42
            },
            RequestSummary = "started workflow",
            ResultSummary = "accepted"
        };
        record.Annotations.Add("risk", "low");
        return record;
    }
}
