using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.Pipeline;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.Tests;

public class SelfEventEnvelopeFactoryTests
{
    [Fact]
    public void Create_ShouldValidateInputs_AndApplyPropagationDeliveryOverrides()
    {
        var inbound = new EventEnvelope
        {
            Id = "inbound-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-inbound",
                CausationEventId = "cause-inbound",
                Trace = new TraceContext
                {
                    TraceId = "trace-inbound",
                    SpanId = "span-inbound",
                },
            },
        };
        inbound.Propagation.Baggage["existing"] = "value";

        var envelope = SelfEventEnvelopeFactory.Create(
            "actor-1",
            new StringValue { Value = "payload" },
            inbound,
            new EventEnvelopePublishOptions
            {
                Propagation = new EventEnvelopePropagationOverrides
                {
                    CorrelationId = "corr-override",
                    CausationEventId = "cause-override",
                    Trace = new TraceContext
                    {
                        TraceId = "trace-override",
                        SpanId = "span-override",
                    },
                    Baggage =
                    {
                        ["mode"] = "override",
                    },
                },
                Delivery = new EventEnvelopeDeliveryOptions
                {
                    DeduplicationOperationId = "op-1",
                },
            });

        envelope.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        envelope.Payload.Unpack<StringValue>().Value.Should().Be("payload");
        envelope.Propagation.Should().NotBeSameAs(inbound.Propagation);
        envelope.Propagation.CorrelationId.Should().Be("corr-override");
        envelope.Propagation.CausationEventId.Should().Be("cause-override");
        envelope.Propagation.Trace.TraceId.Should().Be("trace-override");
        envelope.Propagation.Baggage["existing"].Should().Be("value");
        envelope.Propagation.Baggage["mode"].Should().Be("override");
        envelope.Runtime.Deduplication.OperationId.Should().Be("op-1");
    }

    [Fact]
    public void Create_ShouldSkipOptionalRuntimeFieldsWhenOptionsAreBlank()
    {
        var inbound = new EventEnvelope
        {
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-1",
            },
        };

        var envelope = SelfEventEnvelopeFactory.Create(
            "actor-2",
            new StringValue { Value = "payload" },
            inbound,
            new EventEnvelopePublishOptions
            {
                Propagation = new EventEnvelopePropagationOverrides(),
                Delivery = new EventEnvelopeDeliveryOptions
                {
                    DeduplicationOperationId = " ",
                },
            });

        envelope.Propagation.CorrelationId.Should().Be("corr-1");
        envelope.Propagation.CausationEventId.Should().BeEmpty();
        envelope.Runtime.Should().BeNull();
    }

    [Fact]
    public void Create_ShouldThrowForBlankActorIdOrNullEvent()
    {
        FluentActions.Invoking(() => SelfEventEnvelopeFactory.Create(" ", new StringValue()))
            .Should()
            .Throw<ArgumentException>();
        FluentActions.Invoking(() => SelfEventEnvelopeFactory.Create("actor-1", null!))
            .Should()
            .Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ScheduleSelfDurableTimeoutAsync_ShouldStripRuntimeRetryContext_AndPreservePropagation()
    {
        var scheduler = new RecordingCallbackScheduler();
        var agent = new RetrySchedulingAgent(scheduler);

        var envelope = TestHelper.Envelope(new PingEvent { Message = "retry" }, publisherId: "source-1");
        envelope.EnsurePropagation().CorrelationId = "corr-1";
        envelope.Propagation.EnsureTrace().TraceId = "trace-1";
        envelope.Propagation.Baggage["custom.key"] = "custom-value";
        envelope.EnsureRuntime().EnsureRetry().OriginEventId = "origin-1";
        envelope.Runtime.Retry.Attempt = 2;
        envelope.Runtime.Retry.LastErrorType = "InvalidOperationException";

        await agent.HandleEventAsync(envelope);

        var scheduled = scheduler.Timeouts.Should().ContainSingle().Subject;
        scheduled.TriggerEnvelope.Propagation.CorrelationId.Should().Be("corr-1");
        scheduled.TriggerEnvelope.Propagation.Trace.TraceId.Should().Be("trace-1");
        scheduled.TriggerEnvelope.Propagation.Baggage["custom.key"].Should().Be("custom-value");
        scheduled.TriggerEnvelope.Runtime.Should().BeNull();
    }

    private sealed class RetrySchedulingAgent : GAgentBase
    {
        public RetrySchedulingAgent(RecordingCallbackScheduler scheduler)
        {
            Services = new ServiceCollection()
                .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
                .BuildServiceProvider();
            InitializeId();
        }

        [EventHandler]
        public Task HandlePing(PingEvent evt)
        {
            return ScheduleSelfDurableTimeoutAsync(
                "self-timeout",
                TimeSpan.FromSeconds(1),
                new PongEvent { Reply = evt.Message });
        }
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Timeouts.Add(new RuntimeCallbackTimeoutRequest
            {
                ActorId = request.ActorId,
                CallbackId = request.CallbackId,
                DueTime = request.DueTime,
                TriggerEnvelope = request.TriggerEnvelope.Clone(),
            });
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
