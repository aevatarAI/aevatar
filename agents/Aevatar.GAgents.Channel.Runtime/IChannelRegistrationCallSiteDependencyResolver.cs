namespace Aevatar.GAgents.Channel.Runtime;

public interface IChannelRegistrationCallSiteDependencyResolver
{
    Task<bool> IsAuthorizedDependencyAsync(
        string scopeId,
        string callSiteId,
        string serviceInstanceId,
        CancellationToken ct = default);
}

internal sealed class DefaultChannelRegistrationCallSiteDependencyResolver
    : IChannelRegistrationCallSiteDependencyResolver
{
    public Task<bool> IsAuthorizedDependencyAsync(
        string scopeId,
        string callSiteId,
        string serviceInstanceId,
        CancellationToken ct = default) =>
        Task.FromResult(false);
}
