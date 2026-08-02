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
    [InlineData("nyx_12345678901234567890")]
    [InlineData("sk_12345678901234567890")]
    public void Sanitize_RejectsSecretBearingAnnotationValues(string annotationValue)
    {
        var record = CreateRecord();
        record.Annotations.Add("safe-diagnostic", annotationValue);

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));
    }

    [Fact]
    public void Sanitize_RejectsFullKeyAnnotation()
    {
        var record = CreateRecord();
        record.Annotations.Add("full-key", "redacted");

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));
    }

    [Fact]
    public void Sanitize_RejectsMissingSemanticFields()
    {
        var record = CreateRecord();
        record.ScopeId = string.Empty;

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));
    }

    [Fact]
    public void Sanitize_RejectsTerminalLifecycleWithoutExactlyOneTerminalOutcome()
    {
        var record = CreateRecord();
        record.TerminalOutcome = AuditTerminalOutcome.Unspecified;

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));

        record.LifecyclePhase = AuditLifecyclePhase.Running;
        record.TerminalOutcome = AuditTerminalOutcome.Succeeded;
        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));
    }

    [Fact]
    public void Sanitize_RequiresStructuredFailureForFailedTerminalRecord()
    {
        var record = CreateRecord();
        record.Outcome = AuditOutcome.Error;
        record.TerminalOutcome = AuditTerminalOutcome.Failed;

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));

        record.Failure = new AuditFailure
        {
            Code = "execution_failed",
            Category = AuditFailureCategory.Execution,
            Retryability = AuditRetryability.Unknown,
            FailedPhase = AuditLifecyclePhase.Running,
            SanitizedMessage = "Execution failed.",
        };
        new AuditRecordSanitizer().Sanitize(record).Failure.Code.ShouldBe("execution_failed");
    }

    [Fact]
    public void Sanitize_RejectsStructuredFailureWithoutSanitizedMessage()
    {
        var record = CreateRecord();
        record.Outcome = AuditOutcome.Error;
        record.TerminalOutcome = AuditTerminalOutcome.Failed;
        record.Failure = new AuditFailure
        {
            Code = "execution_failed",
            Category = AuditFailureCategory.Execution,
            Retryability = AuditRetryability.Unknown,
            FailedPhase = AuditLifecyclePhase.Running,
        };

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));
    }

    [Fact]
    public void Sanitize_AllowsLifecycleAndProvenanceToBeAbsentWhenNotApplicable()
    {
        var record = CreateRecord();
        record.LifecyclePhase = AuditLifecyclePhase.Unspecified;
        record.TerminalOutcome = AuditTerminalOutcome.Unspecified;
        record.Provenance = null;

        var sanitized = new AuditRecordSanitizer().Sanitize(record);

        sanitized.LifecyclePhase.ShouldBe(AuditLifecyclePhase.Unspecified);
        sanitized.Provenance.ShouldBeNull();
    }

    [Fact]
    public void Sanitize_RejectsInvalidW3CTraceparent()
    {
        var record = CreateRecord();
        record.Correlation.Traceparent = "not-a-traceparent";

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));
    }

    [Fact]
    public void Sanitize_RejectsTracestateWithoutTraceparent()
    {
        var record = CreateRecord();
        record.Correlation.Tracestate = "vendor=value";

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));
    }

    [Fact]
    public void Sanitize_RejectsTraceIdentifiersThatContradictTraceparent()
    {
        var record = CreateRecord();
        record.Correlation.TraceId = "fedcba9876543210fedcba9876543210";
        record.Correlation.SpanId = "0123456789abcdef";
        record.Correlation.Traceparent =
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01";

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));
    }

    [Fact]
    public void Sanitize_RejectsContradictoryExecutionProvenance()
    {
        var record = CreateRecord();
        record.Correlation.CorrelationId = "correlation-1";
        record.Provenance.CorrelationId = "correlation-2";

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));
    }

    [Fact]
    public void Sanitize_RequiresSpecifiedSurfaceWhenChatProvenanceIsPresent()
    {
        var record = CreateRecord();
        record.Provenance.Chat = new AuditChatProvenance
        {
            ConversationId = "conversation-alpha",
        };

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));

        record.Provenance.Chat.Surface = AuditChatSurface.NyxidAssistant;
        new AuditRecordSanitizer().Sanitize(record).Provenance.Chat.Surface
            .ShouldBe(AuditChatSurface.NyxidAssistant);
    }

    [Fact]
    public void Sanitize_RejectsSecretBearingStructuredFailure()
    {
        var record = CreateRecord();
        record.Outcome = AuditOutcome.Error;
        record.TerminalOutcome = AuditTerminalOutcome.Failed;
        record.Failure = new AuditFailure
        {
            Code = "execution_failed",
            Category = AuditFailureCategory.Execution,
            Retryability = AuditRetryability.Unknown,
            FailedPhase = AuditLifecyclePhase.Running,
            SanitizedMessage = "Bearer must-not-be-stored",
        };

        Should.Throw<ArgumentException>(() => new AuditRecordSanitizer().Sanitize(record));
    }

    private static AuditRecord CreateRecord()
    {
        return new AuditRecord
        {
            AuditId = "audit-1",
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:04:05Z")),
            RecordedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:04:06Z")),
            EventKind = "api.call",
            Subject = "service/svc-1",
            SchemaVersion = "1.0",
            Source = "urn:aevatar:audit:boundary-endpoint",
            ScopeId = "scope-1",
            AuditActorId = "audit_actor:hmac-sha256:abc",
            IdentityKeyId = "key-1",
            ActorKind = AuditActorKind.NyxidUser,
            CredentialSource = AuditCredentialSource.NyxidAssertion,
            OperationKind = AuditOperationKind.Api,
            OperationName = "api.call",
            SensitivityLevel = AuditSensitivityLevel.Internal,
            Outcome = AuditOutcome.Success,
            LifecyclePhase = AuditLifecyclePhase.Terminal,
            TerminalOutcome = AuditTerminalOutcome.Succeeded,
            CapturePlane = AuditCapturePlane.BoundaryEndpoint,
            Target = new AuditTarget { Kind = "service", Id = "svc-1" },
            Correlation = new AuditCorrelation { RequestId = "req-1" },
            Provenance = new AuditExecutionProvenance { ScopeId = "scope-1" },
            Redaction = new AuditRedaction
            {
                Policy = "aevatar.audit.endpoint-safe-fields.v1",
                ValuesSanitized = true,
            },
        };
    }
}
