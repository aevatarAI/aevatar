using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Projection.Metadata;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowExecutionProjectionRegistrationTests
{
    [Fact]
    public async Task AddWorkflowExecutionProjectionCQRS_WhenNoProvidersRegistered_ShouldFailFast()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Production");
        var services = new ServiceCollection();
        RegisterEventStore(services);
        services.AddWorkflowExecutionProjectionCQRS();

        await using var provider = services.BuildServiceProvider();
        Func<Task> act = () => StartHostedServicesAsync(provider);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*document startup probe failed in production environment*");
    }

    [Fact]
    public async Task AddWorkflowExecutionProjectionCQRS_ShouldResolveDispatcherAndStores()
    {
        var services = new ServiceCollection();
        RegisterEventStore(services);
        RegisterInMemoryProviders(services);
        services.AddWorkflowExecutionProjectionCQRS();

        await using var provider = services.BuildServiceProvider();
        var currentStateStore = provider.GetRequiredService<IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string>>();
        var timelineStore = provider.GetRequiredService<IProjectionDocumentReader<WorkflowRunTimelineDocument, string>>();
        var documentStore = provider.GetRequiredService<IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>>();
        var graphArtifactStore = provider.GetRequiredService<IProjectionDocumentReader<WorkflowRunGraphArtifactDocument, string>>();
        var relationStore = provider.GetRequiredService<IProjectionGraphStore>();
        var currentStateDispatcher = provider.GetRequiredService<IProjectionWriteDispatcher<WorkflowExecutionCurrentStateDocument>>();
        var timelineDispatcher = provider.GetRequiredService<IProjectionWriteDispatcher<WorkflowRunTimelineDocument>>();
        var dispatcher = provider.GetRequiredService<IProjectionWriteDispatcher<WorkflowRunInsightReportDocument>>();
        var graphArtifactDispatcher = provider.GetRequiredService<IProjectionWriteDispatcher<WorkflowRunGraphArtifactDocument>>();
        var currentStateMaterializers = provider.GetServices<ICurrentStateProjectionMaterializer<WorkflowExecutionMaterializationContext>>();
        var artifactMaterializers = provider.GetServices<IProjectionArtifactMaterializer<WorkflowExecutionMaterializationContext>>();

        currentStateStore.Should().NotBeNull();
        timelineStore.Should().NotBeNull();
        documentStore.Should().NotBeNull();
        graphArtifactStore.Should().NotBeNull();
        relationStore.Should().NotBeNull();
        currentStateDispatcher.Should().NotBeNull();
        timelineDispatcher.Should().NotBeNull();
        dispatcher.Should().NotBeNull();
        graphArtifactDispatcher.Should().NotBeNull();
        currentStateMaterializers.Should().ContainSingle()
            .Which.Should().BeOfType<WorkflowExecutionCurrentStateProjector>();
        artifactMaterializers.Should().Contain(x => x is WorkflowRunInsightReportArtifactProjector);
        artifactMaterializers.Should().Contain(x => x is WorkflowRunTimelineArtifactProjector);
        artifactMaterializers.Should().Contain(x => x is WorkflowRunGraphArtifactProjector);

        Func<Task> act = () => StartHostedServicesAsync(provider);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_WhenGraphProviderMissing_ShouldThrowOnGraphStoreResolution()
    {
        var services = new ServiceCollection();
        RegisterEventStore(services);
        RegisterElasticsearchDocumentProvider(services);
        services.AddWorkflowExecutionProjectionCQRS();

        using var provider = services.BuildServiceProvider();
        Action act = () => provider.GetRequiredService<IProjectionGraphStore>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IProjectionGraphStore*");
    }

    [Fact]
    public void WorkflowRunInsightReportDocumentMetadataProvider_ShouldExposeExpectedDefaults()
    {
        var provider = new WorkflowRunInsightReportDocumentMetadataProvider();

        provider.Metadata.IndexName.Should().Be("workflow-execution-reports");
        provider.Metadata.Mappings.Should().ContainKey("dynamic").WhoseValue.Should().Be(true);
        provider.Metadata.Settings.Should().BeEmpty();
        provider.Metadata.Aliases.Should().BeEmpty();
    }

    [Fact]
    public void WorkflowExecutionCurrentStateDocumentMetadataProvider_ShouldExposeExpectedDefaults()
    {
        var provider = new WorkflowExecutionCurrentStateDocumentMetadataProvider();

        provider.Metadata.IndexName.Should().Be("workflow-execution-current-states");
        provider.Metadata.Mappings.Should().ContainKey("dynamic").WhoseValue.Should().Be(true);
        provider.Metadata.Settings.Should().BeEmpty();
        provider.Metadata.Aliases.Should().BeEmpty();
    }

    [Fact]
    public void WorkflowRunTimelineDocumentMetadataProvider_ShouldExposeExpectedDefaults()
    {
        var provider = new WorkflowRunTimelineDocumentMetadataProvider();

        provider.Metadata.IndexName.Should().Be("workflow-run-timelines");
        provider.Metadata.Mappings.Should().ContainKey("dynamic").WhoseValue.Should().Be(true);
        provider.Metadata.Settings.Should().BeEmpty();
        provider.Metadata.Aliases.Should().BeEmpty();
    }

    [Fact]
    public void WorkflowRunGraphArtifactDocumentMetadataProvider_ShouldExposeExpectedDefaults()
    {
        var provider = new WorkflowRunGraphArtifactDocumentMetadataProvider();

        provider.Metadata.IndexName.Should().Be("workflow-run-graph-artifacts");
        provider.Metadata.Mappings.Should().ContainKey("dynamic").WhoseValue.Should().Be(true);
        provider.Metadata.Settings.Should().BeEmpty();
        provider.Metadata.Aliases.Should().BeEmpty();
    }

    [Fact]
    public async Task AddWorkflowExecutionProjectionCQRS_ShouldNotRegisterLegacyEventDeduplicator()
    {
        var services = new ServiceCollection();
        RegisterEventStore(services);
        RegisterInMemoryProviders(services);
        services.AddWorkflowExecutionProjectionCQRS();

        await using var provider = services.BuildServiceProvider();
        provider.GetService<IEventDeduplicator>().Should().BeNull();
    }

    private static void RegisterInMemoryProviders(IServiceCollection services)
    {
        services.AddInMemoryDocumentProjectionStore<WorkflowExecutionCurrentStateDocument, string>(
            keySelector: document => document.RootActorId,
            keyFormatter: key => key,
            defaultSortSelector: document => document.UpdatedAt,
            queryTakeMax: 200);
        services.AddInMemoryDocumentProjectionStore<WorkflowRunTimelineDocument, string>(
            keySelector: document => document.RootActorId,
            keyFormatter: key => key,
            defaultSortSelector: document => document.UpdatedAt,
            queryTakeMax: 200);
        services.AddInMemoryDocumentProjectionStore<WorkflowRunInsightReportDocument, string>(
            keySelector: report => report.RootActorId,
            keyFormatter: key => key,
            defaultSortSelector: report => report.CreatedAt,
            queryTakeMax: 200);
        services.AddInMemoryDocumentProjectionStore<WorkflowRunGraphArtifactDocument, string>(
            keySelector: document => document.RootActorId,
            keyFormatter: key => key,
            defaultSortSelector: document => document.UpdatedAt,
            queryTakeMax: 200);
        services.AddInMemoryGraphProjectionStore();
    }

    private static void RegisterElasticsearchDocumentProvider(IServiceCollection services)
    {
        services.AddElasticsearchDocumentProjectionStore<WorkflowExecutionCurrentStateDocument, string>(
            optionsFactory: _ => new ElasticsearchProjectionDocumentStoreOptions
            {
                Endpoints = ["http://localhost:9200"],
            },
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<WorkflowExecutionCurrentStateDocument>>().Metadata,
            keySelector: document => document.RootActorId,
            keyFormatter: key => key);
        services.AddElasticsearchDocumentProjectionStore<WorkflowRunTimelineDocument, string>(
            optionsFactory: _ => new ElasticsearchProjectionDocumentStoreOptions
            {
                Endpoints = ["http://localhost:9200"],
            },
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<WorkflowRunTimelineDocument>>().Metadata,
            keySelector: document => document.RootActorId,
            keyFormatter: key => key);
        services.AddElasticsearchDocumentProjectionStore<WorkflowRunInsightReportDocument, string>(
            optionsFactory: _ => new ElasticsearchProjectionDocumentStoreOptions
            {
                Endpoints = ["http://localhost:9200"],
            },
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<WorkflowRunInsightReportDocument>>().Metadata,
            keySelector: report => report.RootActorId,
            keyFormatter: key => key);
        services.AddElasticsearchDocumentProjectionStore<WorkflowRunGraphArtifactDocument, string>(
            optionsFactory: _ => new ElasticsearchProjectionDocumentStoreOptions
            {
                Endpoints = ["http://localhost:9200"],
            },
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<WorkflowRunGraphArtifactDocument>>().Metadata,
            keySelector: document => document.RootActorId,
            keyFormatter: key => key);
    }

    private static async Task StartHostedServicesAsync(IServiceProvider provider)
    {
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(CancellationToken.None);
    }

    private static void RegisterEventStore(IServiceCollection services)
    {
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSingleton<NoOpActorRuntime>();
        services.AddSingleton<IActorRuntime>(sp => sp.GetRequiredService<NoOpActorRuntime>());
        services.AddSingleton<IActorDispatchPort>(sp => sp.GetRequiredService<NoOpActorRuntime>());
        services.AddSingleton<IAgentTypeVerifier, AlwaysTrueTypeVerifier>();
    }

    private sealed class NoOpActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException("No-op runtime.");

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException("No-op runtime.");

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = actorId;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

    }

    private sealed class AlwaysTrueTypeVerifier : IAgentTypeVerifier
    {
        public Task<bool> IsExpectedAsync(string actorId, Type expectedType, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previous);
        }
    }
}
