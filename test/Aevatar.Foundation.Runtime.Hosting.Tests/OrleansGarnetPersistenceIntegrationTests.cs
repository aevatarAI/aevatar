using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansGarnetPersistenceIntegrationTests
{
    private static readonly TimeSpan HostLifecycleTimeout = TimeSpan.FromSeconds(30);

    [GarnetIntegrationFact]
    public async Task GrainState_ShouldPersistAcrossSiloRestart_WhenUsingGarnetStorage()
    {
        var garnetConnectionString = RequireGarnetConnectionString();
        var actorId = $"actor-{Guid.NewGuid():N}";
        var serviceId = $"aevatar-orleans-garnet-service-{Guid.NewGuid():N}";
        var clusterId = $"aevatar-orleans-garnet-cluster-{Guid.NewGuid():N}";

        var firstHost = await StartSiloHostAsync(
            garnetConnectionString,
            clusterId,
            serviceId);

        try
        {
            var grainFactory = firstHost.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
            var initialized = await grain.InitializeAgentByKindAsync("tests.recording-garnet-persistence");
            initialized.Should().BeTrue();

            await grain.SetParentAsync("parent-garnet");
            await grain.AddChildAsync("child-garnet-1");
            await grain.AddChildAsync("child-garnet-2");

            (await grain.IsInitializedAsync()).Should().BeTrue();
            (await grain.GetParentAsync()).Should().Be("parent-garnet");
            (await grain.GetChildrenAsync()).Should().BeEquivalentTo("child-garnet-1", "child-garnet-2");
        }
        finally
        {
            await firstHost.StopAsync().WaitAsync(HostLifecycleTimeout);
            firstHost.Dispose();
        }

        var secondHost = await StartSiloHostAsync(
            garnetConnectionString,
            clusterId,
            serviceId);

        try
        {
            var grainFactory = secondHost.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);

            (await grain.IsInitializedAsync()).Should().BeTrue();
            (await grain.GetParentAsync()).Should().Be("parent-garnet");
            (await grain.GetChildrenAsync()).Should().BeEquivalentTo("child-garnet-1", "child-garnet-2");

            await grain.PurgeAsync();
            (await grain.IsInitializedAsync()).Should().BeFalse();
        }
        finally
        {
            await secondHost.StopAsync().WaitAsync(HostLifecycleTimeout);
            secondHost.Dispose();
        }
    }

    [GarnetIntegrationFact]
    public async Task StatefulAgentEventSourcedState_ShouldPersistAcrossSiloRestart_WhenUsingGarnetStorage()
    {
        var garnetConnectionString = RequireGarnetConnectionString();
        var actorId = $"stateful-actor-{Guid.NewGuid():N}";
        var serviceId = $"aevatar-orleans-garnet-stateful-service-{Guid.NewGuid():N}";
        var clusterId = $"aevatar-orleans-garnet-stateful-cluster-{Guid.NewGuid():N}";

        var firstHost = await StartSiloHostAsync(
            garnetConnectionString,
            clusterId,
            serviceId);

        try
        {
            var grainFactory = firstHost.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);

            (await grain.InitializeAgentByKindAsync("tests.recording-garnet-stateful")).Should().BeTrue();
            (await grain.GetDescriptionAsync()).Should().Be("activation-count:0");
            await grain.HandleEnvelopeAsync(CreateDirectEnvelope(actorId, "activated"));
            (await grain.GetDescriptionAsync()).Should().Be("activation-count:1");

            await grain.DeactivateAsync();
        }
        finally
        {
            await firstHost.StopAsync().WaitAsync(HostLifecycleTimeout);
            firstHost.Dispose();
        }

        var secondHost = await StartSiloHostAsync(
            garnetConnectionString,
            clusterId,
            serviceId);

        try
        {
            var grainFactory = secondHost.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);

            (await grain.IsInitializedAsync()).Should().BeTrue();
            (await grain.GetDescriptionAsync()).Should().Be("activation-count:1");
            await grain.HandleEnvelopeAsync(CreateDirectEnvelope(actorId, "activated"));
            (await grain.GetDescriptionAsync()).Should().Be("activation-count:2");

            await grain.PurgeAsync();
            (await grain.IsInitializedAsync()).Should().BeFalse();
        }
        finally
        {
            await secondHost.StopAsync().WaitAsync(HostLifecycleTimeout);
            secondHost.Dispose();
        }
    }

    private static async Task<IHost> StartSiloHostAsync(
        string garnetConnectionString,
        string clusterId,
        string serviceId) =>
        await SharedOrleansPortAllocator.StartHostAsync(ports => Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: ports.SiloPort,
                    gatewayPort: ports.GatewayPort,
                    serviceId: serviceId,
                    clusterId: clusterId);
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
                    options.GarnetConnectionString = garnetConnectionString;
                });
                siloBuilder.ConfigureServices(services =>
                {
                    services.AddAevatarAgentKindRegistry(builder => builder
                        .Register<RecordingGarnetPersistenceAgent>()
                        .Register<RecordingGarnetStatefulAgent>());
                });
            })
            .Build(), HostLifecycleTimeout);

    private static byte[] CreateDirectEnvelope(string actorId, string payload) =>
        new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new StringValue { Value = payload }),
            Route = EnvelopeRouteSemantics.CreateDirect("garnet-test", actorId),
        }.ToByteArray();

    private static string RequireGarnetConnectionString() =>
        Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Missing AEVATAR_TEST_GARNET_CONNECTION_STRING.");

    [GAgent("tests.recording-garnet-persistence")]
    private sealed class RecordingGarnetPersistenceAgent : IAgent
    {
        public string Id => "recording-garnet-persistence-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() =>
            Task.FromResult("recording-garnet-persistence-agent");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    [GAgent("tests.recording-garnet-stateful")]
    private sealed class RecordingGarnetStatefulAgent : GAgentBase<Int32Value>
    {
        [EventHandler]
        public Task HandleActivated(StringValue evt) =>
            PersistDomainEventAsync(evt.Clone(), CancellationToken.None);

        protected override Int32Value TransitionState(Int32Value current, IMessage evt)
            => StateTransitionMatcher
                .Match(current, evt)
                .On<StringValue>((state, payload) => payload.Value == "activated"
                    ? new Int32Value { Value = state.Value + 1 }
                    : state)
                .OrCurrent();

        public override Task<string> GetDescriptionAsync() =>
            Task.FromResult($"activation-count:{State.Value}");
    }
}
