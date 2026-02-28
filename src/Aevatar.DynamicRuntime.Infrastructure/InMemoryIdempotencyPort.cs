using System.Collections.Concurrent;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class InMemoryIdempotencyPort : IIdempotencyPort
{
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);

    public Task<bool> TryAcquireAsync(string scope, string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var composite = $"{scope}:{key}";
        return Task.FromResult(_keys.TryAdd(composite, 0));
    }
}
