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
        services.AddSingleton<IProjectionReadModelStore<WorkflowExecutionReport, string>, FailingReadModelStore>();
        await using var provider = services.BuildServiceProvider();
        var startupValidation = new WorkflowReadModelStartupValidationHostedService(
            provider,
            new WorkflowExecutionProjectionOptions
            {
                ValidateDocumentProviderOnStartup = true,
            });

        Func<Task> act = () => startupValidation.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_WhenDocumentProbeFailsInProduction_ShouldFailFast()
    {
        using var env = new EnvironmentVariableScope("ASPNETCORE_ENVIRONMENT", "Production");
        var services = new ServiceCollection();
        services.AddSingleton<IProjectionReadModelStore<WorkflowExecutionReport, string>, FailingReadModelStore>();
        await using var provider = services.BuildServiceProvider();
        var startupValidation = new WorkflowReadModelStartupValidationHostedService(
            provider,
            new WorkflowExecutionProjectionOptions
            {
                ValidateDocumentProviderOnStartup = true,
            });

        Func<Task> act = () => startupValidation.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*document startup probe failed*");
    }

    private sealed class FailingReadModelStore : IProjectionReadModelStore<WorkflowExecutionReport, string>
    {
        public Task UpsertAsync(WorkflowExecutionReport readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task MutateAsync(string key, Action<WorkflowExecutionReport> mutate, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

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
