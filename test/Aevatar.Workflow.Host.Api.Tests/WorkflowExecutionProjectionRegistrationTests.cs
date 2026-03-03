
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;

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
using Aevatar.Workflow.Projection.Reducers;
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
    public async Task AddWorkflowExecutionProjectionCQRS_ShouldResolveReadModelStore()
    {
        var services = new ServiceCollection();
        RegisterEventStore(services);
        RegisterInMemoryProviders(services);
        services.AddWorkflowExecutionProjectionCQRS();

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();

        store.Should().NotBeNull();

        Func<Task> act = () => StartHostedServicesAsync(provider);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void WorkflowExecutionReportDocumentMetadataProvider_ShouldExposeExpectedDefaults()
    {
        var provider = new WorkflowExecutionReportDocumentMetadataProvider();

        provider.Metadata.IndexName.Should().Be("workflow-execution-reports");
        provider.Metadata.Mappings.Should().ContainKey("dynamic").WhoseValue.Should().Be(true);
        provider.Metadata.Settings.Should().BeEmpty();
        provider.Metadata.Aliases.Should().BeEmpty();
    }

    [Fact]
    public async Task AddWorkflowExecutionProjectionCQRS_ShouldRegisterPassthroughEventDeduplicator()
    {
        var services = new ServiceCollection();
        RegisterEventStore(services);
        RegisterInMemoryProviders(services);
        services.AddWorkflowExecutionProjectionCQRS();

        await using var provider = services.BuildServiceProvider();
        var deduplicator = provider.GetRequiredService<IEventDeduplicator>();

        (await deduplicator.TryRecordAsync("evt-1")).Should().BeTrue();
    }

    [Fact]
    public void AddWorkflowExecutionProjectionReducer_ShouldRegisterReducerAsEnumerableSingleton()
    {
        var services = new ServiceCollection();

        services.AddWorkflowExecutionProjectionReducer<TestWorkflowReducer>();
        services.AddWorkflowExecutionProjectionReducer<TestWorkflowReducer>();

        using var provider = services.BuildServiceProvider();
        var reducers = provider.GetServices<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>>().ToList();

        reducers.Should().ContainSingle(x => x.GetType() == typeof(TestWorkflowReducer));
    }

    [Fact]
    public void AddWorkflowExecutionProjectionProjector_ShouldRegisterProjectorAsEnumerableSingleton()
    {
        var services = new ServiceCollection();

        services.AddWorkflowExecutionProjectionProjector<TestWorkflowProjector>();
        services.AddWorkflowExecutionProjectionProjector<TestWorkflowProjector>();

        using var provider = services.BuildServiceProvider();
        var projectors = provider.GetServices<IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>().ToList();

        projectors.Should().ContainSingle(x => x.GetType() == typeof(TestWorkflowProjector));
    }

    [Fact]
    public void AddWorkflowExecutionProjectionExtensionsFromAssembly_ShouldRegisterReducerAndProjectorFromAssembly()
    {
        var services = new ServiceCollection();

        services.AddWorkflowExecutionProjectionExtensionsFromAssembly(typeof(WorkflowExecutionReadModelProjector).Assembly);
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>) &&
            x.ImplementationType == typeof(StartWorkflowEventReducer));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>) &&
            x.ImplementationType == typeof(WorkflowExecutionReadModelProjector));
    }

    private static void RegisterInMemoryProviders(IServiceCollection services)
    {
        services.AddInMemoryDocumentProjectionStore<WorkflowExecutionReport, string>(
            keySelector: report => report.RootActorId,
            keyFormatter: key => key,
            listSortSelector: report => report.CreatedAt,
            listTakeMax: 200);
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
        services.AddSingleton<IActorRuntime, NoOpActorRuntime>();
        services.AddSingleton<IAgentTypeVerifier, AlwaysTrueTypeVerifier>();
    }

    private sealed class NoOpActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException("No-op runtime.");

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException("No-op runtime.");

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RestoreAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class AlwaysTrueTypeVerifier : IAgentTypeVerifier
    {
        public Task<bool> IsExpectedAsync(string actorId, Type expectedType, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class TestWorkflowReducer : IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>
    {
        public string EventTypeUrl => "type://tests/workflow-reducer";

        public bool Reduce(
            WorkflowExecutionReport readModel,
            WorkflowExecutionProjectionContext context,
            EventEnvelope envelope,
            DateTimeOffset now)
        {
            _ = readModel;
            _ = context;
            _ = envelope;
            _ = now;
            return true;
        }
    }

    private sealed class TestWorkflowProjector
        : IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
    {
        public ValueTask InitializeAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
        {
            _ = context;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask ProjectAsync(WorkflowExecutionProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(
            WorkflowExecutionProjectionContext context,
            IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
            CancellationToken ct = default)
        {
            _ = context;
            _ = topology;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
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
