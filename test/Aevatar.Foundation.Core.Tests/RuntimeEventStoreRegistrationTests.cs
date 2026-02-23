using Aevatar.Foundation.Abstractions.Persistence;
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

            eventStore.ShouldBeOfType<FileEventStore>();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
