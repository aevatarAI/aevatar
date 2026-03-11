using Aevatar.Foundation.Abstractions;

namespace Aevatar.Scripting.Infrastructure.Ports;

public abstract class ScriptActorCommandPortBase<TAgent>
    where TAgent : IAgent
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly RuntimeScriptActorAccessor _actorAccessor;

    protected ScriptActorCommandPortBase(
        IActorDispatchPort dispatchPort,
        RuntimeScriptActorAccessor actorAccessor)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
    }

    protected Task<bool> ExistsAsync(string actorId) => _actorAccessor.ExistsAsync(actorId);

    protected Task<IActor?> GetActorAsync(string actorId) => _actorAccessor.GetAsync(actorId);

    protected Task<IActor> CreateActorAsync(string actorId, CancellationToken ct) =>
        _actorAccessor.CreateAsync<TAgent>(actorId, ct);

    protected Task<IActor> GetOrCreateActorAsync(
        string actorId,
        string notFoundMessage,
        CancellationToken ct) =>
        _actorAccessor.GetOrCreateAsync<TAgent>(actorId, notFoundMessage, ct);

    protected async Task<IActor> GetRequiredActorAsync(
        string actorId,
        string notFoundMessage)
    {
        var actor = await _actorAccessor.GetAsync(actorId);
        return actor ?? throw new InvalidOperationException($"{notFoundMessage}: {actorId}");
    }

    protected Task DispatchAsync<TRequest>(
        string actorId,
        TRequest request,
        Func<TRequest, string, EventEnvelope> map,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(map);

        return _dispatchPort.DispatchAsync(actorId, map(request, actorId), ct);
    }
}
