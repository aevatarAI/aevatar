using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Core.EventSourcing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.Foundation.Core.Tests;

public class RuntimeEventStoreRegistrationTests
{
    [Fact]
    public void AddFileEventStore_ShouldReplaceDefaultInMemoryEventStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevatar-eventstore-registration-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddAevatarRuntime();
            services.AddFileEventStore(options => options.RootDirectory = root);

            using var provider = services.BuildServiceProvider();
            var eventStore = provider.GetRequiredService<IEventStore>();
            var snapshotStore = provider.GetRequiredService<IEventSourcingSnapshotStore<CounterState>>();
            var publicationStore = provider.GetRequiredService<ICommittedStatePublicationStateStore>();
            var behaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<CounterState>>();

            eventStore.ShouldBeOfType<FileEventStore>();
            snapshotStore.ShouldBeOfType<FileEventSourcingSnapshotStore<CounterState>>();
            publicationStore.ShouldBeOfType<FileCommittedStatePublicationStateStore>();
            behaviorFactory.ShouldBeOfType<DefaultEventSourcingBehaviorFactory<CounterState>>();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FilePublicationStateStore_ShouldPersistProtobufStateAndEnforceVersionOCC()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "aevatar-publication-state-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileEventStoreOptions { RootDirectory = root };
            var first = new FileCommittedStatePublicationStateStore(options);
            await first.InitializeAsync("actor-file", 0);
            await first.RecordFailureAsync(
                "actor-file",
                0,
                BuildEvent("actor-file", "event-1", 1),
                CommittedStatePublicationFailureStage.AdapterAcceptance,
                new InvalidOperationException("publish failed"));

            var reopened = new FileCommittedStatePublicationStateStore(options);
            var failed = await reopened.LoadAsync("actor-file");
            failed!.Failure.Attempts.ShouldBe(1);
            failed.Failure.ErrorType.ShouldContain(nameof(InvalidOperationException));

            var advanced = await reopened.AdvanceAsync(
                "actor-file",
                0,
                BuildEvent("actor-file", "event-1", 1));
            advanced.PublishedVersion.ShouldBe(1);
            advanced.PublishedEventId.ShouldBe("event-1");
            advanced.Failure.ShouldBeNull();

            await Should.ThrowAsync<CommittedStatePublicationStateConflictException>(
                () => reopened.AdvanceAsync(
                    "actor-file",
                    0,
                    BuildEvent("actor-file", "event-1", 1)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static StateEvent BuildEvent(string actorId, string eventId, long version) =>
        new()
        {
            AgentId = actorId,
            EventId = eventId,
            Version = version,
        };
}
