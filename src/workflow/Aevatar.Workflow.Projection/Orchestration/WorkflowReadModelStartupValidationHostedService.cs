
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowReadModelStartupValidationHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkflowExecutionProjectionOptions _options;
    private readonly ILogger<WorkflowReadModelStartupValidationHostedService> _logger;

    public WorkflowReadModelStartupValidationHostedService(
        IServiceProvider serviceProvider,
        WorkflowExecutionProjectionOptions options,
        ILogger<WorkflowReadModelStartupValidationHostedService>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger ?? NullLogger<WorkflowReadModelStartupValidationHostedService>.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled)
            return;

        var production = IsProductionEnvironment();

        if (_options.ValidateDocumentProviderOnStartup)
        {
            try
            {
                var store = _serviceProvider.GetRequiredService<IProjectionReadModelStore<WorkflowExecutionReport, string>>();
                _ = await store.ListAsync(take: 1, cancellationToken);
                _logger.LogInformation(
                    "Workflow read-model document startup probe passed. readModelType={ReadModelType}",
                    typeof(WorkflowExecutionReport).FullName);
            }
            catch (Exception ex)
            {
                HandleProbeFailure("document", ex, production);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void HandleProbeFailure(string provider, Exception exception, bool production)
    {
        if (production)
        {
            throw new InvalidOperationException(
                $"Workflow read-model {provider} startup probe failed in production environment.",
                exception);
        }

        _logger.LogWarning(
            exception,
            "Workflow read-model {Provider} startup probe failed in non-production environment and will be ignored.",
            provider);
    }

    private static bool IsProductionEnvironment()
    {
        var dotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (string.Equals(dotnetEnvironment, Environments.Production, StringComparison.OrdinalIgnoreCase))
            return true;

        var aspnetEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.Equals(aspnetEnvironment, Environments.Production, StringComparison.OrdinalIgnoreCase);
    }
}
