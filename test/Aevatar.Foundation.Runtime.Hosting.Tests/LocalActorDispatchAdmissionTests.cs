using System.Threading.Channels;
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
        var dispatchPort = new LocalActorDispatchPort(runtime);
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
        var handled = await GateAgent.Handled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        handled.Id.Should().Be("cmd-admitted");
        handled.Should().NotBeSameAs(envelope);
    }

    [Fact]
    public async Task DispatchAsync_ShouldProcessRapidDispatchesInAdmissionOrder()
    {
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new InMemoryStreamForwardingRegistry());
        var services = new ServiceCollection()
            .AddAevatarAgentKindRegistry(builder => builder.Register<OrderedAgent>())
            .BuildServiceProvider();
        var runtime = new LocalActorRuntime(streams, services, streams);
        await runtime.CreateAsync<OrderedAgent>("ordered-actor");
        var dispatchPort = new LocalActorDispatchPort(runtime);

        var first = CreateEnvelope("cmd-1", "ordered-actor");
        var second = CreateEnvelope("cmd-2", "ordered-actor");

        await dispatchPort.DispatchAsync("ordered-actor", first, CancellationToken.None);
        await dispatchPort.DispatchAsync("ordered-actor", second, CancellationToken.None);

        var handledFirst = await OrderedAgent.Handled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        var handledSecond = await OrderedAgent.Handled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        handledFirst.Should().Be("cmd-1");
        handledSecond.Should().Be("cmd-2");
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectMissingActor()
    {
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            new InMemoryStreamForwardingRegistry());
        var runtime = new LocalActorRuntime(streams, new ServiceCollection().BuildServiceProvider(), streams);
        var dispatchPort = new LocalActorDispatchPort(runtime);
        var envelope = new EventEnvelope
        {
            Id = "cmd-missing",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        };

        var act = () => dispatchPort.DispatchAsync("missing-actor", envelope, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Actor missing-actor not found.");
    }

    private static EventEnvelope CreateEnvelope(string id, string targetActorId) =>
        new()
        {
            Id = id,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new StringValue { Value = "payload" }),
            Route = EnvelopeRouteSemantics.CreateDirect("tester", targetActorId),
            Propagation = new EnvelopePropagation { CorrelationId = $"corr-{id}" },
        };

    [GAgent("tests.ordered-agent")]
    private sealed class OrderedAgent : IAgent
    {
        public static Channel<string> Handled { get; private set; } = Channel.CreateUnbounded<string>();

        public string Id => "ordered-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            Handled.Writer.TryWrite(envelope.Id);
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("ordered");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default)
        {
            Handled = Channel.CreateUnbounded<string>();
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [GAgent("tests.gate-agent")]
    private sealed class GateAgent : IAgent
    {
        public static TaskCompletionSource Release { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static TaskCompletionSource<EventEnvelope> Handled { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "gate-agent";

        public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            await Release.Task.WaitAsync(ct);
            Handled.TrySetResult(envelope);
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("gate");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default)
        {
            Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Handled = new TaskCompletionSource<EventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
