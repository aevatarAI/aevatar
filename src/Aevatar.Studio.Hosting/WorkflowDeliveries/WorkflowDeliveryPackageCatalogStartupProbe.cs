using Aevatar.Studio.Application.Delivery;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Hosting.WorkflowDeliveries;

internal sealed class WorkflowDeliveryPackageCatalogStartupProbe(
    IWorkflowDeliveryPackageCatalog packageCatalog) : IHostedService
{
    private const string ProbePrincipalId = "workflow-delivery-startup-probe";

    public async Task StartAsync(CancellationToken cancellationToken) =>
        _ = await packageCatalog.ListAsync(ProbePrincipalId, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
