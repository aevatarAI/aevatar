namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

internal sealed class OrleansAgentProxy : IAgent
{
    private static readonly IReadOnlyList<Type> EmptySubscribedTypes = Array.Empty<Type>();
    private readonly IRuntimeActorGrain _grain;

    public OrleansAgentProxy(string actorId, IRuntimeActorGrain grain)
    {
        Id = actorId;
        _grain = grain;
    }

    public string Id { get; }

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
        _grain.HandleEnvelopeAsync(envelope.ToByteArray());

    public Task<string> GetDescriptionAsync() =>
        _grain.GetDescriptionAsync();

    public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult(EmptySubscribedTypes);

    public Task ActivateAsync(CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) =>
        _grain.DeactivateAsync();
}
