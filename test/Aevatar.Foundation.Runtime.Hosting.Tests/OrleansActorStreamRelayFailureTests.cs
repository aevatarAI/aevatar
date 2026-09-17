using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorStreamRelayFailureTests
{
    [Fact]
    public async Task ProduceAsync_WhenMultipleRelayPublishesFail_ShouldAggregateAndTraverseRemainingBranches()
    {
        var harness = new RelayHarness();
        harness.FailPublish("bad-target-a", () => new InvalidOperationException("bad-target-a failed"));
        harness.FailPublish("bad-target-b", () => new InvalidOperationException("bad-target-b failed"));
        harness.Bind("actor-1", "bad-target-a");
        harness.Bind("actor-1", "good-target");
        harness.Bind("actor-1", "bad-target-b");
        harness.Bind("bad-target-a", "downstream-good-target");

        var act = () => harness.CreateStream().ProduceAsync(new StringValue { Value = "continue traversal" });

        var exception = await act.Should().ThrowAsync<EventPublicationException>();
        exception.Which.Outcome.Should().Be(EventPublicationFailureOutcome.OutcomeUncertain);
        var relayFailures = exception.Which.InnerException.Should().BeOfType<AggregateException>().Subject;
        relayFailures.InnerExceptions.Select(x => x.Message).Should().BeEquivalentTo(
            new[] { "bad-target-a failed", "bad-target-b failed" });
        harness.PublishedTo("good-target").Should().ContainSingle();
        harness.PublishedTo("downstream-good-target").Should().ContainSingle();
    }

    [Fact]
    public async Task ProduceAsync_WhenRelayPublishCancelsCallerToken_ShouldPropagateImmediately()
    {
        using var cts = new CancellationTokenSource();
        var harness = new RelayHarness();
        harness.FailPublish("canceling-target", () =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        });
        harness.Bind("actor-1", "canceling-target");
        harness.Bind("actor-1", "unreached-target");

        var act = () => harness.CreateStream().ProduceAsync(
            new StringValue { Value = "cancel" },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.PublishedTo("unreached-target").Should().BeEmpty();
    }

    private sealed class RelayHarness
    {
        private readonly Dictionary<string, List<StreamForwardingBinding>> _bindings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<Exception>> _publishFailures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<EventEnvelope>> _published = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IAsyncStream<EventEnvelope>> _streams = new(StringComparer.Ordinal);
        private readonly global::Orleans.Streams.IStreamProvider _streamProvider;
        private readonly IStreamForwardingRegistry _forwardingRegistry;

        public RelayHarness()
        {
            _streamProvider = Substitute.For<global::Orleans.Streams.IStreamProvider>();
            _streamProvider.GetStream<EventEnvelope>(Arg.Any<StreamId>())
                .Returns(call => ResolveStream(call.Arg<StreamId>().GetKeyAsString()));

            _forwardingRegistry = Substitute.For<IStreamForwardingRegistry>();
            _forwardingRegistry.ListBySourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(ResolveBindings(call.Arg<string>())));
        }

        public OrleansActorStream CreateStream() => new(
            streamId: "actor-1",
            streamNamespace: "aevatar.events",
            streamProvider: _streamProvider,
            forwardingRegistry: _forwardingRegistry);

        public void Bind(string sourceStreamId, string targetStreamId)
        {
            if (!_bindings.TryGetValue(sourceStreamId, out var bindings))
            {
                bindings = [];
                _bindings[sourceStreamId] = bindings;
            }

            bindings.Add(new StreamForwardingBinding
            {
                SourceStreamId = sourceStreamId,
                TargetStreamId = targetStreamId,
                ForwardingMode = StreamForwardingMode.HandleThenForward,
            });
        }

        public void FailPublish(string targetStreamId, Func<Exception> failureFactory) =>
            _publishFailures[targetStreamId] = failureFactory;

        public IReadOnlyList<EventEnvelope> PublishedTo(string targetStreamId) =>
            _published.TryGetValue(targetStreamId, out var messages) ? messages : [];

        private IReadOnlyList<StreamForwardingBinding> ResolveBindings(string sourceStreamId) =>
            _bindings.TryGetValue(sourceStreamId, out var bindings) ? bindings : [];

        private IAsyncStream<EventEnvelope> ResolveStream(string streamId)
        {
            if (_streams.TryGetValue(streamId, out var existing))
                return existing;

            var stream = Substitute.For<IAsyncStream<EventEnvelope>>();
            stream.OnNextAsync(Arg.Any<EventEnvelope>(), Arg.Any<StreamSequenceToken?>())
                .Returns(call => PublishAsync(streamId, call.Arg<EventEnvelope>()));
            _streams[streamId] = stream;
            return stream;
        }

        private Task PublishAsync(string streamId, EventEnvelope envelope)
        {
            if (_publishFailures.TryGetValue(streamId, out var failureFactory))
                throw failureFactory();

            if (!_published.TryGetValue(streamId, out var messages))
            {
                messages = [];
                _published[streamId] = messages;
            }

            messages.Add(envelope.Clone());
            return Task.CompletedTask;
        }
    }
}
