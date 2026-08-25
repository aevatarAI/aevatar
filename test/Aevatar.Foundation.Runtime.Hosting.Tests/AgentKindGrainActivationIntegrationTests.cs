using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

/// <summary>
/// Issue #498 Phase 1 — end-to-end grain activation tests that boot a real
/// Orleans silo so <c>RuntimeActorGrain</c>'s kind-driven activation path
/// runs in a representative environment (not just unit-tested helpers).
///
/// Covers the activation paths surfaced in PR review: <c>InitializeAgentByKindAsync</c>
/// binds via the registry, persists canonical kind, and the row reactivates
/// correctly on a second grain look-up.
/// </summary>
public sealed class AgentKindGrainActivationIntegrationTests
{
    [Fact]
    public async Task InitializeAgentByKindAsync_PersistsCanonicalKind()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var host = await StartSiloHostAsync();

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);

            var initialized = await grain.InitializeAgentByKindAsync("integrationtests.canonical");
            initialized.Should().BeTrue();

            (await grain.IsInitializedAsync()).Should().BeTrue();
            (await grain.GetAgentKindAsync()).Should().Be("integrationtests.canonical");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ResumeFromPersistedIdentity_ReactivatesByKindOnSecondGrainLookup()
    {
        // Two grain references for the same actor id share state. Activate
        // once via the kind path, deactivate the in-memory agent, and verify
        // the next reference re-resolves identity from the persisted row.
        var actorId = $"actor-{Guid.NewGuid():N}";
        var host = await StartSiloHostAsync();

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var first = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
            (await first.InitializeAgentByKindAsync("integrationtests.canonical")).Should().BeTrue();
            await first.DeactivateAsync();

            var second = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
            (await second.IsInitializedAsync()).Should().BeTrue();
            (await second.GetAgentKindAsync()).Should().Be("integrationtests.canonical");

            // Probe the live agent: GetDescriptionAsync forwards to the bound
            // _agent instance, so a stale Identity row without re-binding
            // would surface as the grain's "Uninitialized:..." fallback. This
            // makes the test exercise the actual resume → bind path, not
            // just the persisted state slots.
            (await second.GetDescriptionAsync()).Should().Be(nameof(IntegrationFixtureCanonicalAgent));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task InitializeAgentByKindAsync_ReturnsFalseForUnknownKind()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var host = await StartSiloHostAsync();

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);

            var initialized = await grain.InitializeAgentByKindAsync("integrationtests.never-registered");
            initialized.Should().BeFalse();
            (await grain.IsInitializedAsync()).Should().BeFalse();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task InitializeAgentByKindAsync_WhenFirstSelfSubscriptionFails_ShouldRetryBoundInitialization()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var streamProvider = new FailFirstSubscriptionStreamProvider();
        var host = await StartSiloHostAsync(streamProvider);

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);

            await FluentActions.Invoking(() =>
                    grain.InitializeAgentByKindAsync("integrationtests.canonical"))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("synthetic first self-stream subscription failure");
            streamProvider.SubscribeAttemptCount.Should().Be(1);

            (await grain.GetAgentKindAsync()).Should().Be("integrationtests.canonical");
            (await grain.GetDescriptionAsync()).Should()
                .Be(nameof(IntegrationFixtureCanonicalAgent));
            streamProvider.SubscribeAttemptCount.Should().Be(1,
                "reading the bound agent must not hide a subscription retry");

            (await grain.InitializeAgentByKindAsync("integrationtests.canonical"))
                .Should().BeTrue();
            streamProvider.SubscribeAttemptCount.Should().Be(2);

            (await grain.InitializeAgentByKindAsync("integrationtests.canonical"))
                .Should().BeTrue();
            streamProvider.SubscribeAttemptCount.Should().Be(2,
                "an established self-stream subscription is idempotent");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static async Task<IHost> StartSiloHostAsync(
        FailFirstSubscriptionStreamProvider? subscriptionStreamProvider = null)
    {
        var serviceId = $"aevatar-agent-kind-it-service-{Guid.NewGuid():N}";
        var clusterId = $"aevatar-agent-kind-it-cluster-{Guid.NewGuid():N}";

        return await SharedOrleansPortAllocator.StartHostAsync(ports => Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(ports.SiloPort, ports.GatewayPort, null, serviceId, clusterId);
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
                siloBuilder.ConfigureServices(services =>
                {
                    // Register the integration-fixture kind on top of the
                    // default registry wired by AddAevatarFoundationRuntimeOrleans.
                    services.AddAevatarAgentKindRegistry(builder =>
                        builder.Register<IntegrationFixtureCanonicalAgent>());
                    if (subscriptionStreamProvider != null)
                    {
                        services.AddKeyedSingleton<global::Orleans.Streams.IStreamProvider>(
                            subscriptionStreamProvider.Name,
                            subscriptionStreamProvider);
                        services.Replace(ServiceDescriptor.Singleton(new AevatarOrleansRuntimeOptions
                        {
                            StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory,
                            StreamProviderName = subscriptionStreamProvider.Name,
                            PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory,
                        }));
                        var fleetBootstrap = services.SingleOrDefault(descriptor =>
                            descriptor.ServiceType == typeof(ILifecycleParticipant<ISiloLifecycle>) &&
                            descriptor.ImplementationType ==
                            typeof(RuntimeFleetAuthoritySiloLifecycleParticipant));
                        if (fleetBootstrap != null)
                            services.Remove(fleetBootstrap);
                    }
                });
            })
            .Build());
    }

    private sealed class FailFirstSubscriptionStreamProvider
        : global::Orleans.Streams.IStreamProvider
    {
        private readonly SubscriptionAsyncStream _stream;

        public FailFirstSubscriptionStreamProvider()
        {
            _stream = new SubscriptionAsyncStream(this);
        }

        public string Name => "fail-first-self-subscription";

        public bool IsRewindable => false;

        public int SubscribeAttemptCount { get; private set; }

        public IAsyncStream<T> GetStream<T>(StreamId streamId)
        {
            _stream.StreamId = streamId;
            return (IAsyncStream<T>)(object)_stream;
        }

        private sealed class SubscriptionAsyncStream(FailFirstSubscriptionStreamProvider owner)
            : IAsyncStream<EventEnvelope>
        {
            public bool IsRewindable => false;

            public string ProviderName => owner.Name;

            public StreamId StreamId { get; set; }

            public Task<StreamSubscriptionHandle<EventEnvelope>> SubscribeAsync(
                IAsyncObserver<EventEnvelope> observer)
            {
                _ = observer;
                owner.SubscribeAttemptCount++;
                if (owner.SubscribeAttemptCount == 1)
                {
                    throw new InvalidOperationException(
                        "synthetic first self-stream subscription failure");
                }

                return Task.FromResult<StreamSubscriptionHandle<EventEnvelope>>(
                    new SubscriptionHandle(StreamId, ProviderName));
            }

            public Task<StreamSubscriptionHandle<EventEnvelope>> SubscribeAsync(
                IAsyncObserver<EventEnvelope> observer,
                StreamSequenceToken? token,
                string? filterData = null) => SubscribeAsync(observer);

            public Task<StreamSubscriptionHandle<EventEnvelope>> SubscribeAsync(
                IAsyncBatchObserver<EventEnvelope> observer) => throw new NotSupportedException();

            public Task<StreamSubscriptionHandle<EventEnvelope>> SubscribeAsync(
                IAsyncBatchObserver<EventEnvelope> observer,
                StreamSequenceToken? token) => throw new NotSupportedException();

            public Task<IList<StreamSubscriptionHandle<EventEnvelope>>> GetAllSubscriptionHandles() =>
                Task.FromResult<IList<StreamSubscriptionHandle<EventEnvelope>>>([]);

            public Task OnNextAsync(
                EventEnvelope item,
                StreamSequenceToken? token = null) => Task.CompletedTask;

            public Task OnNextBatchAsync(
                IEnumerable<EventEnvelope> batch,
                StreamSequenceToken? token = null) => Task.CompletedTask;

            public Task OnCompletedAsync() => Task.CompletedTask;

            public Task OnErrorAsync(Exception ex) => Task.CompletedTask;

            public bool Equals(IAsyncStream<EventEnvelope>? other) =>
                ReferenceEquals(this, other);

            public int CompareTo(IAsyncStream<EventEnvelope>? other) =>
                ReferenceEquals(this, other) ? 0 : 1;
        }

        private sealed class SubscriptionHandle(StreamId streamId, string providerName)
            : StreamSubscriptionHandle<EventEnvelope>
        {
            public override Guid HandleId { get; } = Guid.NewGuid();

            public override StreamId StreamId { get; } = streamId;

            public override string ProviderName { get; } = providerName;

            public override Task UnsubscribeAsync() => Task.CompletedTask;

            public override Task<StreamSubscriptionHandle<EventEnvelope>> ResumeAsync(
                IAsyncObserver<EventEnvelope> observer,
                StreamSequenceToken? token = null) =>
                Task.FromResult<StreamSubscriptionHandle<EventEnvelope>>(this);

            public override Task<StreamSubscriptionHandle<EventEnvelope>> ResumeAsync(
                IAsyncBatchObserver<EventEnvelope> observer,
                StreamSequenceToken? token = null) =>
                Task.FromResult<StreamSubscriptionHandle<EventEnvelope>>(this);

            public override bool Equals(StreamSubscriptionHandle<EventEnvelope>? other) =>
                other is SubscriptionHandle handle && handle.HandleId == HandleId;
        }
    }
}

[GAgent("integrationtests.canonical")]
public sealed class IntegrationFixtureCanonicalAgent : IAgent
{
    public string Id { get; } = "integration-fixture";

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult(nameof(IntegrationFixtureCanonicalAgent));

    public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<Type>>(Array.Empty<Type>());

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
}
