using System.Collections.Concurrent;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class InMemoryIdempotencyPort : IIdempotencyPort
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Task<IdempotencyAcquireResult> AcquireAsync(string scope, string key, byte[] requestHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var composite = $"{scope}:{key}";
        var encodedRequest = Convert.ToHexString(requestHash);

        while (true)
        {
            if (_entries.TryGetValue(composite, out var existing))
            {
                if (!string.Equals(existing.RequestHashHex, encodedRequest, StringComparison.Ordinal))
                    return Task.FromResult(new IdempotencyAcquireResult(false, false, "IDEMPOTENCY_PAYLOAD_MISMATCH"));

                if (string.IsNullOrWhiteSpace(existing.ResponseHashHex))
                    return Task.FromResult(new IdempotencyAcquireResult(false, true));

                return Task.FromResult(new IdempotencyAcquireResult(false, true));
            }

            var created = new Entry(encodedRequest, ResponseHashHex: null);
            if (_entries.TryAdd(composite, created))
                return Task.FromResult(new IdempotencyAcquireResult(true, false));
        }
    }

    public Task CommitAsync(string scope, string key, byte[] responseHash, string responsePayload, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var composite = $"{scope}:{key}";
        var encodedResponse = Convert.ToHexString(responseHash);

        while (true)
        {
            if (!_entries.TryGetValue(composite, out var existing))
                return Task.CompletedTask;

            if (string.Equals(existing.ResponseHashHex, encodedResponse, StringComparison.Ordinal))
                return Task.CompletedTask;

            var updated = existing with
            {
                ResponseHashHex = encodedResponse,
                ResponsePayload = responsePayload,
            };
            if (_entries.TryUpdate(composite, updated, existing))
                return Task.CompletedTask;
        }
    }

    public Task<string?> GetCommittedResponseAsync(string scope, string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var composite = $"{scope}:{key}";
        if (!_entries.TryGetValue(composite, out var existing))
            return Task.FromResult<string?>(null);
        return Task.FromResult(existing.ResponsePayload);
    }

    private sealed record Entry(string RequestHashHex, string? ResponseHashHex, string? ResponsePayload = null);
}
