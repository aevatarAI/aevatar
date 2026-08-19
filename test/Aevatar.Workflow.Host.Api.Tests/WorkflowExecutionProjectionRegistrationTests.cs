using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Projection.Metadata;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
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
        var documentStore = provider.GetRequiredService<IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>>();
        var continuationStore = provider.GetRequiredService<IProjectionDocumentReader<WorkflowExternalApprovalContinuationDocument, string>>();
        var relationStore = provider.GetRequiredService<IProjectionGraphStore>();
        var currentStateDispatcher = provider.GetRequiredService<IProjectionWriteDispatcher<WorkflowExecutionCurrentStateDocument>>();
        var dispatcher = provider.GetRequiredService<IProjectionWriteDispatcher<WorkflowRunInsightReportDocument>>();
        var continuationDispatcher = provider.GetRequiredService<IProjectionWriteDispatcher<WorkflowExternalApprovalContinuationDocument>>();
        var graphWriter = provider.GetRequiredService<IProjectionGraphWriter<WorkflowRunInsightReportDocument>>();
        var versionedGraphStore = provider.GetRequiredService<IVersionedProjectionGraphStore>();
        var currentStateMaterializers = provider.GetServices<ICurrentStateProjectionMaterializer<WorkflowExecutionMaterializationContext>>();
        var artifactMaterializers = provider.GetServices<IProjectionArtifactMaterializer<WorkflowExecutionMaterializationContext>>();
        currentStateStore.Should().NotBeNull();
        documentStore.Should().NotBeNull();
        continuationStore.Should().NotBeNull();
        relationStore.Should().NotBeNull();
        currentStateDispatcher.Should().NotBeNull();
        dispatcher.Should().NotBeNull();
        continuationDispatcher.Should().NotBeNull();
        graphWriter.Should().NotBeNull();
        versionedGraphStore.Should().BeSameAs(relationStore);
        currentStateMaterializers.Should().ContainSingle();
        // Continuation projector, insight report projector, and the committed-fact audit materializer.
        artifactMaterializers.Should().HaveCount(3);
        provider.GetRequiredService<WorkflowExecutionCurrentStateProjector>().Should().NotBeNull();
        provider.GetRequiredService<WorkflowExternalApprovalContinuationProjector>().Should().NotBeNull();
        provider.GetRequiredService<WorkflowRunInsightReportArtifactProjector>().Should().NotBeNull();
        provider.GetRequiredService<WorkflowRunIncrementalGraphMaterializer>().Should().NotBeNull();
        provider.GetRequiredService<WorkflowProjectionGraphCutoverOrchestrator>().Should().NotBeNull();
        provider.GetRequiredService<WorkflowExecutionArtifactQueryPort>().Should().NotBeNull();

        // The binding write path is decorated with the heal/guard dispatcher (Definition supersedes a
        // clobbered Run-kind slot) and the binding projector consumes it.
        provider.GetRequiredService<IProjectionWriteDispatcher<WorkflowActorBindingDocument>>()
            .Should().BeOfType<Aevatar.Workflow.Projection.Orchestration.WorkflowActorBindingHealingWriteDispatcher>();
        provider.GetRequiredService<WorkflowActorBindingProjector>().Should().NotBeNull();

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
        var rootProperties = provider.Metadata.Mappings["properties"]
            .Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        AssertNotIndexedTextFields(rootProperties, "input", "final_output", "final_error");
        var stepEntries = rootProperties["step_entries"]
            .Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        var stepProperties = stepEntries["properties"]
            .Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        AssertNotIndexedTextFields(
            stepProperties,
            "output_preview",
            "error",
            "assigned_value",
            "suspension_prompt",
            "suspension_content",
            "failure_output");
        foreach (var field in new[] { "file_item_results", "vote_agreement_decision", "latest_failed_attempt" })
        {
            var mapping = stepProperties[field]
                .Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
            mapping["enabled"].Should().Be(false);
        }

        AssertObjectTextFieldsNotIndexed(rootProperties, "role_reply_entries", "content");
        AssertObjectTextFieldsNotIndexed(rootProperties, "timeline_entries", "message");
        AssertObjectTextFieldsNotIndexed(
            rootProperties,
            "operation_entries",
            "input_summary",
            "output",
            "error",
            "arguments_json",
            "result_json",
            "reasoning_content");
        provider.Metadata.Settings.Should().BeEmpty();
        provider.Metadata.Aliases.Should().BeEmpty();
    }

    private static void AssertObjectTextFieldsNotIndexed(
        IReadOnlyDictionary<string, object?> rootProperties,
        string objectField,
        params string[] textFields)
    {
        var objectMapping = rootProperties[objectField]
            .Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        var properties = objectMapping["properties"]
            .Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        AssertNotIndexedTextFields(properties, textFields);
    }

    private static void AssertNotIndexedTextFields(
        IReadOnlyDictionary<string, object?> properties,
        params string[] fields)
    {
        foreach (var field in fields)
        {
            var mapping = properties[field]
                .Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
            mapping["type"].Should().Be("text");
            mapping["index"].Should().Be(false);
        }
    }

    [Fact]
    public void WorkflowExecutionCurrentStateDocumentMetadataProvider_ShouldExposeExpectedDefaults()
    {
        var provider = new WorkflowExecutionCurrentStateDocumentMetadataProvider();

        provider.Metadata.IndexName.Should().Be("workflow-execution-current-states");
        provider.Metadata.Mappings.Should().ContainKey("dynamic").WhoseValue.Should().Be(true);
        var rootProperties = provider.Metadata.Mappings["properties"]
            .Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        foreach (var field in new[]
                 {
                     "root_actor_id", "definition_actor_id", "run_id", "workflow_id", "scope_id", "schedule_id",
                     "status", "saga_status", "run_origin", "workflow_name", "input_summary",
                 })
        {
            var mapping = rootProperties[field]
                .Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
            mapping["type"].Should().Be("keyword", $"'{field}' is a current-state filter/search/sort dimension");
        }

        rootProperties["updated_at_utc_value"]
            .Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject["type"].Should().Be("date");
        AssertNotIndexedTextFields(
            rootProperties,
            "workflow_yaml",
            "input",
            "final_output",
            "final_error",
            "compilation_error",
            "dead_letter_error");
        foreach (var field in new[] { "input_file_ref_entries", "connector_approval_entries", "capability_admission_plan" })
        {
            var mapping = rootProperties[field]
                .Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
            mapping["enabled"].Should().Be(false);
        }

        provider.Metadata.Settings.Should().BeEmpty();
        provider.Metadata.Aliases.Should().BeEmpty();
    }

    private static void RegisterInMemoryProviders(IServiceCollection services)
    {
        services.AddInMemoryDocumentProjectionStore<WorkflowExecutionCurrentStateDocument, string>(
            keySelector: document => document.RootActorId,
            keyFormatter: key => key,
            defaultSortSelector: document => document.UpdatedAt,
            queryTakeMax: 200);
        services.AddInMemoryDocumentProjectionStore<WorkflowRunInsightReportDocument, string>(
            keySelector: report => report.RootActorId,
            keyFormatter: key => key,
            defaultSortSelector: report => report.CreatedAt,
            queryTakeMax: 200);
        services.AddInMemoryDocumentProjectionStore<WorkflowExternalApprovalContinuationDocument, string>(
            keySelector: document => document.Id,
            keyFormatter: key => key,
            defaultSortSelector: document => document.UpdatedAt,
            queryTakeMax: 200);
        services.AddInMemoryDocumentProjectionStore<WorkflowActorBindingDocument, string>(
            keySelector: document => document.Id,
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
        services.AddElasticsearchDocumentProjectionStore<WorkflowRunInsightReportDocument, string>(
            optionsFactory: _ => new ElasticsearchProjectionDocumentStoreOptions
            {
                Endpoints = ["http://localhost:9200"],
            },
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<WorkflowRunInsightReportDocument>>().Metadata,
            keySelector: report => report.RootActorId,
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
        services.AddSingleton<IAgentKindVerifier, AlwaysTrueKindVerifier>();
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

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = actorId;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

    }

    private sealed class AlwaysTrueKindVerifier : IAgentKindVerifier
    {
        public Task<bool> IsExpectedKindAsync(string actorId, string expectedKind, CancellationToken ct = default) =>
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
