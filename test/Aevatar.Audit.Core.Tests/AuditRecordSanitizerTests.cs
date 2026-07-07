using Aevatar.Audit;
using Aevatar.Audit.Core.Sanitization;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.Audit.Core.Tests;

public sealed class AuditRecordSanitizerTests
{
    [Fact]
    public void Sanitize_TrimsAndBoundsSummaryAndAnnotations()
    {
        var sanitizer = new AuditRecordSanitizer(new AuditRecordSanitizerOptions
        {
            MaxSummaryLength = 12,
            MaxAnnotationKeyLength = 8,
            MaxAnnotationValueLength = 5,
            MaxAnnotations = 1
        });
        var record = CreateRecord();
        record.RequestSummary = "  alpha   beta   gamma  ";
        record.Annotations.Add("safe-key-one", "abcdefg");
        record.Annotations.Add("safe-key-two", "ignored");

        var sanitized = sanitizer.Sanitize(record);

        sanitized.RequestSummary.ShouldBe("alpha beta g");
        sanitized.Annotations.Count.ShouldBe(1);
        sanitized.Annotations.Single().Key.ShouldBe("safe-key");
        sanitized.Annotations.Single().Value.ShouldBe("abcde");
    }

    [Fact]
    public void Sanitize_RejectsSecretCarrierAnnotations()
    {
        var record = CreateRecord();
        record.Annotations.Add("authorization", "redacted");

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));
    }

    [Theory]
    [InlineData("Bearer caller-token")]
    [InlineData("-----BEGIN PRIVATE KEY----- secret -----END PRIVATE KEY-----")]
    public void Sanitize_RejectsSecretBearingAnnotationValues(string annotationValue)
    {
        var record = CreateRecord();
        record.Annotations.Add("safe-diagnostic", annotationValue);

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));
    }

    [Fact]
    public void Sanitize_RejectsMissingSemanticFields()
    {
        var record = CreateRecord();
        record.ScopeId = string.Empty;

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));
    }

    private static AuditRecord CreateRecord()
    {
        return new AuditRecord
        {
            AuditId = "audit-1",
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:04:05Z")),
            ScopeId = "scope-1",
            AuditActorId = "audit_actor:hmac-sha256:abc",
            IdentityKeyId = "key-1",
            ActorKind = AuditActorKind.NyxidUser,
            CredentialSource = AuditCredentialSource.NyxidAssertion,
            OperationKind = AuditOperationKind.Api,
            OperationName = "api.call",
            SensitivityLevel = AuditSensitivityLevel.Internal,
            Outcome = AuditOutcome.Success,
            CapturePlane = AuditCapturePlane.BoundaryEndpoint,
            Target = new AuditTarget { Kind = "service", Id = "svc-1" },
            Correlation = new AuditCorrelation { RequestId = "req-1" }
        };
    }
}
