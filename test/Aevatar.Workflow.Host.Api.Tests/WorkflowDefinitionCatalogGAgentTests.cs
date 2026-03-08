using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowDefinitionCatalogGAgentTests
{
    [Fact]
    public async Task HandleUpsertWorkflowDefinitionRequested_WhenYamlIsUnchanged_ShouldBeIdempotent()
    {
        var store = new InMemoryEventStore();
        await using var services = BuildServices(store);

        var agent = new WorkflowDefinitionCatalogGAgent
        {
            Services = services,
            Logger = NullLogger.Instance,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowDefinitionCatalogState>>(),
        };

        await agent.ActivateAsync();

        var request = new UpsertWorkflowDefinitionRequestedEvent
        {
            WorkflowName = "demo",
            WorkflowYaml = "name: demo\nsteps: []\n",
        };

        await agent.HandleEventAsync(CreateEnvelope(request));

        var firstVersion = agent.State.LastAppliedEventVersion;
        var firstLastEventId = agent.State.LastEventId;
        var firstStoreEvents = await store.GetEventsAsync(agent.Id);

        firstVersion.Should().Be(1);
        firstLastEventId.Should().Be("demo:1:upserted");
        firstStoreEvents.Should().HaveCount(1);

        await agent.HandleEventAsync(CreateEnvelope(request));

        agent.State.LastAppliedEventVersion.Should().Be(firstVersion);
        agent.State.LastEventId.Should().Be(firstLastEventId);
        (await store.GetEventsAsync(agent.Id)).Should().HaveCount(1);
    }

    private static ServiceProvider BuildServices(IEventStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStreamProvider, InMemoryStreamProvider>();
        services.AddSingleton<InMemoryActorRuntimeCallbackScheduler>();
        services.AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
            sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>());
        services.AddSingleton(store);
        services.AddSingleton<IEventStore>(store);
        services.AddSingleton(new EventSourcingRuntimeOptions());
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton<IEnumerable<IGAgentExecutionHook>>(Array.Empty<IGAgentExecutionHook>());
        return services.BuildServiceProvider();
    }

    private static EventEnvelope CreateEnvelope(IMessage message) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(message),
            PublisherId = "test",
            Direction = EventDirection.Down,
        };
}
