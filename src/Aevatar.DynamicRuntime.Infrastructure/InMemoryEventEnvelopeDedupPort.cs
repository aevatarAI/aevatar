using System.Collections.Concurrent;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class InMemoryEventEnvelopeDedupPort : IEventEnvelopeDedupPort
{
    private readonly ConcurrentDictionary<string, DateTime> _records = new(StringComparer.Ordinal);

    public Task<EnvelopeDedupResult> CheckAndRecordAsync(string scope, string dedupKey, TimeSpan ttl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var composite = $"{scope}:{dedupKey}";
        var now = DateTime.UtcNow;

        while (true)
        {
            if (_records.TryGetValue(composite, out var expiresAt))
            {
                if (expiresAt > now)
                    return Task.FromResult(new EnvelopeDedupResult(false, true, "ENVELOPE_DUPLICATE", "duplicate envelope"));

                if (!_records.TryUpdate(composite, now.Add(ttl), expiresAt))
                    continue;

                return Task.FromResult(new EnvelopeDedupResult(true, false));
            }

            if (_records.TryAdd(composite, now.Add(ttl)))
                return Task.FromResult(new EnvelopeDedupResult(true, false));
        }
    }
}
