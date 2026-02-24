using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowExecutionProjectionRegistrationTests
{
    [Fact]
    public async Task AddWorkflowExecutionProjectionCQRS_WhenStartupValidationEnabledAndProviderMissing_ShouldFailFast()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();

        await using var provider = services.BuildServiceProvider();
        Func<Task> act = () => StartHostedServicesAsync(provider);

        await act.Should().ThrowAsync<ProjectionProviderSelectionException>()
            .WithMessage("*No provider registrations were found*");
    }

    [Fact]
    public async Task AddWorkflowExecutionProjectionCQRS_WhenStartupValidationFindsUnsupportedCapabilities_ShouldFailFast()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProvider(services);
        ConfigureStoreSelectionOptions(services, options =>
        {
            options.Provider = ProjectionReadModelProviderNames.InMemory;
            options.Bindings[typeof(WorkflowExecutionReport).FullName!] = ProjectionReadModelIndexKind.Document.ToString();
            options.FailOnUnsupportedCapabilities = true;
        });
        services.AddWorkflowExecutionProjectionCQRS();

        await using var provider = services.BuildServiceProvider();
        Func<Task> act = () => StartHostedServicesAsync(provider);

        await act.Should().ThrowAsync<ProjectionReadModelCapabilityValidationException>()
            .Where(ex => ex.ReadModelType == typeof(WorkflowExecutionReport));
    }

    [Fact]
    public async Task AddWorkflowExecutionProjectionCQRS_WhenStartupValidationConfiguredCorrectly_ShouldPass()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProvider(services);
        services.AddWorkflowExecutionProjectionCQRS();

        await using var provider = services.BuildServiceProvider();
        Func<Task> act = () => StartHostedServicesAsync(provider);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_ShouldUseInMemoryProviderByDefault()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProvider(services);
        services.AddWorkflowExecutionProjectionCQRS();

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();
        var relationStore = provider.GetRequiredService<IProjectionRelationStore>();

        store.Should().BeOfType<InMemoryProjectionReadModelStore<WorkflowExecutionReport, string>>();
        relationStore.Should().BeOfType<InMemoryProjectionRelationStore>();
        var metadata = store.Should().BeAssignableTo<IProjectionStoreProviderMetadata>().Subject;
        metadata.ProviderCapabilities.ProviderName.Should().Be(ProjectionReadModelProviderNames.InMemory);
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_WhenElasticsearchConfiguredWithoutRelationProvider_ShouldFailFastOnRelationStore()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProvider(services);
        RegisterElasticsearchProvider(services);
        ConfigureStoreSelectionOptions(services, options =>
            options.Provider = ProjectionReadModelProviderNames.Elasticsearch);
        services.AddWorkflowExecutionProjectionCQRS();

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();
        store.Should().BeOfType<ElasticsearchProjectionReadModelStore<WorkflowExecutionReport, string>>();
        var metadata = store.Should().BeAssignableTo<IProjectionStoreProviderMetadata>().Subject;
        metadata.ProviderCapabilities.SupportsIndexing.Should().BeTrue();
        metadata.ProviderCapabilities.IndexKinds.Should().Contain(ProjectionReadModelIndexKind.Document);

        Action act = () => provider.GetRequiredService<IProjectionRelationStore>();
        act.Should().Throw<ProjectionReadModelCapabilityValidationException>()
            .Where(ex => ex.ReadModelType == typeof(ProjectionRelationNode));
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_WhenElasticsearchReadModelWithInMemoryRelationConfigured_ShouldResolveSplitProviders()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProvider(services);
        RegisterElasticsearchProvider(services);
        ConfigureStoreSelectionOptions(services, options =>
        {
            options.Provider = ProjectionReadModelProviderNames.Elasticsearch;
            options.RelationProvider = ProjectionReadModelProviderNames.InMemory;
        });
        services.AddWorkflowExecutionProjectionCQRS();

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();
        var relationStore = provider.GetRequiredService<IProjectionRelationStore>();

        store.Should().BeOfType<ElasticsearchProjectionReadModelStore<WorkflowExecutionReport, string>>();
        relationStore.Should().BeOfType<InMemoryProjectionRelationStore>();
    }

    [Fact]
    public async Task AddWorkflowExecutionProjectionCQRS_WhenNeo4jConfigured_ShouldResolveNeo4jStore()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProvider(services);
        RegisterNeo4jProvider(services);
        ConfigureStoreSelectionOptions(services, options =>
            options.Provider = ProjectionReadModelProviderNames.Neo4j);
        services.AddWorkflowExecutionProjectionCQRS();

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();
        var relationStore = provider.GetRequiredService<IProjectionRelationStore>();

        store.Should().BeOfType<Neo4jProjectionReadModelStore<WorkflowExecutionReport, string>>();
        relationStore.Should().BeOfType<Neo4jProjectionRelationStore>();
        var metadata = store.Should().BeAssignableTo<IProjectionStoreProviderMetadata>().Subject;
        metadata.ProviderCapabilities.SupportsIndexing.Should().BeTrue();
        metadata.ProviderCapabilities.IndexKinds.Should().Contain(ProjectionReadModelIndexKind.Graph);
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_WhenProviderUnsupported_ShouldThrow()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProvider(services);
        ConfigureStoreSelectionOptions(services, options =>
            options.Provider = "UnknownProvider");
        services.AddWorkflowExecutionProjectionCQRS();
        using var provider = services.BuildServiceProvider();

        Action act = () => provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Requested provider*is not registered*");
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_WhenBindingRequiresUnsupportedCapabilities_ShouldFailFast()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProvider(services);
        ConfigureStoreSelectionOptions(services, options =>
        {
            options.Provider = ProjectionReadModelProviderNames.InMemory;
            options.Bindings[typeof(WorkflowExecutionReport).FullName!] = ProjectionReadModelIndexKind.Document.ToString();
            options.FailOnUnsupportedCapabilities = true;
        });
        services.AddWorkflowExecutionProjectionCQRS();
        using var provider = services.BuildServiceProvider();

        Action act = () => provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();

        act.Should().Throw<ProjectionReadModelCapabilityValidationException>()
            .Where(ex => ex.ReadModelType == typeof(WorkflowExecutionReport));
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_WhenFailFastDisabled_ShouldAllowUnsupportedCapabilities()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProvider(services);
        ConfigureStoreSelectionOptions(services, options =>
        {
            options.Provider = ProjectionReadModelProviderNames.InMemory;
            options.Bindings[typeof(WorkflowExecutionReport).FullName!] = ProjectionReadModelIndexKind.Document.ToString();
            options.FailOnUnsupportedCapabilities = false;
        });
        services.AddWorkflowExecutionProjectionCQRS();
        using var provider = services.BuildServiceProvider();

        Action act = () => provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();

        act.Should().NotThrow();
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_WhenStateOnlyModeConfigured_ShouldThrow()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProvider(services);
        ConfigureStoreSelectionOptions(services, options =>
        {
            options.Mode = ProjectionReadModelMode.StateOnly;
            options.Provider = ProjectionReadModelProviderNames.InMemory;
        });
        services.AddWorkflowExecutionProjectionCQRS();

        using var provider = services.BuildServiceProvider();
        Action act = () => provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not support*StateOnly*");
    }

    [Fact]
    public async Task AddWorkflowExecutionProjectionReducer_ShouldSupportExternalReducer()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProvider(services);
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
        RegisterInMemoryProvider(services);
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
        RegisterInMemoryProvider(services);
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
    public void AddWorkflowExecutionProjectionCQRS_MultipleCalls_ShouldUseLastProvider()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProvider(services);
        RegisterElasticsearchProvider(services);
        ConfigureStoreSelectionOptions(services, options =>
            options.Provider = ProjectionReadModelProviderNames.InMemory);
        services.AddWorkflowExecutionProjectionCQRS();
        ConfigureStoreSelectionOptions(services, options =>
            options.Provider = ProjectionReadModelProviderNames.Elasticsearch);
        services.AddWorkflowExecutionProjectionCQRS();

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();

        store.Should().BeOfType<ElasticsearchProjectionReadModelStore<WorkflowExecutionReport, string>>();
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_ShouldExposeGenericProjectionAbstractions()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProvider(services);
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
        RegisterInMemoryProvider(services);
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
        RegisterInMemoryProvider(services);
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

    private static void RegisterElasticsearchProvider(IServiceCollection services)
    {
        services.AddElasticsearchReadModelStoreRegistration<WorkflowExecutionReport, string>(
            optionsFactory: _ => new ElasticsearchProjectionReadModelStoreOptions
            {
                Endpoints = ["http://localhost:9200"],
                IndexPrefix = "aevatar-test",
                AutoCreateIndex = false,
            },
            indexScope: "workflow-execution-reports",
            keySelector: report => report.RootActorId,
            keyFormatter: key => key);
        services.AddElasticsearchRelationStoreRegistration();
    }

    private static void RegisterInMemoryProvider(IServiceCollection services)
    {
        services.AddInMemoryReadModelStoreRegistration<WorkflowExecutionReport, string>(
            keySelector: report => report.RootActorId,
            keyFormatter: key => key,
            listSortSelector: report => report.StartedAt);
        services.AddInMemoryRelationStoreRegistration();
    }

    private static void RegisterNeo4jProvider(IServiceCollection services)
    {
        services.AddNeo4jReadModelStoreRegistration<WorkflowExecutionReport, string>(
            optionsFactory: _ => new Neo4jProjectionReadModelStoreOptions
            {
                Uri = "bolt://localhost:7687",
                Username = "neo4j",
                Password = "test",
                AutoCreateConstraints = false,
            },
            scope: "workflow-execution-reports",
            keySelector: report => report.RootActorId,
            keyFormatter: key => key);
        services.AddNeo4jRelationStoreRegistration(
            optionsFactory: _ => new Neo4jProjectionRelationStoreOptions
            {
                Uri = "bolt://localhost:7687",
                Username = "neo4j",
                Password = "test",
                AutoCreateConstraints = false,
            },
            scope: WorkflowExecutionRelationConstants.Scope);
    }

    private static void ConfigureStoreSelectionOptions(
        IServiceCollection services,
        Action<ProjectionReadModelRuntimeOptions> configure)
    {
        var options = new ProjectionReadModelRuntimeOptions();
        configure(options);
        services.Replace(ServiceDescriptor.Singleton(options));
        services.Replace(ServiceDescriptor.Singleton<IProjectionStoreSelectionRuntimeOptions>(sp =>
            sp.GetRequiredService<ProjectionReadModelRuntimeOptions>()));
    }

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

    private static async Task StartHostedServicesAsync(ServiceProvider provider)
    {
        var hostedServices = provider.GetServices<IHostedService>();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(CancellationToken.None);
    }
}
