using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class LocalActorDeduplicationFailureTests
{
    [Fact]
    public async Task HandleEventAsync_AfterPropagatedFailure_ShouldReleaseReservationForSameEnvelope()
    {
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new InMemoryStreamForwardingRegistry());
        var agent = new AlwaysFailAgent();
        var deduplicator = new FailingForgetDeduplicator();
        var actor = new LocalActor(
            agent,
            "local-dedup-failure",
            streams,
            NullLogger.Instance,
            deduplicator: deduplicator);
        await actor.ActivateAsync();

        try
        {
            var envelope = new EventEnvelope
            {
                Id = "same-provider-attempt",
                Payload = Any.Pack(new StringValue { Value = "fail" }),
                Route = EnvelopeRouteSemantics.CreateDirect("test", actor.Id),
            };

            await actor.Invoking(x => x.HandleEventAsync(envelope))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("local handler failure");
            await actor.Invoking(x => x.HandleEventAsync(envelope))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("local handler failure");

            agent.Attempts.Should().Be(2);
            deduplicator.TryRecordAttempts.Should().Be(1,
                "the activation-scoped bypass must skip the residual reservation on redelivery");
            deduplicator.ForgetAttempts.Should().Be(2);
        }
        finally
        {
            await actor.DeactivateAsync();
        }
    }

    private sealed class FailingForgetDeduplicator : IEventDeduplicator
    {
        private readonly HashSet<string> _entries = [];

        public int TryRecordAttempts { get; private set; }
        public int ForgetAttempts { get; private set; }

        public Task<bool> TryRecordAsync(string eventId)
        {
            TryRecordAttempts++;
            return Task.FromResult(_entries.Add(eventId));
        }

        public Task ForgetAsync(string eventId)
        {
            _ = eventId;
            ForgetAttempts++;
            throw new InvalidOperationException("dedup release failure");
        }
    }

    private sealed class AlwaysFailAgent : IAgent
    {
        public int Attempts { get; private set; }
        public string Id => "local-always-fail";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            Attempts++;
            throw new InvalidOperationException("local handler failure");
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("local-always-fail");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
