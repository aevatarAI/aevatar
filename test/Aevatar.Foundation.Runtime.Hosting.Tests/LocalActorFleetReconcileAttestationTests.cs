using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class LocalActorFleetReconcileAttestationTests
{
    /// <summary>
    /// The scheduler verifier is genuinely asynchronous on Orleans (a grain call), so the
    /// attestation must be bound in the actor's own frame after the await returns; an
    /// AsyncLocal assigned inside the awaited helper never reaches the handler.
    /// </summary>
    [Fact]
    public async Task ReconcileEnvelope_WhenVerifierCompletesAsynchronously_ShouldExposeAttestationToHandler()
    {
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new InMemoryStreamForwardingRegistry());
        var accessor = new AsyncLocalRuntimeFleetReconcileDeliveryAttestationAccessor();
        var agent = new AttestationObservingAgent(accessor);
        var expected = new RuntimeFleetReconcileDeliveryAttestation("reconcile-1", 3, 7, RuntimeCallbackSlotEpoch.OrleansSchedulerV2);
        var actor = new LocalActor(
            agent,
            RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            streams,
            NullLogger.Instance,
            fleetReconcileVerifier: new AsynchronousVerifier(expected),
            fleetReconcileAttestationBinder: accessor);
        await actor.ActivateAsync();

        await actor.HandleEventAsync(new EventEnvelope
        {
            Id = "reconcile-1",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new RuntimeFleetReconcileRequested()),
            Route = EnvelopeRouteSemantics.CreateDirect("scheduler", RuntimeFleetCapabilityAuthorityIdentity.ActorId),
        });

        var observed = await agent.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().Be(expected);
        accessor.Current.Should().BeNull("the binding must not leak past the handled envelope");
        await actor.DeactivateAsync();
    }

    private sealed class AsynchronousVerifier(RuntimeFleetReconcileDeliveryAttestation attestation)
        : IRuntimeFleetReconcileDeliveryVerifier
    {
        public async Task<RuntimeFleetReconcileDeliveryAttestation?> VerifyAsync(
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            // Force a real continuation so the caller's execution context is captured
            // before the verifier returns, exactly like a grain call.
            await Task.Yield();
            return string.Equals(envelope.Id, attestation.EnvelopeId, StringComparison.Ordinal)
                ? attestation
                : null;
        }
    }

    private sealed class AttestationObservingAgent(
        IRuntimeFleetReconcileDeliveryAttestationReader reader) : IAgent
    {
        public TaskCompletionSource<RuntimeFleetReconcileDeliveryAttestation?> Observed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => RuntimeFleetCapabilityAuthorityIdentity.ActorId;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            Observed.TrySetResult(reader.Current);
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("attestation-observer");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
