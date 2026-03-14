using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowReadModelStartupValidationHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WhenDocumentProbeFailsInNonProduction_ShouldContinue()
    {
        using var env = new EnvironmentVariableScope("ASPNETCORE_ENVIRONMENT", "Development");
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionDocumentReader<WorkflowExecutionReport, string>, FailingDocumentStore>();
        services.AddSingleton<IProjectionGraphStore, NoOpGraphStore>();
        await using var provider = services.BuildServiceProvider();
        var startupValidation = new WorkflowReadModelStartupValidationHostedService(
            provider,
            new WorkflowExecutionProjectionOptions
            {
                ValidateDocumentProviderOnStartup = true,
                ValidateGraphProviderOnStartup = false,
            });

        Func<Task> act = () => startupValidation.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_WhenDocumentProbeFailsInProduction_ShouldFailFast()
    {
        using var env = new EnvironmentVariableScope("ASPNETCORE_ENVIRONMENT", "Production");
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionDocumentReader<WorkflowExecutionReport, string>, FailingDocumentStore>();
        services.AddSingleton<IProjectionGraphStore, NoOpGraphStore>();
        await using var provider = services.BuildServiceProvider();
        var startupValidation = new WorkflowReadModelStartupValidationHostedService(
            provider,
            new WorkflowExecutionProjectionOptions
            {
                ValidateDocumentProviderOnStartup = true,
                ValidateGraphProviderOnStartup = false,
            });

        Func<Task> act = () => startupValidation.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*document startup probe failed*");
    }

    [Fact]
    public async Task StartAsync_WhenGraphProbeFailsInProduction_ShouldFailFast()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Production");
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionDocumentReader<WorkflowExecutionReport, string>, NoOpDocumentStore>();
        services.AddSingleton<IProjectionGraphStore, FailingGraphStore>();
        await using var provider = services.BuildServiceProvider();
        var startupValidation = new WorkflowReadModelStartupValidationHostedService(
            provider,
            new WorkflowExecutionProjectionOptions
            {
                ValidateDocumentProviderOnStartup = false,
                ValidateGraphProviderOnStartup = true,
            });

        Func<Task> act = () => startupValidation.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*graph startup probe failed*");
    }

    private sealed class NoOpDocumentStore : IProjectionDocumentReader<WorkflowExecutionReport, string>
    {
        public Task<WorkflowExecutionReport?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowExecutionReport?>(null);
        }

        public Task<IReadOnlyList<WorkflowExecutionReport>> ListAsync(int take = 50, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowExecutionReport>>([]);
        }
    }

    private sealed class FailingDocumentStore : IProjectionDocumentReader<WorkflowExecutionReport, string>
    {
        public Task<WorkflowExecutionReport?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowExecutionReport?>(null);
        }

        public Task<IReadOnlyList<WorkflowExecutionReport>> ListAsync(int take = 50, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("document store unavailable");
        }
    }

    private class NoOpGraphStore : IProjectionGraphStore
    {
        public Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UpsertEdgeAsync(ProjectionGraphEdge edge, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeleteNodeAsync(string scope, string nodeId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public virtual Task<IReadOnlyList<ProjectionGraphNode>> ListNodesByOwnerAsync(
            string scope,
            string ownerId,
            int skip = 0,
            int take = 5000,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ProjectionGraphNode>>([]);
        }

        public Task<IReadOnlyList<ProjectionGraphEdge>> ListEdgesByOwnerAsync(
            string scope,
            string ownerId,
            int skip = 0,
            int take = 5000,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>([]);
        }

        public Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(
            ProjectionGraphQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>([]);
        }

        public Task<ProjectionGraphSubgraph> GetSubgraphAsync(ProjectionGraphQuery query, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectionGraphSubgraph());
        }
    }

    private sealed class FailingGraphStore : NoOpGraphStore
    {
        public override Task<IReadOnlyList<ProjectionGraphNode>> ListNodesByOwnerAsync(
            string scope,
            string ownerId,
            int skip = 0,
            int take = 5000,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("graph store unavailable");
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
