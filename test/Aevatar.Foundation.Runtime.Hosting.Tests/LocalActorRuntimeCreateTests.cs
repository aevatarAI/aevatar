using System.Collections.Concurrent;
using System.Threading;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class LocalActorRuntimeCreateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LinkAsync_WhenFleetAuthorityIsEitherEndpoint_ShouldReject(bool authorityIsParent)
    {
        var runtime = CreateRuntime();
        var parentId = authorityIsParent
            ? RuntimeFleetCapabilityAuthorityIdentity.ActorId
            : "ordinary-parent";
        var childId = authorityIsParent
            ? "ordinary-child"
            : RuntimeFleetCapabilityAuthorityIdentity.ActorId;

        var act = () => runtime.LinkAsync(parentId, childId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot participate in actor hierarchy links*");
    }

    [Fact]
    public async Task CreateAsync_ShouldReturnExistingActor_WhenSameIdAndTypeRequestedAgain()
    {
        var runtime = CreateRuntime();

        var first = await runtime.CreateAsync<SequentialAgent>("shared-id");
        var second = await runtime.CreateAsync<SequentialAgent>("shared-id");

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task CreateAsync_ShouldEmitSpawnActivityOnlyForFirstActivation()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var runtime = CreateRuntime();

        var first = await runtime.CreateAsync<SequentialAgent>("spawn-once");
        var second = await runtime.CreateAsync<SequentialAgent>("spawn-once");

        second.Should().BeSameAs(first);
        var spawnActivities = stopped
            .Where(activity =>
                activity.DisplayName == AevatarActivitySource.AgentSpawnActivityName &&
                string.Equals(
                    activity.GetTagItem(AevatarActivitySource.AgentIdTag) as string,
                    "spawn-once",
                    StringComparison.Ordinal))
            .ToList();

        spawnActivities.Should().ContainSingle();
        spawnActivities[0].GetTagItem(AevatarActivitySource.AgentTypeTag)
            .Should().Be("tests.sequential-agent");
    }

    [Fact]
    public async Task CreateAsync_ShouldMarkSpawnActivityError_WhenActivationThrows()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var runtime = CreateRuntime();

        Func<Task> act = () => runtime.CreateAsync<ThrowingActivateAgent>("spawn-error");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("activate boom");
        stopped
            .Where(activity =>
                activity.DisplayName == AevatarActivitySource.AgentSpawnActivityName &&
                string.Equals(
                    activity.GetTagItem(AevatarActivitySource.AgentIdTag) as string,
                    "spawn-error",
                    StringComparison.Ordinal))
            .Should()
            .ContainSingle()
            .Which
            .Status
            .Should()
            .Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task DestroyAsync_ShouldMarkDeactivateActivityError_WhenDeactivationThrows()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var runtime = CreateRuntime();
        await runtime.CreateAsync<ThrowingDeactivateAgent>("deactivate-error");

        Func<Task> act = () => runtime.DestroyAsync("deactivate-error");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("deactivate boom");
        stopped
            .Where(activity =>
                activity.DisplayName == AevatarActivitySource.AgentDeactivateActivityName &&
                string.Equals(
                    activity.GetTagItem(AevatarActivitySource.AgentIdTag) as string,
                    "deactivate-error",
                    StringComparison.Ordinal))
            .Should()
            .ContainSingle()
            .Which
            .Status
            .Should()
            .Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task CreateAsync_ShouldThrow_WhenSameIdAlreadyUsesDifferentType()
    {
        var runtime = CreateRuntime();
        await runtime.CreateAsync<SequentialAgent>("shared-id");

        var act = () => runtime.CreateAsync<AlternateSequentialAgent>("shared-id");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expected kind 'tests.alternate-sequential-agent'*");
    }

    [Fact]
    public async Task CreateByKindAsync_ShouldCreateActorFromRegisteredKind()
    {
        var runtime = CreateRuntime(services =>
            services.AddAevatarAgentKindRegistry(builder => builder.Register<KindRegisteredAgent>()));

        var actor = await runtime.CreateByKindAsync("tests.local-kind", "kind-actor");

        actor.Id.Should().Be("kind-actor");
        actor.Agent.Should().BeOfType<KindRegisteredAgent>();
    }

    [Fact]
    public async Task CreateByKindAsync_ShouldReturnExistingActor_WhenSameIdAndKindRequestedAgain()
    {
        var runtime = CreateRuntime(services =>
            services.AddAevatarAgentKindRegistry(builder => builder.Register<KindRegisteredAgent>()));

        var first = await runtime.CreateByKindAsync("tests.local-kind", "kind-actor");
        var second = await runtime.CreateByKindAsync("tests.local-kind", "kind-actor");

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task CreateByKindAsync_ShouldThrow_WhenSameIdAlreadyUsesDifferentKindImplementation()
    {
        var runtime = CreateRuntime(services =>
            services.AddAevatarAgentKindRegistry(builder => builder
                .Register<KindRegisteredAgent>()
                .Register<AlternateKindRegisteredAgent>()));
        await runtime.CreateByKindAsync("tests.local-kind", "kind-actor");

        var act = () => runtime.CreateByKindAsync("tests.alternate-local-kind", "kind-actor");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expected kind 'tests.alternate-local-kind'*");
    }

    [Fact]
    public async Task CreateByKindAsync_WhenConcurrentRequestsUseDifferentKinds_ShouldRejectMismatchedWinner()
    {
        var runtime = CreateRuntime(services =>
            services.AddAevatarAgentKindRegistry(builder => builder
                .Register<BlockingKindAAgent>()
                .Register<BlockingKindBAgent>()));
        using var gate = new ConstructorGate(expectedParticipants: 1);
        BlockingAgentGate.Current = gate;

        try
        {
            var firstTask = Task.Run(async () => await runtime.CreateByKindAsync("tests.blocking-kind-a", "kind-race-id"));
            var secondTask = Task.Run(async () => await runtime.CreateByKindAsync("tests.blocking-kind-b", "kind-race-id"));

            gate.WaitUntilReady();
            gate.Release();

            var outcomes = await Task.WhenAll(CaptureAsync(firstTask), CaptureAsync(secondTask));

            outcomes.Count(outcome => outcome.Actor is not null).Should().Be(1);
            outcomes.Count(outcome => outcome.Error is InvalidOperationException).Should().Be(1);
            outcomes.Single(outcome => outcome.Error is InvalidOperationException)
                .Error!
                .Message
                .Should()
                .Contain("expected kind");
        }
        finally
        {
            BlockingAgentGate.Current = null;
        }
    }

    [Fact]
    public async Task CreateByKindAsync_ShouldRemoveActorAndMarkSpawnActivityError_WhenActivationThrows()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var runtime = CreateRuntime(services =>
            services.AddAevatarAgentKindRegistry(builder => builder.Register<ThrowingActivateKindAgent>()));

        var act = () => runtime.CreateByKindAsync("tests.throwing-activate-kind", "kind-spawn-error");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("activate boom");
        (await runtime.GetAsync("kind-spawn-error")).Should().BeNull();
        stopped
            .Where(activity =>
                activity.DisplayName == AevatarActivitySource.AgentSpawnActivityName &&
                string.Equals(
                    activity.GetTagItem(AevatarActivitySource.AgentIdTag) as string,
                    "kind-spawn-error",
                    StringComparison.Ordinal))
            .Should()
            .ContainSingle()
            .Which
            .Status
            .Should()
            .Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task CreateAsync_WhenConcurrentRequestsUseSameType_ShouldReturnAuthoritativeActor()
    {
        var runtime = CreateRuntime();
        using var gate = new ConstructorGate(expectedParticipants: 1);
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
        using var gate = new ConstructorGate(expectedParticipants: 1);
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

    private static LocalActorRuntime CreateRuntime(Action<IServiceCollection>? configureServices = null)
    {
        var registry = new InMemoryStreamForwardingRegistry();
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            registry);
        var servicesBuilder = new ServiceCollection();
        servicesBuilder.AddAevatarAgentKindRegistry(builder => builder
            .Register<SequentialAgent>()
            .Register<AlternateSequentialAgent>()
            .Register<ThrowingActivateAgent>()
            .Register<ThrowingDeactivateAgent>()
            .Register<BlockingSameTypeAgent>()
            .Register<BlockingTypeAAgent>()
            .Register<BlockingTypeBAgent>());
        configureServices?.Invoke(servicesBuilder);
        var services = servicesBuilder.BuildServiceProvider();
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

    [GAgent("tests.sequential-agent")]
    private sealed class SequentialAgent : IAgent
    {
        public string Id => "sequential";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("sequential");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [GAgent("tests.alternate-sequential-agent")]
    private sealed class AlternateSequentialAgent : IAgent
    {
        public string Id => "alternate";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("alternate");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [GAgent("tests.throwing-activate-agent")]
    private sealed class ThrowingActivateAgent : IAgent
    {
        public string Id => "throwing-activate";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("throwing-activate");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("activate boom");
        }

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [GAgent("tests.throwing-deactivate-agent")]
    private sealed class ThrowingDeactivateAgent : IAgent
    {
        public string Id => "throwing-deactivate";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("throwing-deactivate");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("deactivate boom");
        }
    }

    [GAgent("tests.blocking-same-type-agent")]
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

    [GAgent("tests.blocking-type-a-agent")]
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

    [GAgent("tests.blocking-type-b-agent")]
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

    [GAgent("tests.local-kind")]
    private sealed class KindRegisteredAgent : IAgent
    {
        public string Id => "kind-registered";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("kind-registered");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [GAgent("tests.alternate-local-kind")]
    private sealed class AlternateKindRegisteredAgent : IAgent
    {
        public string Id => "alternate-kind-registered";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("alternate-kind-registered");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [GAgent("tests.throwing-activate-kind")]
    private sealed class ThrowingActivateKindAgent : IAgent
    {
        public string Id => "throwing-activate-kind";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("throwing-activate-kind");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("activate boom");
        }

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [GAgent("tests.blocking-kind-a")]
    private sealed class BlockingKindAAgent : IAgent
    {
        public BlockingKindAAgent()
        {
            BlockingAgentGate.Current!.ArriveAndWait();
        }

        public string Id => "blocking-kind-a";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("blocking-kind-a");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [GAgent("tests.blocking-kind-b")]
    private sealed class BlockingKindBAgent : IAgent
    {
        public BlockingKindBAgent()
        {
            BlockingAgentGate.Current!.ArriveAndWait();
        }

        public string Id => "blocking-kind-b";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("blocking-kind-b");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
