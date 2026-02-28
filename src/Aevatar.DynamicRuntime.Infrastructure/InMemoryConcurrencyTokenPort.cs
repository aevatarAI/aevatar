using System.Collections.Concurrent;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class InMemoryConcurrencyTokenPort : IConcurrencyTokenPort
{
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.Ordinal);

    public Task<ConcurrencyCheckResult> CheckAndAdvanceAsync(string aggregateId, string? expectedVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        while (true)
        {
            var current = _versions.GetOrAdd(aggregateId, 0);
            var currentTag = current.ToString();

            if (!string.IsNullOrWhiteSpace(expectedVersion) && !string.Equals(expectedVersion, currentTag, StringComparison.Ordinal))
                return Task.FromResult(new ConcurrencyCheckResult(false, currentTag, currentTag, "VERSION_CONFLICT"));

            var next = current + 1;
            if (_versions.TryUpdate(aggregateId, next, current))
                return Task.FromResult(new ConcurrencyCheckResult(true, currentTag, next.ToString()));
        }
    }
}
