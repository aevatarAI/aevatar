using System.Collections.Concurrent;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class InMemoryConcurrencyTokenPort : IConcurrencyTokenPort
{
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.Ordinal);

    public Task<string> CheckAndAdvanceAsync(string aggregateId, string? expectedETag, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        while (true)
        {
            var current = _versions.GetOrAdd(aggregateId, 0);
            var currentEtag = current.ToString();

            if (!string.IsNullOrWhiteSpace(expectedETag) && !string.Equals(expectedETag, currentEtag, StringComparison.Ordinal))
                throw new InvalidOperationException("VERSION_CONFLICT");

            var next = current + 1;
            if (_versions.TryUpdate(aggregateId, next, current))
                return Task.FromResult(next.ToString());
        }
    }
}
