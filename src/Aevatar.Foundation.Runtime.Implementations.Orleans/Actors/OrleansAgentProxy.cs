namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

internal sealed class OrleansAgentProxy : IAgent
{
    private static readonly IReadOnlyList<Type> EmptySubscribedTypes = Array.Empty<Type>();
    private readonly IRuntimeActorGrain _grain;
    private readonly Aevatar.Foundation.Abstractions.IStreamProvider _streams;

    public OrleansAgentProxy(
        string actorId,
        IRuntimeActorGrain grain,
        Aevatar.Foundation.Abstractions.IStreamProvider streams)
    {
        Id = actorId;
        _grain = grain;
        _streams = streams;
    }

    public string Id { get; }

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return _streams.GetStream(Id).ProduceAsync(envelope, ct);
    }

    public Task<string> GetDescriptionAsync() =>
        _grain.GetDescriptionAsync();

    public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult(EmptySubscribedTypes);

    public Task ActivateAsync(CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) =>
        _grain.DeactivateAsync();
}
