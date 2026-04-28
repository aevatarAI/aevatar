using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class GarnetEventStoreIntegrationTests
{
    [GarnetIntegrationFact]
    public async Task GarnetEventStore_ShouldAppendReplayAndCompactEvents()
    {
        var connectionString = RequireGarnetConnectionString();
        var keyPrefix = $"aevatar:test:eventstore:{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";

        using var provider = BuildProvider(connectionString, keyPrefix);
        var store = provider.GetRequiredService<IEventStore>();

        var firstBatch = CreateEvents(agentId, startVersion: 1, count: 3);
        var firstCommit = await store.AppendAsync(agentId, firstBatch, expectedVersion: 0);
        firstCommit.LatestVersion.Should().Be(3);
        firstCommit.CommittedEvents.Select(x => x.Version).Should().Equal(1, 2, 3);
        (await store.GetVersionAsync(agentId)).Should().Be(3);

        var all = await store.GetEventsAsync(agentId);
        all.Select(x => x.Version).Should().Equal(1, 2, 3);

        var removed = await store.DeleteEventsUpToAsync(agentId, toVersion: 2);
        removed.Should().Be(2);
        (await store.GetVersionAsync(agentId)).Should().Be(3);

        var retained = await store.GetEventsAsync(agentId);
        retained.Select(x => x.Version).Should().Equal(3);

        var secondBatch = CreateEvents(agentId, startVersion: 4, count: 2);
        var secondCommit = await store.AppendAsync(agentId, secondBatch, expectedVersion: 3);
        secondCommit.LatestVersion.Should().Be(5);
        secondCommit.CommittedEvents.Select(x => x.Version).Should().Equal(4, 5);

        var afterAppend = await store.GetEventsAsync(agentId);
        afterAppend.Select(x => x.Version).Should().Equal(3, 4, 5);

        var maintenance = provider.GetRequiredService<IEventStoreMaintenance>();
        (await maintenance.ResetStreamAsync(agentId)).Should().BeTrue();
        (await store.GetVersionAsync(agentId)).Should().Be(0);
        (await store.GetEventsAsync(agentId)).Should().BeEmpty();

        var afterReset = await store.AppendAsync(
            agentId,
            CreateEvents(agentId, startVersion: 1, count: 1),
            expectedVersion: 0);
        afterReset.LatestVersion.Should().Be(1);
    }

    [GarnetIntegrationFact]
    public async Task GarnetEventStore_ShouldEnforceOptimisticConcurrency()
    {
        var connectionString = RequireGarnetConnectionString();
        var keyPrefix = $"aevatar:test:eventstore:{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";

        using var provider = BuildProvider(connectionString, keyPrefix);
        var store = provider.GetRequiredService<IEventStore>();

        await store.AppendAsync(agentId, CreateEvents(agentId, startVersion: 1, count: 1), expectedVersion: 0);

        var conflicting = () => store.AppendAsync(
            agentId,
            CreateEvents(agentId, startVersion: 1, count: 1),
            expectedVersion: 0);
        await conflicting.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Optimistic concurrency conflict*");
    }

    private static ServiceProvider BuildProvider(string connectionString, string keyPrefix)
    {
        var services = new ServiceCollection();
        services.AddGarnetEventStore(options =>
        {
            options.ConnectionString = connectionString;
            options.KeyPrefix = keyPrefix;
        });
        return services.BuildServiceProvider();
    }

    private static StateEvent[] CreateEvents(string agentId, int startVersion, int count)
    {
        var events = new StateEvent[count];
        for (var i = 0; i < count; i++)
        {
            var version = startVersion + i;
            events[i] = new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                AgentId = agentId,
                EventType = "test",
                Version = version,
            };
        }

        return events;
    }

    private static string RequireGarnetConnectionString() =>
        Environment.GetEnvironmentVariable("AEVATAR_TEST_GARNET_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Missing AEVATAR_TEST_GARNET_CONNECTION_STRING.");
}
