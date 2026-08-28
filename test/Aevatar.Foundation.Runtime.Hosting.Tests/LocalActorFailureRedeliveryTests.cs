using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class LocalActorFailureRedeliveryTests
{
    [Fact]
    public async Task SuccessfulEnvelope_WithCompletionSourceAndNoScheduler_ShouldRemainSupported()
    {
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new InMemoryStreamForwardingRegistry());
        var actor = new LocalActor(
            new RetryCompletionSourceAgent(),
            "local-completion-without-scheduler",
            streams,
            NullLogger.Instance);
        await actor.ActivateAsync();

        try
        {
            await actor.HandleEventAsync(CreateCompletionEnvelope(actor.Id));
        }
        finally
        {
            await actor.DeactivateAsync();
        }
    }

    [Fact]
    public async Task SuccessfulEnvelope_WithCompletionScheduler_ShouldCompleteAuthenticatedCursor()
    {
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new InMemoryStreamForwardingRegistry());
        var scheduler = new RecordingCompletionScheduler();
        var actor = new LocalActor(
            new RetryCompletionSourceAgent(),
            "local-completion-with-scheduler",
            streams,
            NullLogger.Instance,
            callbackScheduler: scheduler);
        await actor.ActivateAsync();

        try
        {
            await actor.HandleEventAsync(CreateCompletionEnvelope(actor.Id));

            scheduler.Completions.Should().ContainSingle().Which.Should().Be(
                (actor.Id, new RuntimeEnvelopeRetryCoalescingCursor("source-scope", 17, "evt-17")));
        }
        finally
        {
            await actor.DeactivateAsync();
        }
    }

    [Fact]
    public async Task HandleEventAsync_AfterPropagatedFailure_ShouldInvokeHandlerForSameEnvelopeAgain()
    {
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new InMemoryStreamForwardingRegistry());
        var agent = new AlwaysFailAgent();
        var actor = new LocalActor(
            agent,
            "local-failure-redelivery",
            streams,
            NullLogger.Instance);
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
        }
        finally
        {
            await actor.DeactivateAsync();
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

    private static EventEnvelope CreateCompletionEnvelope(string actorId) => new()
    {
        Id = "handled-observation",
        Payload = Any.Pack(new StringValue { Value = "handled" }),
        Route = EnvelopeRouteSemantics.CreateDirect("source-scope", actorId),
    };

    private sealed class RetryCompletionSourceAgent : IAgent,
        IRuntimeEnvelopeRetryCoalescingCompletionSource
    {
        public string Id => "local-retry-completion-source";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public RuntimeEnvelopeRetryCoalescingCursor? ResolveHandledRetryCoalescingCursor(
            EventEnvelope envelope) =>
            string.Equals(envelope.Id, "handled-observation", StringComparison.Ordinal)
                ? new RuntimeEnvelopeRetryCoalescingCursor("source-scope", 17, "evt-17")
                : null;

        public Task<string> GetDescriptionAsync() =>
            Task.FromResult("local-retry-completion-source");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingCompletionScheduler :
        IActorRuntimeCallbackScheduler,
        IRuntimeEnvelopeRetryCoalescingCallbackScheduler
    {
        public List<(string ActorId, RuntimeEnvelopeRetryCoalescingCursor Cursor)> Completions { get; } = [];

        public Task CompleteRuntimeEnvelopeRetryAsync(
            string actorId,
            RuntimeEnvelopeRetryCoalescingCursor cursor,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Completions.Add((actorId, cursor));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
