using Aevatar.CQRS.Projections.Abstractions;
using Aevatar.CQRS.Projections.Abstractions.ReadModels;
using Aevatar.CQRS.Projections.Configuration;
using Aevatar.CQRS.Projections.DependencyInjection;
using Aevatar.CQRS.Projections.Reducers;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Hosts.Api.Tests;

public class WorkflowExecutionProjectionRegistrationTests
{
    [Fact]
    public async Task AddWorkflowExecutionProjectionReducer_ShouldSupportExternalReducer()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();
        services.AddWorkflowExecutionProjectionReducer<CustomChatRequestReducer>();

        await using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<IWorkflowExecutionProjectionCoordinator>();
        var store = provider.GetRequiredService<IWorkflowExecutionReadModelStore>();

        var context = new WorkflowExecutionProjectionContext
        {
            RunId = "ext-1",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, Wrap(new ChatRequestEvent { Prompt = "hello" }));
        await coordinator.CompleteAsync(context, []);

        var report = await store.GetAsync(context.RunId);
        report.Should().NotBeNull();
        report!.Timeline.Should().ContainSingle(x => x.Stage == "custom.chat.request");
    }

    [Fact]
    public async Task AddWorkflowExecutionProjectionExtensionsFromAssembly_ShouldAutoRegisterReducer()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();
        services.AddWorkflowExecutionProjectionExtensionsFromAssembly(typeof(CustomChatRequestReducer).Assembly);

        await using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<IWorkflowExecutionProjectionCoordinator>();
        var store = provider.GetRequiredService<IWorkflowExecutionReadModelStore>();

        var context = new WorkflowExecutionProjectionContext
        {
            RunId = "ext-2",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, Wrap(new ChatRequestEvent { Prompt = "hello" }));
        await coordinator.CompleteAsync(context, []);

        var report = await store.GetAsync(context.RunId);
        report.Should().NotBeNull();
        report!.Timeline.Should().ContainSingle(x => x.Stage == "custom.chat.request");
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_MultipleCalls_ShouldUseLastOptions()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS(options => options.Enabled = true);
        services.AddWorkflowExecutionProjectionCQRS(options => options.Enabled = false);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<WorkflowExecutionProjectionOptions>();
        var coordinator = provider.GetRequiredService<IWorkflowExecutionProjectionCoordinator>();
        var store = provider.GetRequiredService<IWorkflowExecutionReadModelStore>();

        options.Enabled.Should().BeFalse();
        coordinator.Should().NotBeNull();
        store.Should().NotBeNull();
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_ShouldExposeGenericProjectionContracts()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();

        using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<IProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
        var store = provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();
        var reducers = provider.GetServices<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>>();
        var projectors = provider.GetServices<IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();

        coordinator.Should().NotBeNull();
        store.Should().NotBeNull();
        reducers.Should().NotBeEmpty();
        projectors.Should().NotBeEmpty();
    }

    private static EventEnvelope Wrap(IMessage evt, string publisherId = "test") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        PublisherId = publisherId,
        Direction = EventDirection.Down,
    };

    public sealed class CustomChatRequestReducer : WorkflowExecutionEventReducerBase<ChatRequestEvent>
    {
        public override int Order => 1000;

        protected override void Reduce(
            WorkflowExecutionReport report,
            WorkflowExecutionProjectionContext context,
            EventEnvelope envelope,
            ChatRequestEvent evt,
            DateTimeOffset now)
        {
            report.Timeline.Add(new WorkflowExecutionTimelineEvent
            {
                Timestamp = now,
                Stage = "custom.chat.request",
                Message = evt.Prompt ?? "",
                AgentId = envelope.PublisherId ?? "",
                EventType = envelope.Payload?.TypeUrl ?? "",
            });
        }
    }
}
