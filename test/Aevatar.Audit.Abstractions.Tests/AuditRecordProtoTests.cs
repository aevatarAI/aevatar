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
        parsed.Target.Kind.ShouldBe("workflow");
        parsed.Correlation.RequestId.ShouldBe("req-1");
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
            .ToList();

        foreach (var forbiddenName in forbidden)
        {
            fieldNames.ShouldNotContain(forbiddenName);
        }
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
            Target = new AuditTarget { Kind = "workflow", Id = "wf-1", DisplayName = "Workflow One" },
            Correlation = new AuditCorrelation { TraceId = "trace-1", RequestId = "req-1" },
            RequestSummary = "started workflow",
            ResultSummary = "accepted"
        };
        record.Annotations.Add("risk", "low");
        return record;
    }
}
