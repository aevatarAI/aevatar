namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IGAgentActorStore
{
    Task<IReadOnlyList<GAgentActorGroup>> GetAsync(CancellationToken cancellationToken = default);
    Task AddActorAsync(string gagentType, string actorId, CancellationToken cancellationToken = default);
    Task RemoveActorAsync(string gagentType, string actorId, CancellationToken cancellationToken = default);
}

public sealed record GAgentActorGroup(string GAgentType, IReadOnlyList<string> ActorIds);
