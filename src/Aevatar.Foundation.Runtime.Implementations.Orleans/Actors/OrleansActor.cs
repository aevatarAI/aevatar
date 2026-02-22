namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

public sealed class OrleansActor : IActor
{
    private readonly IRuntimeActorGrain _grain;

    public OrleansActor(string id, IRuntimeActorGrain grain)
    {
        Id = id;
        _grain = grain;
        Agent = new OrleansAgentProxy(id, grain);
    }

    public string Id { get; }

    public IAgent Agent { get; }

    public Task ActivateAsync(CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) =>
        _grain.DeactivateAsync();

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
        _grain.HandleEnvelopeAsync(envelope.ToByteArray());

    public Task<string?> GetParentIdAsync() =>
        _grain.GetParentAsync();

    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
        _grain.GetChildrenAsync();
}
