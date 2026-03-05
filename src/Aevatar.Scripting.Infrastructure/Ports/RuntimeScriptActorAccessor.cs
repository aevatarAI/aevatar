using Aevatar.Foundation.Abstractions;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptActorAccessor
{
    private readonly IActorRuntime _runtime;

    public RuntimeScriptActorAccessor(IActorRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task<IActor> GetOrCreateAsync<TAgent>(
        string actorId,
        string notFoundMessage,
        CancellationToken ct)
        where TAgent : IAgent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(notFoundMessage);

        if (!await _runtime.ExistsAsync(actorId))
            return await _runtime.CreateAsync<TAgent>(actorId, ct);

        return await _runtime.GetAsync(actorId)
            ?? throw new InvalidOperationException($"{notFoundMessage}: {actorId}");
    }

    public Task<IActor?> GetAsync(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return _runtime.GetAsync(actorId);
    }

    public Task<bool> ExistsAsync(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return _runtime.ExistsAsync(actorId);
    }

    public Task<IActor> CreateAsync<TAgent>(string actorId, CancellationToken ct)
        where TAgent : IAgent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return _runtime.CreateAsync<TAgent>(actorId, ct);
    }
}
