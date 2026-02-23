using Aevatar.Foundation.Abstractions.Persistence;
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
            var behaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<CounterState>>();

            eventStore.ShouldBeOfType<FileEventStore>();
            snapshotStore.ShouldBeOfType<FileEventSourcingSnapshotStore<CounterState>>();
            behaviorFactory.ShouldBeOfType<DefaultEventSourcingBehaviorFactory<CounterState>>();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
