using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class LocalActorDispatchAdmissionTests
{
    [Fact]
    public async Task DispatchAsync_ShouldReturnAdmissionBeforeActorHandlerCompletes()
    {
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new InMemoryStreamForwardingRegistry());
        var services = new ServiceCollection()
            .AddAevatarAgentKindRegistry(builder => builder.Register<GateAgent>())
            .BuildServiceProvider();
        var runtime = new LocalActorRuntime(streams, services, streams);
        await runtime.CreateAsync<GateAgent>("admission-actor");
        var dispatchPort = new LocalActorDispatchPort(runtime, streams);
        var envelope = new EventEnvelope
        {
            Id = "cmd-admitted",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new StringValue { Value = "payload" }),
            Route = EnvelopeRouteSemantics.CreateDirect("tester", "admission-actor"),
            Propagation = new EnvelopePropagation { CorrelationId = "corr-admitted" },
        };

        var admissionTask = dispatchPort.DispatchAsync("admission-actor", envelope, CancellationToken.None);

        var admission = await admissionTask.WaitAsync(TimeSpan.FromSeconds(1));
        admission.Accepted.Should().BeTrue();
        admission.CommandId.Should().Be("cmd-admitted");
        admission.CorrelationId.Should().Be("corr-admitted");
        admission.ActorId.Should().Be("admission-actor");
        GateAgent.Handled.Task.IsCompleted.Should().BeFalse();

        GateAgent.Release.SetResult();
        await GateAgent.Handled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [GAgent("tests.gate-agent")]
    private sealed class GateAgent : IAgent
    {
        public static TaskCompletionSource Release { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static TaskCompletionSource Handled { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "gate-agent";

        public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            await Release.Task.WaitAsync(ct);
            Handled.TrySetResult();
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("gate");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default)
        {
            Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
