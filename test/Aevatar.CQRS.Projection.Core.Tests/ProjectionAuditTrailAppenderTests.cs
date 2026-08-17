using System.Security.Cryptography;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.Projection;
using Aevatar.Audit.Core.Sanitization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionAuditTrailAppenderTests
{
    [Fact]
    public async Task AppendAsync_ShouldWriteAuditArtifactDocument_WithCopiedFieldsAndContentHash()
    {
        var store = new RecordingAuditTrailArtifactStore();
        var appender = new ProjectionAuditTrailAppender([store]);
        var record = CreateRecord("audit-1");

        var result = await appender.AppendAsync(record);

        result.Status.Should().Be(AuditTrailAppendStatus.Appended);
        result.AuditId.Should().Be("audit-1");
        result.AuditActorId.Should().Be("actor-audit-1");
        result.OccurredAt.Should().Be(DateTimeOffset.Parse("2026-07-03T08:09:10+00:00"));
        var document = store.Documents.Should().ContainSingle().Subject;
        document.Id.Should().Be("audit-1");
        document.AuditId.Should().Be("audit-1");
        document.ContentHash.Should().Be(ComputeContentHash(record));
        document.Record.Should().NotBeSameAs(record);
        document.Record.Should().Be(record);
        document.OccurredAt.ToDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-07-03T08:09:10+00:00"));
        document.UpdatedAt.ToDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-07-03T08:09:11+00:00"));
        document.AuditActorId.Should().Be("actor-audit-1");
        document.ScopeId.Should().Be("scope-audit-1");
        document.OperationName.Should().Be("audit.operation");
        document.Outcome.Should().Be(AuditOutcome.Success);
        document.SensitivityLevel.Should().Be(AuditSensitivityLevel.Confidential);
        document.TargetKind.Should().Be("workflow");
        document.TargetId.Should().Be("target-audit-1");
        document.RequestId.Should().Be("request-audit-1");
        document.CommandId.Should().Be("command-audit-1");
        document.CorrelationId.Should().Be("correlation-audit-1");
        document.TraceId.Should().Be("trace-audit-1");
        document.SessionId.Should().Be("session-audit-1");
        document.WorkflowRunId.Should().Be("run-audit-1");
        document.CommittedEventId.Should().Be("event-audit-1");
        document.CommittedActorId.Should().Be("committed-actor-audit-1");
        document.CommittedActorType.Should().Be("CommittedActorType");
        document.CommittedEventTypeUrl.Should().Be("type.googleapis.com/aevatar.audit.TestEvent");
        document.CommittedStateVersion.Should().Be(42);
    }

    [Fact]
    public async Task AppendAsync_ToolExecution_ShouldMapTypedDetailsIntoArtifactDocument()
    {
        var store = new RecordingAuditTrailArtifactStore();
        var appender = new ProjectionAuditTrailAppender([store]);
        var record = CreateRecord("audit-tool");
        record.CapturePlane = AuditCapturePlane.ToolExecution;
        record.OperationKind = AuditOperationKind.Tool;
        record.Source = "urn:aevatar:audit:tool-execution";
        record.ToolExecution = new AuditToolExecution
        {
            ArgumentsSha256 = new string('a', 64),
            ExecutionPhase = AuditToolExecutionPhase.Terminal,
            IsMutation = true,
        };

        var result = await appender.AppendAsync(record);

        result.Status.Should().Be(AuditTrailAppendStatus.Appended);
        var document = store.Documents.Should().ContainSingle().Subject;
        document.ToolArgumentsSha256.Should().Be(new string('a', 64));
        document.ToolExecutionPhase.Should().Be(AuditToolExecutionPhase.Terminal);
        document.ToolIsMutation.Should().BeTrue();
        document.Record.ToolExecution.Should().Be(record.ToolExecution);
    }

    [Fact]
    public async Task AppendAsync_ShouldSanitizeBeforeHashingAndWriting()
    {
        var store = new RecordingAuditTrailArtifactStore();
        var sanitizer = new AuditRecordSanitizer(new AuditRecordSanitizerOptions
        {
            MaxSummaryLength = 64,
            MaxAnnotationValueLength = 64,
        });
        var appender = new ProjectionAuditTrailAppender([store], sanitizer);
        var record = CreateRecord("audit-sanitized");
        record.RequestSummary = "  request\n  summary  ";
        record.Annotations.Add("note", "  safe\n  detail  ");
        var originalHash = ComputeContentHash(record);

        var result = await appender.AppendAsync(record);

        result.Status.Should().Be(AuditTrailAppendStatus.Appended);
        var document = store.Documents.Should().ContainSingle().Subject;
        document.Record.RequestSummary.Should().Be("request summary");
        document.Record.Annotations.Should().Contain("note", "safe detail");
        document.ContentHash.Should().Be(ComputeContentHash(document.Record));
        document.ContentHash.Should().NotBe(originalHash);
    }

    [Fact]
    public async Task AppendAsync_WhenSanitizerRejectsSecretCarrier_ShouldReturnConflictWithoutStoreAccess()
    {
        var store = new RecordingAuditTrailArtifactStore();
        var appender = new ProjectionAuditTrailAppender([store]);
        var record = CreateRecord("audit-secret");
        record.Annotations.Add("authorization", "Bearer must-not-be-stored");

        var result = await appender.AppendAsync(record);

        result.Status.Should().Be(AuditTrailAppendStatus.Conflict);
        result.Message.Should().Be("Audit record is invalid.");
        store.ReadCount.Should().Be(0);
        store.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_WhenExistingContentHashMatches_ShouldReturnDuplicateWithoutWriting()
    {
        var record = CreateRecord("audit-1");
        var store = new RecordingAuditTrailArtifactStore
        {
            Existing = new AuditTrailDocument
            {
                AuditId = "audit-1",
                ContentHash = ComputeContentHash(record),
            },
        };
        var appender = new ProjectionAuditTrailAppender([store]);

        var result = await appender.AppendAsync(record);

        result.Status.Should().Be(AuditTrailAppendStatus.Duplicate);
        result.AuditId.Should().Be("audit-1");
        store.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_WhenRetryHasLaterRecordedAt_ShouldRemainDuplicate()
    {
        var original = CreateRecord("audit-1");
        var retry = original.Clone();
        retry.RecordedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-03T08:10:11+00:00"));
        var store = new RecordingAuditTrailArtifactStore
        {
            Existing = new AuditTrailDocument
            {
                AuditId = "audit-1",
                ContentHash = ComputeContentHash(original),
                Record = original,
            },
        };
        var appender = new ProjectionAuditTrailAppender([store]);

        var result = await appender.AppendAsync(retry);

        result.Status.Should().Be(AuditTrailAppendStatus.Duplicate);
        store.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_WhenLegacyHashesDifferOnlyByTransportTrace_ShouldReturnDuplicate()
    {
        var original = CreateRecord("audit-1");
        original.Correlation.TraceId = "0123456789abcdef0123456789abcdef";
        original.Correlation.SpanId = "0123456789abcdef";
        original.Correlation.Traceparent =
            "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01";
        original.Correlation.Tracestate = "vendor=attempt-1";
        var retry = original.Clone();
        retry.Correlation.TraceId = "fedcba9876543210fedcba9876543210";
        retry.Correlation.SpanId = "fedcba9876543210";
        retry.Correlation.Traceparent =
            "00-fedcba9876543210fedcba9876543210-fedcba9876543210-01";
        retry.Correlation.Tracestate = "vendor=attempt-2";
        retry.RecordedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-03T08:10:11+00:00"));
        var originalHash = ComputeContentHash(original);
        var retryHash = ComputeContentHash(retry);
        var store = new RecordingAuditTrailArtifactStore
        {
            Existing = new AuditTrailDocument
            {
                AuditId = "audit-1",
                ContentHash = originalHash,
                Record = original,
            },
        };
        var appender = new ProjectionAuditTrailAppender([store]);

        var result = await appender.AppendAsync(retry);

        originalHash.Should().NotBe(retryHash);
        result.Status.Should().Be(AuditTrailAppendStatus.Duplicate);
        store.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_WhenLegacyHashDiffersAndBusinessCorrelationChanges_ShouldReturnConflict()
    {
        var original = CreateRecord("audit-1");
        var conflicting = original.Clone();
        conflicting.Correlation.RequestId = "different-request";
        var store = new RecordingAuditTrailArtifactStore
        {
            Existing = new AuditTrailDocument
            {
                AuditId = "audit-1",
                ContentHash = ComputeContentHash(original),
                Record = original,
            },
        };
        var appender = new ProjectionAuditTrailAppender([store]);

        var result = await appender.AppendAsync(conflicting);

        result.Status.Should().Be(AuditTrailAppendStatus.Conflict);
        store.Documents.Should().BeEmpty();
    }

    [Theory]
    [InlineData("waiting_approval")]
    [InlineData("running")]
    [InlineData("terminal")]
    public async Task AppendAsync_WhenSamePhaseFactIsRecreatedAtLaterClock_ShouldRemainDuplicate(
        string executionPhase)
    {
        var original = CreateRecord($"audit-{executionPhase}");
        original.Annotations.Add("execution_phase", executionPhase);
        var retry = original.Clone();
        retry.OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-03T08:10:10+00:00"));
        retry.RecordedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-03T08:10:11+00:00"));
        var store = new RecordingAuditTrailArtifactStore
        {
            Existing = new AuditTrailDocument
            {
                AuditId = original.AuditId,
                ContentHash = ComputeContentHash(original),
                Record = original,
            },
        };
        var appender = new ProjectionAuditTrailAppender([store]);

        var result = await appender.AppendAsync(retry);

        result.Status.Should().Be(AuditTrailAppendStatus.Duplicate);
        store.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_WhenExistingContentHashDiffers_ShouldReturnConflictWithoutWriting()
    {
        var store = new RecordingAuditTrailArtifactStore
        {
            Existing = new AuditTrailDocument
            {
                AuditId = "audit-1",
                ContentHash = "different-content",
            },
        };
        var appender = new ProjectionAuditTrailAppender([store]);

        var result = await appender.AppendAsync(CreateRecord("audit-1"));

        result.Status.Should().Be(AuditTrailAppendStatus.Conflict);
        result.Message.Should().Contain("different content");
        store.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_WhenArtifactStoreReportsConflict_ShouldReturnConflict()
    {
        var store = new RecordingAuditTrailArtifactStore
        {
            WriteResult = AuditTrailArtifactWriteResult.Conflict(),
        };
        var appender = new ProjectionAuditTrailAppender([store]);

        var result = await appender.AppendAsync(CreateRecord("audit-1"));

        result.Status.Should().Be(AuditTrailAppendStatus.Conflict);
        result.Message.Should().Contain("write conflict");
    }

    [Fact]
    public async Task AppendAsync_WhenAuditIdIsBlank_ShouldReturnConflictWithoutReadingStore()
    {
        var store = new RecordingAuditTrailArtifactStore();
        var appender = new ProjectionAuditTrailAppender([store]);

        var result = await appender.AppendAsync(CreateRecord(" "));

        result.Status.Should().Be(AuditTrailAppendStatus.Conflict);
        result.AuditId.Should().BeEmpty();
        result.Message.Should().Contain("Audit id is required");
        store.ReadCount.Should().Be(0);
        store.Documents.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ThrowOn.Read)]
    [InlineData(ThrowOn.Write)]
    public async Task AppendAsync_WhenArtifactStoreThrows_ShouldReturnStoreUnavailable(ThrowOn throwOn)
    {
        var store = new RecordingAuditTrailArtifactStore { ThrowOn = throwOn };
        var appender = new ProjectionAuditTrailAppender([store]);

        var result = await appender.AppendAsync(CreateRecord("audit-1"));

        result.Status.Should().Be(AuditTrailAppendStatus.StoreUnavailable);
        result.AuditId.Should().Be("audit-1");
        result.Message.Should().Be($"artifact store {throwOn.ToString().ToLowerInvariant()} failed");
    }

    [Theory]
    [InlineData(CancelOn.Read)]
    [InlineData(CancelOn.Write)]
    public async Task AppendAsync_WhenArtifactStoreObservesCancelledToken_ShouldPreserveCancellation(CancelOn cancelOn)
    {
        var store = new RecordingAuditTrailArtifactStore { CancelOn = cancelOn };
        var appender = new ProjectionAuditTrailAppender([store]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => appender.AppendAsync(CreateRecord("audit-1"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static AuditRecord CreateRecord(string auditId) =>
        new()
        {
            AuditId = auditId,
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-03T08:09:10+00:00")),
            RecordedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-03T08:09:11+00:00")),
            EventKind = "audit.operation",
            Subject = $"workflow/target-{auditId}",
            SchemaVersion = "1.0",
            Source = "urn:aevatar:audit:projection-artifact",
            ScopeId = $"scope-{auditId}",
            AuditActorId = $"actor-{auditId}",
            IdentityKeyId = "identity-key-1",
            ActorKind = AuditActorKind.NyxidUser,
            CredentialSource = AuditCredentialSource.BearerToken,
            OperationKind = AuditOperationKind.Api,
            OperationName = "audit.operation",
            Outcome = AuditOutcome.Success,
            LifecyclePhase = AuditLifecyclePhase.Terminal,
            TerminalOutcome = AuditTerminalOutcome.Succeeded,
            SensitivityLevel = AuditSensitivityLevel.Confidential,
            CapturePlane = AuditCapturePlane.ProjectionArtifact,
            Target = new AuditTarget
            {
                Kind = "workflow",
                Id = $"target-{auditId}",
            },
            Correlation = new AuditCorrelation
            {
                TraceId = $"trace-{auditId}",
                CorrelationId = $"correlation-{auditId}",
                RequestId = $"request-{auditId}",
                CommandId = $"command-{auditId}",
                SessionId = $"session-{auditId}",
                WorkflowRunId = $"run-{auditId}",
            },
            CommittedFactRef = new AuditCommittedFactReference
            {
                CommittedEventId = $"event-{auditId}",
                ActorId = $"committed-actor-{auditId}",
                ActorType = "CommittedActorType",
                EventTypeUrl = "type.googleapis.com/aevatar.audit.TestEvent",
                StateVersion = 42,
            },
            Provenance = new AuditExecutionProvenance
            {
                ScopeId = $"scope-{auditId}",
                RunId = $"run-{auditId}",
                ActorId = $"committed-actor-{auditId}",
                ActorStateVersion = 42,
                ActorEventId = $"event-{auditId}",
            },
            Redaction = new AuditRedaction
            {
                Policy = "aevatar.audit.safe-fields.v1",
                ValuesSanitized = true,
            },
        };

    private static string ComputeContentHash(AuditRecord record)
    {
        var semanticRecord = record.Clone();
        semanticRecord.OccurredAt = null;
        semanticRecord.RecordedAt = null;
        return Convert.ToHexString(SHA256.HashData(semanticRecord.ToByteArray())).ToLowerInvariant();
    }

    private sealed class RecordingAuditTrailArtifactStore : IAuditTrailArtifactStore
    {
        public AuditTrailDocument? Existing { get; init; }

        public AuditTrailArtifactWriteResult WriteResult { get; init; } = AuditTrailArtifactWriteResult.Applied();

        public CancelOn CancelOn { get; init; } = CancelOn.None;

        public ThrowOn ThrowOn { get; init; } = ThrowOn.None;

        public int ReadCount { get; private set; }

        public List<AuditTrailDocument> Documents { get; } = [];

        public Task<AuditTrailDocument?> GetAsync(string auditId, CancellationToken ct = default)
        {
            ReadCount++;
            if (ThrowOn == ThrowOn.Read)
                throw new InvalidOperationException("artifact store read failed");

            if (CancelOn == CancelOn.Read)
                ct.ThrowIfCancellationRequested();

            return Task.FromResult(Existing?.Clone());
        }

        public Task<AuditTrailArtifactWriteResult> UpsertAsync(AuditTrailDocument document, CancellationToken ct = default)
        {
            if (ThrowOn == ThrowOn.Write)
                throw new InvalidOperationException("artifact store write failed");

            if (CancelOn == CancelOn.Write)
                ct.ThrowIfCancellationRequested();

            Documents.Add(document.Clone());
            return Task.FromResult(WriteResult);
        }
    }

    public enum CancelOn
    {
        None = 0,
        Read = 1,
        Write = 2,
    }

    public enum ThrowOn
    {
        None = 0,
        Read = 1,
        Write = 2,
    }
}
