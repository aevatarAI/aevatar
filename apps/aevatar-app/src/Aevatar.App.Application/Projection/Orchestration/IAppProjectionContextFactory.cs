namespace Aevatar.App.Application.Projection.Orchestration;

public interface IAppProjectionContextFactory
{
    AppProjectionContext Create(string actorId);
}
