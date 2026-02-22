using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

public sealed class OrleansActor : IActor
{
    private readonly IRuntimeActorGrain _grain;
    private readonly IOrleansTransportEventSender? _transportEventSender;

    public OrleansActor(
        string id,
        IRuntimeActorGrain grain,
        IOrleansTransportEventSender? transportEventSender = null)
    {
        Id = id;
        _grain = grain;
        _transportEventSender = transportEventSender;
        Agent = new OrleansAgentProxy(id, grain, transportEventSender);
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

        if (_transportEventSender != null)
            return _transportEventSender.SendAsync(Id, envelope, ct);

        return _grain.HandleEnvelopeAsync(envelope.ToByteArray());
    }

    public Task<string?> GetParentIdAsync() =>
        _grain.GetParentAsync();

    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
        _grain.GetChildrenAsync();
}
