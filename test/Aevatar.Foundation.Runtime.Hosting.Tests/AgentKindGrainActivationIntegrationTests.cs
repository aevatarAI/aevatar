using System.Net;
using System.Net.Sockets;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Compatibility;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
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
/// binds via the registry, persists canonical kind + AgentTypeName mirror,
/// and the row reactivates correctly on a second grain look-up. The legacy
/// CLR-name → kind lazy-tag path is exercised via <c>InitializeAgentAsync</c>
/// with a class registered through <c>[GAgent]</c>.
/// </summary>
public sealed class AgentKindGrainActivationIntegrationTests
{
    [Fact]
    public async Task InitializeAgentByKindAsync_PersistsCanonicalKindAndMirrorsAgentTypeName()
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
            // The AgentTypeName mirror must be populated so older runtime
            // pods on the same cluster can still resolve the row through
            // their CLR-name lookup during the Phase 1/2 mixed-version window.
            (await grain.GetAgentTypeNameAsync())
                .Should().Be(typeof(IntegrationFixtureCanonicalAgent).FullName);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task InitializeAgentByKindAsync_WithLegacyAlias_PersistsCanonicalKind()
    {
        // P2: the caller passes a legacy alias; the persisted Identity.Kind
        // must be the canonical kind from the registry, not the deprecated
        // token. Otherwise removing the alias later would orphan the row.
        var actorId = $"actor-{Guid.NewGuid():N}";
        var host = await StartSiloHostAsync();

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);

            var initialized = await grain.InitializeAgentByKindAsync("integrationtests.deprecated-alias");
            initialized.Should().BeTrue();

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
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task InitializeAgentAsync_WithLegacyClrName_LazyTagsIdentityKind()
    {
        // Older callers still pass a CLR full name. The grain must resolve
        // via the registry's [GAgent] / [LegacyClrTypeName] lookup, lazy-tag
        // Identity.Kind on the row, and preserve AgentTypeName so older
        // pods reading the row continue to function.
        var actorId = $"actor-{Guid.NewGuid():N}";
        var host = await StartSiloHostAsync();

        try
        {
            var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
            var grain = grainFactory.GetGrain<IRuntimeActorGrain>(actorId);

            var initialized = await grain.InitializeAgentAsync(
                typeof(IntegrationFixtureCanonicalAgent).AssemblyQualifiedName!);
            initialized.Should().BeTrue();

            (await grain.GetAgentKindAsync()).Should().Be("integrationtests.canonical");
            (await grain.GetAgentTypeNameAsync())
                .Should().Be(typeof(IntegrationFixtureCanonicalAgent).AssemblyQualifiedName);
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
        var siloPort = ReserveTcpPort();
        var gatewayPort = ReserveTcpPort();
        var serviceId = $"aevatar-agent-kind-it-service-{Guid.NewGuid():N}";
        var clusterId = $"aevatar-agent-kind-it-cluster-{Guid.NewGuid():N}";

        var host = Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(siloPort, gatewayPort, null, serviceId, clusterId);
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
            .Build();

        await host.StartAsync();
        return host;
    }

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

[GAgent("integrationtests.canonical")]
[LegacyAgentKind("integrationtests.deprecated-alias")]
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
