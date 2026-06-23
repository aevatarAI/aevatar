namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceExternalExposureIntentPort
{
    Task ApplyAsync(
        ServiceExternalExposureIntentRequest request,
        CancellationToken ct = default);
}
