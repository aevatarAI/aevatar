using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

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

    private static async Task<IHost> StartSiloHostAsync()
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
                });
            })
            .Build());
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
