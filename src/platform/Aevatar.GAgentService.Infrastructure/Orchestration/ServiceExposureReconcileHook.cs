using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Google.Protobuf;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Infrastructure.Orchestration;

public sealed class ServiceExposureReconcileHook : ICommittedStatePublicationHook
{
    private readonly IServiceCatalogQueryReader _catalogReader;
    private readonly ServiceExternalExposureIntentService _intentService;
    private readonly ServiceExternalExposureOptions _options;

    public ServiceExposureReconcileHook(
        IServiceCatalogQueryReader catalogReader,
        ServiceExternalExposureIntentService intentService,
        IOptions<ServiceExternalExposureOptions> options)
    {
        _catalogReader = catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
        ArgumentNullException.ThrowIfNull(options);
        _intentService = intentService ?? throw new ArgumentNullException(nameof(intentService));
        _options = options.Value;
    }

    public async Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_options.Enabled)
            return;

        var payload = context.Published.StateEvent?.EventData;
        if (payload == null)
            return;

        if (payload.Is(ServiceDeploymentActivatedEvent.Descriptor))
        {
            await HandleActivatedAsync(payload.Unpack<ServiceDeploymentActivatedEvent>(), ct);
            return;
        }

        if (payload.Is(ServiceDeploymentDeactivatedEvent.Descriptor))
            await HandleDeactivatedAsync(payload.Unpack<ServiceDeploymentDeactivatedEvent>(), ct);
    }

    private async Task HandleActivatedAsync(ServiceDeploymentActivatedEvent evt, CancellationToken ct)
    {
        if (evt.Status != ServiceDeploymentStatus.Active || evt.Identity == null)
            return;

        var service = await _catalogReader.GetAsync(evt.Identity, ct);
        if (service == null || !_intentService.ShouldExpose(service))
            return;

        await _intentService.ReconcileAsync(evt.Identity, service, ct);
    }

    private async Task HandleDeactivatedAsync(ServiceDeploymentDeactivatedEvent evt, CancellationToken ct)
    {
        if (evt.Identity == null)
            return;

        await _intentService.RetireAsync(evt.Identity, ct: ct);
    }
}
