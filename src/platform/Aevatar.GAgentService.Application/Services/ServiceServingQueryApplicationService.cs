using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceServingQueryApplicationService : IServiceServingQueryPort
{
    private readonly IServiceServingSetQueryReader _servingSetQueryReader;
    private readonly IServiceRolloutQueryReader _rolloutQueryReader;
    private readonly IServiceTrafficViewQueryReader _trafficViewQueryReader;

    public ServiceServingQueryApplicationService(
        IServiceServingSetQueryReader servingSetQueryReader,
        IServiceRolloutQueryReader rolloutQueryReader,
        IServiceTrafficViewQueryReader trafficViewQueryReader)
    {
        _servingSetQueryReader = servingSetQueryReader ?? throw new ArgumentNullException(nameof(servingSetQueryReader));
        _rolloutQueryReader = rolloutQueryReader ?? throw new ArgumentNullException(nameof(rolloutQueryReader));
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

    public Task<ServiceTrafficViewSnapshot?> GetServiceTrafficViewAsync(
        ServiceIdentity identity,
        CancellationToken ct = default) =>
        _trafficViewQueryReader.GetAsync(identity, ct);
}
