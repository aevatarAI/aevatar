using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceServingQueryApplicationService : IServiceServingQueryPort
{
    private readonly IServiceServingSetQueryReader _servingSetQueryReader;
    private readonly IServiceRolloutQueryReader _rolloutQueryReader;
    private readonly IServiceRolloutCommandObservationQueryReader _rolloutCommandObservationQueryReader;
    private readonly IServiceTrafficViewQueryReader _trafficViewQueryReader;

    public ServiceServingQueryApplicationService(
        IServiceServingSetQueryReader servingSetQueryReader,
        IServiceRolloutQueryReader rolloutQueryReader,
        IServiceRolloutCommandObservationQueryReader rolloutCommandObservationQueryReader,
        IServiceTrafficViewQueryReader trafficViewQueryReader)
    {
        _servingSetQueryReader = servingSetQueryReader ?? throw new ArgumentNullException(nameof(servingSetQueryReader));
        _rolloutQueryReader = rolloutQueryReader ?? throw new ArgumentNullException(nameof(rolloutQueryReader));
        _rolloutCommandObservationQueryReader = rolloutCommandObservationQueryReader ?? throw new ArgumentNullException(nameof(rolloutCommandObservationQueryReader));
        _trafficViewQueryReader = trafficViewQueryReader ?? throw new ArgumentNullException(nameof(trafficViewQueryReader));
    }

    public Task<ServiceServingSetSnapshot?> GetServiceServingSetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default) =>
        _servingSetQueryReader.GetAsync(identity, ct);

    public Task<ServiceRolloutSnapshot?> GetServiceRolloutAsync(
        ServiceIdentity identity,
        CancellationToken ct = default) =>
        _rolloutQueryReader.GetAsync(identity, ct);

    public async Task<ServiceRolloutCommandObservationSnapshot?> GetServiceRolloutCommandObservationAsync(
        ServiceIdentity identity,
        string commandId,
        CancellationToken ct = default)
    {
        var observation = await _rolloutCommandObservationQueryReader.GetAsync(commandId, ct);
        if (observation == null)
            return null;

        return string.Equals(observation.ServiceKey, ServiceKeys.Build(identity), StringComparison.Ordinal)
            ? observation
            : null;
    }

    public Task<ServiceTrafficViewSnapshot?> GetServiceTrafficViewAsync(
        ServiceIdentity identity,
        CancellationToken ct = default) =>
        _trafficViewQueryReader.GetAsync(identity, ct);
}
