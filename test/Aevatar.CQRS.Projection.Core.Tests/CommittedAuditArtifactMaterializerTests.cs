using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.Audit.Core.Projection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class CommittedAuditArtifactMaterializerTests
{
    [Fact]
    public async Task ProjectAsync_ShouldAppendTranslatedCommittedRecord_WithVersionAndCorrelation()
    {
        var translator = new StringValueAuditTranslator();
        var appender = new RecordingAuditTrailAppender();
        var materializer = new CommittedAuditArtifactMaterializer<TestContext>(
            new AuditCommittedEventTranslatorRegistry([translator]),
            appender,
            new FixedClock(DateTimeOffset.Parse("2026-07-03T08:00:00+00:00")));

        await materializer.ProjectAsync(
            new TestContext(),
            BuildEnvelope(
                "outer-command-1",
                "state-event-1",
                42,
                new StringValue { Value = "payload" },
                commandId: "cmd-from-baggage",
                requestId: "req-from-baggage",
                correlationId: "corr-1"));

        appender.Records.Should().ContainSingle();
        var record = appender.Records[0];
        record.Outcome.Should().Be(AuditOutcome.Committed);
        record.OperationKind.Should().Be(AuditOperationKind.CommittedFact);
        record.TargetVersion.Should().Be(42);
        record.CommandId.Should().Be("cmd-from-baggage");
        record.RequestId.Should().Be("req-from-baggage");
        record.CorrelationId.Should().Be("corr-1");
        record.ActorKind.Should().Be(AuditActorKind.System);
        record.Annotations["source_event_type_url"].Should().Be(StringValueAuditTranslator.TypeUrl);
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreUnregisteredCommittedEvent()
    {
        var appender = new RecordingAuditTrailAppender();
        var materializer = new CommittedAuditArtifactMaterializer<TestContext>(
            new AuditCommittedEventTranslatorRegistry([new StringValueAuditTranslator()]),
            appender,
            new FixedClock(DateTimeOffset.Parse("2026-07-03T08:00:00+00:00")));

        await materializer.ProjectAsync(
            new TestContext(),
            BuildEnvelope("outer-command-1", "state-event-1", 42, new Int32Value { Value = 1 }));

        appender.Records.Should().BeEmpty();
    }

    [Fact]
    public void Registry_ShouldRejectDuplicateExactTypeUrl()
    {
        Action act = () => _ = new AuditCommittedEventTranslatorRegistry(
            [new StringValueAuditTranslator(), new StringValueAuditTranslator()]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate audit committed-event translator*");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIsolateThrowingTranslator()
    {
        var appender = new RecordingAuditTrailAppender();
        var materializer = new CommittedAuditArtifactMaterializer<TestContext>(
            new AuditCommittedEventTranslatorRegistry([new ThrowingStringValueAuditTranslator()]),
            appender,
            new FixedClock(DateTimeOffset.Parse("2026-07-03T08:00:00+00:00")));

        await materializer.ProjectAsync(
            new TestContext(),
            BuildEnvelope("outer-command-1", "state-event-1", 42, new StringValue { Value = "payload" }));

        appender.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_WhenAuditDocumentStoreIsMissing_ShouldReturnStoreUnavailable()
    {
        var appender = new ProjectionAuditTrailAppender([], []);

        var result = await appender.AppendAsync(new AuditRecord { AuditId = "audit-1" });

        result.Status.Should().Be(AuditTrailAppendStatus.StoreUnavailable);
        result.AuditId.Should().Be("audit-1");
        result.Message.Should().Contain("not registered");
    }

    private static EventEnvelope BuildEnvelope(
        string envelopeId,
        string eventId,
        long version,
        IMessage payload,
        string commandId = "",
        string requestId = "",
        string correlationId = "")
    {
        var envelope = new EventEnvelope
        {
            Id = envelopeId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-03T08:01:00+00:00")),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId,
            },
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = "actor-1",
                    EventId = eventId,
                    Version = version,
                    EventData = Any.Pack(payload),
                },
            }),
        };
        if (!string.IsNullOrWhiteSpace(commandId))
            envelope.Propagation.Baggage.Add("command_id", commandId);
        if (!string.IsNullOrWhiteSpace(requestId))
            envelope.Propagation.Baggage.Add("request_id", requestId);
        return envelope;
    }

    private sealed class StringValueAuditTranslator : IAuditCommittedEventTranslator
    {
        public static readonly string TypeUrl = AuditCommittedEventTypeUrl.FromDescriptor(StringValue.Descriptor);

        public string EventTypeUrl => TypeUrl;

        public IReadOnlyList<AuditRecord> Translate(CommittedAuditTranslationContext context, Any eventPayload) =>
        [
            CommittedAuditRecordFactory.CreateSystemRecord(
                context,
                new CommittedAuditSeed(
                    "test.string.committed",
                    "test",
                    eventPayload.Unpack<StringValue>().Value,
                    "scope-1",
                    CommandId: context.CommandId,
                    RequestId: context.RequestId,
                    CorrelationId: context.CorrelationId,
                    ResultSummary: "String value committed."))
        ];
    }

    private sealed class ThrowingStringValueAuditTranslator : IAuditCommittedEventTranslator
    {
        public string EventTypeUrl => StringValueAuditTranslator.TypeUrl;

        public IReadOnlyList<AuditRecord> Translate(CommittedAuditTranslationContext context, Any eventPayload) =>
            throw new InvalidOperationException("translator failed");
    }

    private sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendResult> AppendAsync(AuditRecord record, CancellationToken ct = default)
        {
            Records.Add(record.Clone());
            return Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
        }
    }

    private sealed class TestContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = "actor-1";

        public string ProjectionKind { get; init; } = "test";
    }

    private sealed class FixedClock : IProjectionClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
