namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

public sealed class OrleansActor : IActor
{
    private readonly IRuntimeActorGrain _grain;
    private readonly Aevatar.Foundation.Abstractions.IStreamProvider _streams;

    public OrleansActor(
        string id,
        IRuntimeActorGrain grain,
        Aevatar.Foundation.Abstractions.IStreamProvider streams)
    {
        Id = id;
        _grain = grain;
        _streams = streams;
        Agent = new OrleansAgentProxy(id, grain, streams);
    }

    public string Id { get; }

    public IAgent Agent { get; }

    public Task ActivateAsync(CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) =>
        _grain.DeactivateAsync();

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return _streams.GetStream(Id).ProduceAsync(envelope, ct);
    }

    public Task<string?> GetParentIdAsync() =>
        _grain.GetParentAsync();

    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
        _grain.GetChildrenAsync();
}
