using System.Collections.Concurrent;
using System.Threading;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class LocalActorRuntimeCreateTests
{
    [Fact]
    public async Task CreateAsync_ShouldReturnExistingActor_WhenSameIdAndTypeRequestedAgain()
    {
        var runtime = CreateRuntime();

        var first = await runtime.CreateAsync<SequentialAgent>("shared-id");
        var second = await runtime.CreateAsync<SequentialAgent>("shared-id");

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task CreateAsync_ShouldThrow_WhenSameIdAlreadyUsesDifferentType()
    {
        var runtime = CreateRuntime();
        await runtime.CreateAsync<SequentialAgent>("shared-id");

        var act = () => runtime.CreateAsync<AlternateSequentialAgent>("shared-id");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expected*AlternateSequentialAgent*");
    }

    [Fact]
    public async Task CreateAsync_WhenConcurrentRequestsUseSameType_ShouldReturnAuthoritativeActor()
    {
        var runtime = CreateRuntime();
        using var gate = new ConstructorGate(expectedParticipants: 2);
        BlockingAgentGate.Current = gate;

        try
        {
            var firstTask = Task.Run(async () => await runtime.CreateAsync<BlockingSameTypeAgent>("race-id"));
            var secondTask = Task.Run(async () => await runtime.CreateAsync<BlockingSameTypeAgent>("race-id"));

            gate.WaitUntilReady();
            gate.Release();

            var first = await firstTask;
            var second = await secondTask;

            first.Should().BeSameAs(second);
        }
        finally
        {
            BlockingAgentGate.Current = null;
        }
    }

    [Fact]
    public async Task CreateAsync_WhenConcurrentRequestsUseDifferentTypes_ShouldRejectMismatchedWinner()
    {
        var runtime = CreateRuntime();
        using var gate = new ConstructorGate(expectedParticipants: 2);
        BlockingAgentGate.Current = gate;

        try
        {
            var firstTask = Task.Run(async () => await runtime.CreateAsync<BlockingTypeAAgent>("race-id"));
            var secondTask = Task.Run(async () => await runtime.CreateAsync<BlockingTypeBAgent>("race-id"));

            gate.WaitUntilReady();
            gate.Release();

            var outcomes = await Task.WhenAll(CaptureAsync(firstTask), CaptureAsync(secondTask));

            outcomes.Count(outcome => outcome.Actor is not null).Should().Be(1);
            outcomes.Count(outcome => outcome.Error is InvalidOperationException).Should().Be(1);
            outcomes.Single(outcome => outcome.Error is InvalidOperationException)
                .Error!
                .Message
                .Should()
                .Contain("expected");
        }
        finally
        {
            BlockingAgentGate.Current = null;
        }
    }

    private static LocalActorRuntime CreateRuntime()
    {
        var registry = new InMemoryStreamForwardingRegistry();
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            registry);
        var services = new ServiceCollection().BuildServiceProvider();
        return new LocalActorRuntime(streams, services, streams);
    }

    private static async Task<CreateOutcome> CaptureAsync(Task<IActor> task)
    {
        try
        {
            return new CreateOutcome(await task, null);
        }
        catch (Exception ex)
        {
            return new CreateOutcome(null, ex);
        }
    }

    private sealed record CreateOutcome(IActor? Actor, Exception? Error);

    private sealed class ConstructorGate : IDisposable
    {
        private readonly CountdownEvent _ready;
        private readonly ManualResetEventSlim _release = new(false);

        public ConstructorGate(int expectedParticipants)
        {
            _ready = new CountdownEvent(expectedParticipants);
        }

        public void ArriveAndWait()
        {
            _ready.Signal();
            _ready.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            _release.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        }

        public void WaitUntilReady()
        {
            _ready.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
            _ready.Dispose();
        }
    }

    private static class BlockingAgentGate
    {
        public static ConstructorGate? Current { get; set; }
    }

    private sealed class SequentialAgent : IAgent
    {
        public string Id => "sequential";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("sequential");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class AlternateSequentialAgent : IAgent
    {
        public string Id => "alternate";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("alternate");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class BlockingSameTypeAgent : IAgent
    {
        public BlockingSameTypeAgent()
        {
            BlockingAgentGate.Current!.ArriveAndWait();
        }

        public string Id => "blocking-same";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("blocking-same");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class BlockingTypeAAgent : IAgent
    {
        public BlockingTypeAAgent()
        {
            BlockingAgentGate.Current!.ArriveAndWait();
        }

        public string Id => "blocking-a";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("blocking-a");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class BlockingTypeBAgent : IAgent
    {
        public BlockingTypeBAgent()
        {
            BlockingAgentGate.Current!.ArriveAndWait();
        }

        public string Id => "blocking-b";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("blocking-b");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
