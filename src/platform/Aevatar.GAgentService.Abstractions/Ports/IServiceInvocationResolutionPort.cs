namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceInvocationResolutionPort
{
    Task<bool> HasServiceAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<ServiceInvocationResolvedTarget> ResolveAsync(
        ServiceInvocationRequest request,
        CancellationToken ct = default);
}
