using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Foundation.Runtime.Persistence;
using Shouldly;

namespace Aevatar.Foundation.Core.Tests;

public class FileEventStoreTests
{
    [Fact]
    public async Task AppendAndRead_ShouldPersistAcrossStoreInstances()
    {
        var root = CreateTempRoot();
        try
        {
            var store1 = new FileEventStore(new FileEventStoreOptions { RootDirectory = root });
            var commitResult = await store1.AppendAsync("agent-1",
            [
                new StateEvent
                {
                    EventId = "e1",
                    Timestamp = TimestampHelper.Now(),
                    Version = 1,
                    EventType = "evt-1",
                    AgentId = "agent-1",
                },
                new StateEvent
                {
                    EventId = "e2",
                    Timestamp = TimestampHelper.Now(),
                    Version = 2,
                    EventType = "evt-2",
                    AgentId = "agent-1",
                },
            ],
            expectedVersion: 0);

            commitResult.LatestVersion.ShouldBe(2);
            commitResult.CommittedEvents.Count.ShouldBe(2);

            var store2 = new FileEventStore(new FileEventStoreOptions { RootDirectory = root });
            var events = await store2.GetEventsAsync("agent-1");
            var version = await store2.GetVersionAsync("agent-1");

            events.Count.ShouldBe(2);
            events[0].EventId.ShouldBe("e1");
            events[1].EventId.ShouldBe("e2");
            version.ShouldBe(2);
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public async Task AppendAsync_WithVersionConflict_ShouldThrow()
    {
        var root = CreateTempRoot();
        try
        {
            var store = new FileEventStore(new FileEventStoreOptions { RootDirectory = root });
            await store.AppendAsync("agent-1",
            [
                new StateEvent
                {
                    EventId = "e1",
                    Timestamp = TimestampHelper.Now(),
                    Version = 1,
                    EventType = "evt-1",
                    AgentId = "agent-1",
                },
            ],
            expectedVersion: 0);

            var act = () => store.AppendAsync("agent-1",
            [
                new StateEvent
                {
                    EventId = "e2",
                    Timestamp = TimestampHelper.Now(),
                    Version = 2,
                    EventType = "evt-2",
                    AgentId = "agent-1",
                },
            ],
            expectedVersion: 0);

            await act.ShouldThrowAsync<InvalidOperationException>();
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public async Task GetEventsAsync_FromVersion_ShouldReturnOnlyNewerEvents()
    {
        var root = CreateTempRoot();
        try
        {
            var store = new FileEventStore(new FileEventStoreOptions { RootDirectory = root });
            await store.AppendAsync("agent-1",
                Enumerable.Range(1, 5).Select(i => new StateEvent
                {
                    EventId = $"e{i}",
                    Timestamp = TimestampHelper.Now(),
                    Version = i,
                    EventType = "evt",
                    AgentId = "agent-1",
                }),
                expectedVersion: 0);

            var events = await store.GetEventsAsync("agent-1", fromVersion: 3);

            events.Count.ShouldBe(2);
            events[0].Version.ShouldBe(4);
            events[1].Version.ShouldBe(5);
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public async Task DeleteEventsUpToAsync_ShouldCompactHistory_AndKeepLatestVersion()
    {
        var root = CreateTempRoot();
        try
        {
            var store = new FileEventStore(new FileEventStoreOptions { RootDirectory = root });
            await store.AppendAsync("agent-1",
                Enumerable.Range(1, 5).Select(i => new StateEvent
                {
                    EventId = $"e{i}",
                    Timestamp = TimestampHelper.Now(),
                    Version = i,
                    EventType = "evt",
                    AgentId = "agent-1",
                }),
                expectedVersion: 0);

            var deleted = await store.DeleteEventsUpToAsync("agent-1", 4);
            deleted.ShouldBe(4);

            var versionAfterCompact = await store.GetVersionAsync("agent-1");
            versionAfterCompact.ShouldBe(5);

            var remained = await store.GetEventsAsync("agent-1");
            remained.Count.ShouldBe(1);
            remained[0].Version.ShouldBe(5);

            await store.AppendAsync("agent-1",
            [
                new StateEvent
                {
                    EventId = "e6",
                    Timestamp = TimestampHelper.Now(),
                    Version = 6,
                    EventType = "evt",
                    AgentId = "agent-1",
                },
            ],
            expectedVersion: 5);

            var store2 = new FileEventStore(new FileEventStoreOptions { RootDirectory = root });
            var version = await store2.GetVersionAsync("agent-1");
            var events = await store2.GetEventsAsync("agent-1");
            version.ShouldBe(6);
            events.Count.ShouldBe(2);
            events[0].Version.ShouldBe(5);
            events[1].Version.ShouldBe(6);
        }
        finally
        {
            SafeDelete(root);
        }
    }

    [Fact]
    public async Task ResetStreamAsync_ShouldDeleteStreamFileAndResetVersion()
    {
        var root = CreateTempRoot();
        try
        {
            var store = new FileEventStore(new FileEventStoreOptions { RootDirectory = root });
            await store.AppendAsync("agent-1",
            [
                new StateEvent
                {
                    EventId = "e1",
                    Timestamp = TimestampHelper.Now(),
                    Version = 1,
                    EventType = "evt",
                    AgentId = "agent-1",
                },
            ],
            expectedVersion: 0);

            var reset = await store.ResetStreamAsync("agent-1");

            reset.ShouldBeTrue();
            (await store.GetVersionAsync("agent-1")).ShouldBe(0);
            (await store.GetEventsAsync("agent-1")).ShouldBeEmpty();

            await store.AppendAsync("agent-1",
            [
                new StateEvent
                {
                    EventId = "e2",
                    Timestamp = TimestampHelper.Now(),
                    Version = 1,
                    EventType = "evt",
                    AgentId = "agent-1",
                },
            ],
            expectedVersion: 0);

            var store2 = new FileEventStore(new FileEventStoreOptions { RootDirectory = root });
            (await store2.GetVersionAsync("agent-1")).ShouldBe(1);
            (await store2.GetEventsAsync("agent-1")).ShouldHaveSingleItem().EventId.ShouldBe("e2");
        }
        finally
        {
            SafeDelete(root);
        }
    }

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "aevatar-eventstore-tests", Guid.NewGuid().ToString("N"));

    private static void SafeDelete(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
