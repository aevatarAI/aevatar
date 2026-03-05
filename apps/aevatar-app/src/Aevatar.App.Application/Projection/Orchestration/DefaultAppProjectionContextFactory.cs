namespace Aevatar.App.Application.Projection.Orchestration;

public sealed class DefaultAppProjectionContextFactory : IAppProjectionContextFactory
{
    public AppProjectionContext Create(string actorId) =>
        new()
        {
            ActorId = actorId,
            RootActorId = actorId,
        };
}
