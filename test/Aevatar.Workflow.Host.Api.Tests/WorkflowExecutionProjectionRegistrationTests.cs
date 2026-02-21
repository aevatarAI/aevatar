using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.AI.Projection.Reducers;
using Aevatar.Workflow.Extensions.AIProjection;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Projection.Reducers;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowExecutionProjectionRegistrationTests
{
    [Fact]
    public async Task AddWorkflowExecutionProjectionReducer_ShouldSupportExternalReducer()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();
        services.AddWorkflowExecutionProjectionReducer<CustomChatRequestReducer>();

        await using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<IProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
        var store = provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "ext-1",
            CommandId = "cmd-ext-1",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, Wrap(new ChatRequestEvent { Prompt = "hello" }));
        await coordinator.CompleteAsync(context, []);

        var report = await store.GetAsync(context.RootActorId);
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
        var coordinator = provider.GetRequiredService<IProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
        var store = provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "ext-2",
            CommandId = "cmd-ext-2",
            RootActorId = "root",
            WorkflowName = "direct",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, Wrap(new ChatRequestEvent { Prompt = "hello" }));
        await coordinator.CompleteAsync(context, []);

        var report = await store.GetAsync(context.RootActorId);
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
        var coordinator = provider.GetRequiredService<IProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
        var store = provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();

        options.Enabled.Should().BeFalse();
        coordinator.Should().NotBeNull();
        store.Should().NotBeNull();
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_ShouldExposeGenericProjectionAbstractions()
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

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_WithAIExtensions_ShouldRegisterDefaultAIReducers()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();
        services.AddWorkflowAIProjectionExtensions();

        using var provider = services.BuildServiceProvider();
        var reducerTypes = provider
            .GetServices<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>>()
            .Select(x => x.GetType())
            .ToList();

        reducerTypes.Should().Contain(x =>
            x.IsGenericType &&
            x.GetGenericTypeDefinition() == typeof(TextMessageEndProjectionReducer<,>));
        reducerTypes.Should().Contain(x =>
            x.IsGenericType &&
            x.GetGenericTypeDefinition() == typeof(TextMessageStartProjectionReducer<,>));
        reducerTypes.Should().Contain(x =>
            x.IsGenericType &&
            x.GetGenericTypeDefinition() == typeof(TextMessageContentProjectionReducer<,>));
        reducerTypes.Should().Contain(x =>
            x.IsGenericType &&
            x.GetGenericTypeDefinition() == typeof(ToolCallProjectionReducer<,>));
        reducerTypes.Should().Contain(x =>
            x.IsGenericType &&
            x.GetGenericTypeDefinition() == typeof(ToolResultProjectionReducer<,>));
    }

    [Fact]
    public async Task AddWorkflowExecutionProjectionCQRS_WithAIExtensions_ShouldProjectAIEventsWithoutWorkflowApplier()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();
        services.AddWorkflowAIProjectionExtensions();

        await using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<IProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
        var store = provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "ai-layer-1",
            CommandId = "cmd-ai-layer-1",
            RootActorId = "root",
            WorkflowName = "wf",
            StartedAt = DateTimeOffset.UtcNow,
            Input = "hello",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, Wrap(new TextMessageStartEvent { SessionId = "s1" }, "assistant"));
        await coordinator.ProjectAsync(context, Wrap(new TextMessageContentEvent { SessionId = "s1", Delta = "hi" }, "assistant"));
        await coordinator.ProjectAsync(context, Wrap(new TextMessageEndEvent { SessionId = "s1", Content = "hello" }, "assistant"));
        await coordinator.ProjectAsync(context, Wrap(new ToolCallEvent { ToolName = "search", CallId = "c1" }, "assistant"));
        await coordinator.ProjectAsync(context, Wrap(new ToolResultEvent { CallId = "c1", Success = true }, "assistant"));
        await coordinator.CompleteAsync(context, []);

        var report = await store.GetAsync(context.RootActorId);
        report.Should().NotBeNull();
        report!.Timeline.Should().Contain(x => x.Stage == "llm.start");
        report.Timeline.Should().Contain(x => x.Stage == "llm.content");
        report.Timeline.Should().Contain(x => x.Stage == "llm.end");
        report.Timeline.Should().Contain(x => x.Stage == "tool.call");
        report.Timeline.Should().Contain(x => x.Stage == "tool.result");
        report.RoleReplies.Should().ContainSingle(x => x.RoleId == "assistant");
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
        protected override bool Reduce(
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

            return true;
        }
    }
}
